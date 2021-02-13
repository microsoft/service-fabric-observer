// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
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
        public FabricResourceUsageData<float> MemDataCommittedBytes
        {
            get; set;
        }

        public FabricResourceUsageData<int> FirewallData
        {
            get; set;
        }

        public FabricResourceUsageData<int> ActivePortsData
        {
            get; set;
        }

        public FabricResourceUsageData<int> EphemeralPortsData
        {
            get; set;
        }

        public FabricResourceUsageData<double> MemDataPercentUsed
        {
            get; set;
        }

        public FabricResourceUsageData<float> CpuTimeData
        {
            get; set;
        }

        // These are only useful for Linux.\\

        // Holds data for percentage of total configured file descriptors that are in use.
        public FabricResourceUsageData<double> LinuxFileHandlesDataPercentAllocated
        {
            get; set;
        }

        // TODO: Holds raw number of allocated file descriptors.
        public FabricResourceUsageData<int> LinuxFileDescriptorDataRawNumberAllocated
        {
            get; set;
        }

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

        // TODO..
        public int LinuxFileHandlesErrorCount
        {
            get; set;
        }

        // TODO..
        public int LinuxFileHandlesWarningCount
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NodeObserver"/> class.
        /// </summary>
        public NodeObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            this.stopwatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            this.stopwatch.Start();

            Initialize();

            Token = token;

            await GetSystemCpuMemoryValuesAsync(token).ConfigureAwait(true);

            this.stopwatch.Stop();
            RunDuration = this.stopwatch.Elapsed;
            this.stopwatch.Reset();

            await ReportAsync(token).ConfigureAwait(true);

            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                if (CsvFileLogger != null && CsvFileLogger.EnableCsvLogging)
                {
                    var fileName = "CpuMemFirewallsPorts" + NodeName;

                    // Log (csv) system-wide CPU/Mem data.
                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "CPU Time",
                        "Average",
                        Math.Round(CpuTimeData.AverageDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "CPU Time",
                        "Peak",
                        Math.Round(CpuTimeData.MaxDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "Committed Memory (MB)",
                        "Average",
                        Math.Round(this.MemDataCommittedBytes.AverageDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "Committed Memory (MB)",
                        "Peak",
                        Math.Round(this.MemDataCommittedBytes.MaxDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "All Active Ports",
                        "Total",
                        this.ActivePortsData.Data[0]);

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "Ephemeral Active Ports",
                        "Total",
                        this.EphemeralPortsData.Data[0]);

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "Firewall Rules",
                        "Total",
                        this.FirewallData.Data[0]);

                    DataTableFileLogger.Flush();
                }

                // Report on the global health state (system-wide (node) metrics).
                // User-configurable in NodeObserver.config.json
                var timeToLiveWarning = SetHealthReportTimeToLive();

                // CPU
                if (this.CpuTimeData != null && (this.CpuErrorUsageThresholdPct > 0 || this.CpuWarningUsageThresholdPct > 0))
                {
                    ProcessResourceDataReportHealth(
                        this.CpuTimeData,
                        this.CpuErrorUsageThresholdPct,
                        this.CpuWarningUsageThresholdPct,
                        timeToLiveWarning);
                }

                // Memory - MB
                if (this.MemDataCommittedBytes != null && (this.MemErrorUsageThresholdMb > 0 || this.MemWarningUsageThresholdMb > 0))
                {
                    ProcessResourceDataReportHealth(
                        this.MemDataCommittedBytes,
                        this.MemErrorUsageThresholdMb,
                        this.MemWarningUsageThresholdMb,
                        timeToLiveWarning);
                }

                // Memory - Percent
                if (this.MemDataPercentUsed != null && (this.MemoryErrorLimitPercent > 0 || this.MemoryWarningLimitPercent > 0))
                {
                    ProcessResourceDataReportHealth<double>(
                        this.MemDataPercentUsed,
                        this.MemoryErrorLimitPercent,
                        this.MemoryWarningLimitPercent,
                        timeToLiveWarning);
                }

                // Firewall rules
                if (this.FirewallData != null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (this.FirewallRulesErrorThreshold > 0 || this.FirewallRulesWarningThreshold > 0))
                {
                    ProcessResourceDataReportHealth(
                        this.FirewallData,
                        this.FirewallRulesErrorThreshold,
                        this.FirewallRulesWarningThreshold,
                        timeToLiveWarning);
                }

                // Ports - Active TCP
                if (this.ActivePortsData != null && (this.ActivePortsErrorThreshold > 0 || this.ActivePortsWarningThreshold > 0))
                {
                    ProcessResourceDataReportHealth(
                        this.ActivePortsData,
                        this.ActivePortsErrorThreshold,
                        this.ActivePortsWarningThreshold,
                        timeToLiveWarning);
                }

                // Ports - Active Ephemeral TCP
                if (this.EphemeralPortsData != null && (this.EphemeralPortsErrorThreshold > 0 || this.EphemeralPortsWarningThreshold > 0))
                {
                    ProcessResourceDataReportHealth(
                        this.EphemeralPortsData,
                        this.EphemeralPortsErrorThreshold,
                        this.EphemeralPortsWarningThreshold,
                        timeToLiveWarning);
                }

                // File Handles (Linux) - Percent Allocated (in use) of configured Maximum File Handles.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (this.LinuxFileHandlesDataPercentAllocated != null && (this.LinuxFileHandlesErrorPercent > 0 || this.LinuxFileHandlesWarningPercent > 0))
                    {
                        ProcessResourceDataReportHealth(
                            this.LinuxFileHandlesDataPercentAllocated,
                            this.LinuxFileHandlesErrorPercent,
                            this.LinuxFileHandlesWarningPercent,
                            timeToLiveWarning);
                    }
                }

                return Task.CompletedTask;
            }
            catch (AggregateException e) when (e.InnerException is OperationCanceledException || e.InnerException is TaskCanceledException || e.InnerException is TimeoutException)
            {
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                HealthReporter.ReportFabricObserverServiceHealth(
                    FabricServiceContext.ServiceName.OriginalString,
                    ObserverName,
                    HealthState.Warning,
                    $"Unhandled exception re-thrown:{Environment.NewLine}{e}");

                throw;
            }
        }

        private void Initialize()
        {
            if (!IsTestRun)
            {
                SetThresholdSFromConfiguration();
            }

            InitializeDataContainers();
        }

        private void InitializeDataContainers()
        {
            if (this.CpuTimeData == null && (this.CpuErrorUsageThresholdPct > 0 || this.CpuWarningUsageThresholdPct > 0))
            {
                this.CpuTimeData = new FabricResourceUsageData<float>(ErrorWarningProperty.TotalCpuTime, "TotalCpuTime", DataCapacity, UseCircularBuffer);
            }

            if (this.MemDataCommittedBytes == null && (this.MemErrorUsageThresholdMb > 0 || this.MemWarningUsageThresholdMb > 0))
            {
                this.MemDataCommittedBytes = new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, "MemoryConsumedMb", DataCapacity, UseCircularBuffer);
            }

            if (this.MemDataPercentUsed == null && (this.MemoryErrorLimitPercent > 0 || this.MemoryWarningLimitPercent > 0))
            {
                this.MemDataPercentUsed = new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, "MemoryConsumedPercentage", DataCapacity, UseCircularBuffer);
            }

            if (this.FirewallData == null && (this.FirewallRulesErrorThreshold > 0 || this.FirewallRulesWarningThreshold > 0))
            {
                this.FirewallData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActiveFirewallRules, "ActiveFirewallRules", 1);
            }

            if (this.ActivePortsData == null && (this.ActivePortsErrorThreshold > 0 || this.ActivePortsWarningThreshold > 0))
            {
                this.ActivePortsData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, "AllPortsInUse", 1);
            }

            if (this.EphemeralPortsData == null && (this.EphemeralPortsErrorThreshold > 0 || this.EphemeralPortsWarningThreshold > 0))
            {
                this.EphemeralPortsData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, "EphemeralPortsInUse", 1);
            }

            // This only makes sense for Linux.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (this.LinuxFileHandlesDataPercentAllocated == null && (this.LinuxFileHandlesErrorPercent > 0 || this.LinuxFileHandlesWarningPercent > 0))
                {
                    this.LinuxFileHandlesDataPercentAllocated = new FabricResourceUsageData<double>(ErrorWarningProperty.TotalFileHandlesPct, "TotalFileHandlesPercentage", 1);
                }
            }
        }

        private void SetThresholdSFromConfiguration()
        {
            /* Error thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuError = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError) && float.TryParse(cpuError, out float cpuErrorUsageThresholdPct))
            {
                if (cpuErrorUsageThresholdPct > 0 && cpuErrorUsageThresholdPct <= 100)
                {
                    this.CpuErrorUsageThresholdPct = cpuErrorUsageThresholdPct;
                }
            }

            var memError = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryErrorLimitMb);

            if (!string.IsNullOrEmpty(memError) && int.TryParse(memError, out int memErrorUsageThresholdMb))
            {
                this.MemErrorUsageThresholdMb = memErrorUsageThresholdMb;
            }

            var portsErr = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorActivePorts);

            if (!string.IsNullOrEmpty(portsErr) && !int.TryParse(portsErr, out int activePortsErrorThreshold))
            {
                this.ActivePortsErrorThreshold = activePortsErrorThreshold;
            }

            var ephemeralPortsErr = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorEphemeralPorts);

            if (!string.IsNullOrEmpty(portsErr) && int.TryParse(ephemeralPortsErr, out int ephemeralPortsErrorThreshold))
            {
                this.EphemeralPortsErrorThreshold = ephemeralPortsErrorThreshold;
            }

            var errFirewallRules = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorFirewallRules);

            if (!string.IsNullOrEmpty(errFirewallRules) && int.TryParse(errFirewallRules, out int firewallRulesErrorThreshold))
            {
                this.FirewallRulesErrorThreshold = firewallRulesErrorThreshold;
            }

            var errMemPercentUsed = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryUsePercentError);

            if (!string.IsNullOrEmpty(errMemPercentUsed) && double.TryParse(errMemPercentUsed, out double memoryPercentUsedErrorThreshold))
            {
                if (memoryPercentUsedErrorThreshold > 0 && memoryPercentUsedErrorThreshold <= 100)
                {
                    this.MemoryErrorLimitPercent = memoryPercentUsedErrorThreshold;
                }
            }

            // Linux FDs.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var errFileHandlesPercentUsed = GetSettingParameterValue(
                    ConfigurationSectionName,
                    ObserverConstants.NodeObserverLinuxFileHandlesErrorLimitPct);

                if (!string.IsNullOrEmpty(errFileHandlesPercentUsed) && double.TryParse(errFileHandlesPercentUsed, out double fileDescriptorsPercentUsedErrorThreshold))
                {
                    if (fileDescriptorsPercentUsedErrorThreshold > 0 && fileDescriptorsPercentUsedErrorThreshold <= 100)
                    {
                        this.LinuxFileHandlesErrorPercent = fileDescriptorsPercentUsedErrorThreshold;
                    }
                }
            }

            /* Warning thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn) && int.TryParse(cpuWarn, out int cpuWarningUsageThresholdPct))
            {
                if (cpuWarningUsageThresholdPct > 0 && cpuWarningUsageThresholdPct <= 100)
                {
                    this.CpuWarningUsageThresholdPct = cpuWarningUsageThresholdPct;
                }
            }

            var memWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryWarningLimitMb);

            if (!string.IsNullOrEmpty(memWarn) && int.TryParse(memWarn, out int memWarningUsageThresholdMb))
            {
                this.MemWarningUsageThresholdMb = memWarningUsageThresholdMb;
            }

            var portsWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningActivePorts);

            if (!string.IsNullOrEmpty(portsWarn) && int.TryParse(portsWarn, out int activePortsWarningThreshold))
            {
                this.ActivePortsWarningThreshold = activePortsWarningThreshold;
            }

            var ephemeralPortsWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningEphemeralPorts);

            if (!string.IsNullOrEmpty(ephemeralPortsWarn) && int.TryParse(ephemeralPortsWarn, out int ephemeralPortsWarningThreshold))
            {
                this.EphemeralPortsWarningThreshold = ephemeralPortsWarningThreshold;
            }

            var warnFirewallRules = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningFirewallRules);

            if (!string.IsNullOrEmpty(warnFirewallRules) && int.TryParse(warnFirewallRules, out int firewallRulesWarningThreshold))
            {
                this.FirewallRulesWarningThreshold = firewallRulesWarningThreshold;
            }

            var warnMemPercentUsed = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryUsePercentWarning);

            if (!string.IsNullOrEmpty(warnMemPercentUsed) && double.TryParse(warnMemPercentUsed, out double memoryPercentUsedWarningThreshold))
            {
                if (memoryPercentUsedWarningThreshold > 0 && memoryPercentUsedWarningThreshold <= 100)
                {
                    this.MemoryWarningLimitPercent = memoryPercentUsedWarningThreshold;
                }
            }

            // Linux FDs.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var warnFileHandlesPercentUsed = GetSettingParameterValue(
                    ConfigurationSectionName,
                    ObserverConstants.NodeObserverLinuxFileHandlesWarningLimitPct);

                if (!string.IsNullOrEmpty(warnFileHandlesPercentUsed) && double.TryParse(warnFileHandlesPercentUsed, out double fileDescriptorsPercentUsedWarningThreshold))
                {
                    if (fileDescriptorsPercentUsedWarningThreshold > 0 && fileDescriptorsPercentUsedWarningThreshold <= 100)
                    {
                        this.LinuxFileHandlesWarningPercent = fileDescriptorsPercentUsedWarningThreshold;
                    }
                }
            }
        }

        private async Task GetSystemCpuMemoryValuesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            CpuUtilizationProvider cpuUtilizationProvider = null;

            try
            {
                // Ports.
                if (this.ActivePortsData != null && (this.ActivePortsErrorThreshold > 0 || this.ActivePortsWarningThreshold > 0))
                {
                    int activePortCountTotal = OperatingSystemInfoProvider.Instance.GetActivePortCount();
                    this.ActivePortsData.Data.Add(activePortCountTotal);
                }

                if (this.EphemeralPortsData != null && (this.EphemeralPortsErrorThreshold > 0 || this.EphemeralPortsWarningThreshold > 0))
                {
                    int ephemeralPortCountTotal = OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount();
                    this.EphemeralPortsData.Data.Add(ephemeralPortCountTotal);
                }

                // Firewall rules.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && this.FirewallData != null)
                {
                    int firewalls = NetworkUsage.GetActiveFirewallRulesCount();
                    this.FirewallData.Data.Add(firewalls);
                }

                TimeSpan duration = TimeSpan.FromSeconds(10);

                if (this.MonitorDuration > TimeSpan.MinValue)
                {
                    duration = MonitorDuration;
                }

                /* CPU, Memory, File Descriptors (Linux)
                 
                   Note: Please make sure you understand the normal state of your nodes
                   with respect to the machine resource use and/or abuse by your service(s).
                   For example, if it is normal for your services to consume 90% of available CPU and memory
                   as part of the work they perform under normal traffic flow, then it doesn't make sense to warn or
                   error on these conditions. */

                if (this.CpuTimeData != null && (this.CpuErrorUsageThresholdPct > 0 || this.CpuWarningUsageThresholdPct > 0))
                {
                    cpuUtilizationProvider = CpuUtilizationProvider.Create();

                    // Warm up the counter.
                    _ = await cpuUtilizationProvider.NextValueAsync();
                }

                // VM-level file handle monitoring only makes sense for Linux, where Maximum number of handles is a configuration setting.
                // Windows does not have a configurable setting for Max Handles as the number of handles available to the system is dynamic (even if the max per process is not). 
                // As such, for Windows, GetMaximumConfiguredFileHandlesCount always return -1, by design. Also, GetTotalAllocatedFileHandlesCount is not implemented for Windows (just returns -1).
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (this.LinuxFileHandlesDataPercentAllocated != null && (this.LinuxFileHandlesErrorPercent > 0 || this.LinuxFileHandlesWarningPercent > 0))
                    {
                        float totalOpenFileHandles = OperatingSystemInfoProvider.Instance.GetTotalAllocatedFileHandlesCount();

                        if (totalOpenFileHandles > 0)
                        {
                            int maximumConfiguredFDCount = OperatingSystemInfoProvider.Instance.GetMaximumConfiguredFileHandlesCount();

                            if (maximumConfiguredFDCount > 0)
                            {
                                double usedPct = totalOpenFileHandles / maximumConfiguredFDCount * 100;
                                this.LinuxFileHandlesDataPercentAllocated.Data.Add(Math.Round(usedPct, 2));
                            }
                        }
                    } 
                }

                while (this.stopwatch.Elapsed <= duration)
                {
                    token.ThrowIfCancellationRequested();

                    if (this.CpuTimeData != null && (this.CpuErrorUsageThresholdPct > 0 || this.CpuWarningUsageThresholdPct > 0))
                    {
                        this.CpuTimeData.Data.Add(await cpuUtilizationProvider.NextValueAsync());
                    }

                    if (this.MemDataCommittedBytes != null && (this.MemErrorUsageThresholdMb > 0 || this.MemWarningUsageThresholdMb > 0))
                    {
                        float committedMegaBytes = MemoryUsageProvider.Instance.GetCommittedBytes() / 1048576.0f;
                        this.MemDataCommittedBytes.Data.Add(committedMegaBytes);
                    }

                    if (this.MemDataPercentUsed != null && (this.MemoryErrorLimitPercent > 0 || this.MemoryWarningLimitPercent > 0))
                    {
                        this.MemDataPercentUsed.Data.Add(OperatingSystemInfoProvider.Instance.TupleGetTotalPhysicalMemorySizeAndPercentInUse().PercentInUse);
                    }

                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
            catch (AggregateException e) when (e.InnerException is OperationCanceledException || e.InnerException is TaskCanceledException || e.InnerException is TimeoutException)
            {
                return;
            }
            catch (Exception e)
            {
                HealthReporter.ReportFabricObserverServiceHealth(
                        FabricServiceContext.ServiceName.OriginalString,
                        ObserverName,
                        HealthState.Warning,
                        $"Unhandled exception in GetSystemCpuMemoryValuesAsync:{Environment.NewLine}{e}");

                throw;
            }
            finally
            {
                cpuUtilizationProvider?.Dispose();
            }
        }
    }
}