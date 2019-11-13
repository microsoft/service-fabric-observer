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
    // under node details in SFX as well as emit ETW events. It is best to *not* disable this observer...
    // The output (a local file) is used by the API service and returns HTML output (http://localhost:5000/api/ObserverManager).
    public class OSObserver : ObserverBase
    {
        private string osReport;
        private string osStatus;
        private int totalVisibleMemoryGB = -1;

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
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example...
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            this.GetComputerInfo(token);
            await this.ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
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

                var osReport = new Utilities.HealthReport
                {
                    Observer = this.ObserverName,
                    HealthMessage = this.osReport,
                    State = HealthState.Ok,
                    NodeName = this.NodeName,
                    HealthReportTimeToLive = this.SetTimeToLiveWarning(),
                };

                this.HealthReporter.ReportHealthToServiceFabric(osReport);

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

        private static string GetWindowsHotFixes(CancellationToken token, bool generateUrl = true)
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

                    if (generateUrl)
                    {
                        sb.AppendFormat("<a href=\"{0}\" target=\"_blank\">{1}</a>", obj["Caption"], obj["HotFixID"]);
                    }
                    else
                    {
                        sb.AppendFormat("{0}", obj["HotFixID"]);
                    }

                    sb.AppendLine();
                }

                ret = sb.ToString().Trim();
                sb.Clear();
            }
            catch (ArgumentException)
            {
            }
            catch (ManagementException)
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

        private void GetComputerInfo(CancellationToken token)
        {
            ManagementObjectSearcher win32OSInfo = null;
            ManagementObjectCollection results = null;

            var sb = new StringBuilder();
            var diskUsage = new DiskUsage();

            string osName = string.Empty;
            string osVersion = string.Empty;
            string numProcs = "-1";
            string lastBootTime = string.Empty;
            string installDate = string.Empty;
            int driveCount = 0;

            try
            {
                win32OSInfo = new ManagementObjectSearcher("SELECT Caption,Version,Status,OSLanguage,NumberOfProcesses,FreePhysicalMemory,FreeVirtualMemory,TotalVirtualMemorySize,TotalVisibleMemorySize,InstallDate,LastBootUpTime FROM Win32_OperatingSystem");
                results = win32OSInfo.Get();
                sb.AppendLine($"\nOS Info:\n");

                foreach (var prop in results)
                {
                    token.ThrowIfCancellationRequested();

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
                            osName = v;
                        }

                        if (n.ToLower() == "numberofprocesses")
                        {
                            // Number of running processes
                            numProcs = v;

                            // Also show number of processors on machine (logical cores)...
                            sb.AppendLine($"LogicalProcessorCount: {Environment.ProcessorCount}");
                        }

                        if (n.ToLower() == "status")
                        {
                            this.osStatus = v;
                        }

                        if (n.ToLower() == "version")
                        {
                            osVersion = v;
                        }

                        if (n.ToLower().Contains("bootuptime"))
                        {
                            v = ManagementDateTimeConverter.ToDateTime(v).ToUniversalTime().ToString("o");
                            lastBootTime = v;
                        }

                        if (n.ToLower().Contains("date"))
                        {
                            v = ManagementDateTimeConverter.ToDateTime(v).ToUniversalTime().ToString("o");
                            installDate = v;
                        }

                        if (n.ToLower().Contains("memory"))
                        {
                            if (n.ToLower().Contains("freephysical") ||
                                n.ToLower().Contains("freevirtual"))
                            {
                                continue;
                            }

                            // For output...
                            int i = int.Parse(v) / 1024 / 1024;
                            v = i.ToString() + " GB";

                            // TotalVisible only needs to be set once...
                            if (n.ToLower().Contains("totalvisible"))
                            {
                                this.totalVisibleMemoryGB = i;
                            }
                        }

                        sb.AppendLine($"{n}: {v}");
                    }
                }

                try
                {
                    // We only care about ready drives (so, things like an empty DVD drive are not interesting...)
                    driveCount = DriveInfo.GetDrives().Where(d => d.IsReady).Count();
                    sb.AppendLine($"LogicalDriveCount: {driveCount}");
                }
                catch (ArgumentException)
                {
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                this.osReport = sb.ToString();

                // Active, bound ports...
                int activePorts = NetworkUsage.GetActivePortCount();

                // Active, ephemeral ports...
                int activeEphemeralPorts = NetworkUsage.GetActiveEphemeralPortCount();
                var dynamicPortRange = NetworkUsage.TupleGetDynamicPortRange();
                string clusterManifestXml = null;
                string osEphemeralPortRange = string.Empty;
                string fabricAppPortRange = string.Empty;

                if (this.IsTestRun)
                {
                    clusterManifestXml = File.ReadAllText(this.TestManifestPath);
                }
                else
                {
                    clusterManifestXml = this.FabricClientInstance.ClusterManager.GetClusterManifestAsync().GetAwaiter().GetResult();
                }

                var appPortRange = NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(this.FabricServiceContext.NodeContext.NodeType, clusterManifestXml);

                // Enabled Firewall rules...
                int firewalls = NetworkUsage.GetActiveFirewallRulesCount();

                if (firewalls > -1)
                {
                    this.osReport += $"EnabledFireWallRules: {firewalls}\r\n";
                }

                if (activePorts > -1)
                {
                    this.osReport += $"TotalActiveTCPPorts: {activePorts}\r\n";
                }

                if (dynamicPortRange.Item1 > -1)
                {
                    osEphemeralPortRange = $"{dynamicPortRange.Item1} - {dynamicPortRange.Item2}";
                    this.osReport += $"WindowsEphemeralTCPPortRange: {osEphemeralPortRange}\r\n";
                }

                if (appPortRange.Item1 > -1)
                {
                    fabricAppPortRange = $"{appPortRange.Item1} - {appPortRange.Item2}";
                    this.osReport += $"FabricApplicationTCPPortRange: {fabricAppPortRange}\r\n";
                }

                if (activeEphemeralPorts > -1)
                {
                    this.osReport += $"ActiveEphemeralTCPPorts: {activeEphemeralPorts}\r\n";
                }

                string osHotFixes = GetWindowsHotFixes(token);

                if (!string.IsNullOrEmpty(osHotFixes))
                {
                    this.osReport += $"\nOS Patches/Hot Fixes:\n\n{osHotFixes}\r\n";
                }

                // ETW...
                if (this.IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        $"FabricObserverDataEvent",
                        new
                        {
                            Level = 0, // Info
                            Node = this.NodeName,
                            Observer = this.ObserverName,
                            OS = osName,
                            OSVersion = osVersion,
                            OSInstallDate = installDate,
                            LastBootUpTime = lastBootTime,
                            TotalMemorySizeGB = this.totalVisibleMemoryGB,
                            LogicalProcessorCount = Environment.ProcessorCount,
                            LogicalDriveCount = driveCount,
                            NumberOfRunningProcesses = int.Parse(numProcs),
                            ActiveFirewallRules = firewalls,
                            ActivePorts = activePorts,
                            ActiveEphemeralPorts = activeEphemeralPorts,
                            WindowsDynamicPortRange = osEphemeralPortRange,
                            FabricAppPortRange = fabricAppPortRange,
                            HotFixes = GetWindowsHotFixes(token, false).Replace("\r\n", ", ").TrimEnd(','),
                        });
                }
            }
            catch (ManagementException)
            {
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
                diskUsage?.Dispose();
                sb.Clear();
            }
        }
    }
}
