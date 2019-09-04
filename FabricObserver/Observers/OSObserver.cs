// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Utilities;

namespace FabricObserver
{
    // This observer monitors OS health state and provides static and dynamic OS level information.
    // This observer is not configurable. It will signal infinte TTL Ok Health Reports that will show up
    // under node details in SFX.
    // The output (a local file) is used by the API service and returns HTML output (http://localhost:5000/api/ObserverManager).
    public class OSObserver : ObserverBase
    {
        private string osReport;
        private string osStatus;

        public static int PercentTotalMemoryInUseOnVM { get; private set; } = -1;

        public string TestManifestPath { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OSObserver"/> class.
        /// </summary>
        public OSObserver()
            : base(ObserverConstants.OSObserverName)
        {
        }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            this.GetComputerInfo(token);
            await this.ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
        }

        private void GetComputerInfo(CancellationToken token)
        {
            ManagementObjectSearcher win32OSInfo = null;
            ManagementObjectCollection results = null;

            var sb = new StringBuilder();

            try
            {
                win32OSInfo = new ManagementObjectSearcher("SELECT Caption,Version,Status,OSLanguage,NumberOfProcesses,FreePhysicalMemory,FreeVirtualMemory,TotalVirtualMemorySize,TotalVisibleMemorySize,InstallDate,LastBootUpTime FROM Win32_OperatingSystem");
                results = win32OSInfo.Get();
                sb.AppendLine($"\nOS Info:\n");

                foreach (var prop in results)
                {
                    token.ThrowIfCancellationRequested();
                    long visibleTotal = -1;
                    long freePhysical = -1;

                    foreach (var p in prop.Properties)
                    {
                        token.ThrowIfCancellationRequested();

                        string n = p.Name;
                        string v = p.Value.ToString();

                        if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(v))
                        {
                            continue;
                        }

                        if (n.ToLower() == "caption")
                        {
                            n = "OS";
                        }

                        if (n.ToLower() == "status")
                        {
                            this.osStatus = v;
                        }

                        if (n.ToLower().Contains("date") || n.ToLower().Contains("bootuptime"))
                        {
                            v = ManagementDateTimeConverter.ToDateTime(v).ToString();
                        }

                        if (n.ToLower().Contains("memory"))
                        {
                            // For output...
                            int i = int.Parse(v) / 1024 / 1024;
                            v = i.ToString() + " GB";

                            // For use by any other observer that needs to know percent of RAM in use on node...
                            if (n.ToLower().Contains("totalvisible"))
                            {
                                visibleTotal = i;
                            }

                            if (n.ToLower().Contains("freephysical"))
                            {
                                freePhysical = i;
                            }
                        }

                        sb.AppendLine($"{n}: {v}");
                    }

                    // Calculate percent RAM available...
                    if (visibleTotal > -1 && freePhysical > -1)
                    {
                        double usedPct = ((double)(visibleTotal - freePhysical)) / visibleTotal;
                        PercentTotalMemoryInUseOnVM = (int)(usedPct * 100);
                        sb.AppendLine($"Percent RAM in use: {PercentTotalMemoryInUseOnVM}%");
                    }
                }

                this.osReport = sb.ToString();
                sb.Clear();

                // Active, bound ports...
                int activePorts = NetworkUsage.GetActivePortCount();

                // Active, ephemeral ports...
                int activeEphemeralPorts = NetworkUsage.GetActiveEphemeralPortCount();
                Tuple<int, int> dynamicPortRange = NetworkUsage.TupleGetDynamicPortRange();
                string clusterManifestXml = null;
                if (this.IsTestRun)
                {
                    clusterManifestXml = File.ReadAllText(this.TestManifestPath);
                }
                else
                {
                    clusterManifestXml = this.FabricClientInstance.ClusterManager.GetClusterManifestAsync().GetAwaiter().GetResult();
                }

                Tuple<int, int> appPortRange = NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(this.FabricServiceContext.NodeContext.NodeType, clusterManifestXml);

                // Enabled Firewall rules...
                int firewalls = NetworkUsage.GetActiveFirewallRulesCount();

                if (firewalls > -1)
                {
                    this.osReport += $"Total number of enabled Firewall rules: {firewalls}\n";
                }

                if (activePorts > -1)
                {
                    this.osReport += $"Total number of active TCP ports: {activePorts}\n";
                }

                if (dynamicPortRange.Item1 > -1)
                {
                    this.osReport += $"Windows ephemeral TCP port range: {dynamicPortRange.Item1} - {dynamicPortRange.Item2}\n";
                }

                if (appPortRange.Item1 > -1)
                {
                    this.osReport += $"Fabric Application TCP port range: {appPortRange.Item1} - {appPortRange.Item2}\n";
                }

                if (activeEphemeralPorts > -1)
                {
                    this.osReport += $"Total number of active ephemeral TCP ports: {activeEphemeralPorts}\n";
                }

                string osHotFixes = GetHotFixes(token);

                if (!string.IsNullOrEmpty(osHotFixes))
                {
                    this.osReport += $"\nOS Patches/Hot Fixes:\n\n{osHotFixes}\n";
                }
            }
            catch (Exception e)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Error,
                    $"Unhandled exception processing OS information: {e.Message}: \n {e.StackTrace}");
                throw;
            }
            finally
            {
                results?.Dispose();
                win32OSInfo?.Dispose();
            }
        }

        public static string GetHotFixes(CancellationToken token)
        {
            ManagementObjectSearcher searcher = null;
            ManagementObjectCollection results = null;
            string ret = string.Empty;
            token.ThrowIfCancellationRequested();

            try
            {
                searcher = new ManagementObjectSearcher("SELECT * FROM Win32_QuickFixEngineering");
                results = searcher.Get();

                if (results.Count < 1)
                {
                    return string.Empty;
                }

                var resultsOrdered = results.Cast<ManagementObject>()
                                            .OrderByDescending(obj => obj["InstalledOn"]);

                var sb = new StringBuilder();

                foreach (var obj in resultsOrdered)
                {
                    token.ThrowIfCancellationRequested();

                    sb.AppendFormat("{0}  {1}", obj["HotFixID"], obj["InstalledOn"]);
                    sb.AppendLine();
                }

                ret = sb.ToString().Trim();
                sb.Clear();
            }
            catch (ArgumentException)
            {
            }
            finally
            {
                results?.Dispose();
                results = null;
                searcher?.Dispose();
                searcher = null;
            }

            return ret;
        }

        /// <inheritdoc/>
        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                // OS Health...
                if (this.osStatus != null &&
                    this.osStatus.ToUpper() != "OK")
                {
                    string healthMessage = $"OS reporting unhealthy: {this.osStatus}";
                    var healthReport = new Utilities.HealthReport
                    {
                        Observer = this.ObserverName,
                        NodeName = this.NodeName,
                        HealthMessage = healthMessage,
                        State = HealthState.Error,
                        HealthReportTimeToLive = this.SetTimeToLiveWarning(),
                    };

                    this.HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // This means this observer created a Warning or Error SF Health Report
                    this.HasActiveFabricErrorOrWarning = true;

                    // Send Health Report as Telemetry (perhaps it signals an Alert from App Insights, for example...)...
                    if (this.IsTelemetryEnabled)
                    {
                        _ = this.ObserverTelemetryClient?.ReportHealthAsync(
                            FabricRuntime.GetActivationContext().ApplicationName,
                            this.FabricServiceContext.ServiceName.OriginalString,
                            "FabricObserver",
                            this.ObserverName,
                            $"{this.NodeName}/OS reporting unhealthy: {this.osStatus}",
                            HealthState.Error,
                            token);
                    }
                }
                else if (this.HasActiveFabricErrorOrWarning &&
                         this.osStatus != null &&
                         this.osStatus.ToUpper() == "OK")
                {
                    // Clear Error or Warning with an OK Health Report...
                    string healthMessage = $"OS reporting healthy: {this.osStatus}";
                    var healthReport = new Utilities.HealthReport
                    {
                        Observer = this.ObserverName,
                        NodeName = this.NodeName,
                        HealthMessage = healthMessage,
                        State = HealthState.Ok,
                        HealthReportTimeToLive = default(TimeSpan),
                    };

                    this.HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // Reset internal health state...
                    this.HasActiveFabricErrorOrWarning = false;
                }

                var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "SysInfo.txt");

                // This file is used by the web application (log reader...)...
                if (!this.ObserverLogger.TryWriteLogFile(logPath, $"Last updated on {DateTime.UtcNow.ToString("M/d/yyyy HH:mm:ss")} UTC<br/>{this.osReport}"))
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.OriginalString,
                        this.ObserverName,
                        HealthState.Warning,
                        "Unable to create SysInfo.txt file...");
                }

                var persistentReport = new Utilities.HealthReport
                {
                    Observer = this.ObserverName,
                    HealthMessage = this.osReport,
                    State = HealthState.Ok,
                    NodeName = this.NodeName,
                    HealthReportTimeToLive = TimeSpan.MaxValue,
                };

                this.HealthReporter.ReportHealthToServiceFabric(persistentReport);

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Error,
                    $"Unhandled exception processing OS information: {e.Message}: \n {e.StackTrace}");
                throw;
            }
        }
    }
}
