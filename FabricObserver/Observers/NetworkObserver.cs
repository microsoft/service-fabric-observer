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
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    /// <summary>
    /// This observer monitors network conditions related to user-supplied configuration settings in
    /// NetworkObserver.json.config. This includes testing the connection state (is destination reachable, nothing more) of supplied endpoint/port pairs.
    /// The output (a local file) is used by the API service and the HTML frontend (https://[domain:[port]]/api/ObserverManager).
    /// Health Report processor will also emit diagnostic telemetry if configured in Settings.xml.
    /// </summary>
    public class NetworkObserver : ObserverBase
    {
        private const int MaxTcpConnTestRetries = 5;
        private readonly List<NetworkObserverConfig> defaultConfig = new List<NetworkObserverConfig>
        {
            new NetworkObserverConfig
            {
                TargetApp = "fabric:/test",
                Endpoints = new List<Endpoint>
                {
                    new Endpoint
                    {
                        HostName = "www.microsoft.com",
                        Port = 443
                    },
                    new Endpoint
                    {
                        HostName = "www.facebook.com",
                        Port = 443
                    },
                    new Endpoint
                    {
                        HostName = "www.google.com",
                        Port = 443
                    }
                }
            }
        };

        private readonly List<NetworkObserverConfig> userConfig = new List<NetworkObserverConfig>();
        private readonly List<ConnectionState> connectionStatus = new List<ConnectionState>();
        private readonly Dictionary<string, bool> connEndpointTestResults = new Dictionary<string, bool>();
        private readonly Stopwatch stopwatch;
        private HealthState healthState = HealthState.Ok;
        private bool hasRun;
        private int tcpConnTestRetried;

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public NetworkObserver(StatelessServiceContext context) : base(null, context)
        {
            stopwatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
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
            hasRun = true;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            var timeToLiveWarning = GetHealthReportTimeToLive();

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
                        var telemetryData = new TelemetryData()
                        {
                            ApplicationName = conn.TargetApp,
                            Code = FOErrorWarningCodes.AppWarningNetworkEndpointUnreachable,
                            EntityType = EntityType.Application,
                            HealthState = HealthState.Warning,
                            Description = healthMessage,
                            ObserverName = ObserverName,
                            Metric = ErrorWarningProperty.InternetConnectionFailure,
                            NodeName = NodeName,
                            Source = ObserverConstants.FabricObserverName
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
                            EmitLogEvent = EnableVerboseLogging || IsObserverWebApiAppDeployed,
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
                        if (conn.Health != HealthState.Warning || conn.Health != HealthState.Error)
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
                            EmitLogEvent = EnableVerboseLogging || IsObserverWebApiAppDeployed,
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

                        var telemetryData = new TelemetryData()
                        {
                            ApplicationName = conn.TargetApp,
                            Code = FOErrorWarningCodes.Ok,
                            EntityType = EntityType.Application,
                            HealthState = HealthState.Ok,
                            Description = healthMessage,
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

        private static string GetNetworkInterfaceInfo(CancellationToken token)
        {
            try
            {
                var iPGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
                var nics = NetworkInterface.GetAllNetworkInterfaces();

                if (nics.Length < 1)
                {
                    return string.Empty;
                }

                var interfaceInfo = new StringBuilder($"Network Interface information for {iPGlobalProperties.HostName}:{Environment.NewLine}     ");

                foreach (var nic in nics)
                {
                    token.ThrowIfCancellationRequested();

                    _ = interfaceInfo.Append($"{Environment.NewLine}{nic.Description}{Environment.NewLine}");
                    _ = interfaceInfo.AppendFormat($"  Interface type    : {0}{Environment.NewLine}", nic.NetworkInterfaceType);
                    _ = interfaceInfo.AppendFormat($"  Operational status: {0}{Environment.NewLine}", nic.OperationalStatus);

                    // Traffic.
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    _ = interfaceInfo.AppendLine("  Traffic Info:");

                    var stats = nic.GetIPv4Statistics();

                    _ = interfaceInfo.AppendFormat($"    Bytes received: {0}{Environment.NewLine}", stats.BytesReceived);
                    _ = interfaceInfo.AppendFormat($"    Bytes sent: {0}{Environment.NewLine}", stats.BytesSent);
                    _ = interfaceInfo.AppendFormat($"    Incoming Packets With Errors: {0}{Environment.NewLine}", stats.IncomingPacketsWithErrors);
                    _ = interfaceInfo.AppendFormat($"    Outgoing Packets With Errors: {0}{Environment.NewLine}", stats.OutgoingPacketsWithErrors);
                    _ = interfaceInfo.AppendLine();
                }

                var s = interfaceInfo.ToString();
                _ = interfaceInfo.Clear();

                return s;
            }
            catch (NetworkInformationException)
            {
                return string.Empty;
            }
        }

        private async Task<bool> InitializeAsync()
        {
            Token.ThrowIfCancellationRequested();

            // This only needs to be logged once.
            // This file is used by the ObserverWebApi application.
            if (IsObserverWebApiAppDeployed && !hasRun)
            {
                var logPath = Path.Combine(ObserverLogger.LogFolderBasePath, "NetInfo.txt");

                Console.WriteLine($"logPath: {logPath}");

                if (!ObserverLogger.TryWriteLogFile(logPath, GetNetworkInterfaceInfo(Token)))
                {
                    ObserverLogger.LogWarning("Unable to create NetInfo.txt file.");
                }
            }

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
                    if (connEndpointTestResults.ContainsKey(endpoint.HostName))
                    {
                        SetHealthState(endpoint, config.TargetApp, connEndpointTestResults[endpoint.HostName]);
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

                            var request = (HttpWebRequest)WebRequest.Create(new Uri($"{prefix}{endpoint.HostName}:{endpoint.Port}"));
                            request.AuthenticationLevel = AuthenticationLevel.MutualAuthRequired;
                            request.ImpersonationLevel = TokenImpersonationLevel.Impersonation;
                            request.Timeout = 60000;
                            request.Method = "GET";

                            using var response = (HttpWebResponse)request.GetResponse();
                            var status = response.StatusCode;

                            // The target server responded with something.
                            // It doesn't really matter what it "said".
                            if (status == HttpStatusCode.OK || response.Headers?.Count > 0)
                            {
                                passed = true;
                            }
                        }
                        catch (IOException ie)
                        {
                            if (ie.InnerException is ProtocolViolationException)
                            {
                                passed = true;
                            }
                        }
                        catch (WebException we)
                        {
                            if (we.Status == WebExceptionStatus.ProtocolError
                                || we.Status == WebExceptionStatus.TrustFailure
                                || we.Status == WebExceptionStatus.SecureChannelFailure
                                || we.Response?.Headers?.Count > 0)
                            {
                                // Could not establish trust or server doesn't want to hear from you, or...
                                // Either way, the Server *responded*. It's reachable.
                                // You could always add code to grab your app or cluster certs from local store
                                // and apply it to the request. See CertificateObserver for how to get
                                // both your App cert(s) and Cluster cert. The goal of NetworkObserver is
                                // to test availability. Nothing more.
                                passed = true;
                            }
                            else if (we.Status == WebExceptionStatus.SendFailure
                                        && we.InnerException != null
                                        && (we.InnerException.Message.ToLower().Contains("authentication")
                                        || we.InnerException.HResult == -2146232800))
                            {
                                passed = true;
                            }
                        }
                        catch (Exception e) when (!(e is OperationCanceledException))
                        {
                            ObserverLogger.LogWarning(e.ToString());

                            // Fix the bug..
                            throw;
                        }
                    }

                    SetHealthState(endpoint, config.TargetApp, passed);

                    if (!connEndpointTestResults.ContainsKey(endpoint.HostName))
                    {
                        connEndpointTestResults.Add(endpoint.HostName, passed);
                    }
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
                    if (se.SocketErrorCode == SocketError.ConnectionRefused || se.SocketErrorCode == SocketError.ConnectionReset)
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
                if (se.SocketErrorCode != SocketError.ConnectionRefused && se.SocketErrorCode != SocketError.ConnectionReset)
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
                if (healthState == HealthState.Warning && connectionStatus.Any(conn => conn.HostName == endpoint.HostName && conn.Health == HealthState.Warning))
                {
                    _ = connectionStatus.RemoveAll(conn => conn.HostName == endpoint.HostName);

                    connectionStatus.Add(
                        new ConnectionState
                        {
                            HostName = endpoint.HostName,
                            Connected = true,
                            Health = HealthState.Warning,
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
                            Health = HealthState.Ok,
                            TargetApp = targetApp
                        });
                }
            }
            else
            {
                if (connectionStatus.Any(
                    conn =>
                        conn.HostName == endpoint.HostName && conn.TargetApp == targetApp &&
                        conn.Health == HealthState.Warning))
                {
                    return;
                }

                connectionStatus.Add(
                    new ConnectionState
                    {
                        HostName = endpoint.HostName,
                        Connected = false,
                        Health = HealthState.Warning,
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