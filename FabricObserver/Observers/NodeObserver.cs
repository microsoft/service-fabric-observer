// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
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
        public FabricResourceUsageData<float> AllCpuTimeData { get; set; }

        public int CpuErrorUsageThresholdPct { get; set; }

        public int MemErrorUsageThresholdMb { get; set; }

        public int CpuWarningUsageThresholdPct { get; set; }

        public int MemWarningUsageThresholdMb { get; set; }

        public int ActivePortsErrorThreshold { get; set; }

        public int EphemeralPortsErrorThreshold { get; set; }

        public int FirewallRulesErrorThreshold { get; set; }

        public int ActivePortsWarningThreshold { get; set; }

        public int EphemeralPortsWarningThreshold { get; set; }

        public int FirewallRulesWarningThreshold { get; set; }

        public int MemoryErrorLimitPercent { get; set; }

        public int MemoryWarningLimitPercent { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="NodeObserver"/> class.
        /// </summary>
        public NodeObserver()
        {
            this.stopwatch = new Stopwatch();
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

            this.stopwatch.Start();

            this.Initialize();

            this.Token = token;

            await this.GetSystemCpuMemoryValuesAsync(token).ConfigureAwait(true);

            this.stopwatch.Stop();
            this.RunDuration = this.stopwatch.Elapsed;
            this.stopwatch.Reset();

            await this.ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
        }

        /// <inheritdoc/>
        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                if (this.CsvFileLogger != null && this.CsvFileLogger.EnableCsvLogging)
                {
                    var fileName = "CpuMemFirewallsPorts" + this.NodeName;

                    // Log (csv) system-wide CPU/Mem data.
                    this.CsvFileLogger.LogData(
                        fileName,
                        this.NodeName,
                        "CPU Time",
                        "Average",
                        Math.Round(this.AllCpuTimeData.AverageDataValue));

                    this.CsvFileLogger.LogData(
                        fileName,
                        this.NodeName,
                        "CPU Time",
                        "Peak",
                        Math.Round(this.AllCpuTimeData.MaxDataValue));

                    this.CsvFileLogger.LogData(
                        fileName,
                        this.NodeName,
                        "Committed Memory (MB)",
                        "Average",
                        Math.Round(this.allMemDataCommittedBytes.AverageDataValue));

                    this.CsvFileLogger.LogData(
                        fileName,
                        this.NodeName,
                        "Committed Memory (MB)",
                        "Peak",
                        Math.Round(this.allMemDataCommittedBytes.MaxDataValue));

                    this.CsvFileLogger.LogData(
                        fileName,
                        this.NodeName,
                        "All Active Ports",
                        "Total",
                        this.activePortsData.Data[0]);

                    this.CsvFileLogger.LogData(
                        fileName,
                        this.NodeName,
                        "Ephemeral Active Ports",
                        "Total",
                        this.ephemeralPortsData.Data[0]);

                    this.CsvFileLogger.LogData(
                        fileName,
                        this.NodeName,
                        "Firewall Rules",
                        "Total",
                        this.firewallData.Data[0]);

                    DataTableFileLogger.Flush();
                }

                // Report on the global health state (system-wide (node) metrics).
                // User-configurable in NodeObserver.config.json
                var timeToLiveWarning = this.SetHealthReportTimeToLive();

                // CPU
                if (this.AllCpuTimeData.AverageDataValue > 0)
                {
                    this.ProcessResourceDataReportHealth(
                        this.AllCpuTimeData,
                        this.CpuErrorUsageThresholdPct,
                        this.CpuWarningUsageThresholdPct,
                        timeToLiveWarning);
                }

                // Memory
                if (this.allMemDataCommittedBytes.AverageDataValue > 0)
                {
                    this.ProcessResourceDataReportHealth(
                        this.allMemDataCommittedBytes,
                        this.MemErrorUsageThresholdMb,
                        this.MemWarningUsageThresholdMb,
                        timeToLiveWarning);
                }

                if (this.allMemDataPercentUsed.AverageDataValue > 0)
                {
                    this.ProcessResourceDataReportHealth(
                        this.allMemDataPercentUsed,
                        this.MemoryErrorLimitPercent,
                        this.MemoryWarningLimitPercent,
                        timeToLiveWarning);
                }

                // Firewall rules
                this.ProcessResourceDataReportHealth(
                    this.firewallData,
                    this.FirewallRulesErrorThreshold,
                    this.FirewallRulesWarningThreshold,
                    timeToLiveWarning);

                // Ports
                this.ProcessResourceDataReportHealth(
                    this.activePortsData,
                    this.ActivePortsErrorThreshold,
                    this.ActivePortsWarningThreshold,
                    timeToLiveWarning);

                this.ProcessResourceDataReportHealth(
                    this.ephemeralPortsData,
                    this.EphemeralPortsErrorThreshold,
                    this.EphemeralPortsWarningThreshold,
                    timeToLiveWarning);

                return Task.FromResult(1);
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException)
                {
                    return Task.FromResult(1);
                }

                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    e.ToString());

                throw;
            }
        }

        private void Initialize()
        {
            if (!IsTestRun)
            {
                this.SetThresholdSFromConfiguration();
            }

            this.InitializeDataContainers();
        }

        private void InitializeDataContainers()
        {
            if (this.AllCpuTimeData == null)
            {
                this.AllCpuTimeData = new FabricResourceUsageData<float>(ErrorWarningProperty.TotalCpuTime, "TotalCpuTime", this.DataCapacity, this.UseCircularBuffer);
            }

            if (this.allMemDataCommittedBytes == null)
            {
                this.allMemDataCommittedBytes = new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, "MemoryConsumedMb", this.DataCapacity, this.UseCircularBuffer);
            }

            if (this.allMemDataPercentUsed == null)
            {
                this.allMemDataPercentUsed = new FabricResourceUsageData<int>(ErrorWarningProperty.TotalMemoryConsumptionPct, "MemoryConsumedPercentage", this.DataCapacity, this.UseCircularBuffer);
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

            this.Token.ThrowIfCancellationRequested();

            var cpuError = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError) && int.TryParse(cpuError, out int cpuErrorUsageThresholdPct))
            {
                this.CpuErrorUsageThresholdPct = cpuErrorUsageThresholdPct;
            }

            var memError = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryErrorLimitMb);

            if (!string.IsNullOrEmpty(memError) && int.TryParse(memError, out int memErrorUsageThresholdMb))
            {
                this.MemErrorUsageThresholdMb = memErrorUsageThresholdMb;
            }

            var portsErr = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorActivePorts);

            if (!string.IsNullOrEmpty(portsErr) && !int.TryParse(portsErr, out int activePortsErrorThreshold))
            {
                this.ActivePortsErrorThreshold = activePortsErrorThreshold;
            }

            var ephemeralPortsErr = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorEphemeralPorts);

            if (!string.IsNullOrEmpty(portsErr) && int.TryParse(ephemeralPortsErr, out int ephemeralPortsErrorThreshold))
            {
                this.EphemeralPortsErrorThreshold = ephemeralPortsErrorThreshold;
            }

            var errFirewallRules = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkErrorFirewallRules);

            if (!string.IsNullOrEmpty(errFirewallRules) && int.TryParse(errFirewallRules, out int firewallRulesErrorThreshold))
            {
                this.FirewallRulesErrorThreshold = firewallRulesErrorThreshold;
            }

            var errMemPercentUsed = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryUsePercentError);

            if (!string.IsNullOrEmpty(errMemPercentUsed) && int.TryParse(errMemPercentUsed, out int memoryPercentUsedErrorThreshold))
            {
                this.MemoryErrorLimitPercent = memoryPercentUsedErrorThreshold;
            }

            /* Warning thresholds */

            this.Token.ThrowIfCancellationRequested();

            var cpuWarn = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn) && int.TryParse(cpuWarn, out int cpuWarningUsageThresholdPct))
            {
                this.CpuWarningUsageThresholdPct = cpuWarningUsageThresholdPct;
            }

            var memWarn = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverMemoryWarningLimitMb);

            if (!string.IsNullOrEmpty(memWarn) && int.TryParse(memWarn, out int memWarningUsageThresholdMb))
            {
                this.MemWarningUsageThresholdMb = memWarningUsageThresholdMb;
            }

            var portsWarn = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningActivePorts);

            if (!string.IsNullOrEmpty(portsWarn) && int.TryParse(portsWarn, out int activePortsWarningThreshold))
            {
                this.ActivePortsWarningThreshold = activePortsWarningThreshold;
            }

            var ephemeralPortsWarn = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningEphemeralPorts);

            if (!string.IsNullOrEmpty(ephemeralPortsWarn) && int.TryParse(ephemeralPortsWarn, out int ephemeralPortsWarningThreshold))
            {
                this.EphemeralPortsWarningThreshold = ephemeralPortsWarningThreshold;
            }

            var warnFirewallRules = this.GetSettingParameterValue(
                ObserverConstants.NodeObserverConfigurationSectionName,
                ObserverConstants.NodeObserverNetworkWarningFirewallRules);

            if (!string.IsNullOrEmpty(warnFirewallRules) && int.TryParse(warnFirewallRules, out int firewallRulesWarningThreshold))
            {
                this.FirewallRulesWarningThreshold = firewallRulesWarningThreshold;
            }

            var warnMemPercentUsed = this.GetSettingParameterValue(
              ObserverConstants.NodeObserverConfigurationSectionName,
              ObserverConstants.NodeObserverMemoryUsePercentWarning);

            if (!string.IsNullOrEmpty(warnMemPercentUsed) && int.TryParse(warnMemPercentUsed, out int memoryPercentUsedWarningThreshold))
            {
                this.MemoryWarningLimitPercent = memoryPercentUsedWarningThreshold;
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
                TimeSpan duration = TimeSpan.FromSeconds(30);

                if (this.MonitorDuration > TimeSpan.MinValue)
                {
                    duration = this.MonitorDuration;
                }

                cpuUtilizationProvider = CpuUtilizationProvider.Create();

                // Warn up the counters.
                _ = await cpuUtilizationProvider.NextValueAsync();

                while (this.stopwatch.Elapsed <= duration)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(500);

                    if (this.CpuWarningUsageThresholdPct > 0
                        && this.CpuWarningUsageThresholdPct <= 100)
                    {
                        this.AllCpuTimeData.Data.Add(await cpuUtilizationProvider.NextValueAsync());
                    }

                    if (this.MemWarningUsageThresholdMb > 0)
                    {
                        float committedMegaBytes = MemoryUsageProvider.Instance.GetCommittedBytes() / 1048576.0f;
                        this.allMemDataCommittedBytes.Data.Add(committedMegaBytes);
                    }

                    if (this.MemoryWarningLimitPercent > 0)
                    {
                        this.allMemDataPercentUsed.Data.Add(
                            OperatingSystemInfoProvider.Instance.TupleGetTotalPhysicalMemorySizeAndPercentInUse().PercentInUse);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.OriginalString,
                        this.ObserverName,
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