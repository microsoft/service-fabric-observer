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
using FabricObserver.Utilities;

namespace FabricObserver
{
    // This observer monitors all Fabric system service processes across various resource usage metrics.
    // It will signal Warnings or Errors based on settings supplied in Settings.xml.
    // The output (a local file) is used by the API service and the HTML frontend (http://localhost:5000/api/ObserverManager).
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    // ***FabricSystemObserver is disabled by default.***
    // You should not enable this observer unless you have spent some time analyzing how your services impact SF system services (like Fabric.exe)
    // If Fabric.exe is running at 70% CPU due to your service code, and this is normal for your workloads, then do not warn at this threshold.
    // As with all of these observers, you must first understand what are the happy (normal) states across resource usage before you set thresholds for the unhappy ones...
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

        // amount of time, in seconds, it took this observer to complete run run...
        private TimeSpan runtime = TimeSpan.MinValue;
        private Stopwatch stopWatch;
        private bool disposed = false;

        // Health Report data container - For use in analysis to deterWarne health state...
        private List<FabricResourceUsageData<int>> allCpuData;
        private List<FabricResourceUsageData<long>> allMemData;

        // Windows only... (EventLog)...
        private List<EventRecord> evtRecordList = null;
        private WindowsPerfCounters perfCounters = null;
        private DiskUsage diskUsage = null;
        private bool monitorWinEventLog = false;
        private int unhealthyNodesErrorThreshold = 0;
        private int unhealthyNodesWarnThreshold = 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricSystemObserver"/> class.
        /// </summary>
        public FabricSystemObserver()
            : base(ObserverConstants.FabricSystemObserverName)
        {
        }

        public int CpuErrorUsageThresholdPct { get; set; } = 90;

        public int MemErrorUsageThresholdMB { get; set; } = 15000;

        public int DiskErrorIOReadsThresholdMS { get; set; } = 0;

        public int DiskErrorIOWritesThresholdMS { get; set; } = 0;

        public int TotalActivePortCount { get; set; } = 0;

        public int TotalActiveEphemeralPortCount { get; set; } = 0;

        public int PortCountWarning { get; set; } = 1000;

        public int PortCountError { get; set; } = 5000;

        public int CpuWarnUsageThresholdPct { get; set; } = 70;

        public int MemWarnUsageThresholdMB { get; set; } = 14000;

        public int DiskWarnIOReadsThresholdMS { get; set; } = 20000;

        public int DiskWarnIOWritesThresholdMS { get; set; } = 20000;

        public string ErorrOrWarningKind { get; set; } = null;

        private bool Initialize()
        {
            if (this.stopWatch == null)
            {
                this.stopWatch = new Stopwatch();
            }

            this.Token.ThrowIfCancellationRequested();

            this.stopWatch.Start();

            this.SetThresholdsFromConfiguration();

            if (this.allMemData == null)
            {
                this.allMemData = new List<FabricResourceUsageData<long>>
                {
                    // Mem data...
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "Fabric"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricApplicationGateway"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricCAS"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricDCA"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricDnsService"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricGateway"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricHost"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricIS"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricRM"),
                    new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionPct, "FabricUS"),
                };
            }

            if (this.allCpuData == null)
            {
                this.allCpuData = new List<FabricResourceUsageData<int>>
                {
                    // Cpu data...
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "Fabric"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricApplicationGateway"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricCAS"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricDCA"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricDnsService"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricGateway"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricHost"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricIS"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricRM"),
                    new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, "FabricUS"),
                };
            }

            if (this.monitorWinEventLog)
            {
                this.evtRecordList = new List<EventRecord>();
            }

            return true;
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
                this.CpuErrorUsageThresholdPct = int.Parse(cpuError);
            }

            var memError = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorMemory);

            if (!string.IsNullOrEmpty(memError))
            {
                this.MemErrorUsageThresholdMB = int.Parse(memError);
            }

            var diskIOReadsError = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorDiskIOReads);

            if (!string.IsNullOrEmpty(diskIOReadsError))
            {
                this.DiskErrorIOReadsThresholdMS = int.Parse(diskIOReadsError);
            }

            var diskIOWritesError = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorDiskIOWrites);

            if (!string.IsNullOrEmpty(diskIOWritesError))
            {
                this.DiskErrorIOWritesThresholdMS = int.Parse(diskIOWritesError);
            }

            var percentErrorUnhealthyNodes = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorPercentUnhealthyNodes);

            if (!string.IsNullOrEmpty(percentErrorUnhealthyNodes))
            {
                this.unhealthyNodesErrorThreshold = int.Parse(percentErrorUnhealthyNodes);
            }

            /* Warning thresholds */

            this.Token.ThrowIfCancellationRequested();

            var cpuWarn = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnCpu);

            if (!string.IsNullOrEmpty(cpuWarn))
            {
                this.CpuWarnUsageThresholdPct = int.Parse(cpuWarn);
            }

            var memWarn = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnMemory);

            if (!string.IsNullOrEmpty(memWarn))
            {
                this.MemWarnUsageThresholdMB = int.Parse(memWarn);
            }

            var diskIOReadsWarn = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnDiskIOReads);

            if (!string.IsNullOrEmpty(diskIOReadsWarn))
            {
                this.DiskWarnIOReadsThresholdMS = int.Parse(diskIOReadsWarn);
            }

            var diskIOWritesWarn = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnDiskIOWrites);

            if (!string.IsNullOrEmpty(diskIOWritesWarn))
            {
                this.DiskWarnIOWritesThresholdMS = int.Parse(diskIOWritesWarn);
            }

            var percentWarnUnhealthyNodes = this.GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnPercentUnhealthyNodes);

            if (!string.IsNullOrEmpty(percentWarnUnhealthyNodes))
            {
                this.unhealthyNodesWarnThreshold = int.Parse(percentWarnUnhealthyNodes);
            }

            // Monitor Windows event log for SF and System Error/Critical events?
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
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example...
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

            this.Initialize();

            if (this.FabricClientInstance.QueryManager.GetNodeListAsync().GetAwaiter().GetResult()?.Count > 3
                && await this.CheckClusterHealthStateAsync(
                    this.unhealthyNodesWarnThreshold,
                    this.unhealthyNodesErrorThreshold).ConfigureAwait(true) == HealthState.Error)
            {
                return;
            }

            this.perfCounters = new WindowsPerfCounters();
            this.diskUsage = new DiskUsage();

            this.Token.ThrowIfCancellationRequested();

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
                        "Unhandled exception in ObserveAsync. Failed to observe CPU and Memory usage of " + string.Join(",", this.processWatchList) + ": " + e.ToString(),
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

                // Set TTL...
                this.stopWatch.Stop();
                this.runtime = this.stopWatch.Elapsed;
                this.stopWatch.Reset();
                await this.ReportAsync(token).ConfigureAwait(true);

                // No need to keep these objects in memory aross healthy iterations...
                if (!this.HasActiveFabricErrorOrWarning)
                {
                    // Clear out/null list objects...
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

            if (processes?.Length == 0)
            {
                return;
            }

            foreach (var process in processes)
            {
                try
                {
                    this.Token.ThrowIfCancellationRequested();

                    // ports in use by Fabric services...
                    this.TotalActivePortCount += NetworkUsage.GetActivePortCount(process.Id);
                    this.TotalActiveEphemeralPortCount += NetworkUsage.GetActiveEphemeralPortCount(process.Id);

                    int procCount = Environment.ProcessorCount;

                    while (!process.HasExited && procCount > 0)
                    {
                        this.Token.ThrowIfCancellationRequested();

                        try
                        {
                            int cpu = (int)this.perfCounters.PerfCounterGetProcessorInfo("% Processor Time", "Process", process.ProcessName);

                            this.allCpuData.FirstOrDefault(x => x.Id == procName).Data.Add(cpu);

                            // Memory - Private WS for proc...
                            var workingset = this.perfCounters.PerfCounterGetProcessPrivateWorkingSetMB(process.ProcessName);
                            this.allMemData.FirstOrDefault(x => x.Id == procName).Data.Add((long)workingset);

                            --procCount;
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
                    // This will always be the case if FabricObserver.exe is not running as Admin or LocalSystem...
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

            // Critical and Errors only...
            string xQuery = "*[System/Level <= 2] and " + datexQuery;

            // SF Admin Event Store...
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

            // SF Operational Event Store...
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

            // SF Lease Admin Event Store...
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

            // SF Lease Operational Event Store...
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

            // System Event Store...
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
            var timeToLiveWarning = this.SetTimeToLiveWarning(this.runtime.Seconds);
            var portInformationReport = new Utilities.HealthReport
            {
                Observer = this.ObserverName,
                NodeName = this.NodeName,
                HealthMessage = $"Number of ports in use by Fabric services: {this.TotalActivePortCount}\n" +
                                $"Number of ephemeral ports in use by Fabric services: {this.TotalActiveEphemeralPortCount}",
                State = HealthState.Ok,
                HealthReportTimeToLive = timeToLiveWarning,
            };

            // TODO: Report on port count based on thresholds PortCountWarning/Error...
            this.HealthReporter.ReportHealthToServiceFabric(portInformationReport);

            // Reset ports counters...
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
                this.MemErrorUsageThresholdMB,
                this.MemWarnUsageThresholdMB);

            // Windows Event Log
            if (ObserverManager.ObserverWebAppDeployed
                && this.monitorWinEventLog)
            {
                // SF Eventlog Errors?
                // Write this out to a new file, for use by the web front end log viewer...
                // Format = HTML...
                int count = this.evtRecordList.Count();
                var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "EventVwrErrors.txt");

                // Remove existing file...
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

                    sb.AppendLine("<br/><div><strong>" +
                                  "<a href='javascript:toggle(\"evtContainer\")'>" +
                                  "<div id=\"plus\" style=\"display: inline; font-size: 25px;\">+</div> " + count +
                                  " Error Events in ServiceFabric and System</a> " +
                                  "Event logs</strong>.<br/></div>");

                    sb.AppendLine("<div id='evtContainer' style=\"display: none;\">");

                    foreach (var evt in this.evtRecordList.Distinct())
                    {
                        token.ThrowIfCancellationRequested();

                        try
                        {
                            // Access event properties:
                            sb.AppendLine("<div>" + evt.LogName + "</div>");
                            sb.AppendLine("<div>" + evt.LevelDisplayName + "</div>");
                            if (evt.TimeCreated.HasValue)
                            {
                                sb.AppendLine("<div>" + evt.TimeCreated.Value.ToShortDateString() + "</div>");
                            }

                            foreach (var prop in evt.Properties)
                            {
                                if (prop.Value != null && Convert.ToString(prop.Value).Length > 0)
                                {
                                    sb.AppendLine("<div>" + prop.Value + "</div>");
                                }
                            }
                        }
                        catch (EventLogException)
                        {
                        }
                    }

                    sb.AppendLine("</div>");

                    this.ObserverLogger.TryWriteLogFile(logPath, sb.ToString());
                    sb.Clear();
                }

                // Clean up...
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

                this.disposed = true;
            }
        }

        private void ProcessResourceDataList<T>(
            List<FabricResourceUsageData<T>> data,
            T thresholdError,
            T thresholdWarning)
        {
            foreach (var dataItem in data)
            {
                this.Token.ThrowIfCancellationRequested();

                if (dataItem.Data.Count == 0 || dataItem.AverageDataValue < 0)
                {
                    continue;
                }

                var propertyName = data.First().Property;
                if (this.CsvFileLogger.EnableCsvLogging || this.IsTelemetryEnabled)
                {
                    var fileName = "FabricSystemServices_" + this.NodeName;

                    // Log average data value to long-running store (CSV)...
                    string dataLogMonitorType = propertyName;

                    // Log file output...
                    string resourceProp = propertyName + " use";

                    if (propertyName == ErrorWarningProperty.TotalMemoryConsumptionPct)
                    {
                        dataLogMonitorType = "Working Set (MB)";
                    }

                    if (propertyName == ErrorWarningProperty.TotalCpuTime)
                    {
                        dataLogMonitorType = "% CPU Time";
                    }

                    if (propertyName.Contains("Disk IO"))
                    {
                        dataLogMonitorType += "/ms";
                        resourceProp = propertyName;
                    }

                    this.CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Average", Math.Round(dataItem.AverageDataValue, 2));
                    this.CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Peak", Math.Round(Convert.ToDouble(dataItem.MaxDataValue)));
                }

                this.ProcessResourceDataReportHealth(
                    dataItem,
                    thresholdError,
                    thresholdWarning,
                    this.SetTimeToLiveWarning(this.runtime.Seconds));
            }
        }

        private async Task<HealthState> CheckClusterHealthStateAsync(int warningThreshold, int errorThreshold)
        {
            try
            {
                var clusterHealth = await this.FabricClientInstance.HealthManager.GetClusterHealthAsync().ConfigureAwait(true);
                double errorNodesCount = clusterHealth.NodeHealthStates.Count(nodeHealthState => nodeHealthState.AggregatedHealthState == HealthState.Error);
                int errorNodesCountPercentage = (int)((errorNodesCount / clusterHealth.NodeHealthStates.Count) * 100);

                if (errorNodesCountPercentage >= errorThreshold)
                {
                    return HealthState.Error;
                }
                else if (errorNodesCountPercentage >= warningThreshold)
                {
                    return HealthState.Warning;
                }
            }
            catch (TimeoutException te)
            {
                this.ObserverLogger.LogInfo("Handled TimeoutException:\n {0}", te.ToString());

                return HealthState.Unknown;
            }
            catch (FabricException fe)
            {
                this.ObserverLogger.LogInfo("Handled FabricException:\n {0}", fe.ToString());

                return HealthState.Unknown;
            }
            catch (Exception e)
            {
                this.ObserverLogger.LogWarning("Unhandled Exception querying Cluster health:\n {0}", e.ToString());

                throw;
            }

            return HealthState.Ok;
        }
    }
}
