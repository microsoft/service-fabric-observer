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
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    // This observer monitors OS health state and provides static and dynamic OS level information.
    // This observer is not configurable. It will signal infinite TTL Ok Health Reports that will show up
    // under node details in SFX as well as emit ETW events.
    // If FabricObserverWebApi is installed, the output includes a local file that is used
    // by the API service and returns Hardware/OS info as HTML (http://localhost:5000/api/ObserverManager).
    public class OsObserver : ObserverBase
    {
        private string osReport;
        private string osStatus;
        private int totalVisibleMemoryGb = -1;

        public string TestManifestPath { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OsObserver"/> class.
        /// </summary>
        public OsObserver()
            : base(ObserverConstants.OsObserverName)
        {
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

                // OS Health.
                if (this.osStatus != null &&
                    this.osStatus.ToUpper() != "OK")
                {
                    string healthMessage = $"OS reporting unhealthy: {this.osStatus}";
                    var healthReport = new HealthReport
                    {
                        Observer = this.ObserverName,
                        NodeName = this.NodeName,
                        HealthMessage = healthMessage,
                        State = HealthState.Error,
                        HealthReportTimeToLive = this.SetHealthReportTimeToLive(),
                    };

                    this.HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // This means this observer created a Warning or Error SF Health Report
                    this.HasActiveFabricErrorOrWarning = true;

                    // Send Health Report as Telemetry (perhaps it signals an Alert from App Insights, for example.).
                    if (this.IsTelemetryProviderEnabled)
                    {
                        _ = this.TelemetryClient?.ReportHealthAsync(
                            HealthScope.Application,
                            FabricRuntime.GetActivationContext().ApplicationName,
                            HealthState.Error,
                            $"{this.NodeName} - OS reporting unhealthy: {this.osStatus}",
                            this.ObserverName,
                            this.Token);
                    }
                }
                else if (this.HasActiveFabricErrorOrWarning &&
                         this.osStatus != null &&
                         this.osStatus.ToUpper() == "OK")
                {
                    // Clear Error or Warning with an OK Health Report.
                    string healthMessage = $"OS reporting healthy: {this.osStatus}";
                    var healthReport = new HealthReport
                    {
                        Observer = this.ObserverName,
                        NodeName = this.NodeName,
                        HealthMessage = healthMessage,
                        State = HealthState.Ok,
                        HealthReportTimeToLive = default(TimeSpan),
                    };

                    this.HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // Reset internal health state.
                    this.HasActiveFabricErrorOrWarning = false;
                }

                if (ObserverManager.ObserverWebAppDeployed)
                {
                    var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "SysInfo.txt");

                    // This file is used by the web application (log reader.).
                    if (!this.ObserverLogger.TryWriteLogFile(logPath, $"Last updated on {DateTime.UtcNow.ToString("M/d/yyyy HH:mm:ss")} UTC<br/>{this.osReport}"))
                    {
                        this.HealthReporter.ReportFabricObserverServiceHealth(
                            this.FabricServiceContext.ServiceName.OriginalString,
                            this.ObserverName,
                            HealthState.Warning,
                            "Unable to create SysInfo.txt file.");
                    }
                }

                var report = new HealthReport
                {
                    Observer = this.ObserverName,
                    HealthMessage = this.osReport,
                    State = HealthState.Ok,
                    NodeName = this.NodeName,
                    HealthReportTimeToLive = this.SetHealthReportTimeToLive(),
                };

                this.HealthReporter.ReportHealthToServiceFabric(report);

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

                    _ = generateUrl ? sb.AppendFormat(
                        "<a href=\"{0}\" target=\"_blank\">{1}</a>   {2}",
                        obj["Caption"],
                        obj["HotFixID"],
                        obj["InstalledOn"]) : sb.AppendFormat("{0}", obj["HotFixID"]);

                    _ = sb.AppendLine();
                }

                ret = sb.ToString().Trim();
                _ = sb.Clear();
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
                searcher?.Dispose();
            }

            return ret;
        }

        private void GetComputerInfo(CancellationToken token)
        {
            ManagementObjectSearcher win32OsInfo = null;
            ManagementObjectCollection results = null;

            var sb = new StringBuilder();
            var diskUsage = new DiskUsage();

            string osName = string.Empty;
            string osVersion = string.Empty;
            int numProcs = 0;
            string lastBootTime = string.Empty;
            string installDate = string.Empty;
            int logicalProcessorCount = Environment.ProcessorCount;
            int totalVirtualMem = 0;
            string osLang = string.Empty;
            double freePhysicalMem = 0;
            double freeVirtualMem = 0;

            try
            {
                win32OsInfo = new ManagementObjectSearcher("SELECT Caption,Version,Status,OSLanguage,NumberOfProcesses,FreePhysicalMemory,FreeVirtualMemory,TotalVirtualMemorySize,TotalVisibleMemorySize,InstallDate,LastBootUpTime FROM Win32_OperatingSystem");
                results = win32OsInfo.Get();

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

                        switch (name.ToLower())
                        {
                            case "caption":
                                osName = value;
                                break;
                            case "numberofprocesses":
                                // Number of running processes
                                _ = int.TryParse(value, out numProcs);
                                break;
                            case "status":
                                this.osStatus = value;
                                break;
                            case "oslanguage":
                                osLang = value;
                                break;
                            case "version":
                                osVersion = value;
                                break;
                            default:
                            {
                                if (name.ToLower().Contains("bootuptime"))
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
                                    // For output.
                                    int i = int.Parse(value) / 1024 / 1024;

                                    // TotalVisible only needs to be set once.
                                    if (name.ToLower().Contains("totalvisible"))
                                    {
                                        this.totalVisibleMemoryGb = i;
                                    }
                                    else if (name.ToLower().Contains("totalvirtual"))
                                    {
                                        totalVirtualMem = i;
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

                                break;
                            }
                        }
                    }
                }

                // Active, bound ports.
                var activePorts = NetworkUsage.GetActivePortCount();

                // Active, ephemeral ports.
                var activeEphemeralPorts = NetworkUsage.GetActiveEphemeralPortCount();
                var (lowPortOs, highPortOs) = NetworkUsage.TupleGetDynamicPortRange();
                string osEphemeralPortRange = string.Empty;
                var fabricAppPortRange = string.Empty;

                var clusterManifestXml = this.IsTestRun ? File.ReadAllText(this.TestManifestPath) : this.FabricClientInstance.ClusterManager.GetClusterManifestAsync(this.AsyncClusterOperationTimeoutSeconds, this.Token).GetAwaiter().GetResult();

                var (lowPortApp, highPortApp) = NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(this.FabricServiceContext.NodeContext.NodeType, clusterManifestXml);
                int firewalls = NetworkUsage.GetActiveFirewallRulesCount();

                // OS info.
                _ = sb.AppendLine("OS Information:\r\n");
                _ = sb.AppendLine($"Name: {osName}");
                _ = sb.AppendLine($"Version: {osVersion}");
                _ = sb.AppendLine($"InstallDate: {installDate}");
                _ = sb.AppendLine($"LastBootUpTime*: {lastBootTime}");
                _ = sb.AppendLine($"OSLanguage: {osLang}");
                _ = sb.AppendLine($"OSHealthStatus*: {this.osStatus}");
                _ = sb.AppendLine($"NumberOfProcesses*: {numProcs}");

                if (lowPortOs > -1)
                {
                    osEphemeralPortRange = $"{lowPortOs} - {highPortOs}";
                    _ = sb.AppendLine($"WindowsEphemeralTCPPortRange: {osEphemeralPortRange} (Active*: {activeEphemeralPorts})");
                }

                if (lowPortApp > -1)
                {
                    fabricAppPortRange = $"{lowPortApp} - {highPortApp}";
                    _ = sb.AppendLine($"FabricApplicationTCPPortRange: {fabricAppPortRange}");
                }

                if (firewalls > -1)
                {
                    _ = sb.AppendLine($"ActiveFirewallRules*: {firewalls}");
                }

                if (activePorts > -1)
                {
                    _ = sb.AppendLine($"TotalActiveTCPPorts*: {activePorts}");
                }

                // Hardware info.
                // Proc/Mem
                _ = sb.AppendLine("\r\nHardware Information:\r\n");
                _ = sb.AppendLine($"LogicalProcessorCount: {logicalProcessorCount}");
                _ = sb.AppendLine($"TotalVirtualMemorySize: {totalVirtualMem} GB");
                _ = sb.AppendLine($"TotalVisibleMemorySize: {this.totalVisibleMemoryGb} GB");
                _ = sb.AppendLine($"FreePhysicalMemory*: {Math.Round(freePhysicalMem / 1024 / 1024, 2)} GB");
                _ = sb.AppendLine($"FreeVirtualMemory*: {Math.Round(freeVirtualMem / 1024 / 1024, 2)} GB");

                // Disk
                var drivesInformationTuple = diskUsage.GetCurrentDiskSpaceTotalAndUsedPercentAllDrives(SizeUnit.Gigabytes);
                var logicalDriveCount = drivesInformationTuple.Count;
                string driveInfo = string.Empty;

                _ = sb.AppendLine($"LogicalDriveCount: {logicalDriveCount}");

                foreach (var (driveName, diskSize, percentConsumed) in drivesInformationTuple)
                {
                    string systemDrv = "Data";

                    if (Environment.SystemDirectory.Substring(0, 1) == driveName)
                    {
                        systemDrv = "System";
                    }

                    string drvSize = $"Drive {driveName} ({systemDrv}) Size: {diskSize} GB";
                    string drvConsumed = $"Drive {driveName} ({systemDrv}) Consumed*: {percentConsumed}%";

                    _ = sb.AppendLine(drvSize);
                    _ = sb.AppendLine(drvConsumed);

                    driveInfo += $"{drvSize}{Environment.NewLine}{drvConsumed}{Environment.NewLine}";
                }

                string osHotFixes = GetWindowsHotFixes(token);

                if (!string.IsNullOrEmpty(osHotFixes))
                {
                    _ = sb.AppendLine($"\nWindows Patches/Hot Fixes*:\n\n{osHotFixes}");
                }

                // Dynamic info qualifier (*)
                _ = sb.AppendLine($"\n* Dynamic data.");

                this.osReport = sb.ToString();

                // ETW.
                if (this.IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        $"FabricObserverDataEvent",
                        new MachineTelemetryData
                        {
                            HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                            Node = this.NodeName,
                            Observer = this.ObserverName,
                            OS = osName,
                            OSVersion = osVersion,
                            OSInstallDate = installDate,
                            LastBootUpTime = lastBootTime,
                            TotalMemorySizeGB = this.totalVisibleMemoryGb,
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

                if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                {
                    this.TelemetryClient?.ReportMetricAsync(
                        new MachineTelemetryData
                        {
                            HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                            Node = this.NodeName,
                            Observer = this.ObserverName,
                            OS = osName,
                            OSVersion = osVersion,
                            OSInstallDate = installDate,
                            LastBootUpTime = lastBootTime,
                            TotalMemorySizeGB = this.totalVisibleMemoryGb,
                            AvailablePhysicalMemory = freePhysicalMem,
                            AvailableVirtualMemory = freeVirtualMem,
                            LogicalProcessorCount = logicalProcessorCount,
                            LogicalDriveCount = logicalDriveCount,
                            DriveInfo = driveInfo,
                            NumberOfRunningProcesses = numProcs,
                            ActiveFirewallRules = firewalls,
                            ActivePorts = activePorts,
                            ActiveEphemeralPorts = activeEphemeralPorts,
                            WindowsDynamicPortRange = osEphemeralPortRange,
                            FabricAppPortRange = fabricAppPortRange,
                            HotFixes = GetWindowsHotFixes(token, false).Replace("\r\n", ", ").TrimEnd(','),
                        }, this.Token);
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
                win32OsInfo?.Dispose();
                diskUsage?.Dispose();
                _ = sb.Clear();
            }
        }
    }
}
