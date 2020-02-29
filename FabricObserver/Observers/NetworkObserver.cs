// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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
    /// networkobserver.json.config. This includes testing the connection state of supplied endpoint/port pairs,
    /// and measuring network traffic (bytes/sec, up/down).
    /// The output (a local file) is used by the API service and the HTML frontend (https://[domain:[port]]/api/ObserverManager).
    /// Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    /// </summary>
    public class NetworkObserver : ObserverBase
    {
        private readonly List<NetworkObserverConfig> defaultEndpoints = new List<NetworkObserverConfig>
        {
            new NetworkObserverConfig
            {
                AppTarget = "test",
                Endpoints = new List<Endpoint>
                {
                    new Endpoint
                    {
                        HostName = "www.microsoft.com",
                        Port = 443,
                    },
                    new Endpoint
                    {
                        HostName = "www.facebook.com",
                        Port = 443,
                    },
                    new Endpoint
                    {
                        HostName = "www.google.com",
                        Port = 443,
                    },
                },
            },
        };

        private readonly string dataPackagePath;
        private readonly InternetProtocol protocol = InternetProtocol.Tcp;
        private List<NetworkObserverConfig> userEndpoints = new List<NetworkObserverConfig>();
        private List<ConnectionState> connectionStatus = new List<ConnectionState>();
        private HealthState healthState = HealthState.Ok;
        private bool hasRun;
        private CancellationToken cancellationToken;

        /// <summary>
        /// Initializes a new instance of the <see cref="NetworkObserver"/> class.
        /// </summary>
        public NetworkObserver()
            : base(ObserverConstants.NetworkObserverName)
        {
            this.dataPackagePath = ConfigSettings.ConfigPackagePath;
        }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            if (!await this.Initialize().ConfigureAwait(true) || token.IsCancellationRequested)
            {
                return;
            }

            this.cancellationToken = token;

            // Run conn tests.
            Retry.Do(
                this.InternetConnectionStateIsConnected,
                TimeSpan.FromSeconds(10),
                token);

            await this.ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
            this.hasRun = true;
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

                var interfaceInfo = new StringBuilder(string.Format(
                    "Network Interface information for {0}:\n     ",
                    iPGlobalProperties.HostName));

                foreach (var nic in nics)
                {
                    token.ThrowIfCancellationRequested();

                    _ = interfaceInfo.Append("\n" + nic.Description + "\n");
                    _ = interfaceInfo.AppendFormat("  Interface type    : {0}\n", nic.NetworkInterfaceType);
                    _ = interfaceInfo.AppendFormat("  Operational status: {0}\n", nic.OperationalStatus);

                    // Traffic.
                    if (nic.OperationalStatus == OperationalStatus.Up)
                    {
                        _ = interfaceInfo.AppendLine("  Traffic Info:");

                        var stats = nic.GetIPv4Statistics();

                        _ = interfaceInfo.AppendFormat("    Bytes received: {0}\n", stats.BytesReceived);
                        _ = interfaceInfo.AppendFormat("    Bytes sent: {0}\n", stats.BytesSent);
                        _ = interfaceInfo.AppendFormat("    Incoming Packets With Errors: {0}\n", stats.IncomingPacketsWithErrors);
                        _ = interfaceInfo.AppendFormat("    Outgoing Packets With Errors: {0}\n", stats.OutgoingPacketsWithErrors);
                        _ = interfaceInfo.AppendLine();
                    }
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

        private async Task<bool> Initialize()
        {
            this.WriteToLogWithLevel(
                this.ObserverName,
                $"Initializing {this.ObserverName} for network monitoring. | {this.NodeName}",
                LogLevel.Information);

            this.cancellationToken.ThrowIfCancellationRequested();

            // This only needs to be logged once.
            // This file is used by the ObserverWebApi application.
            if (ObserverManager.ObserverWebAppDeployed && !this.hasRun)
            {
                var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "NetInfo.txt");

                if (!this.ObserverLogger.TryWriteLogFile(logPath, GetNetworkInterfaceInfo(this.cancellationToken)))
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.OriginalString,
                        this.ObserverName,
                        HealthState.Warning,
                        "Unable to create NetInfo.txt file.");
                }
            }

            // Is this a unit test run?
            if (this.IsTestRun)
            {
                return true;
            }

            var settings = this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(ObserverConstants.ObserverConfigurationPackageName)?.Settings;

            ConfigSettings.Initialize(settings, ObserverConstants.NetworkObserverConfigurationSectionName, "NetworkObserverDataFileName");

            var networkObserverConfigFileName = Path.Combine(this.dataPackagePath, ConfigSettings.NetworkObserverDataFileName);

            if (string.IsNullOrWhiteSpace(networkObserverConfigFileName))
            {
                this.ObserverLogger.LogError("Endpoint list file is not specified. Please Add file containing endpoints that need to be monitored.");

                return false;
            }

            this.cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(networkObserverConfigFileName))
            {
                this.ObserverLogger.LogError("Endpoint list file is not specified. Please Add file containing endpoints that need to be monitored. Using default endpoints for connection testing.");

                return false;
            }

            this.cancellationToken.ThrowIfCancellationRequested();

            if (this.userEndpoints.Count == 0)
            {
                using (Stream stream = new FileStream(networkObserverConfigFileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    this.userEndpoints.AddRange(JsonHelper.ReadFromJsonStream<NetworkObserverConfig[]>(stream));
                }

                if (this.userEndpoints.Count == 0)
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.ToString(),
                        this.ObserverName,
                        HealthState.Warning,
                        "Missing required configuration data: endpoints.");
                    return false;
                }
            }

            int configCount = 0;

            for (int i = 0; i < this.userEndpoints.Count; i++)
            {
                this.cancellationToken.ThrowIfCancellationRequested();

                var config = this.userEndpoints[i];

                var deployedApps = await this.FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(
                    this.NodeName,
                    new Uri(config.AppTarget)).ConfigureAwait(true);

                if (deployedApps == null || deployedApps.Count < 1)
                {
                    this.userEndpoints.RemoveAt(i);

                    continue;
                }

                configCount++;

                foreach (var endpoint in config.Endpoints)
                {
                    this.cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(endpoint.HostName))
                    {
                        this.HealthReporter.ReportFabricObserverServiceHealth(
                            this.FabricServiceContext.ServiceName.ToString(),
                            this.ObserverName,
                            HealthState.Warning,
                            $"Initialize() | Required endpoint parameter not set. Nothing to test.");
                        continue;
                    }

                    this.WriteToLogWithLevel(
                        this.ObserverName,
                        $"Monitoring outbound connection state to {endpoint.HostName} on Node {this.NodeName} for app {config.AppTarget}",
                        LogLevel.Information);
                }
            }

            // This observer shouldn't run if there are no app-specific endpoint/port pairs provided.
            if (configCount < 1)
            {
                return false;
            }

            return true;
        }

        // x-plat?
        private void InternetConnectionStateIsConnected()
        {
            var configList = this.defaultEndpoints;

            if (this.userEndpoints.Count > 0)
            {
                configList = this.userEndpoints;
            }

            if (this.protocol == InternetProtocol.Icmp)
            {
                using (var pingSender = new Ping())
                {
                    foreach (var config in configList)
                    {
                        foreach (var endpoint in config.Endpoints)
                        {
                            bool passed = false;
                            try
                            {
                                var reply = pingSender.Send(endpoint.HostName);
                                if (reply != null && reply.Status == IPStatus.Success)
                                {
                                    passed = true;
                                }
                            }
                            catch (PingException)
                            {
                            }

                            this.SetHealthState(endpoint, passed);
                        }
                    }
                }
            }
            else
            {
                foreach (var config in configList)
                {
                    this.cancellationToken.ThrowIfCancellationRequested();
                    foreach (var endpoint in config.Endpoints)
                    {
                        bool passed = false;

                        try
                        {
                            string prefix = "http://";

                            if (endpoint.Port == 443)
                            {
                                prefix = "https://";
                            }

                            if (!string.IsNullOrEmpty(endpoint.HostName))
                            {
                                var request = (HttpWebRequest)WebRequest.Create(new Uri(prefix + endpoint.HostName));
                                using (var response = (HttpWebResponse)request.GetResponse())
                                {
                                    if (response.StatusCode == HttpStatusCode.OK ||
                                        response.StatusCode == HttpStatusCode.Accepted)
                                    {
                                        passed = true;
                                    }
                                }
                            }
                        }
                        catch (WebException)
                        {
                        }
                        catch (Exception e)
                        {
                            this.HealthReporter.ReportFabricObserverServiceHealth(
                                this.FabricServiceContext.ServiceName.OriginalString,
                                this.ObserverName,
                                HealthState.Warning,
                                e.ToString());

                            throw;
                        }

                        this.SetHealthState(endpoint, passed);
                    }

                    this.cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        private void SetHealthState(Endpoint endpoint, bool passed)
        {
            if (passed)
            {
                if (this.healthState == HealthState.Warning &&
                    this.connectionStatus.Any(conn => conn.HostName == endpoint.HostName &&
                                                      conn.Health == HealthState.Warning))
                {
                    _ = this.connectionStatus.RemoveAll(conn => conn.HostName == endpoint.HostName);
                    this.connectionStatus.Add(new ConnectionState { HostName = endpoint.HostName, Connected = true, Health = HealthState.Warning });
                }
                else
                {
                    this.connectionStatus.Add(new ConnectionState { HostName = endpoint.HostName, Connected = true, Health = HealthState.Ok });
                }
            }
            else
            {
                if (!this.connectionStatus.Any(conn => conn.HostName == endpoint.HostName &&
                                               conn.Health == HealthState.Warning))
                {
                    this.connectionStatus.Add(new ConnectionState { HostName = endpoint.HostName, Connected = false, Health = HealthState.Warning });
                }
            }
        }

        /// <inheritdoc/>
        public override async Task ReportAsync(CancellationToken token)
        {
            var timeToLiveWarning = this.SetTimeToLiveWarning();

            // Report on connection state.
            foreach (var t in this.userEndpoints)
            {
                token.ThrowIfCancellationRequested();

                var deployedApps = await this.FabricClientInstance.QueryManager
                    .GetDeployedApplicationListAsync(
                        this.NodeName,
                        new Uri(t.AppTarget)).ConfigureAwait(true);

                // We only care about deployed apps.
                if (deployedApps == null || deployedApps.Count < 1)
                {
                    continue;
                }

                foreach (var t1 in this.connectionStatus)
                {
                    token.ThrowIfCancellationRequested();

                    var connStatus = t1;

                    if (!connStatus.Connected)
                    {
                        this.healthState = HealthState.Warning;
                        var healthMessage = "Outbound Internet connection failure detected for endpoint " + connStatus.HostName + "\n";

                        var report = new HealthReport
                        {
                            AppName = new Uri(t.AppTarget),
                            Code = FoErrorWarningCodes.AppWarningNetworkEndpointUnreachable,
                            EmitLogEvent = true,
                            HealthMessage = healthMessage,
                            HealthReportTimeToLive = timeToLiveWarning,
                            State = this.healthState,
                            NodeName = this.NodeName,
                            Observer = this.ObserverName,
                            ReportType = HealthReportType.Application,
                            ResourceUsageDataProperty = $"{ErrorWarningProperty.InternetConnectionFailure}: connStatus.HostName",
                        };

                        // Send health report Warning and log event locally.
                        this.HealthReporter.ReportHealthToServiceFabric(report);

                        // This means this observer created a Warning or Error SF Health Report
                        this.HasActiveFabricErrorOrWarning = true;

                        // Send Health Report as Telemetry (perhaps it signals an Alert from App Insights, for example.).
                        if (this.IsTelemetryEnabled)
                        {
                            _ = this.ObserverTelemetryClient?.ReportHealthAsync(
                                HealthScope.Application,
                                t.AppTarget,
                                HealthState.Warning,
                                $"{this.NodeName}/{FoErrorWarningCodes.AppWarningNetworkEndpointUnreachable}: {healthMessage}",
                                this.ObserverName,
                                this.Token);
                        }
                    }
                    else
                    {
                        if (connStatus.Health != HealthState.Warning)
                        {
                            continue;
                        }

                        this.healthState = HealthState.Ok;
                        var healthMessage = "Outbound Internet connection test successful.";

                        // Clear existing Health Warning.
                        var report = new HealthReport
                        {
                            AppName = new Uri(t.AppTarget),
                            EmitLogEvent = true,
                            HealthMessage = healthMessage,
                            HealthReportTimeToLive = default(TimeSpan),
                            State = this.healthState,
                            NodeName = this.NodeName,
                            Observer = this.ObserverName,
                            ReportType = HealthReportType.Application,
                        };

                        this.HealthReporter.ReportHealthToServiceFabric(report);

                        // Reset health state.
                        this.HasActiveFabricErrorOrWarning = false;
                    }
                }
            }

            // Clear
            _ = this.connectionStatus.RemoveAll(conn => conn.Connected);
            this.connectionStatus.TrimExcess();
        }
    }

    internal enum InternetProtocol
    {
        Icmp,
        Tcp,
        Udp,
    }
}