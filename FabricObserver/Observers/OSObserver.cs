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
using WUApiLib;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    // This observer monitors OS health state and provides static and dynamic OS level information.
    // It will signal Ok Health Reports that will show up under node details in SFX as well as emit ETW events.
    // If FabricObserverWebApi is installed, the output includes a local file that is used
    // by the API service and returns Hardware/OS info as HTML (http://localhost:5000/api/ObserverManager).
    public class OSObserver : ObserverBase
    {
        private const string AuStateUnknownMessage = "Unable to determine Windows AutoUpdate state.";
        private string osReport;
        private string osStatus;
        private bool auStateUnknown;
        private bool isWindowsUpdateAutoDownloadEnabled;
        private bool isWUADSettingEnabled;

        public string TestManifestPath
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OSObserver"/> class.
        /// </summary>
        public OSObserver()
        {

        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            // This only makes sense for Windows and only for non-dev clusters.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var nodes = FabricClientInstance.QueryManager.GetNodeListAsync(
                                null,
                                AsyncClusterOperationTimeoutSeconds,
                                Token).GetAwaiter().GetResult();

                if (nodes.Count > 1 && bool.TryParse(
                                        GetSettingParameterValue(
                                            ConfigurationSectionName,
                                            ObserverConstants.EnableWindowsAutoUpdateCheck),
                                         out isWUADSettingEnabled))
                {
                    if (isWUADSettingEnabled)
                    {
                        await CheckWuAutoDownloadEnabledAsync(token).ConfigureAwait(false);
                    }
                }
            }

            await GetComputerInfoAsync(token).ConfigureAwait(false);

            await ReportAsync(token).ConfigureAwait(false);
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                // OS Health.
                if (this.osStatus != null && !string.Equals(this.osStatus, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    string healthMessage = $"OS reporting unhealthy: {this.osStatus}";
                    var healthReport = new HealthReport
                    {
                        Observer = ObserverName,
                        NodeName = NodeName,
                        HealthMessage = healthMessage,
                        State = HealthState.Error,
                        HealthReportTimeToLive = SetHealthReportTimeToLive(),
                    };

                    HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // This means this observer created a Warning or Error SF Health Report
                    HasActiveFabricErrorOrWarning = true;

                    // Send Health Report as Telemetry (perhaps it signals an Alert from App Insights, for example.).
                    if (IsTelemetryProviderEnabled && IsObserverTelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportHealthAsync(
                            HealthScope.Application,
                            FabricRuntime.GetActivationContext().ApplicationName,
                            HealthState.Error,
                            $"{NodeName} - OS reporting unhealthy: {this.osStatus}",
                            ObserverName,
                            Token);
                    }
                }
                else if (HasActiveFabricErrorOrWarning && string.Equals(this.osStatus, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    // Clear Error or Warning with an OK Health Report.
                    string healthMessage = $"OS reporting healthy: {this.osStatus}";

                    var healthReport = new HealthReport
                    {
                        Observer = ObserverName,
                        NodeName = NodeName,
                        HealthMessage = healthMessage,
                        State = HealthState.Ok,
                        HealthReportTimeToLive = default(TimeSpan),
                    };

                    HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // Reset internal health state.
                    HasActiveFabricErrorOrWarning = false;
                }

                if (ObserverManager.ObserverWebAppDeployed)
                {
                    var logPath = Path.Combine(ObserverLogger.LogFolderBasePath, "SysInfo.txt");

                    // This file is used by the web application (log reader.).
                    if (!ObserverLogger.TryWriteLogFile(logPath, $"Last updated on {DateTime.UtcNow.ToString("M/d/yyyy HH:mm:ss")} UTC<br/>{this.osReport}"))
                    {
                        HealthReporter.ReportFabricObserverServiceHealth(
                            FabricServiceContext.ServiceName.OriginalString,
                            ObserverName,
                            HealthState.Warning,
                            "Unable to create SysInfo.txt file.");
                    }
                }

                var report = new HealthReport
                {
                    Observer = ObserverName,
                    HealthMessage = this.osReport,
                    State = HealthState.Ok,
                    NodeName = NodeName,
                    HealthReportTimeToLive = SetHealthReportTimeToLive(),
                };

                HealthReporter.ReportHealthToServiceFabric(report);

                // Windows Update automatic download enabled?
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    && this.isWindowsUpdateAutoDownloadEnabled)
                {
                    string linkText =
                        $"{Environment.NewLine}For clusters of Silver durability or above, " +
                        $"please consider <a href=\"https://docs.microsoft.com/azure/virtual-machine-scale-sets/virtual-machine-scale-sets-automatic-upgrade\" target=\"blank\">" +
                        $"enabling VMSS automatic OS image upgrades</a> to prevent unexpected VM reboots. " +
                        $"For Bronze durability clusters, please consider deploying the " +
                        $"<a href=\"https://docs.microsoft.com/azure/service-fabric/service-fabric-patch-orchestration-application\" target=\"blank\">Patch Orchestration Service</a>.";

                    string auServiceEnabledMessage = $"Windows Update Automatic Download is enabled.{linkText}";

                    report = new HealthReport
                    {
                        Observer = ObserverName,
                        Property = "OSConfiguration",
                        HealthMessage = auServiceEnabledMessage,
                        State = HealthState.Warning,
                        NodeName = NodeName,
                        HealthReportTimeToLive = SetHealthReportTimeToLive(),
                    };

                    HealthReporter.ReportHealthToServiceFabric(report);

                    if (IsTelemetryProviderEnabled
                        && IsObserverTelemetryEnabled
                        && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // Send Health Report as Telemetry (perhaps it signals an Alert from App Insights, for example.).
                        var telemetryData = new TelemetryData(FabricClientInstance, token)
                        {
                            HealthEventDescription = auServiceEnabledMessage,
                            HealthState = "Warning",
                            Metric = "WUAutoDownloadEnabled",
                            Value = this.isWindowsUpdateAutoDownloadEnabled,
                            NodeName = NodeName,
                            ObserverName = ObserverName,
                            Source = ObserverConstants.FabricObserverName,
                        };

                        _ = TelemetryClient?.ReportMetricAsync(
                            telemetryData,
                            Token);
                    }

                    // ETW.
                    if (IsEtwEnabled && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        Logger.EtwLogger?.Write(
                            ObserverConstants.FabricObserverETWEventName,
                            new
                            {
                                HealthState = "Warning",
                                HealthEventDescription = auServiceEnabledMessage,
                                ObserverName,
                                Metric = "WUAutoDownloadEnabled",
                                Value = this.isWindowsUpdateAutoDownloadEnabled,
                                NodeName,
                            });
                    }
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // reset au globals for fresh detection during next observer run.
                    this.isWindowsUpdateAutoDownloadEnabled = false;
                    this.auStateUnknown = false;
                    this.isWUADSettingEnabled = false;
                }

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                HealthReporter.ReportFabricObserverServiceHealth(
                    FabricServiceContext.ServiceName.OriginalString,
                    ObserverName,
                    HealthState.Error,
                    $"Unhandled exception processing OS information:{Environment.NewLine}{e}");

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
                var baseUrl = "https://support.microsoft.com/help/";

                foreach (var obj in resultsOrdered)
                {
                    token.ThrowIfCancellationRequested();

                    _ = generateUrl ? sb.AppendLine(
                        $"<a href=\"{baseUrl}{((string)obj["HotFixID"])?.ToLower()?.Replace("kb", string.Empty)}/\" target=\"_blank\">{obj["HotFixID"]}</a>   {obj["InstalledOn"]}") : sb.AppendLine($"{obj["HotFixID"]}");
                }

                ret = sb.ToString().Trim();
                _ = sb.Clear();
            }
            catch (Exception e) when (
                e is ArgumentException ||
                e is FormatException ||
                e is InvalidCastException ||
                e is ManagementException ||
                e is NullReferenceException ||
                e is OperationCanceledException)
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
                var wuLibAutoUpdates = new AutomaticUpdatesClass();
                this.isWindowsUpdateAutoDownloadEnabled =
                    wuLibAutoUpdates.ServiceEnabled &&
                    wuLibAutoUpdates.Settings.NotificationLevel == AutomaticUpdatesNotificationLevel.aunlScheduledInstallation;
            }
            catch (Exception e) when (
                e is COMException ||
                e is InvalidOperationException ||
                e is SecurityException ||
                e is Win32Exception)
            {
                ObserverLogger.LogWarning(
                    $"{AuStateUnknownMessage}{Environment.NewLine}{e}");

                this.auStateUnknown = true;
            }

            return Task.CompletedTask;
        }

        private async Task GetComputerInfoAsync(CancellationToken token)
        {
            var sb = new StringBuilder();
            int logicalProcessorCount = Environment.ProcessorCount;

            try
            {
                OSInfo osInfo = await OperatingSystemInfoProvider.Instance.GetOSInfoAsync(token);
                this.osStatus = osInfo.Status;

                // Active, bound ports.
                int activePorts = OperatingSystemInfoProvider.Instance.GetActivePortCount();

                // Active, ephemeral ports.
                int activeEphemeralPorts = OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount();
                (int lowPortOS, int highPortOS) = OperatingSystemInfoProvider.Instance.TupleGetDynamicPortRange();
                string osEphemeralPortRange = string.Empty;
                string fabricAppPortRange = string.Empty;

                string clusterManifestXml = IsTestRun ? File.ReadAllText(
                    TestManifestPath) : await FabricClientInstance.ClusterManager.GetClusterManifestAsync(
                        AsyncClusterOperationTimeoutSeconds, Token).ConfigureAwait(false);

                (int lowPortApp, int highPortApp) =
                    NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(
                    FabricServiceContext.NodeContext.NodeType,
                    clusterManifestXml);

                int firewalls = NetworkUsage.GetActiveFirewallRulesCount();

                // OS info.
                _ = sb.AppendLine("OS Information:\r\n");
                _ = sb.AppendLine($"Name: {osInfo.Name}");
                _ = sb.AppendLine($"Version: {osInfo.Version}");

                if (string.IsNullOrEmpty(osInfo.InstallDate))
                {
                    _ = sb.AppendLine($"InstallDate: {osInfo.InstallDate}");
                }

                _ = sb.AppendLine($"LastBootUpTime*: {osInfo.LastBootUpTime}");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // WU AutoUpdate - Download enabled.
                    // If the config setting EnableWindowsAutoUpdateCheck is set to false, then don't add this info to sb.
                    if (this.isWUADSettingEnabled)
                    {
                        string auMessage = "WindowsUpdateAutoDownloadEnabled: ";

                        if (this.auStateUnknown)
                        {
                            auMessage += "Unknown";
                        }
                        else
                        {
                            auMessage += this.isWindowsUpdateAutoDownloadEnabled;
                        }
                        _ = sb.AppendLine(auMessage);
                    }

                    // Not supported for Linux.
                    _ = sb.AppendLine($"OSLanguage: {osInfo.Language}");
                    _ = sb.AppendLine($"OSHealthStatus*: {osInfo.Status}");
                }

                _ = sb.AppendLine($"NumberOfProcesses*: {osInfo.NumberOfProcesses}");

                if (lowPortOS > -1)
                {
                    osEphemeralPortRange = $"{lowPortOS} - {highPortOS}";
                    _ = sb.AppendLine($"OSEphemeralTCPPortRange: {osEphemeralPortRange} (Active*: {activeEphemeralPorts})");
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

                if (osInfo.TotalVirtualMemorySizeKB > 0)
                {
                    _ = sb.AppendLine($"TotalVirtualMemorySize: {osInfo.TotalVirtualMemorySizeKB / 1048576} GB");
                }

                if (osInfo.TotalVisibleMemorySizeKB > 0)
                {
                    _ = sb.AppendLine($"TotalVisibleMemorySize: {osInfo.TotalVisibleMemorySizeKB / 1048576} GB");
                }

                _ = sb.AppendLine($"FreePhysicalMemory*: {Math.Round(osInfo.AvailableMemoryKB / 1048576.0, 2)} GB");
                _ = sb.AppendLine($"FreeVirtualMemory*: {Math.Round(osInfo.FreeVirtualMemoryKB / 1048576.0, 2)} GB");

                // Disk
                var drivesInformationTuple = DiskUsage.GetCurrentDiskSpaceTotalAndUsedPercentAllDrives(SizeUnit.Gigabytes);
                var logicalDriveCount = drivesInformationTuple.Count;
                string driveInfo = string.Empty;

                _ = sb.AppendLine($"LogicalDriveCount: {logicalDriveCount}");

                foreach (var (driveName, diskSize, percentConsumed) in drivesInformationTuple)
                {
                    string drvSize;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        string systemDrv = "Data";

                        if (string.Equals(Environment.SystemDirectory.Substring(0, 1), driveName.Substring(0, 1), StringComparison.OrdinalIgnoreCase))
                        {
                            systemDrv = "System";
                        }

                        drvSize = $"Drive {driveName} ({systemDrv}) Size: {diskSize} GB, Consumed*: {percentConsumed}%";
                    }
                    else
                    {
                        drvSize = $"Mount point: {driveName}, Size: {diskSize} GB, Consumed*: {percentConsumed}%";
                    }

                    _ = sb.AppendLine(drvSize);

                    driveInfo += $"{drvSize}{Environment.NewLine}";
                }

                string osHotFixes = string.Empty;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    osHotFixes = GetWindowsHotFixes(token);
                }

                if (!string.IsNullOrEmpty(osHotFixes))
                {
                    _ = sb.AppendLine($"\nWindows Patches/Hot Fixes*:\n\n{osHotFixes}");
                }

                // Dynamic info qualifier (*)
                _ = sb.AppendLine($"\n* Dynamic data.");

                this.osReport = sb.ToString();

                string hotFixes = string.Empty;

                // ETW.
                if (IsEtwEnabled)
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        hotFixes = GetWindowsHotFixes(token, generateUrl: false).Replace("\r\n", ", ").TrimEnd(',');
                    }

                    Logger.EtwLogger?.Write(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            HealthState = "Ok",
                            Node = NodeName,
                            Observer = ObserverName,
                            OS = osInfo.Name,
                            OSVersion = osInfo.Version,
                            OSInstallDate = osInfo.InstallDate,
                            AutoUpdateEnabled = this.auStateUnknown ? "Unknown" : this.isWindowsUpdateAutoDownloadEnabled.ToString(),
                            osInfo.LastBootUpTime,
                            WindowsAutoUpdateEnabled = this.isWindowsUpdateAutoDownloadEnabled,
                            TotalMemorySizeGB = (int)(osInfo.TotalVisibleMemorySizeKB / 1048576),
                            AvailablePhysicalMemoryGB = Math.Round(osInfo.FreePhysicalMemoryKB / 1048576.0, 2),
                            AvailableVirtualMemoryGB = Math.Round(osInfo.FreeVirtualMemoryKB / 1048576.0, 2),
                            LogicalProcessorCount = logicalProcessorCount,
                            LogicalDriveCount = logicalDriveCount,
                            DriveInfo = driveInfo,
                            NumberOfRunningProcesses = osInfo.NumberOfProcesses,
                            ActiveFirewallRules = firewalls,
                            ActivePorts = activePorts,
                            ActiveEphemeralPorts = activeEphemeralPorts,
                            WindowsDynamicPortRange = osEphemeralPortRange,
                            FabricAppPortRange = fabricAppPortRange,
                            HotFixes = hotFixes,
                        });
                }

                // Telemetry
                if (IsTelemetryProviderEnabled && IsObserverTelemetryEnabled)
                {
                    if (string.IsNullOrEmpty(hotFixes) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        hotFixes = GetWindowsHotFixes(token, generateUrl: false).Replace("\r\n", ", ").TrimEnd(',');
                    }

                    TelemetryClient?.ReportMetricAsync(
                        new MachineTelemetryData
                        {
                            HealthState = "Ok",
                            Node = NodeName,
                            Observer = ObserverName,
                            OS = osInfo.Name,
                            OSVersion = osInfo.Version,
                            OSInstallDate = osInfo.InstallDate,
                            LastBootUpTime = osInfo.LastBootUpTime,
                            WindowsUpdateAutoDownloadEnabled = this.isWindowsUpdateAutoDownloadEnabled,
                            TotalMemorySizeGB = (int)osInfo.TotalVisibleMemorySizeKB / 1048576,
                            AvailablePhysicalMemoryGB = Math.Round(osInfo.FreePhysicalMemoryKB / 1048576.0, 2),
                            AvailableVirtualMemoryGB = Math.Round(osInfo.FreeVirtualMemoryKB / 1048576.0, 2),
                            LogicalProcessorCount = logicalProcessorCount,
                            LogicalDriveCount = logicalDriveCount,
                            DriveInfo = driveInfo,
                            NumberOfRunningProcesses = osInfo.NumberOfProcesses,
                            ActiveFirewallRules = firewalls,
                            ActivePorts = activePorts,
                            ActiveEphemeralPorts = activeEphemeralPorts,
                            WindowsDynamicPortRange = osEphemeralPortRange,
                            FabricAppPortRange = fabricAppPortRange,
                            HotFixes = hotFixes,
                        }, Token);
                }
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(
                        FabricServiceContext.ServiceName.OriginalString,
                        ObserverName,
                        HealthState.Error,
                        $"Unhandled exception processing OS information:{Environment.NewLine}{e}");

                    throw;
                }
            }
        }
    }
}
