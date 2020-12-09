// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
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
        private FabricResourceUsageData<float> allMemDataCommittedBytes;
        private FabricResourceUsageData<int> firewallData;
        private FabricResourceUsageData<int> activePortsData;
        private FabricResourceUsageData<int> ephemeralPortsData;
        private FabricResourceUsageData<int> allMemDataPercentUsed;

        // public because unit test.
        public FabricResourceUsageData<float> AllCpuTimeData
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

        public int MemoryErrorLimitPercent
        {
            get; set;
        }

        public int MemoryWarningLimitPercent
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
            if (RunInterval > TimeSpan.MinValue
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
                        Math.Round(AllCpuTimeData.AverageDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "CPU Time",
                        "Peak",
                        Math.Round(AllCpuTimeData.MaxDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "Committed Memory (MB)",
                        "Average",
                        Math.Round(this.allMemDataCommittedBytes.AverageDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "Committed Memory (MB)",
                        "Peak",
                        Math.Round(this.allMemDataCommittedBytes.MaxDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "All Active Ports",
                        "Total",
                        this.activePortsData.Data[0]);

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "Ephemeral Active Ports",
                        "Total",
                        this.ephemeralPortsData.Data[0]);

                    CsvFileLogger.LogData(
                        fileName,
                        NodeName,
                        "Firewall Rules",
                        "Total",
                        this.firewallData.Data[0]);

                    DataTableFileLogger.Flush();
                }

                // Report on the global health state (system-wide (node) metrics).
                // User-configurable in NodeObserver.config.json
                var timeToLiveWarning = SetHealthReportTimeToLive();

                // CPU
                if (AllCpuTimeData.AverageDataValue > 0)
                {
                    ProcessResourceDataReportHealth(
                        AllCpuTimeData,
                        CpuErrorUsageThresholdPct,
                        CpuWarningUsageThresholdPct,
                        timeToLiveWarning);
                }

                // Memory
                if (this.allMemDataCommittedBytes.AverageDataValue > 0)
                {
                    ProcessResourceDataReportHealth(
                        this.allMemDataCommittedBytes,
                        MemErrorUsageThresholdMb,
                        MemWarningUsageThresholdMb,
                        timeToLiveWarning);
                }

                if (this.allMemDataPercentUsed.AverageDataValue > 0)
                {
                    ProcessResourceDataReportHealth(
                        this.allMemDataPercentUsed,
                        MemoryErrorLimitPercent,
                        MemoryWarningLimitPercent,
                        timeToLiveWarning);
                }

                // Firewall rules
                ProcessResourceDataReportHealth(
                    this.firewallData,
                    FirewallRulesErrorThreshold,
                    FirewallRulesWarningThreshold,
                    timeToLiveWarning);

                // Ports
                ProcessResourceDataReportHealth(
                    this.activePortsData,
                    ActivePortsErrorThreshold,
                    ActivePortsWarningThreshold,
                    timeToLiveWarning);

                ProcessResourceDataReportHealth(
                    this.ephemeralPortsData,
                    EphemeralPortsErrorThreshold,
                    EphemeralPortsWarningThreshold,
                    timeToLiveWarning);

                return Task.FromResult(1);
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException)
                {
                    return Task.FromResult(1);
                }

                HealthReporter.ReportFabricObserverServiceHealth(
                    FabricServiceContext.ServiceName.OriginalString,
                    ObserverName,
                    HealthState.Warning,
                    e.ToString());

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
            if (AllCpuTimeData == null)
            {
                AllCpuTimeData = new FabricResourceUsageData<float>(ErrorWarningProperty.TotalCpuTime, "TotalCpuTime", DataCapacity, UseCircularBuffer);
            }

            if (this.allMemDataCommittedBytes == null)
            {
                this.allMemDataCommittedBytes = new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, "MemoryConsumedMb", DataCapacity, UseCircularBuffer);
            }

            if (this.allMemDataPercentUsed == null)
            {
                this.allMemDataPercentUsed = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalMemoryConsumptionPct, "MemoryConsumedPercentage", DataCapacity, UseCircularBuffer);
            }

            if (this.firewallData == null)
            {
                this.firewallData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActiveFirewallRules, "ActiveFirewallRules", 1);
            }

            if (this.activePortsData == null)
            {
                this.activePortsData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, "AllPortsInUse", 1);
            }

            if (this.ephemeralPortsData == null)
            {
                this.ephemeralPortsData = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, "EphemeralPortsInUse", 1);
            }
        }

        private void SetThresholdSFromConfiguration()
        {
            /* Error thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuError = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError) && int.TryParse(cpuError, out int cpuErrorUsageThresholdPct))
            {
                CpuErrorUsageThresholdPct = cpuErrorUsageThresholdPct;
            }

            var memError = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryErrorLimitMb);

            if (!string.IsNullOrEmpty(memError) && int.TryParse(memError, out int memErrorUsageThresholdMb))
            {
                MemErrorUsageThresholdMb = memErrorUsageThresholdMb;
            }

            var portsErr = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorActivePorts);

            if (!string.IsNullOrEmpty(portsErr) && !int.TryParse(portsErr, out int activePortsErrorThreshold))
            {
                ActivePortsErrorThreshold = activePortsErrorThreshold;
            }

            var ephemeralPortsErr = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorEphemeralPorts);

            if (!string.IsNullOrEmpty(portsErr) && int.TryParse(ephemeralPortsErr, out int ephemeralPortsErrorThreshold))
            {
                EphemeralPortsErrorThreshold = ephemeralPortsErrorThreshold;
            }

            var errFirewallRules = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorFirewallRules);

            if (!string.IsNullOrEmpty(errFirewallRules) && int.TryParse(errFirewallRules, out int firewallRulesErrorThreshold))
            {
                FirewallRulesErrorThreshold = firewallRulesErrorThreshold;
            }

            var errMemPercentUsed = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryUsePercentError);

            if (!string.IsNullOrEmpty(errMemPercentUsed) && int.TryParse(errMemPercentUsed, out int memoryPercentUsedErrorThreshold))
            {
                MemoryErrorLimitPercent = memoryPercentUsedErrorThreshold;
            }

            /* Warning thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn) && int.TryParse(cpuWarn, out int cpuWarningUsageThresholdPct))
            {
                CpuWarningUsageThresholdPct = cpuWarningUsageThresholdPct;
            }

            var memWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryWarningLimitMb);

            if (!string.IsNullOrEmpty(memWarn) && int.TryParse(memWarn, out int memWarningUsageThresholdMb))
            {
                MemWarningUsageThresholdMb = memWarningUsageThresholdMb;
            }

            var portsWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningActivePorts);

            if (!string.IsNullOrEmpty(portsWarn) && int.TryParse(portsWarn, out int activePortsWarningThreshold))
            {
                ActivePortsWarningThreshold = activePortsWarningThreshold;
            }

            var ephemeralPortsWarn = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningEphemeralPorts);

            if (!string.IsNullOrEmpty(ephemeralPortsWarn) && int.TryParse(ephemeralPortsWarn, out int ephemeralPortsWarningThreshold))
            {
                EphemeralPortsWarningThreshold = ephemeralPortsWarningThreshold;
            }

            var warnFirewallRules = GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningFirewallRules);

            if (!string.IsNullOrEmpty(warnFirewallRules) && int.TryParse(warnFirewallRules, out int firewallRulesWarningThreshold))
            {
                FirewallRulesWarningThreshold = firewallRulesWarningThreshold;
            }

            var warnMemPercentUsed = GetSettingParameterValue(
              ConfigurationSectionName,
              ObserverConstants.NodeObserverMemoryUsePercentWarning);

            if (!string.IsNullOrEmpty(warnMemPercentUsed) && int.TryParse(warnMemPercentUsed, out int memoryPercentUsedWarningThreshold))
            {
                MemoryWarningLimitPercent = memoryPercentUsedWarningThreshold;
            }
        }

        private async Task GetSystemCpuMemoryValuesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            CpuUtilizationProvider cpuUtilizationProvider = null;

            try
            {
                // Ports.
                int activePortCountTotal = OperatingSystemInfoProvider.Instance.GetActivePortCount();
                int ephemeralPortCountTotal = OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount();
                this.activePortsData.Data.Add(activePortCountTotal);
                this.ephemeralPortsData.Data.Add(ephemeralPortCountTotal);

                // Firewall rules.
                int firewalls = NetworkUsage.GetActiveFirewallRulesCount();
                this.firewallData.Data.Add(firewalls);

                // CPU and Memory.
                // Note: Please make sure you understand the normal state of your nodes
                // with respect to the machine resource use and/or abuse by your service(s).
                // For example, if it is normal for your services to consume 90% of available CPU and memory
                // as part of the work they perform under normal traffic flow, then it doesn't make sense to warn or
                // error on these conditions.
                // TODO: Look into making this a long running background task with signaling.
                TimeSpan duration = TimeSpan.FromSeconds(15);

                if (MonitorDuration > TimeSpan.MinValue)
                {
                    duration = MonitorDuration;
                }

                cpuUtilizationProvider = CpuUtilizationProvider.Create();

                // Warn up the counters.
                _ = await cpuUtilizationProvider.NextValueAsync();

                while (this.stopwatch.Elapsed <= duration)
                {
                    token.ThrowIfCancellationRequested();

                    if (CpuWarningUsageThresholdPct > 0
                        && CpuWarningUsageThresholdPct <= 100)
                    {
                        AllCpuTimeData.Data.Add(await cpuUtilizationProvider.NextValueAsync());
                    }

                    if (MemWarningUsageThresholdMb > 0)
                    {
                        float committedMegaBytes = MemoryUsageProvider.Instance.GetCommittedBytes() / 1048576.0f;
                        this.allMemDataCommittedBytes.Data.Add(committedMegaBytes);
                    }

                    if (MemoryWarningLimitPercent > 0)
                    {
                        this.allMemDataPercentUsed.Data.Add(
                            OperatingSystemInfoProvider.Instance.TupleGetTotalPhysicalMemorySizeAndPercentInUse().PercentInUse);
                    }

                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
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