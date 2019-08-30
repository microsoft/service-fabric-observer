// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Model;
using FabricObserver.Utilities;
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

namespace FabricObserver
{
    // This observer monitors network conditions related to user-supplied configuration settings in 
    // networkobserver.json.config. This includes testing the connection state of supplied endpoint/port pairs,
    // and measuring network traffic (bytes/sec, up/down)...
    // The output (a local file) is used by the API service and the HTML frontend (https://[domain:[port]]/api/ObserverManager).
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    public class NetworkObserver : ObserverBase
    {
        private List<NetworkObserverConfig> userEndpoints = new List<NetworkObserverConfig>();
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
        private List<ConnectionState> connectionStatus = new List<ConnectionState>();
        private HealthState healthState = HealthState.Ok;
        private readonly string dataPackagePath;
        private readonly InternetProtocol protocol = InternetProtocol.TCP;
        private bool hasRun = false;
        private CancellationToken token;

        public NetworkObserver() : base(ObserverConstants.NetworkObserverName)
        {
            this.dataPackagePath = ConfigSettings.ObserversDataPackagePath;
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            if (!await Initialize().ConfigureAwait(true) || token.IsCancellationRequested)
            {
                return;
            }

            this.token = token;
            token.ThrowIfCancellationRequested();

            // This only needs to be logged once...
            if (!this.hasRun)
            {
                var logPath = Path.Combine(ObserverLogger.LogFolderBasePath, "NetInfo.txt");

                // This file is used by the web application (log reader...)...
                if (!ObserverLogger.TryWriteLogFile(logPath, GetNetworkInterfaceInfo()))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                                     ObserverName,
                                                                     HealthState.Warning,
                                                                     "Unable to create NetInfo.txt file...");
                }
            }

            // Run conn tests...
            Retry.Do(InternetConnectionStateIsConnected,
                     TimeSpan.FromSeconds(10), token, 3);

            await ReportAsync(token).ConfigureAwait(true);
            LastRunDateTime = DateTime.Now;
            this.hasRun = true;
        }

        private async Task<bool> Initialize()
        {
            WriteToLogWithLevel(ObserverName,
                                $"Initializing {ObserverName} for network monitoring..." +
                                $"| {NodeName}",
                                LogLevel.Information);

            token.ThrowIfCancellationRequested();

            // Is this a unit test run?
            if (IsTestRun)
            {
                return true;
            } 

            var settings = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(ObserverConstants.ConfigPackageName)?.Settings;

            ConfigSettings.Initialize(settings, ObserverConstants.NetworkObserverConfiguration, "NetworkObserverDataFileName");

            var NetworkObserverDataFileName = Path.Combine(this.dataPackagePath, ConfigSettings.NetworkObserverDataFileName);

            if (string.IsNullOrWhiteSpace(NetworkObserverDataFileName))
            {
                ObserverLogger.LogError("Endpoint list file is not specified. Please Add file containing endpoints that need to be monitored...");

                return false;
            }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(NetworkObserverDataFileName))
            {
                ObserverLogger.LogError("Endpoint list file is not specified. Please Add file containing endpoints that need to be monitored. Using default endpoints for connection testing...");

                return false;
            }

            token.ThrowIfCancellationRequested();

            if (this.userEndpoints.Count < 1)
            {
                using (Stream stream = new FileStream(NetworkObserverDataFileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    this.userEndpoints.AddRange(JsonHelper.ReadFromJsonStream<NetworkObserverConfig[]>(stream));
                }

                if (this.userEndpoints.Count < 1)
                {
                    HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.ToString(),
                                                                     ObserverName, HealthState.Warning,
                                                                     "Missing required configuration data: endpoints...");
                    return false;
                }
            }

            foreach (var config in this.userEndpoints)
            {
                token.ThrowIfCancellationRequested();

                var deployedApps = await FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(NodeName, new Uri(config.AppTarget)).ConfigureAwait(true);

                if (deployedApps == null || deployedApps.Count < 1)
                {
                    continue;
                }

                foreach (var endpoint in config.Endpoints)
                {
                    token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(endpoint.HostName))
                    {
                        HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.ToString(),
                                                                         ObserverName, HealthState.Warning,
                                                                         $"Initialize() | Required endpoint parameter not set. Nothing to test...");
                        continue;
                    }

                    WriteToLogWithLevel(ObserverName,
                                        $"Monitoring outbound connection state to {endpoint.HostName} " +
                                        $"on Node {NodeName} " +
                                        $"for app {config.AppTarget}",
                                        LogLevel.Information);
                }
            }

            return true;
        }

        // x-plat?
        private void InternetConnectionStateIsConnected()
        {
            var configList = defaultEndpoints;

            if (userEndpoints.Count > 0)
            {
                configList = userEndpoints;
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
                            catch (PingException) { }

                            SetHealthState(endpoint, passed);
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
                                        response.StatusCode == HttpStatusCode.Accepted) // More?
                                    {
                                        passed = true;
                                    }
                                }
                            }
                        }
                        catch (WebException) { }
                        catch (Exception e)
                        {
                            HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                                             ObserverName,
                                                                             HealthState.Warning,
                                                                             e.ToString());
                            throw;
                        }

                        SetHealthState(endpoint, passed);
                    }
                }
            }
        }

        private string GetNetworkInterfaceInfo()
        {
            var iPGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            var nics = NetworkInterface.GetAllNetworkInterfaces();

            if (nics?.Length < 1)
            {
                return null;
            }

            var interfaceInfo = new StringBuilder(string.Format("Network Interface information for {0}:\n     ",
                                                  iPGlobalProperties.HostName));

            foreach (var nic in nics)
            {
                var properties = nic.GetIPProperties();

                interfaceInfo.Append("\n" + nic.Description + "\n");
                interfaceInfo.AppendFormat("  Interface type    : {0}\n", nic.NetworkInterfaceType);
                //interfaceInfo.AppendFormat("  Physical Address  : {0}\n", nic.GetPhysicalAddress().ToString());
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

        public override async Task ReportAsync(CancellationToken token)
        {
            string app;
            var timeToLiveWarning = SetTimeToLiveWarning();

            // Report on connection state...
            for (int j = 0; j < this.userEndpoints.Count; j++)
            {
                token.ThrowIfCancellationRequested();

                var deployedApps = await FabricClientInstance.QueryManager
                                        .GetDeployedApplicationListAsync(NodeName,
                                                                         new Uri(this.userEndpoints[j].AppTarget)).ConfigureAwait(true);
                // We only care about deployed apps...
                if (deployedApps == null || deployedApps.Count < 1)
                {
                    continue;
                }

                app = this.userEndpoints[j].AppTarget.Replace("fabric:/", "");

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
                            NodeName = NodeName,
                            Observer = ObserverName,
                            ReportType = HealthReportType.Application
                        };

                        // Send health report Warning and log event locally...
                        HealthReporter.ReportHealthToServiceFabric(report);

                        // This means this observer created a Warning or Error SF Health Report 
                        HasActiveFabricErrorOrWarning = true;

                        // Send Health Report as Telemetry (perhaps it signals an Alert from App Insights, for example...)...
                        if (IsTelemetryEnabled)
                        {
                            _ = ObserverTelemetryClient?.ReportHealthAsync(this.userEndpoints[j].AppTarget,
                                                                           FabricServiceContext.ServiceName.OriginalString,
                                                                           "FabricObserver",
                                                                           ObserverName,
                                                                           $"{NodeName}/{ErrorWarningCode.WarningNetworkEndpointUnreachable}: {healthMessage}",
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
                                NodeName = NodeName,
                                Observer = ObserverName,
                                ReportType = HealthReportType.Application
                            };

                            HealthReporter.ReportHealthToServiceFabric(report);

                            // Reset health state...
                            HasActiveFabricErrorOrWarning = false;
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
        UDP
    }

    internal class ConnectionState
    {
        public string HostName { get; set; }
        public bool Connected { get; set; }
        public HealthState Health { get; set; }
    }
}