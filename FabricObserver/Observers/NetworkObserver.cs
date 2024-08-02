// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.TelemetryLib;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    /// <summary>
    /// This observer monitors network conditions related to user-supplied configuration settings in
    /// NetworkObserver.json.config. This includes testing the connection state (is destination reachable, nothing more) of supplied endpoint/port pairs.
    /// The output (a local file) is used by the API service and the HTML frontend (https://[domain:[port]]/api/ObserverManager).
    /// Health Report processor will also emit diagnostic telemetry if configured in Settings.xml.
    /// </summary>
    /// <remarks>
    /// Creates a new instance of the type.
    /// </remarks>
    /// <param name="context">The StatelessServiceContext instance.</param>
    public sealed class NetworkObserver(StatelessServiceContext context) : ObserverBase(null, context)
    {
        private const int MaxTcpConnTestRetries = 5;
        private readonly List<NetworkObserverConfig> defaultConfig =
        [
            new NetworkObserverConfig
            {
                TargetApp = "fabric:/test",
                Endpoints =
                [
                    new() {
                        HostName = "www.microsoft.com",
                        Port = 443
                    },
                    new() {
                        HostName = "www.facebook.com",
                        Port = 443
                    },
                    new() {
                        HostName = "www.google.com",
                        Port = 443
                    }
                ]
            }
        ];
        private readonly List<NetworkObserverConfig> userConfig = [];
        private readonly List<ConnectionState> connectionStatus = [];
        private readonly Dictionary<string, bool> connEndpointTestResults = [];
        private readonly Stopwatch stopwatch = new();
        private HealthState healthState = HealthState.Ok;
        private int tcpConnTestRetried;

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            if (RunInterval > TimeSpan.Zero && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                ObserverLogger.LogInfo($"ObserveAsync: RunInterval ({RunInterval}) has not elapsed. Exiting.");
                return;
            }

            if (!await InitializeAsync() || token.IsCancellationRequested)
            {
                stopwatch.Stop();
                stopwatch.Reset();
                LastRunDateTime = DateTime.Now;
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            Token = token;
            stopwatch.Start();

            // Run conn tests.
            Retry.Do(InternetConnectionStateIsConnected, TimeSpan.FromSeconds(10), token);
            await ReportAsync(token);

            // The time it took to run this observer.
            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;

            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration: {RunDuration}");
            }

            stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            var timeToLiveWarning = GetHealthReportTTL();

            // Report on connection state.
            foreach (var config in userConfig)
            {
                token.ThrowIfCancellationRequested();

                foreach (var conn in connectionStatus.Where(cs => cs.TargetApp == config.TargetApp))
                {
                    token.ThrowIfCancellationRequested();

                    if (!conn.Connected)
                    {
                        healthState = HealthState.Warning;
                        var healthMessage = $"Outbound Internet connection failure detected for endpoint {conn.HostName}";

                        // Send Health Telemetry (perhaps it signals an Alert in AppInsights or LogAnalytics).
                        // This will also be serialized into the health event Description.
                        var telemetryData = new NetworkTelemetryData()
                        {
                            ApplicationName = conn.TargetApp,
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            Code = FOErrorWarningCodes.AppWarningNetworkEndpointUnreachable,
                            EntityType = EntityType.Application,
                            HealthState = HealthState.Warning,
                            Description = healthMessage,
                            NodeType = FabricServiceContext.NodeContext.NodeType,
                            ObserverName = ObserverName,
                            Metric = ErrorWarningProperty.InternetConnectionFailure,
                            NodeName = NodeName,
                            Source = ObserverConstants.FabricObserverName,
                            Property = $"EndpointUnreachable({conn.HostName})"
                        };

                        if (IsTelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportHealthAsync(telemetryData, Token);
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                        }

                        var report = new HealthReport
                        {
                            AppName = new Uri(conn.TargetApp),
                            Code = FOErrorWarningCodes.AppWarningNetworkEndpointUnreachable,
                            EntityType = EntityType.Application,
                            EmitLogEvent = EnableVerboseLogging,
                            HealthData = telemetryData,
                            HealthMessage = healthMessage,
                            HealthReportTimeToLive = timeToLiveWarning,
                            SourceId = $"{ObserverConstants.NetworkObserverName}({FOErrorWarningCodes.AppWarningNetworkEndpointUnreachable})",
                            State = healthState,
                            NodeName = NodeName,
                            Observer = ObserverName,
                            Property = $"EndpointUnreachable({conn.HostName})",
                            ResourceUsageDataProperty = $"{ErrorWarningProperty.InternetConnectionFailure}: {conn.HostName}"
                        };

                        // Send health report Warning and log event locally.
                        HealthReporter.ReportHealthToServiceFabric(report);

                        // This means this observer created a Warning or Error SF Health Report
                        HasActiveFabricErrorOrWarning = true;
                    }
                    else
                    {
                        if (conn.HealthState == HealthState.Ok)
                        {
                            continue;
                        }

                        healthState = HealthState.Ok;
                        var healthMessage = $"Outbound Internet connection successful for {conn.HostName} from node {NodeName}.";

                        // Clear existing Health Warning.
                        var report = new HealthReport
                        {
                            AppName = new Uri(conn.TargetApp),
                            Code = FOErrorWarningCodes.AppWarningNetworkEndpointUnreachable,
                            EmitLogEvent = EnableVerboseLogging,
                            HealthMessage = healthMessage,
                            HealthReportTimeToLive = default,
                            SourceId = $"{ObserverConstants.NetworkObserverName}({FOErrorWarningCodes.AppWarningNetworkEndpointUnreachable})",
                            State = HealthState.Ok,
                            NodeName = NodeName,
                            Observer = ObserverName,
                            Property = $"EndpointUnreachable({conn.HostName})",
                            EntityType = EntityType.Application
                        };

                        HealthReporter.ReportHealthToServiceFabric(report);

                        var telemetryData = new NetworkTelemetryData()
                        {
                            ApplicationName = conn.TargetApp,
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            Code = FOErrorWarningCodes.Ok,
                            EntityType = EntityType.Application,
                            HealthState = HealthState.Ok,
                            Description = healthMessage,
                            NodeType = FabricServiceContext.NodeContext.NodeType,
                            ObserverName = ObserverName,
                            Metric = "Internet Connection State",
                            NodeName = NodeName,
                            Source = ObserverConstants.FabricObserverName
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportHealthAsync(telemetryData, Token);
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                        }

                        // Reset health state.
                        HasActiveFabricErrorOrWarning = false;
                    }
                }
            }

            // Clear
            _ = connectionStatus.RemoveAll(conn => conn.Connected);
            connectionStatus.TrimExcess();
            connEndpointTestResults.Clear();

            return Task.CompletedTask;
        }

        private async Task<bool> InitializeAsync()
        {
            Token.ThrowIfCancellationRequested();

            var networkObserverConfigFileName =
                Path.Combine(ConfigPackage.Path, GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.ConfigurationFileNameParameter));

            if (string.IsNullOrWhiteSpace(networkObserverConfigFileName))
            {
                ObserverLogger.LogWarning("NetworkObserver configuration file path not specified. Exiting.");
                return false;
            }

            if (!File.Exists(networkObserverConfigFileName))
            {
                ObserverLogger.LogWarning("NetworkObserver configuration file not found. Exiting.");
                return false;
            }

            if (userConfig.Count != 0)
            {
                MonitoredAppCount = userConfig.Count;
                return true;
            }

            // Get the user config settings and fill userConfig list.
            await using Stream stream = new FileStream(networkObserverConfigFileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var configs = JsonHelper.ReadFromJsonStream<NetworkObserverConfig[]>(stream);

            foreach (var netConfig in configs)
            {
                var deployedApps = await FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(NodeName, new Uri(netConfig.TargetApp));

                if (deployedApps == null || deployedApps.Count < 1)
                {
                    continue;
                }

                userConfig.Add(netConfig);
            }

            MonitoredAppCount = userConfig.Count;
            return userConfig.Count != 0;
        }

        private void InternetConnectionStateIsConnected()
        {
            var configList = defaultConfig;

            if (userConfig.Count > 0)
            {
                configList = userConfig;
            }

            using HttpClient httpClient = new();

            foreach (var config in configList)
            {
                Token.ThrowIfCancellationRequested();

                foreach (var endpoint in config.Endpoints)
                {
                    Token.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(endpoint.HostName))
                    {
                        continue;
                    }

                    // Don't re-test endpoint if it has already been tested for a different targetApp.
                    if (connEndpointTestResults.TryGetValue(endpoint.HostName, out bool value))
                    {
                        SetHealthState(endpoint, config.TargetApp, value);
                        continue;
                    }

                    bool passed = false;

                    // SQL Azure, other database services that are addressable over direct TCP.
                    if (endpoint.Protocol == DirectInternetProtocol.Tcp)
                    {
                        passed = TcpEndpointDoConnectionTest(endpoint.HostName, endpoint.Port);
                    }
                    else // Default is http.
                    {
                        // Service REST endpoints, CosmosDB REST endpoint, etc.
                        // Http protocol means any enpoint/port pair that is addressable over HTTP/s.
                        // E.g., REST endpoints, etc.
                        try
                        {
                            Token.ThrowIfCancellationRequested();

                            ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
                            string prefix = endpoint.Port == 443 ? "https://" : "http://";

                            if (endpoint.HostName.Contains("://"))
                            {
                                prefix = string.Empty;
                            }

                            using (HttpResponseMessage response =
                                    httpClient.Send(
                                        new HttpRequestMessage(
                                        HttpMethod.Get, new Uri($"{prefix}{endpoint.HostName}:{endpoint.Port}")),
                                        HttpCompletionOption.ResponseHeadersRead, Token))
                            {

                                HttpStatusCode status = response.StatusCode;

                                // The target server responded with something. It doesn't really matter what it "said".
                                if (status == HttpStatusCode.OK || response.Headers.Any())
                                {
                                    passed = true;
                                }
                            }
                        }
                        catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
                        {
                            ObserverLogger.LogWarning($"Handled NetworkObserver Failure:{Environment.NewLine}{e.Message}");
                        }
                        catch (Exception e) when (e is OperationCanceledException or TaskCanceledException)
                        {
                            return;
                        }
                        catch (Exception e)
                        {
                            ObserverLogger.LogWarning($"Unhandled NetworkObserver Failure:{Environment.NewLine}{e}");

                            // Fix the bug.
                            throw;
                        }
                    }

                    SetHealthState(endpoint, config.TargetApp, passed);
                    _ = connEndpointTestResults.TryAdd(endpoint.HostName, passed);
                }
            }
        }

        private bool TcpEndpointDoConnectionTest(string hostName, int port)
        {
            TcpClient tcpClient = null;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // NetworkObserver only cares about remote endpoint/port *availability*, nothing more.
            // This test simply tries to connect to a remote endpoint using TCP sockets. It will attempt to
            // send a byte of data to the remote connection. If it fails, it will retry 5 times.
            try
            {
                // The ctor used here will throw a SocketException (e.g., HostNotFound) if the hostname can't be resolved,
                // so it would therefore be unreachable as far as this test is concerned.
                tcpClient = new TcpClient(hostName, port);

                if (tcpClient.Client.Connected)
                {
                    // Data to send to the remote host.
                    byte[] sendBuffer = new byte[1];
                    tcpClient.SendTimeout = 10000;

                    if (tcpClient.Client.Poll(1000, SelectMode.SelectWrite))
                    {
                        tcpClient.Client.Send(sendBuffer);
                        tcpConnTestRetried = 0;

                        return true;
                    }
                }
            }
            catch (IOException ie)
            {
                if (ie.InnerException is SocketException se)
                {
                    if (se.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
                    {
                        if (tcpConnTestRetried <= MaxTcpConnTestRetries)
                        {
                            tcpConnTestRetried++;
                            Thread.Sleep(1000);
                            _ = TcpEndpointDoConnectionTest(hostName, port);
                        }
                        else
                        {
                            tcpConnTestRetried = 0;
                        }
                    }
                }
            }
            catch (SocketException se)
            {
                if (se.SocketErrorCode is not SocketError.ConnectionRefused and not SocketError.ConnectionReset)
                {
                    return false;
                }

                if (tcpConnTestRetried < MaxTcpConnTestRetries)
                {
                    tcpConnTestRetried++;
                    Thread.Sleep(1000);
                    _ = TcpEndpointDoConnectionTest(hostName, port);
                }
                else
                {
                    tcpConnTestRetried = 0;
                }

                return false;
            }
            finally
            {
                tcpClient?.Close();
            }

            return false;
        }

        private void SetHealthState(Endpoint endpoint, string targetApp, bool passed)
        {
            if (passed)
            {
                if (healthState == HealthState.Warning && connectionStatus.Any(conn => conn.HostName == endpoint.HostName && conn.HealthState == HealthState.Warning))
                {
                    _ = connectionStatus.RemoveAll(conn => conn.HostName == endpoint.HostName);

                    connectionStatus.Add(
                        new ConnectionState
                        {
                            HostName = endpoint.HostName,
                            Connected = true,
                            HealthState = HealthState.Warning,
                            TargetApp = targetApp
                        });
                }
                else
                {
                    connectionStatus.Add(
                        new ConnectionState
                        {
                            HostName = endpoint.HostName,
                            Connected = true,
                            HealthState = HealthState.Ok,
                            TargetApp = targetApp
                        });
                }
            }
            else
            {
                if (connectionStatus.Any(
                    conn =>
                        conn.HostName == endpoint.HostName && conn.TargetApp == targetApp &&
                        conn.HealthState == HealthState.Warning))
                {
                    return;
                }

                connectionStatus.Add(
                    new ConnectionState
                    {
                        HostName = endpoint.HostName,
                        Connected = false,
                        HealthState = HealthState.Warning,
                        TargetApp = targetApp
                    });

                if (!ServiceNames.Contains(targetApp))
                {
                    ServiceNames.Enqueue(targetApp);
                }
            }
        }
    }
}