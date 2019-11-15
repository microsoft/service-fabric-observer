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
using FabricObserver.Model;
using FabricObserver.Utilities;

namespace FabricObserver
{
    /// <summary>
    /// This observer monitors network conditions related to user-supplied configuration settings in
    /// networkobserver.json.config. This includes testing the connection state of supplied endpoint/port pairs,
    /// and measuring network traffic (bytes/sec, up/down)...
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
        private readonly InternetProtocol protocol = InternetProtocol.TCP;
        private List<NetworkObserverConfig> userEndpoints = new List<NetworkObserverConfig>();
        private List<ConnectionState> connectionStatus = new List<ConnectionState>();
        private HealthState healthState = HealthState.Ok;
        private bool hasRun = false;
        private CancellationToken token;

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
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example...
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            if (!await this.Initialize().ConfigureAwait(true) || token.IsCancellationRequested)
            {
                return;
            }

            this.token = token;
            token.ThrowIfCancellationRequested();

            // Run conn tests...
            Retry.Do(
                this.InternetConnectionStateIsConnected,
                TimeSpan.FromSeconds(10),
                token,
                3);

            await this.ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
            this.hasRun = true;
        }

        private async Task<bool> Initialize()
        {
            this.WriteToLogWithLevel(
                this.ObserverName,
                $"Initializing {this.ObserverName} for network monitoring... | {this.NodeName}",
                LogLevel.Information);

            this.token.ThrowIfCancellationRequested();

            // This only needs to be logged once...
            // This file is used by the ObserverWebApi application.
            if (ObserverManager.ObserverWebAppDeployed && !this.hasRun)
            {
                var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "NetInfo.txt");

                if (!this.ObserverLogger.TryWriteLogFile(logPath, this.GetNetworkInterfaceInfo()))
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.OriginalString,
                        this.ObserverName,
                        HealthState.Warning,
                        "Unable to create NetInfo.txt file...");
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
                this.ObserverLogger.LogError("Endpoint list file is not specified. Please Add file containing endpoints that need to be monitored...");

                return false;
            }

            this.token.ThrowIfCancellationRequested();
            if (!File.Exists(networkObserverConfigFileName))
            {
                this.ObserverLogger.LogError("Endpoint list file is not specified. Please Add file containing endpoints that need to be monitored. Using default endpoints for connection testing...");

                return false;
            }

            this.token.ThrowIfCancellationRequested();

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
                        "Missing required configuration data: endpoints...");
                    return false;
                }
            }

            int configCount = 0;

            foreach (var config in this.userEndpoints)
            {
                this.token.ThrowIfCancellationRequested();

                var deployedApps = await this.FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(this.NodeName, new Uri(config.AppTarget)).ConfigureAwait(true);

                if (deployedApps == null || deployedApps.Count < 1)
                {
                    continue;
                }

                configCount++;

                foreach (var endpoint in config.Endpoints)
                {
                    this.token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(endpoint.HostName))
                    {
                        this.HealthReporter.ReportFabricObserverServiceHealth(
                            this.FabricServiceContext.ServiceName.ToString(),
                            this.ObserverName,
                            HealthState.Warning,
                            $"Initialize() | Required endpoint parameter not set. Nothing to test...");
                        continue;
                    }

                    this.WriteToLogWithLevel(
                        this.ObserverName,
                        $"Monitoring outbound connection state to {endpoint.HostName} on Node {this.NodeName} for app {config.AppTarget}",
                        LogLevel.Information);
                }
            }

            // This observer shouldn't run if there are no app-specific endpoint/port pairs provided...
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

            if (this.protocol == InternetProtocol.ICMP)
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
                                if (reply.Status == IPStatus.Success)
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
                }
            }
        }

        private string GetNetworkInterfaceInfo()
        {
            try
            {
                var iPGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
                var nics = NetworkInterface.GetAllNetworkInterfaces();

                if (nics == null || nics.Length < 1)
                {
                    return string.Empty;
                }

                var interfaceInfo = new StringBuilder(string.Format(
                    "Network Interface information for {0}:\n     ",
                    iPGlobalProperties.HostName));

                foreach (var nic in nics)
                {
                    var properties = nic.GetIPProperties();

                    interfaceInfo.Append("\n" + nic.Description + "\n");
                    interfaceInfo.AppendFormat("  Interface type    : {0}\n", nic.NetworkInterfaceType);
                    interfaceInfo.AppendFormat("  Operational status: {0}\n", nic.OperationalStatus);

                    // Traffic...
                    if (nic.OperationalStatus == OperationalStatus.Up)
                    {
                        interfaceInfo.AppendLine("  Traffic Info:");

                        var stats = nic.GetIPv4Statistics();

                        interfaceInfo.AppendFormat("    Bytes received: {0}\n", stats.BytesReceived);
                        interfaceInfo.AppendFormat("    Bytes sent: {0}\n", stats.BytesSent);
                        interfaceInfo.AppendFormat("    Incoming Packets With Errors: {0}\n", stats.IncomingPacketsWithErrors);
                        interfaceInfo.AppendFormat("    Outgoing Packets With Errors: {0}\n", stats.OutgoingPacketsWithErrors);
                        interfaceInfo.AppendLine();
                    }
                }

                var s = interfaceInfo.ToString();
                interfaceInfo.Clear();

                return s;
            }
            catch (NetworkInformationException)
            {
                return string.Empty;
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
                    this.connectionStatus.RemoveAll(conn => conn.HostName == endpoint.HostName);
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
            string app;
            var timeToLiveWarning = this.SetTimeToLiveWarning();

            // Report on connection state...
            for (int j = 0; j < this.userEndpoints.Count; j++)
            {
                token.ThrowIfCancellationRequested();

                var deployedApps = await this.FabricClientInstance.QueryManager
                                        .GetDeployedApplicationListAsync(
                                            this.NodeName,
                                            new Uri(this.userEndpoints[j].AppTarget)).ConfigureAwait(true);

                // We only care about deployed apps...
                if (deployedApps == null || deployedApps.Count < 1)
                {
                    continue;
                }

                app = this.userEndpoints[j].AppTarget.Replace("fabric:/", string.Empty);

                for (int i = 0; i < this.connectionStatus.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var connStatus = this.connectionStatus[i];

                    if (!connStatus.Connected)
                    {
                        this.healthState = HealthState.Warning;
                        var healthMessage = "Outbound Internet connection failure detected for endpoint " + connStatus.HostName + "\n";

                        Utilities.HealthReport report = new Utilities.HealthReport
                        {
                            AppName = new Uri(this.userEndpoints[j].AppTarget),
                            Code = ErrorWarningCode.WarningNetworkEndpointUnreachable,
                            EmitLogEvent = true,
                            HealthMessage = healthMessage,
                            HealthReportTimeToLive = timeToLiveWarning,
                            State = this.healthState,
                            NodeName = this.NodeName,
                            Observer = this.ObserverName,
                            ReportType = HealthReportType.Application,
                        };

                        // Send health report Warning and log event locally...
                        this.HealthReporter.ReportHealthToServiceFabric(report);

                        // This means this observer created a Warning or Error SF Health Report
                        this.HasActiveFabricErrorOrWarning = true;

                        // Send Health Report as Telemetry (perhaps it signals an Alert from App Insights, for example...)...
                        if (this.IsTelemetryEnabled)
                        {
                            _ = this.ObserverTelemetryClient?.ReportHealthAsync(
                                this.userEndpoints[j].AppTarget,
                                this.FabricServiceContext.ServiceName.OriginalString,
                                "FabricObserver",
                                this.ObserverName,
                                $"{this.NodeName}/{ErrorWarningCode.WarningNetworkEndpointUnreachable}: {healthMessage}",
                                HealthState.Warning,
                                token);
                        }
                    }
                    else
                    {
                        if (connStatus.Health == HealthState.Warning)
                        {
                            this.healthState = HealthState.Ok;
                            var healthMessage = "Outbound Internet connection test successful.";

                            // Clear existing Health Warning...
                            Utilities.HealthReport report = new Utilities.HealthReport
                            {
                                AppName = new Uri(this.userEndpoints[j].AppTarget),
                                EmitLogEvent = true,
                                HealthMessage = healthMessage,
                                HealthReportTimeToLive = default(TimeSpan),
                                State = this.healthState,
                                NodeName = this.NodeName,
                                Observer = this.ObserverName,
                                ReportType = HealthReportType.Application,
                            };

                            this.HealthReporter.ReportHealthToServiceFabric(report);

                            // Reset health state...
                            this.HasActiveFabricErrorOrWarning = false;
                        }
                    }
                }
            }

            // Clear
            this.connectionStatus.RemoveAll(conn => conn.Connected == true);
            this.connectionStatus.TrimExcess();
        }
    }

    internal enum InternetProtocol
    {
        ICMP,
        TCP,
        UDP,
    }

    internal class ConnectionState
    {
        public string HostName { get; set; }

        public bool Connected { get; set; }

        public HealthState Health { get; set; }
    }
}