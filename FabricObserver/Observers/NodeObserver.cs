// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Utilities;
using System;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver
{
    // This observer monitors VM level resource usage across CPU and Memory, and reports port and firewall rule counts.
    // Thresholds for Erorr and Warning signals are user-supplied in NodeObserver.config.json.
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    public class NodeObserver : ObserverBase
    {
        private FabricResourceUsageData<float> allCpuDataPrivTime = null;
        private FabricResourceUsageData<float> allMemDataCommittedBytes = null;
        private FabricResourceUsageData<int> firewallData = null;
        private FabricResourceUsageData<int> activePortsData = null;
        private FabricResourceUsageData<int> ephemeralPortsData = null;
        private WindowsPerfCounters perfCounters;

        public int CpuErrorUsageThresholdPct { get; set; }
        public int MemErrorUsageThresholdMB { get; set; }
        public int CpuWarningUsageThresholdPct { get; set; }
        public int MemWarningUsageThresholdMB { get; set; }
        public int ActivePortsErrorThreshold { get; set; }
        public int EphemeralPortsErrorThreshold { get; set; }
        public int FirewallRulesErrorThreshold { get; set; }
        public int ActivePortsWarningThreshold { get; set; }
        public int EphemeralPortsWarningThreshold { get; set; }
        public int FirewallRulesWarningThreshold { get; set; }

        public NodeObserver() : base(ObserverConstants.NodeObserverName) { }

        public override async Task ObserveAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            Initialize();

            this.Token = token;

            try
            {
                this.perfCounters = new WindowsPerfCounters();
                await GetSystemCpuMemoryValuesAsync(token).ConfigureAwait(true);
                await ReportAsync(token).ConfigureAwait(true);
                LastRunDateTime = DateTime.Now;
            }
            finally
            {
                // Clean up...
                this.perfCounters?.Dispose();
                this.perfCounters = null;
            }
        }

        private void Initialize()
        {
            if (!IsTestRun)
            {
                SetThresholdsFromConfiguration();
            }
            InitializeDataContainers();
        }

        private void InitializeDataContainers()
        {
            if (this.allCpuDataPrivTime == null)
            {
                this.allCpuDataPrivTime = new FabricResourceUsageData<float>("Total CPU Time", "SysCpuTimePct");
            }

            if (this.allMemDataCommittedBytes == null)
            {
                this.allMemDataCommittedBytes = new FabricResourceUsageData<float>("Total Committed Memory", "SysMemoryCommittedMb");
            }

            if (this.firewallData == null)
            {
                this.firewallData = new FabricResourceUsageData<int>("Active Firewall Rules", "ActiveFirewallRules");
            }

            if (this.activePortsData == null)
            {
                this.activePortsData = new FabricResourceUsageData<int>("All Active Ports", "AllPortsInUse");
            }

            if (this.ephemeralPortsData == null)
            {
                this.ephemeralPortsData = new FabricResourceUsageData<int>("Ephemeral Active Ports", "EphemeralPortsInUse");
            }
        }

        private void SetThresholdsFromConfiguration()
        {
            /* Error thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuError = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                    ObserverConstants.NodeObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError) && int.TryParse(cpuError, out int cpuErrorUsageThresholdPct))
            {
               CpuErrorUsageThresholdPct = cpuErrorUsageThresholdPct;
            }

            var memError = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                    ObserverConstants.NodeObserverMemoryErrorLimitMB);

            if (!string.IsNullOrEmpty(memError) && int.TryParse(memError, out int memErrorUsageThresholdMB))
            {
                MemErrorUsageThresholdMB = memErrorUsageThresholdMB;
            }

            var portsErr = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                    ObserverConstants.NodeObserverNetworkErrorActivePorts);

            if (!string.IsNullOrEmpty(portsErr) && !int.TryParse(portsErr, out int activePortsErrorThreshold))
            {
                ActivePortsErrorThreshold = activePortsErrorThreshold;
            }

            var ephemeralPortsErr = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                             ObserverConstants.NodeObserverNetworkErrorEphemeralPorts);

            if (!string.IsNullOrEmpty(portsErr) && int.TryParse(ephemeralPortsErr, out int ephemeralPortsErrorThreshold))
            {
                EphemeralPortsErrorThreshold = ephemeralPortsErrorThreshold;
            }

            var errFirewallRules = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                            ObserverConstants.NodeObserverNetworkErrorFirewallRules);

            if (!string.IsNullOrEmpty(errFirewallRules) && int.TryParse(errFirewallRules, out int firewallRulesErrorThreshold))
            {
                FirewallRulesErrorThreshold = firewallRulesErrorThreshold;
            }

            /* Warning thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuWarn = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                   ObserverConstants.NodeObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn) && int.TryParse(cpuWarn, out int cpuWarningUsageThresholdPct))
            {
                CpuWarningUsageThresholdPct = cpuWarningUsageThresholdPct;
            }

            var memWarn = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                   ObserverConstants.NodeObserverMemoryWarningLimitMB);

            if (!string.IsNullOrEmpty(memWarn) && int.TryParse(memWarn, out int memWarningUsageThresholdMB))
            {
                MemWarningUsageThresholdMB = memWarningUsageThresholdMB;
            }

            var portsWarn = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                     ObserverConstants.NodeObserverNetworkWarningActivePorts);

            if (!string.IsNullOrEmpty(portsWarn) && int.TryParse(portsWarn, out int activePortsWarningThreshold))
            {
                ActivePortsWarningThreshold = activePortsWarningThreshold;
            }

            var ephemeralPortsWarn = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                              ObserverConstants.NodeObserverNetworkWarningEphemeralPorts);

            if (!string.IsNullOrEmpty(ephemeralPortsWarn) && int.TryParse(ephemeralPortsWarn, out int ephemeralPortsWarningThreshold))
            {
                EphemeralPortsWarningThreshold = ephemeralPortsWarningThreshold;
            }

            var warnFirewallRules = GetSettingParameterValue(ObserverConstants.NodeObserverConfigurationSectionName,
                                                             ObserverConstants.NodeObserverNetworkWarningFirewallRules);

            if (!string.IsNullOrEmpty(warnFirewallRules) && int.TryParse(warnFirewallRules, out int firewallRulesWarningThreshold))
            {
                FirewallRulesWarningThreshold = firewallRulesWarningThreshold;
            }
        }

        private async Task GetSystemCpuMemoryValuesAsync(CancellationToken token)
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    // Ports...
                    int activePortCountTotal = NetworkUsage.GetActivePortCount();
                    int ephemeralPortCountTotal = NetworkUsage.GetActiveEphemeralPortCount();
                    this.activePortsData.Data.Add(activePortCountTotal);
                    this.ephemeralPortsData.Data.Add(ephemeralPortCountTotal);

                    // Firewall rules...
                    int firewalls = NetworkUsage.GetActiveFirewallRulesCount();
                    this.firewallData.Data.Add(firewalls);

                    for (int i = 0; i < 30; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        if (CpuWarningUsageThresholdPct > 0 
                            && CpuWarningUsageThresholdPct <= 100)
                        {
                            this.allCpuDataPrivTime.Data.Add(perfCounters.PerfCounterGetProcessorInfo());
                        }

                        if (MemWarningUsageThresholdMB > 0)
                        {
                            this.allMemDataCommittedBytes.Data.Add(perfCounters.PerfCounterGetMemoryInfoMB());
                        }

                        Thread.Sleep(250);
                    }
                }
                catch (Exception e)
                {
                    if (!(e is OperationCanceledException))
                    {
                        HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                                         ObserverName,
                                                                         HealthState.Warning,
                                                                         $"Unhandled exception in GetSystemCpuMemoryValuesAsync: {e.Message}: \n {e.StackTrace}");
                    }

                    throw;
                }

            }, token).ConfigureAwait(true);
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                if (CsvFileLogger.EnableCsvLogging || IsTelemetryEnabled)
                {
                    var fileName = "CpuMemFirewallsPorts" + NodeName;

                    // Log (csv) system-wide CPU/Mem data...
                    CsvFileLogger.LogData(fileName,
                                          NodeName,
                                          "CPU Time",
                                          "Average",
                                          Math.Round(this.allCpuDataPrivTime.AverageDataValue));

                    CsvFileLogger.LogData(fileName,
                                          NodeName,
                                          "CPU Time",
                                          "Peak",
                                          Math.Round(this.allCpuDataPrivTime.MaxDataValue));

                    CsvFileLogger.LogData(fileName,
                                          NodeName,
                                          "Committed Memory (MB)",
                                          "Average",
                                          Math.Round(this.allMemDataCommittedBytes.AverageDataValue));

                    CsvFileLogger.LogData(fileName,
                                          NodeName,
                                          "Committed Memory (MB)",
                                          "Peak",
                                          Math.Round(this.allMemDataCommittedBytes.MaxDataValue));

                    CsvFileLogger.LogData(fileName,
                                          NodeName,
                                          "All Active Ports",
                                          "Total",
                                          this.activePortsData.Data[0]);

                    CsvFileLogger.LogData(fileName,
                                          NodeName,
                                          "Ephemeral Active Ports",
                                          "Total",
                                          this.ephemeralPortsData.Data[0]);

                    CsvFileLogger.LogData(fileName,
                                          NodeName,
                                          "Firewall Rules",
                                          "Total",
                                          this.firewallData.Data[0]);

                    DataTableFileLogger.Flush();
                }

                // Report on the global health state (system-wide (node) metrics). 
                // User-configurable in NodeObserver.config.json
                var timeToLiveWarning = SetTimeToLiveWarning();
                
                // CPU
                if (this.allCpuDataPrivTime.AverageDataValue > 0)
                {
                    ProcessResourceDataReportHealth(this.allCpuDataPrivTime,
                                                    CpuErrorUsageThresholdPct,
                                                    CpuWarningUsageThresholdPct,
                                                    timeToLiveWarning);
                }

                // Memory
                if (this.allMemDataCommittedBytes.AverageDataValue > 0)
                {
                    ProcessResourceDataReportHealth(this.allMemDataCommittedBytes,
                                                    MemErrorUsageThresholdMB,
                                                    MemWarningUsageThresholdMB,
                                                    timeToLiveWarning);
                }

                // Firewall rules
                ProcessResourceDataReportHealth(this.firewallData,
                                                FirewallRulesErrorThreshold,
                                                FirewallRulesWarningThreshold,
                                                timeToLiveWarning);
                // Ports
                ProcessResourceDataReportHealth(this.activePortsData,
                                                ActivePortsErrorThreshold,
                                                ActivePortsWarningThreshold,
                                                timeToLiveWarning);

                ProcessResourceDataReportHealth(this.ephemeralPortsData,
                                                EphemeralPortsErrorThreshold,
                                                EphemeralPortsWarningThreshold,
                                                timeToLiveWarning);

                return Task.FromResult(1);

            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                                     ObserverName,
                                                                     HealthState.Warning,
                                                                     e.ToString());
                }

                throw;
            }
        }
    }
}