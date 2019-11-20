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

                if (ObserverManager.ObserverWebAppDeployed)
                {
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
                                            .OrderByDescending(obj => DateTime.Parse(obj["InstalledOn"]?.ToString()));

                var sb = new StringBuilder();

                foreach (var obj in resultsOrdered)
                {
                    token.ThrowIfCancellationRequested();

                    if (generateUrl)
                    {
                        sb.AppendFormat("<a href=\"{0}\" target=\"_blank\">{1}</a>   {2}", obj["Caption"], obj["HotFixID"], obj["InstalledOn"]);
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
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (ManagementException)
            {
            }
            catch (NullReferenceException)
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
            int numProcs = 0;
            string lastBootTime = string.Empty;
            string installDate = string.Empty;
            int logicalProcessorCount = Environment.ProcessorCount;
            int logicalDriveCount = 0;
            int activePorts = 0;
            int activeEphemeralPorts = 0;
            int totalVirtMem = 0;
            string windowsDynamicPortRange = string.Empty;
            string fabricAppPortRange = string.Empty;
            string hotFixes = string.Empty;
            string osLang = string.Empty;
            double freePhysicalMem = 0;
            double freeVirtualMem = 0;

            try
            {
                win32OSInfo = new ManagementObjectSearcher("SELECT Caption,Version,Status,OSLanguage,NumberOfProcesses,FreePhysicalMemory,FreeVirtualMemory,TotalVirtualMemorySize,TotalVisibleMemorySize,InstallDate,LastBootUpTime FROM Win32_OperatingSystem");
                results = win32OSInfo.Get();

                foreach (var prop in results)
                {
                    token.ThrowIfCancellationRequested();

                    foreach (var p in prop.Properties)
                    {
                        token.ThrowIfCancellationRequested();

                        string name = p.Name;
                        string value = p.Value.ToString();

                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                        {
                            continue;
                        }

                        if (name.ToLower() == "caption")
                        {
                            osName = value;
                        }
                        else if (name.ToLower() == "numberofprocesses")
                        {
                            // Number of running processes
                            _ = int.TryParse(value, out numProcs);
                        }
                        else if (name.ToLower() == "status")
                        {
                            this.osStatus = value;
                        }
                        else if (name.ToLower() == "oslanguage")
                        {
                            osLang = value;
                        }
                        else if (name.ToLower() == "version")
                        {
                            osVersion = value;
                        }
                        else if (name.ToLower().Contains("bootuptime"))
                        {
                            value = ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().ToString("o");
                            lastBootTime = value;
                        }
                        else if (name.ToLower().Contains("date"))
                        {
                            value = ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().ToString("o");
                            installDate = value;
                        }
                        else if (name.ToLower().Contains("memory"))
                        {
                            // For output...
                            int i = int.Parse(value) / 1024 / 1024;

                            // TotalVisible only needs to be set once...
                            if (name.ToLower().Contains("totalvisible"))
                            {
                                this.totalVisibleMemoryGB = i;
                            }
                            else if (name.ToLower().Contains("totalvirtual"))
                            {
                                totalVirtMem = i;
                            }
                            else if (name.ToLower().Contains("freephysical"))
                            {
                                _ = double.TryParse(value, out freePhysicalMem);
                            }
                            else if (name.ToLower().Contains("freevirtual"))
                            {
                                _ = double.TryParse(value, out freeVirtualMem);
                            }
                        }
                    }
                }

                // Active, bound ports...
                activePorts = NetworkUsage.GetActivePortCount();

                // Active, ephemeral ports...
                activeEphemeralPorts = NetworkUsage.GetActiveEphemeralPortCount();
                var dynamicPortRange = NetworkUsage.TupleGetDynamicPortRange();
                string clusterManifestXml = null;
                string osEphemeralPortRange = string.Empty;
                fabricAppPortRange = string.Empty;

                if (this.IsTestRun)
                {
                    clusterManifestXml = File.ReadAllText(this.TestManifestPath);
                }
                else
                {
                    clusterManifestXml = this.FabricClientInstance.ClusterManager.GetClusterManifestAsync().GetAwaiter().GetResult();
                }

                var appPortRange = NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(this.FabricServiceContext.NodeContext.NodeType, clusterManifestXml);
                int firewalls = NetworkUsage.GetActiveFirewallRulesCount();

                // OS info...
                sb.AppendLine("OS Information:\r\n");
                sb.AppendLine($"Name: {osName}");
                sb.AppendLine($"Version: {osVersion}");
                sb.AppendLine($"InstallDate: {installDate}");
                sb.AppendLine($"LastBootUpTime*: {lastBootTime}");
                sb.AppendLine($"OSLanguage: {osLang}");
                sb.AppendLine($"OSHealthStatus*: {this.osStatus}");
                sb.AppendLine($"NumberOfProcesses*: {numProcs}");

                if (dynamicPortRange.Item1 > -1)
                {
                    osEphemeralPortRange = $"{dynamicPortRange.Item1} - {dynamicPortRange.Item2}";
                    sb.AppendLine($"WindowsEphemeralTCPPortRange: {osEphemeralPortRange} (Active*: {activeEphemeralPorts})");
                }

                if (appPortRange.Item1 > -1)
                {
                    fabricAppPortRange = $"{appPortRange.Item1} - {appPortRange.Item2}";
                    sb.AppendLine($"FabricApplicationTCPPortRange: {fabricAppPortRange}");
                }

                if (firewalls > -1)
                {
                    sb.AppendLine($"ActiveFirewallRules*: {firewalls}");
                }

                if (activePorts > -1)
                {
                    sb.AppendLine($"TotalActiveTCPPorts*: {activePorts}");
                }

                // Hardware info...
                // Proc/Mem
                sb.AppendLine("\r\nHardware Information:\r\n");
                sb.AppendLine($"LogicalProcessorCount: {logicalProcessorCount}");
                sb.AppendLine($"TotalVirtualMemorySize: {totalVirtMem} GB");
                sb.AppendLine($"TotalVisibleMemorySize: {this.totalVisibleMemoryGB} GB");
                sb.AppendLine($"FreePhysicalMemory*: {Math.Round(freePhysicalMem / 1024 / 1024, 2)} GB");
                sb.AppendLine($"FreeVirtualMemory*: {Math.Round(freeVirtualMem / 1024 / 1024, 2)} GB");

                // Disk
                var drivesInformation = diskUsage.GetCurrentDiskSpaceTotalAndUsedPercentAllDrives(SizeUnit.Gigabytes);
                sb.AppendLine($"LogicalDriveCount: {drivesInformation.Count}");

                foreach (var tuple in drivesInformation)
                {
                    string systemDrv = "Data";

                    if (Environment.SystemDirectory.Substring(0, 1) == tuple.Item1)
                    {
                        systemDrv = "System";
                    }

                    sb.AppendLine($"Drive {tuple.Item1} ({systemDrv}) Size: {tuple.Item2} GB");
                    sb.AppendLine($"Drive {tuple.Item1} ({systemDrv}) Consumed*: {tuple.Item3}%");
                }

                string osHotFixes = GetWindowsHotFixes(token);

                if (!string.IsNullOrEmpty(osHotFixes))
                {
                    sb.AppendLine($"\nWindows Patches/Hot Fixes*:\n\n{osHotFixes}");
                }

                // Dynamic info qualifier (*)
                sb.AppendLine($"\n* Dynamic data.");

                this.osReport = sb.ToString();

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
                            LogicalProcessorCount = logicalProcessorCount,
                            LogicalDriveCount = logicalDriveCount,
                            NumberOfRunningProcesses = numProcs,
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
