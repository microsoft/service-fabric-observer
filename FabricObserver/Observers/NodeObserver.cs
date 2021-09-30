// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Fabric;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;

namespace FabricObserver.Observers
{
    // This observer monitors VM level resource usage across CPU and Memory, and reports port and firewall rule counts.
    // Thresholds for Error and Warning signals are user-supplied in NodeObserver.config.json.
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    public class NodeObserver : ObserverBase
    {
        private readonly Stopwatch stopwatch;

        // These are public properties because they are used in unit tests.
        public FabricResourceUsageData<float> MemDataInUse;
        public FabricResourceUsageData<int> FirewallData;
        public FabricResourceUsageData<int> ActivePortsData;
        public FabricResourceUsageData<int> EphemeralPortsData;
        public FabricResourceUsageData<double> MemDataPercent;
        public FabricResourceUsageData<float> CpuTimeData;

        // These are only useful for Linux.\\

        // Holds data for percentage of total configured file descriptors that are in use.
        public FabricResourceUsageData<double> LinuxFileHandlesDataPercentAllocated;
        public FabricResourceUsageData<int> LinuxFileHandlesDataTotalAllocated;

        public float CpuErrorUsageThresholdPct
        {
            get; set;
        }

        public int MemErrorUsageThresholdMb
        {
            get; set;
        }

        public float CpuWarningUsageThresholdPct
        {
            get; set;
        }

        public int MemWarningUsageThresholdMb
        {
            get; set;
        }

        public int ActivePortsErrorThreshold
        {
            get; set;
        }

        public int EphemeralPortsErrorThreshold
        {
            get; set;
        }

        public int FirewallRulesErrorThreshold
        {
            get; set;
        }

        public int ActivePortsWarningThreshold
        {
            get; set;
        }

        public int EphemeralPortsWarningThreshold
        {
            get; set;
        }

        public int FirewallRulesWarningThreshold
        {
            get; set;
        }

        public double MemoryErrorLimitPercent
        {
            get; set;
        }

        public double MemoryWarningLimitPercent
        {
            get; set;
        }

        /* Thresholds for Percentage/Raw Count of available FileHandles/FDs in use on VM.
           NOTE: These are only used for Linux. */
        public double LinuxFileHandlesErrorPercent
        {
            get; set;
        }

        public double LinuxFileHandlesWarningPercent
        {
            get; set;
        }

        public int LinuxFileHandlesErrorTotalAllocated
        {
            get; set;
        }

        public int LinuxFileHandlesWarningTotalAllocated
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NodeObserver"/> class.
        /// </summary>
        public NodeObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
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

            Token = token;
            stopwatch.Start();

            Initialize();
            await GetSystemCpuMemoryValuesAsync(token).ConfigureAwait(true);
            await ReportAsync(token).ConfigureAwait(true);
            CleanUp();

            // The time it took to run this observer.
            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;

            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration: {RunDuration}");
            }

            stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                if (EnableCsvLogging)
                {
                    var fileName = $"MachineResourceData{(CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives ? "_" + DateTime.UtcNow.ToString("o") : string.Empty)}";

                    // Log (csv) system-wide CPU/Mem/Ports/File Handles(Linux)/Firewall Rules(Windows) data.
                    if (CpuTimeData != null && (CpuErrorUsageThresholdPct > 0 || CpuWarningUsageThresholdPct > 0))
                    {
                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "CPU Time",
                                        "Average",
                                        Math.Round(CpuTimeData.AverageDataValue, 1));

                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "CPU Time",
                                        "Peak",
                                        Math.Round(CpuTimeData.MaxDataValue, 1));
                    }

                    // Memory - Committed (MB)
                    if (MemDataInUse != null && (MemErrorUsageThresholdMb > 0 || MemWarningUsageThresholdMb > 0))
                    {
                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "Committed Memory (MB)",
                                        "Average",
                                        Math.Round(MemDataInUse.AverageDataValue, 1));

                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "Committed Memory (MB)",
                                        "Peak",
                                        Math.Round(MemDataInUse.MaxDataValue));
                    }

                    // % of Total
                    if (MemDataPercent != null && (MemoryErrorLimitPercent > 0 || MemoryWarningLimitPercent > 0))
                    {
                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "Memory in Use (%)",
                                        "Average",
                                        Math.Round(MemDataPercent.AverageDataValue, 1));

                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "Memory in Use (%)",
                                        "Peak",
                                        Math.Round(MemDataPercent.MaxDataValue, 1));
                    }

                    if (ActivePortsData != null && (ActivePortsErrorThreshold > 0 || ActivePortsWarningThreshold > 0))
                    {
                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "All Active Ports",
                                        "Total",
                                        Math.Round(ActivePortsData.AverageDataValue));
                    }

                    if (EphemeralPortsData != null && (EphemeralPortsErrorThreshold > 0 || EphemeralPortsWarningThreshold > 0))
                    {
                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "Ephemeral Active Ports",
                                        "Total",
                                        Math.Round(EphemeralPortsData.AverageDataValue));
                    }

                    if (FirewallData != null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (FirewallRulesErrorThreshold > 0 || FirewallRulesWarningThreshold > 0))
                    {
                        CsvFileLogger.LogData(
                                        fileName,
                                        NodeName,
                                        "Firewall Rules",
                                        "Total",
                                        Math.Round(FirewallData.AverageDataValue));
                    }

                    // Windows does not have a corresponding FD/FH limit which can be set by a user, nor does Windows have a reliable way of determining the total number of open handles in the system.
                    // As such, it does not make sense to monitor system-wide, percent usage of ALL available file handles on Windows. This feature of NodeObserver is therefore Linux-only.
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        if (LinuxFileHandlesDataPercentAllocated != null && (LinuxFileHandlesErrorPercent > 0 || LinuxFileHandlesWarningPercent > 0))
                        {
                            CsvFileLogger.LogData(
                                            fileName,
                                            NodeName,
                                            ErrorWarningProperty.TotalFileHandlesPct,
                                            "Percent In Use",
                                            Math.Round(LinuxFileHandlesDataPercentAllocated.AverageDataValue));
                        }

                        if (LinuxFileHandlesDataTotalAllocated != null && (LinuxFileHandlesErrorTotalAllocated > 0 || LinuxFileHandlesWarningTotalAllocated > 0))
                        {
                            CsvFileLogger.LogData(
                                            fileName,
                                            NodeName,
                                            ErrorWarningProperty.TotalFileHandles,
                                            "Total Allocated",
                                            Math.Round(LinuxFileHandlesDataTotalAllocated.AverageDataValue));
                        }
                    }

                    DataTableFileLogger.Flush();
                }

                // Report on the global health state (system-wide (node) metrics).
                // User-configurable in NodeObserver.config.json
                var timeToLiveWarning = GetHealthReportTimeToLive();

                // CPU
                if (CpuTimeData != null && (CpuErrorUsageThresholdPct > 0 || CpuWarningUsageThresholdPct > 0))
                {
                    ProcessResourceDataReportHealth(
                            CpuTimeData,
                            CpuErrorUsageThresholdPct,
                            CpuWarningUsageThresholdPct,
                            timeToLiveWarning);
                }

                // Memory - MB
                if (MemDataInUse != null && (MemErrorUsageThresholdMb > 0 || MemWarningUsageThresholdMb > 0))
                {
                    ProcessResourceDataReportHealth(
                            MemDataInUse,
                            MemErrorUsageThresholdMb,
                            MemWarningUsageThresholdMb,
                            timeToLiveWarning);
                }

                // Memory - Percent
                if (MemDataPercent != null && (MemoryErrorLimitPercent > 0 || MemoryWarningLimitPercent > 0))
                {
                    ProcessResourceDataReportHealth(
                            MemDataPercent,
                            MemoryErrorLimitPercent,
                            MemoryWarningLimitPercent,
                            timeToLiveWarning);
                }

                // Firewall rules
                if (FirewallData != null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (FirewallRulesErrorThreshold > 0 || FirewallRulesWarningThreshold > 0))
                {
                    ProcessResourceDataReportHealth(
                            FirewallData,
                            FirewallRulesErrorThreshold,
                            FirewallRulesWarningThreshold,
                            timeToLiveWarning);
                }

                // Ports - Active TCP
                if (ActivePortsData != null && (ActivePortsErrorThreshold > 0 || ActivePortsWarningThreshold > 0))
                {
                    ProcessResourceDataReportHealth(
                            ActivePortsData,
                            ActivePortsErrorThreshold,
                            ActivePortsWarningThreshold,
                            timeToLiveWarning);
                }

                // Ports - Active Ephemeral TCP
                if (EphemeralPortsData != null && (EphemeralPortsErrorThreshold > 0 || EphemeralPortsWarningThreshold > 0))
                {
                    ProcessResourceDataReportHealth(
                            EphemeralPortsData,
                            EphemeralPortsErrorThreshold,
                            EphemeralPortsWarningThreshold,
                            timeToLiveWarning);
                }

                // Total Open File Handles % (Linux-only) - Total Percentage Allocated (in use) of the configured Maximum number of File Handles the linux kernel will allocate.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (LinuxFileHandlesDataPercentAllocated != null && (LinuxFileHandlesErrorPercent > 0 || LinuxFileHandlesWarningPercent > 0))
                    {
                        ProcessResourceDataReportHealth(
                                LinuxFileHandlesDataPercentAllocated,
                                LinuxFileHandlesErrorPercent,
                                LinuxFileHandlesWarningPercent,
                                timeToLiveWarning);
                    }

                    if (LinuxFileHandlesDataTotalAllocated != null && (LinuxFileHandlesErrorTotalAllocated > 0 || LinuxFileHandlesWarningTotalAllocated > 0))
                    {
                        ProcessResourceDataReportHealth(
                                LinuxFileHandlesDataTotalAllocated,
                                LinuxFileHandlesErrorTotalAllocated,
                                LinuxFileHandlesWarningTotalAllocated,
                                timeToLiveWarning);
                    }
                }

                return Task.CompletedTask;
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogWarning($"Unhandled exception re-thrown:{Environment.NewLine}{e}");

                // Fix the bug..
                throw;
            }
        }

        private void Initialize()
        {
            SetThresholdSFromConfiguration();
            InitializeDataContainers();
        }

        private void InitializeDataContainers()
        {
            int frudCapacity = 8;

            if (UseCircularBuffer)
            {
                frudCapacity = DataCapacity > 0 ? DataCapacity : 4;
            }
            else if (MonitorDuration > TimeSpan.MinValue)
            {
                frudCapacity = (int)MonitorDuration.TotalSeconds * 4;
            }

            if (CpuTimeData == null && (CpuErrorUsageThresholdPct > 0 || CpuWarningUsageThresholdPct > 0))
            {
                CpuTimeData = new FabricResourceUsageData<float>(ErrorWarningProperty.TotalCpuTime, "TotalCpuTime", frudCapacity, UseCircularBuffer);
            }

            if (MemDataInUse == null && (MemErrorUsageThresholdMb > 0 || MemWarningUsageThresholdMb > 0))
            {
                MemDataInUse = new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, "MemoryConsumedMb", frudCapacity, UseCircularBuffer);
            }

            if (MemDataPercent == null && (MemoryErrorLimitPercent > 0 || MemoryWarningLimitPercent > 0))
            {
                MemDataPercent = new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, "MemoryConsumedPercentage", frudCapacity, UseCircularBuffer);
            }

            if (FirewallData == null && (FirewallRulesErrorThreshold > 0 || FirewallRulesWarningThreshold > 0))
            {
                FirewallData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActiveFirewallRules, "ActiveFirewallRules", 1);
            }

            if (ActivePortsData == null && (ActivePortsErrorThreshold > 0 || ActivePortsWarningThreshold > 0))
            {
                ActivePortsData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, "AllPortsInUse", 1);
            }

            if (EphemeralPortsData == null && (EphemeralPortsErrorThreshold > 0 || EphemeralPortsWarningThreshold > 0))
            {
                EphemeralPortsData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, "EphemeralPortsInUse", 1);
            }

            // This only makes sense for Linux.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (LinuxFileHandlesDataPercentAllocated == null && (LinuxFileHandlesErrorPercent > 0 || LinuxFileHandlesWarningPercent > 0))
                {
                    LinuxFileHandlesDataPercentAllocated = new FabricResourceUsageData<double>(ErrorWarningProperty.TotalFileHandlesPct, "TotalFileHandlesPercentage", 1);
                }

                if (LinuxFileHandlesDataTotalAllocated == null && (LinuxFileHandlesErrorTotalAllocated > 0 || LinuxFileHandlesWarningTotalAllocated > 0))
                {
                    LinuxFileHandlesDataTotalAllocated = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalFileHandles, "TotalFileHandlesCount", 1);
                }
            }
        }

        private void SetThresholdSFromConfiguration()
        {
            /* Error thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError) && float.TryParse(cpuError, out float cpuErrorUsageThresholdPct))
            {
                if (cpuErrorUsageThresholdPct > 0 && cpuErrorUsageThresholdPct <= 100)
                {
                    CpuErrorUsageThresholdPct = cpuErrorUsageThresholdPct;
                }
            }

            var memError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverMemoryErrorLimitMb);

            if (!string.IsNullOrEmpty(memError) && int.TryParse(memError, out int memErrorUsageThresholdMb))
            {
                MemErrorUsageThresholdMb = memErrorUsageThresholdMb;
            }

            var portsErr = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverNetworkErrorActivePorts);

            if (!string.IsNullOrEmpty(portsErr) && !int.TryParse(portsErr, out int activePortsErrorThreshold))
            {
                ActivePortsErrorThreshold = activePortsErrorThreshold;
            }

            var ephemeralPortsErr = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverNetworkErrorEphemeralPorts);

            if (!string.IsNullOrEmpty(ephemeralPortsErr) && int.TryParse(ephemeralPortsErr, out int ephemeralPortsErrorThreshold))
            {
                EphemeralPortsErrorThreshold = ephemeralPortsErrorThreshold;
            }

            var errFirewallRules = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverNetworkErrorFirewallRules);

            if (!string.IsNullOrEmpty(errFirewallRules) && int.TryParse(errFirewallRules, out int firewallRulesErrorThreshold))
            {
                FirewallRulesErrorThreshold = firewallRulesErrorThreshold;
            }

            var errMemPercentUsed = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverMemoryUsePercentError);

            if (!string.IsNullOrEmpty(errMemPercentUsed) && double.TryParse(errMemPercentUsed, out double memoryPercentUsedErrorThreshold))
            {
                if (memoryPercentUsedErrorThreshold > 0 && memoryPercentUsedErrorThreshold <= 100)
                {
                    MemoryErrorLimitPercent = memoryPercentUsedErrorThreshold;
                }
            }

            // Linux FDs.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var errFileHandlesPercentUsed = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverLinuxFileHandlesErrorLimitPct);

                if (!string.IsNullOrEmpty(errFileHandlesPercentUsed) && double.TryParse(errFileHandlesPercentUsed, out double fdsPercentUsedErrorThreshold))
                {
                    if (fdsPercentUsedErrorThreshold > 0 && fdsPercentUsedErrorThreshold <= 100)
                    {
                        LinuxFileHandlesErrorPercent = fdsPercentUsedErrorThreshold;
                    }
                }

                var errFileHandlesCount = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverLinuxFileHandlesErrorTotalAllocated);

                if (!string.IsNullOrEmpty(errFileHandlesCount) && int.TryParse(errFileHandlesCount, out int fdsErrorCountThreshold))
                {
                    if (fdsErrorCountThreshold > 0)
                    {
                        LinuxFileHandlesErrorTotalAllocated = fdsErrorCountThreshold;
                    }
                }
            }

            /* Warning thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuWarn = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn) && int.TryParse(cpuWarn, out int cpuWarningUsageThresholdPct))
            {
                if (cpuWarningUsageThresholdPct > 0 && cpuWarningUsageThresholdPct <= 100)
                {
                    CpuWarningUsageThresholdPct = cpuWarningUsageThresholdPct;
                }
            }

            var memWarn = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverMemoryWarningLimitMb);

            if (!string.IsNullOrEmpty(memWarn) && int.TryParse(memWarn, out int memWarningUsageThresholdMb))
            {
                MemWarningUsageThresholdMb = memWarningUsageThresholdMb;
            }

            var portsWarn = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverNetworkWarningActivePorts);

            if (!string.IsNullOrEmpty(portsWarn) && int.TryParse(portsWarn, out int activePortsWarningThreshold))
            {
                ActivePortsWarningThreshold = activePortsWarningThreshold;
            }

            var ephemeralPortsWarn = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverNetworkWarningEphemeralPorts);

            if (!string.IsNullOrEmpty(ephemeralPortsWarn) && int.TryParse(ephemeralPortsWarn, out int ephemeralPortsWarningThreshold))
            {
                EphemeralPortsWarningThreshold = ephemeralPortsWarningThreshold;
            }

            var warnFirewallRules = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverNetworkWarningFirewallRules);

            if (!string.IsNullOrEmpty(warnFirewallRules) && int.TryParse(warnFirewallRules, out int firewallRulesWarningThreshold))
            {
                FirewallRulesWarningThreshold = firewallRulesWarningThreshold;
            }

            var warnMemPercentUsed = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverMemoryUsePercentWarning);

            if (!string.IsNullOrEmpty(warnMemPercentUsed) && double.TryParse(warnMemPercentUsed, out double memoryPercentUsedWarningThreshold))
            {
                if (memoryPercentUsedWarningThreshold > 0 && memoryPercentUsedWarningThreshold <= 100)
                {
                    MemoryWarningLimitPercent = memoryPercentUsedWarningThreshold;
                }
            }

            /* Linux FDs */

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return;
            }

            var warnFileHandlesPercentUsed = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverLinuxFileHandlesWarningLimitPct);

            if (!string.IsNullOrEmpty(warnFileHandlesPercentUsed) && double.TryParse(warnFileHandlesPercentUsed, out double fdsPercentUsedWarningThreshold))
            {
                if (fdsPercentUsedWarningThreshold > 0 && fdsPercentUsedWarningThreshold <= 100)
                {
                    LinuxFileHandlesWarningPercent = fdsPercentUsedWarningThreshold;
                }
            }

            var warnFileHandlesCount = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.NodeObserverLinuxFileHandlesWarningTotalAllocated);

            if (string.IsNullOrEmpty(warnFileHandlesCount) || !int.TryParse(warnFileHandlesCount, out int fdsWarningCountThreshold))
            {
                return;
            }

            if (fdsWarningCountThreshold > 0)
            {
                LinuxFileHandlesWarningTotalAllocated = fdsWarningCountThreshold;
            }
        }

        private async Task GetSystemCpuMemoryValuesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var timer = new Stopwatch();

            try
            {
                // Firewall rules.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && FirewallData != null)
                {
                    int firewalls = NetworkUsage.GetActiveFirewallRulesCount();
                    FirewallData.Data.Add(firewalls);
                }

                TimeSpan duration = TimeSpan.FromSeconds(5);

                if (MonitorDuration > TimeSpan.MinValue)
                {
                    duration = MonitorDuration;
                }

                /* CPU, Memory, File Handles/FDs (Linux)
                 
                   Note: Please make sure you understand the normal state of your nodes
                   with respect to the machine resource use and/or abuse by your service(s).
                   For example, if it is normal for your services to consume 90% of available CPU and memory
                   as part of the work they perform under normal traffic flow, then it doesn't make sense to warn or
                   error on these conditions. 
                */

                if (CpuTimeData != null && (CpuErrorUsageThresholdPct > 0 || CpuWarningUsageThresholdPct > 0))
                {
                    // Warm up counter.
                    _ = CpuUtilizationProvider.Instance.GetProcessorTimePercentage();
                }

                // OS-level file handle monitoring only makes sense for Linux, where the Maximum system-wide number of handles the kernel will allocate is a user-configurable setting.
                // Windows does not have a configurable setting for Max Handles as the number of handles available to the system is dynamic (even if the max per process is not). 
                // As such, for Windows, GetMaximumConfiguredFileHandlesCount always return -1, by design. Also, GetTotalAllocatedFileHandlesCount is not implemented for Windows (just returns -1).
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (LinuxFileHandlesDataPercentAllocated != null && (LinuxFileHandlesErrorPercent > 0 || LinuxFileHandlesWarningPercent > 0))
                    {
                        float totalOpenFileHandles = OSInfoProvider.Instance.GetTotalAllocatedFileHandlesCount();

                        if (totalOpenFileHandles > 0)
                        {
                            int maximumConfiguredFDCount = OSInfoProvider.Instance.GetMaximumConfiguredFileHandlesCount();

                            if (maximumConfiguredFDCount > 0)
                            {
                                double usedPct = totalOpenFileHandles / maximumConfiguredFDCount * 100;
                                LinuxFileHandlesDataPercentAllocated.Data.Add(Math.Round(usedPct, 2));
                            }
                        }
                    }

                    if (LinuxFileHandlesDataTotalAllocated != null && (LinuxFileHandlesErrorTotalAllocated > 0 || LinuxFileHandlesWarningTotalAllocated > 0))
                    {
                        int totalOpenFileHandles = OSInfoProvider.Instance.GetTotalAllocatedFileHandlesCount();

                        if (totalOpenFileHandles > 0)
                        {
                            LinuxFileHandlesDataTotalAllocated.Data.Add(totalOpenFileHandles);
                        }
                    }
                }

                // Ports.
                if (ActivePortsData != null && (ActivePortsErrorThreshold > 0 || ActivePortsWarningThreshold > 0))
                {
                    int activePortCountTotal = OSInfoProvider.Instance.GetActiveTcpPortCount();
                    ActivePortsData.Data.Add(activePortCountTotal);
                }

                if (EphemeralPortsData != null && (EphemeralPortsErrorThreshold > 0 || EphemeralPortsWarningThreshold > 0))
                {
                    int ephemeralPortCountTotal = OSInfoProvider.Instance.GetActiveEphemeralPortCount();
                    EphemeralPortsData.Data.Add(ephemeralPortCountTotal);
                }

                timer.Start();

                while (timer.Elapsed <= duration)
                {
                    token.ThrowIfCancellationRequested();

                    // CPU
                    if (CpuTimeData != null && (CpuErrorUsageThresholdPct > 0 || CpuWarningUsageThresholdPct > 0))
                    {
                        CpuTimeData.Data.Add(CpuUtilizationProvider.Instance.GetProcessorTimePercentage());
                    }

                    // Memory
                    if (MemDataInUse != null || MemDataPercent != null)
                    {
                        var (TotalMemoryGb, MemoryInUseMb, PercentInUse) = OSInfoProvider.Instance.TupleGetMemoryInfo();

                        if (MemDataInUse != null && (MemErrorUsageThresholdMb > 0 || MemWarningUsageThresholdMb > 0))
                        {
                            MemDataInUse.Data.Add(MemoryInUseMb);
                        }

                        if (MemDataPercent != null && (MemoryErrorLimitPercent > 0 || MemoryWarningLimitPercent > 0))
                        {
                            MemDataPercent.Data.Add(PercentInUse);
                        }
                    }

                    await Task.Delay(250, Token).ConfigureAwait(true);
                }

                timer.Stop();
                timer.Reset();
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogWarning($"Unhandled exception in GetSystemCpuMemoryValuesAsync:{Environment.NewLine}{e}");

                // Fix the bug..
                throw;
            }
            finally
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    CpuUtilizationProvider.Instance?.Dispose();
                    CpuUtilizationProvider.Instance = null;
                }
            }
        }

        private void CleanUp()
        {
            if (ActivePortsData != null && !ActivePortsData.ActiveErrorOrWarning)
            {
                ActivePortsData = null;
            }

            if (CpuTimeData != null && !CpuTimeData.ActiveErrorOrWarning)
            {
                CpuTimeData = null;
            }

            if (EphemeralPortsData != null && !EphemeralPortsData.ActiveErrorOrWarning)
            {
                EphemeralPortsData = null;
            }

            if (MemDataInUse != null && !MemDataInUse.ActiveErrorOrWarning)
            {
                MemDataInUse = null;
            }

            if (MemDataPercent != null && !MemDataPercent.ActiveErrorOrWarning)
            {
                MemDataPercent = null;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && FirewallData != null && !FirewallData.ActiveErrorOrWarning)
            {
                FirewallData = null;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (LinuxFileHandlesDataPercentAllocated != null && !LinuxFileHandlesDataPercentAllocated.ActiveErrorOrWarning)
                {
                    LinuxFileHandlesDataPercentAllocated = null;
                }

                if (LinuxFileHandlesDataTotalAllocated != null && !LinuxFileHandlesDataTotalAllocated.ActiveErrorOrWarning)
                {
                    LinuxFileHandlesDataTotalAllocated = null;
                }
            }
        }
    }
}