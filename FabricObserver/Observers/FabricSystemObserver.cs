// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Fabric;
using System.Fabric.Health;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    // ***FabricSystemObserver is disabled by default.***
    // This observer monitors all Fabric system service processes across various resource usage metrics.
    // It will signal Warnings or Errors based on settings supplied in Settings.xml.
    // If the FabricObserverWebApi service is deployed: The output (a local file) is created for and used by the API service (http://localhost:5000/api/ObserverManager).
    // SF Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    // You should not enable this observer unless you have spent some time analyzing how your services impact SF system services (like Fabric.exe)
    // If Fabric.exe is running at 70% CPU due to your service code, and this is normal for your workloads, then do not warn at this threshold.
    // As with all observers, you should first understand what are the happy (normal) states across resource usage before you set thresholds for the unhappy states.
    public class FabricSystemObserver : ObserverBase
    {
        private readonly List<string> processWatchList = new List<string>
        {
            "Fabric",
            "FabricApplicationGateway",
            "FabricCAS",
            "FabricDCA",
            "FabricDnsService",
            "FabricGateway",
            "FabricHost",
            "FabricIS",
            "FabricRM",
            "FabricUS",
        };

        private Stopwatch stopwatch;
        private bool disposed;

        // Health Report data container - For use in analysis to deterWarne health state.
        private List<FabricResourceUsageData<int>> allCpuData;
        private List<FabricResourceUsageData<float>> allMemData;

        // Windows only. (EventLog).
        private List<EventRecord> evtRecordList;
        private WindowsPerfCounters perfCounters;
        private DiskUsage diskUsage;
        private bool monitorWinEventLog;
        private int unhealthyNodesErrorThreshold;
        private int unhealthyNodesWarnThreshold;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricSystemObserver"/> class.
        /// </summary>
        public FabricSystemObserver()
            : base(ObserverConstants.FabricSystemObserverName)
        {
        }

        public int CpuErrorUsageThresholdPct { get; set; } = 90;

        public int MemErrorUsageThresholdMb { get; set; } = 15000;

        public int TotalActivePortCount { get; set; }

        public int TotalActiveEphemeralPortCount { get; set; }

        public int PortCountWarning { get; set; } = 1000;

        public int PortCountError { get; set; } = 5000;

        public int CpuWarnUsageThresholdPct { get; set; } = 70;

        public int MemWarnUsageThresholdMb { get; set; } = 14000;

        public string ErrorOrWarningKind { get; set; } = null;

        private void Initialize()
        {
            if (this.stopwatch == null)
            {
                this.stopwatch = new Stopwatch();
            }

            this.Token.ThrowIfCancellationRequested();

            this.stopwatch.Start();

            this.SetThresholdsFromConfiguration();

            if (this.allMemData == null)
            {
                this.allMemData = new List<FabricResourceUsageData<float>>
                {
                    // Mem data.
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "Fabric",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricApplicationGateway",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricCAS",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricDCA",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricDnsService",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricGateway",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricHost",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricIS",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricRM",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<float>(
                        ErrorWarningProperty.TotalMemoryConsumptionPct,
                        "FabricUS",
                        DataCapacity,
                        UseCircularBuffer),
                };
            }

            if (this.allCpuData == null)
            {
                this.allCpuData = new List<FabricResourceUsageData<int>>
                {
                    // Cpu data.
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "Fabric",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricApplicationGateway",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricCAS",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricDCA",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricDnsService",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricGateway",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricHost",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricIS",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricRM",
                        DataCapacity,
                        UseCircularBuffer),
                    new FabricResourceUsageData<int>(
                        ErrorWarningProperty.TotalCpuTime,
                        "FabricUS",
                        DataCapacity,
                        UseCircularBuffer),
                };
            }

            if (this.monitorWinEventLog)
            {
                this.evtRecordList = new List<EventRecord>();
            }
        }

        private void SetThresholdsFromConfiguration()
        {
            /* Error thresholds */

            this.Token.ThrowIfCancellationRequested();

            var cpuError = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorCpu);

            if (!string.IsNullOrEmpty(cpuError))
            {
                _ = int.TryParse(cpuError, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverErrorCpu}.");
                }

                this.CpuErrorUsageThresholdPct = threshold;
            }

            var memError = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorMemory);

            if (!string.IsNullOrEmpty(memError))
            {
                _ = int.TryParse(memError, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverErrorMemory}.");
                }

                this.MemErrorUsageThresholdMb = threshold;
            }

            var percentErrorUnhealthyNodes = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorPercentUnhealthyNodes);

            if (!string.IsNullOrEmpty(percentErrorUnhealthyNodes))
            {
                _ = int.TryParse(percentErrorUnhealthyNodes, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverErrorPercentUnhealthyNodes}.");
                }

                this.unhealthyNodesErrorThreshold = threshold;
            }

            /* Warning thresholds */

            this.Token.ThrowIfCancellationRequested();

            var cpuWarn = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnCpu);

            if (!string.IsNullOrEmpty(cpuWarn))
            {
                _ = int.TryParse(cpuWarn, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverWarnCpu}.");
                }

                this.CpuWarnUsageThresholdPct = threshold;
            }

            var memWarn = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnMemory);

            if (!string.IsNullOrEmpty(memWarn))
            {
                _ = int.TryParse(memWarn, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} MB is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverWarnMemory}.");
                }

                this.MemWarnUsageThresholdMb = threshold;
            }

            var percentWarnUnhealthyNodes = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnPercentUnhealthyNodes);

            if (!string.IsNullOrEmpty(percentWarnUnhealthyNodes))
            {
                _ = int.TryParse(percentWarnUnhealthyNodes, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold for a {ObserverConstants.FabricSystemObserverWarnPercentUnhealthyNodes}.");
                }

                this.unhealthyNodesWarnThreshold = threshold;
            }

            // Monitor Windows event log for SF and System Error/Critical events?
            // This can be noisy. Use wisely.
            var watchEvtLog = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverMonitorWindowsEventLog);

            if (!string.IsNullOrEmpty(watchEvtLog) && bool.TryParse(watchEvtLog, out bool watchEl))
            {
                this.monitorWinEventLog = watchEl;
            }
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

            this.Token = token;

            if (this.Token.IsCancellationRequested)
            {
                return;
            }

            // Don't run this observer if the aggregated health state of the cluster is Warning or Error.
            if (this.FabricClientInstance.QueryManager.GetNodeListAsync().GetAwaiter().GetResult()?.Count > 3
                && await this.GetClusterHealthStateWithPercentErrorNodesAsync(
                    this.unhealthyNodesWarnThreshold,
                    this.unhealthyNodesErrorThreshold).ConfigureAwait(true) == HealthState.Error)
            {
                return;
            }

            this.Initialize();

            this.perfCounters = new WindowsPerfCounters();
            this.diskUsage = new DiskUsage();

            try
            {
                foreach (var proc in this.processWatchList)
                {
                    this.Token.ThrowIfCancellationRequested();

                    this.GetProcessInfo(proc);
                }
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    this.WriteToLogWithLevel(
                        this.ObserverName,
                        "Unhandled exception in ObserveAsync. Failed to observe CPU and Memory usage of " + string.Join(",", this.processWatchList) + ": " + e,
                        LogLevel.Error);
                }

                throw;
            }

            try
            {
                if (ObserverManager.ObserverWebAppDeployed
                    && this.monitorWinEventLog)
                {
                    this.ReadServiceFabricWindowsEventLog();
                }

                // Set TTL.
                this.stopwatch.Stop();
                this.RunDuration = this.stopwatch.Elapsed;
                this.stopwatch.Reset();

                await this.ReportAsync(token).ConfigureAwait(true);

                // No need to keep these objects in memory aross healthy iterations.
                if (!this.HasActiveFabricErrorOrWarning)
                {
                    // Clear out/null list objects.
                    this.allCpuData.Clear();
                    this.allCpuData = null;

                    this.allMemData.Clear();
                    this.allMemData = null;
                }

                this.LastRunDateTime = DateTime.Now;
            }
            finally
            {
                this.diskUsage?.Dispose();
                this.diskUsage = null;
                this.perfCounters?.Dispose();
                this.perfCounters = null;
            }
        }

        private void GetProcessInfo(string procName)
        {
            var processes = Process.GetProcessesByName(procName);

            if (processes.Length == 0)
            {
                return;
            }

            Stopwatch timer = new Stopwatch();

            foreach (var process in processes)
            {
                try
                {
                    this.Token.ThrowIfCancellationRequested();

                    // ports in use by Fabric services.
                    this.TotalActivePortCount += NetworkUsage.GetActivePortCount(process.Id);
                    this.TotalActiveEphemeralPortCount += NetworkUsage.GetActiveEphemeralPortCount(process.Id);

                    TimeSpan duration = TimeSpan.FromSeconds(15);

                    if (this.MonitorDuration > TimeSpan.MinValue)
                    {
                        duration = this.MonitorDuration;
                    }

                    // Warm up the counters.
                    _ = this.perfCounters.PerfCounterGetProcessorInfo("% Processor Time", "Process", process.ProcessName);
                    _ = this.perfCounters.PerfCounterGetProcessPrivateWorkingSetMb(process.ProcessName);

                    timer.Start();

                    while (!process.HasExited && timer.Elapsed <= duration)
                    {
                        this.Token.ThrowIfCancellationRequested();

                        try
                        {
                            // CPU Time for service process.
                            int cpu = (int)this.perfCounters.PerfCounterGetProcessorInfo("% Processor Time", "Process", process.ProcessName);
                            this.allCpuData.FirstOrDefault(x => x.Id == procName)?.Data.Add(cpu);

                            // Private Working Set for service process.
                            float mem = this.perfCounters.PerfCounterGetProcessPrivateWorkingSetMb(process.ProcessName);
                            this.allMemData.FirstOrDefault(x => x.Id == procName)?.Data.Add(mem);

                            Thread.Sleep(250);
                        }
                        catch (Exception e)
                        {
                            this.WriteToLogWithLevel(
                                this.ObserverName,
                                $"Can't observe {process} details due to {e.Message} - {e.StackTrace}",
                                LogLevel.Warning);

                            throw;
                        }
                    }
                }
                catch (Win32Exception)
                {
                    // This will always be the case if FabricObserver.exe is not running as Admin or LocalSystem.
                    // It's OK. Just means that the elevated process (like FabricHost.exe) won't be observed.
                    this.WriteToLogWithLevel(
                        this.ObserverName,
                        $"Can't observe {process} due to it's privilege level - " + "FabricObserver must be running as System or Admin for this specific task.",
                        LogLevel.Information);

                    break;
                }
                finally
                {
                    process?.Dispose();
                }

                timer.Stop();
                timer.Reset();
            }
        }

        /// <summary>
        /// ReadServiceFabricWindowsEventLog().
        /// </summary>
        public void ReadServiceFabricWindowsEventLog()
        {
            string sfOperationalLogSource = "Microsoft-ServiceFabric/Operational";
            string sfAdminLogSource = "Microsoft-ServiceFabric/Admin";
            string systemLogSource = "System";
            string sfLeaseAdminLogSource = "Microsoft-ServiceFabric-Lease/Admin";
            string sfLeaseOperationalLogSource = "Microsoft-ServiceFabric-Lease/Operational";

            var range2Days = DateTime.UtcNow.AddDays(-1);
            var format = range2Days.ToString(
                         "yyyy-MM-ddTHH:mm:ss.fffffff00K",
                         CultureInfo.InvariantCulture);
            var datexQuery = string.Format(
                             "*[System/TimeCreated/@SystemTime >='{0}']",
                             format);

            // Critical and Errors only.
            string xQuery = "*[System/Level <= 2] and " + datexQuery;

            // SF Admin Event Store.
            var evtLogQuery = new EventLogQuery(sfAdminLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }

            // SF Operational Event Store.
            evtLogQuery = new EventLogQuery(sfOperationalLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }

            // SF Lease Admin Event Store.
            evtLogQuery = new EventLogQuery(sfLeaseAdminLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }

            // SF Lease Operational Event Store.
            evtLogQuery = new EventLogQuery(sfLeaseOperationalLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }

            // System Event Store.
            evtLogQuery = new EventLogQuery(systemLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }
        }

        /// <inheritdoc/>
        public override Task ReportAsync(CancellationToken token)
        {
            this.Token.ThrowIfCancellationRequested();
            var timeToLiveWarning = this.SetHealthReportTimeToLive();
            var portInformationReport = new HealthReport
            {
                Observer = this.ObserverName,
                NodeName = this.NodeName,
                HealthMessage = $"Number of ports in use by Fabric services: {this.TotalActivePortCount}\n" +
                                $"Number of ephemeral ports in use by Fabric services: {this.TotalActiveEphemeralPortCount}",
                State = HealthState.Ok,
                HealthReportTimeToLive = timeToLiveWarning,
            };

            // TODO: Report on port count based on thresholds PortCountWarning/Error.
            this.HealthReporter.ReportHealthToServiceFabric(portInformationReport);

            // Reset ports counters.
            this.TotalActivePortCount = 0;
            this.TotalActiveEphemeralPortCount = 0;

            // CPU
            this.ProcessResourceDataList(
                this.allCpuData,
                this.CpuErrorUsageThresholdPct,
                this.CpuWarnUsageThresholdPct);

            // Memory
            this.ProcessResourceDataList(
                this.allMemData,
                this.MemErrorUsageThresholdMb,
                this.MemWarnUsageThresholdMb);

            // Windows Event Log
            if (ObserverManager.ObserverWebAppDeployed
                && this.monitorWinEventLog)
            {
                // SF Eventlog Errors?
                // Write this out to a new file, for use by the web front end log viewer.
                // Format = HTML.
                int count = this.evtRecordList.Count();
                var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "EventVwrErrors.txt");

                // Remove existing file.
                if (File.Exists(logPath))
                {
                    try
                    {
                        File.Delete(logPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                if (count >= 10)
                {
                    var sb = new StringBuilder();

                    _ = sb.AppendLine("<br/><div><strong>" +
                                  "<a href='javascript:toggle(\"evtContainer\")'>" +
                                  "<div id=\"plus\" style=\"display: inline; font-size: 25px;\">+</div> " + count +
                                  " Error Events in ServiceFabric and System</a> " +
                                  "Event logs</strong>.<br/></div>");

                    _ = sb.AppendLine("<div id='evtContainer' style=\"display: none;\">");

                    foreach (var evt in this.evtRecordList.Distinct())
                    {
                        token.ThrowIfCancellationRequested();

                        try
                        {
                            // Access event properties:
                            _ = sb.AppendLine("<div>" + evt.LogName + "</div>");
                            _ = sb.AppendLine("<div>" + evt.LevelDisplayName + "</div>");
                            if (evt.TimeCreated.HasValue)
                            {
                                _ = sb.AppendLine("<div>" + evt.TimeCreated.Value.ToShortDateString() + "</div>");
                            }

                            foreach (var prop in evt.Properties)
                            {
                                if (prop.Value != null && Convert.ToString(prop.Value).Length > 0)
                                {
                                    _ = sb.AppendLine("<div>" + prop.Value + "</div>");
                                }
                            }
                        }
                        catch (EventLogException)
                        {
                        }
                    }

                    _ = sb.AppendLine("</div>");

                    _ = this.ObserverLogger.TryWriteLogFile(logPath, sb.ToString());
                    _ = sb.Clear();
                }

                // Clean up.
                if (count > 0)
                {
                    this.evtRecordList.Clear();
                }
            }

            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }

            if (disposing)
            {
                if (this.perfCounters != null)
                {
                    this.perfCounters.Dispose();
                    this.perfCounters = null;
                }

                if (this.diskUsage != null)
                {
                    this.diskUsage.Dispose();
                    this.diskUsage = null;
                }

                // Data lists.
                this.processWatchList?.Clear();
                this.allCpuData?.Clear();
                this.allMemData?.Clear();
                this.evtRecordList?.Clear();

                this.disposed = true;
            }
        }

        private void ProcessResourceDataList<T>(
            IReadOnlyCollection<FabricResourceUsageData<T>> data,
            T thresholdError,
            T thresholdWarning)
                where T : struct
        {
            foreach (var dataItem in data)
            {
                this.Token.ThrowIfCancellationRequested();

                if (dataItem.Data.Count == 0 || Convert.ToDouble(dataItem.AverageDataValue) < 0)
                {
                    continue;
                }

                if (this.CsvFileLogger.EnableCsvLogging)
                {
                    var fileName = "FabricSystemServices_" + this.NodeName;
                    var propertyName = data.First().Property;

                    // Log average data value to long-running store (CSV).
                    string dataLogMonitorType = propertyName;

                    switch (propertyName)
                    {
                        case ErrorWarningProperty.TotalMemoryConsumptionPct:
                            dataLogMonitorType = "Working Set %";
                            break;

                        case ErrorWarningProperty.TotalCpuTime:
                            dataLogMonitorType = "% CPU Time";
                            break;
                    }

                    this.CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Average", Math.Round(Convert.ToDouble(dataItem.AverageDataValue), 2));
                    this.CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Peak", Math.Round(Convert.ToDouble(dataItem.MaxDataValue)));
                }

                this.ProcessResourceDataReportHealth(
                    dataItem,
                    thresholdError,
                    thresholdWarning,
                    this.SetHealthReportTimeToLive());
            }
        }

        private async Task<HealthState> GetClusterHealthStateWithPercentErrorNodesAsync(int warningThreshold, int errorThreshold)
        {
            try
            {
                var clusterHealth = await this.FabricClientInstance.HealthManager.GetClusterHealthAsync(
                                            this.AsyncClusterOperationTimeoutSeconds,
                                            this.Token).ConfigureAwait(true);

                // The cluster is in an Error state.
                if (clusterHealth.AggregatedHealthState == HealthState.Error)
                {
                    return clusterHealth.AggregatedHealthState;
                }

                // If you we get here, then see if your supplied (FSO configuration setting) thresholds of sustainable nodes in error/warning have been reached or exceeded.
                double errorNodesCount = clusterHealth.NodeHealthStates.Count(nodeHealthState => nodeHealthState.AggregatedHealthState == HealthState.Error);
                int errorNodesCountPercentage = (int)((errorNodesCount / clusterHealth.NodeHealthStates.Count) * 100);

                if (errorNodesCountPercentage >= errorThreshold)
                {
                    return HealthState.Error;
                }

                if (errorNodesCountPercentage >= warningThreshold)
                {
                    return HealthState.Warning;
                }
            }
            catch (TimeoutException)
            {
                return HealthState.Unknown;
            }
            catch (FabricException)
            {
                return HealthState.Unknown;
            }
            catch (Exception e)
            {
                this.ObserverLogger.LogWarning(
                    $"Unhandled Exception querying Cluster health:{Environment.NewLine}{e}");

                throw;
            }

            return HealthState.Ok;
        }
    }
}
