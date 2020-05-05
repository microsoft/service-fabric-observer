// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Win32;
using WUApiLib;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    // This observer monitors OS health state and provides static and dynamic OS level information.
    // It will signal Ok Health Reports that will show up under node details in SFX as well as emit ETW events.
    // If FabricObserverWebApi is installed, the output includes a local file that is used
    // by the API service and returns Hardware/OS info as HTML (http://localhost:5000/api/ObserverManager).
    public class OsObserver : ObserverBase
    {
        private const string AuStateUnknownMessage = "Unable to determine Windows AutoUpdate state.";
        private string osReport;
        private string osStatus;
        private string auServiceEnabledMessage;
        private int totalVisibleMemoryGb = -1;
        private bool auStateUnknown;
        private bool isWindowsUpdateAutoDownloadEnabled;

        public string TestManifestPath
        {
            get; set;
        }

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

            await this.CheckWuAutoDownloadEnabledAsync(token).ConfigureAwait(false);
            await this.GetComputerInfoAsync(token).ConfigureAwait(false);
            await this.ReportAsync(token).ConfigureAwait(false);
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
                    if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
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

                // Windows Update automatic download enabled?
                if (this.isWindowsUpdateAutoDownloadEnabled)
                {
                    string linkText =
                        $"{Environment.NewLine}For clusters of Silver durability or above, " +
                        $"please consider <a href=\"https://docs.microsoft.com/azure/virtual-machine-scale-sets/virtual-machine-scale-sets-automatic-upgrade\" target=\"blank\">" +
                        $"enabling VMSS automatic OS image upgrades</a> to prevent unexpected VM reboots. " +
                        $"For Bronze durability clusters, please consider deploying the " +
                        $"<a href=\"https://docs.microsoft.com/azure/service-fabric/service-fabric-patch-orchestration-application\" target=\"blank\">Patch Orchestration Service</a>.";

                    this.auServiceEnabledMessage = $"Windows Update Automatic Download is enabled.{linkText}";

                    report = new HealthReport
                    {
                        Observer = this.ObserverName,
                        Property = "OSConfiguration",
                        HealthMessage = this.auServiceEnabledMessage,
                        State = HealthState.Warning,
                        NodeName = this.NodeName,
                        HealthReportTimeToLive = this.SetHealthReportTimeToLive(),
                    };

                    this.HealthReporter.ReportHealthToServiceFabric(report);

                    if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                    {
                        // Send Health Report as Telemetry (perhaps it signals an Alert from App Insights, for example.).
                        var telemetryData = new TelemetryData(FabricClientInstance, token)
                        {
                            HealthEventDescription = this.auServiceEnabledMessage,
                            HealthState = "Warning",
                            Metric = "WUAutoDownloadEnabled",
                            Value = this.isWindowsUpdateAutoDownloadEnabled,
                            NodeName = this.NodeName,
                            ObserverName = this.ObserverName,
                            Source = ObserverConstants.FabricObserverName,
                        };

                        _ = this.TelemetryClient?.ReportMetricAsync(
                            telemetryData,
                            this.Token);
                    }

                    // ETW.
                    if (this.IsEtwEnabled)
                    {
                        Logger.EtwLogger?.Write(
                            ObserverConstants.FabricObserverETWEventName,
                            new
                            {
                                HealthState = "Warning",
                                HealthEventDescription = this.auServiceEnabledMessage,
                                ObserverName = this.ObserverName,
                                Metric = "WUAutoDownloadEnabled",
                                Value = this.isWindowsUpdateAutoDownloadEnabled,
                                NodeName = this.NodeName,
                            });
                    }
                }

                // reset au globals for fresh detection during next observer run.
                this.isWindowsUpdateAutoDownloadEnabled = false;
                this.auStateUnknown = false;

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
            catch (Exception e) when (
                e is ArgumentException ||
                e is FormatException ||
                e is InvalidCastException ||
                e is ManagementException ||
                e is NullReferenceException)
            {
            }
            finally
            {
                results?.Dispose();
                searcher?.Dispose();
            }

            return ret;
        }

        private Task CheckWuAutoDownloadEnabledAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            // Windows Update Automatic Download enabled (automatically downloading an update without notification beforehand)?
            // If so, it's best to disable this and deploy either POA (for Bronze durability clusters)
            // or enable VMSS automatic OS image upgrades for Silver+ durability clusters. 
            // This is important to prevent unexpected, concurrent VM reboots due to Windows Updates.
            try
            {
                var wuLibAutoUpdates = new WUApiLib.AutomaticUpdatesClass();
                this.isWindowsUpdateAutoDownloadEnabled =
                    wuLibAutoUpdates.ServiceEnabled &&
                    wuLibAutoUpdates.Settings.NotificationLevel != AutomaticUpdatesNotificationLevel.aunlNotifyBeforeDownload;
            }
            catch (Exception e) when (
                e is COMException ||
                e is InvalidOperationException ||
                e is SecurityException ||
                e is Win32Exception)
            {
                this.ObserverLogger.LogWarning(
                    $"{AuStateUnknownMessage}{Environment.NewLine}{e}");

                this.auStateUnknown = true;
            }

            return Task.CompletedTask;
        }

        private async Task GetComputerInfoAsync(CancellationToken token)
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

                var clusterManifestXml = this.IsTestRun ? File.ReadAllText(
                    this.TestManifestPath) : await this.FabricClientInstance.ClusterManager.GetClusterManifestAsync(
                        this.AsyncClusterOperationTimeoutSeconds, this.Token).ConfigureAwait(false);

                var (lowPortApp, highPortApp) =
                    NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(
                    this.FabricServiceContext.NodeContext.NodeType,
                    clusterManifestXml);

                int firewalls = NetworkUsage.GetActiveFirewallRulesCount();

                // WU AutoUpdate
                string auMessage = "WindowsUpdateAutoDownloadEnabled: ";

                if (this.auStateUnknown)
                {
                    auMessage += "Unknown";
                }
                else
                {
                    auMessage += this.isWindowsUpdateAutoDownloadEnabled;
                }

                // OS info.
                _ = sb.AppendLine("OS Information:\r\n");
                _ = sb.AppendLine($"Name: {osName}");
                _ = sb.AppendLine($"Version: {osVersion}");
                _ = sb.AppendLine($"InstallDate: {installDate}");
                _ = sb.AppendLine($"LastBootUpTime*: {lastBootTime}");
                _ = sb.AppendLine(auMessage);
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
                _ = sb.AppendLine($"{Environment.NewLine}Hardware Information:{Environment.NewLine}");
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
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            HealthState = "Ok",
                            Node = this.NodeName,
                            Observer = this.ObserverName,
                            OS = osName,
                            OSVersion = osVersion,
                            OSInstallDate = installDate,
                            AutoUpdateEnabled = this.auStateUnknown ? "Unknown" : this.isWindowsUpdateAutoDownloadEnabled.ToString(),
                            LastBootUpTime = lastBootTime,
                            WindowsAutoUpdateEnabled = this.isWindowsUpdateAutoDownloadEnabled,
                            TotalMemorySizeGB = this.totalVisibleMemoryGb,
                            AvailablePhysicalMemoryGB = Math.Round(freePhysicalMem / 1024 / 1024, 2),
                            AvailableVirtualMemoryGB = Math.Round(freeVirtualMem / 1024 / 1024, 2),
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
                        });
                }

                // Telemetry
                if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                {
                    this.TelemetryClient?.ReportMetricAsync(
                        new MachineTelemetryData
                        {
                            HealthState = "Ok",
                            Node = this.NodeName,
                            Observer = this.ObserverName,
                            OS = osName,
                            OSVersion = osVersion,
                            OSInstallDate = installDate,
                            LastBootUpTime = lastBootTime,
                            WindowsUpdateAutoDownloadEnabled = this.isWindowsUpdateAutoDownloadEnabled,
                            TotalMemorySizeGB = this.totalVisibleMemoryGb,
                            AvailablePhysicalMemoryGB = Math.Round(freePhysicalMem / 1024 / 1024, 2),
                            AvailableVirtualMemoryGB = Math.Round(freeVirtualMem / 1024 / 1024, 2),
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
                    $"Unhandled exception processing OS information:{Environment.NewLine}{e}");

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

        private void LogCurrentAUValues(RegistryKey regKey)
        {
            StringBuilder result = new StringBuilder();
            var values = regKey.GetValueNames();
            foreach (string value in values)
            {
                result.AppendFormat(
                    "{0} = {1}{2}",
                    value,
                    regKey.GetValue(value),
                    Environment.NewLine);
            }

            string s = result.ToString();

            int majorVersion = Environment.OSVersion.Version.Major;
            int minorVersion = Environment.OSVersion.Version.Minor;

            this.HealthReporter.ReportHealthToServiceFabric(new HealthReport
            {
                HealthMessage = s + Environment.NewLine + majorVersion + "." + minorVersion,
                Observer = this.ObserverName,
                Property = "DebugOutputAU",
                State = HealthState.Ok,
                NodeName = this.NodeName,
                ReportType = HealthReportType.Node,
            });
        }
    }
}
