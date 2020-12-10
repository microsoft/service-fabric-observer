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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    // When enabled, FabricSystemObserver monitors all Fabric system service processes across various resource usage metrics (CPU Time, Workingset, Ephemeral and all Active TCP ports).
    // It will signal Warnings or Errors based on settings supplied in ApplicationManifest.xml (Like many observers, most of it's settings are overridable and can be reset with application parameter updates).
    // If the FabricObserverWebApi service (DEPRECATED - it will not evolve...) is deployed: The output (a local file) is created for and used by the API service (http://localhost:5000/api/ObserverManager).
    // SF Health Report processor will also emit ETW telemetry if configured in ApplicationManifest.xml.
    // As with all observers, you should first determine the happy (normal) states across resource usage before you set thresholds for the unhappy ones.
    public class FabricSystemObserver : ObserverBase
    {
        private readonly List<string> processWatchList;
        private Stopwatch stopwatch;
        private bool disposed;

        // Health Report data container - For use in analysis to determine health state.
        private List<FabricResourceUsageData<int>> allCpuData;
        private List<FabricResourceUsageData<float>> allMemData;
        private List<FabricResourceUsageData<int>> allActiveTcpPortData;
        private List<FabricResourceUsageData<int>> allEphemeralTcpPortData;

        // Windows only. (EventLog).
        private List<EventRecord> evtRecordList;
        private bool monitorWinEventLog;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricSystemObserver"/> class.
        /// </summary>
        public FabricSystemObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            // Linux
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                this.processWatchList = new List<string>
                {
                    "Fabric",
                    "FabricCAS.dll",
                    "FabricDCA.dll",
                    "FabricDnsService",
                    "FabricFAS.dll",
                    "FabricGateway.exe",
                    "FabricHost",
                    "FabricIS.dll",
                    "FabricRM",
                    "FabricUS",
                };
            }
            else
            {
                // Windows
                this.processWatchList = new List<string>
                {
                    "Fabric",
                    "FabricApplicationGateway",
                    "FabricCAS",
                    "FabricDCA",
                    "FabricDnsService",
                    "FabricFAS",
                    "FabricGateway",
                    "FabricHost",
                    "FabricIS",
                    "FabricRM",
                    "FabricUS",
                };
            }
        }

        public int CpuErrorUsageThresholdPct
        {
            get; set;
        }

        public int MemErrorUsageThresholdMb
        {
            get; set;
        }

        public int TotalActivePortCountAllSystemServices
        {
            get; set;
        }

        public int TotalActiveEphemeralPortCountAllSystemServices
        {
            get; set;
        }

        public int ActiveTcpPortCountError
        {
            get; set;
        }

        public int ActiveEphemeralPortCountError
        {
            get; set;
        }

        public int ActiveTcpPortCountWarning
        {
            get; set;
        }

        public int ActiveEphemeralPortCountWarning
        {
            get; set;
        }

        public int CpuWarnUsageThresholdPct
        {
            get; set;
        }

        public int MemWarnUsageThresholdMb
        {
            get; set;
        }

        public string ErrorOrWarningKind 
        { 
            get; set; 
        } = null;

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            Token = token;

            if (Token.IsCancellationRequested)
            {
                return;
            }

            Initialize();

            try
            {
                foreach (var procName in this.processWatchList)
                {
                    Token.ThrowIfCancellationRequested();
                    string dotnet = string.Empty;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && procName.EndsWith(".dll"))
                    {
                        dotnet = "dotnet ";
                    }

                    GetProcessInfo($"{dotnet}{procName}");
                }
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    WriteToLogWithLevel(
                        ObserverName,
                        "Unhandled exception in ObserveAsync. Failed to observe CPU and Memory usage of " + string.Join(",", this.processWatchList) + ": " + e,
                        LogLevel.Error);
                }

                throw;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && ObserverManager.ObserverWebAppDeployed
                && this.monitorWinEventLog)
            {
                ReadServiceFabricWindowsEventLog();
            }

            // Set TTL.
            this.stopwatch.Stop();
            RunDuration = this.stopwatch.Elapsed;
            this.stopwatch.Reset();

            await ReportAsync(token).ConfigureAwait(true);

            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            Token.ThrowIfCancellationRequested();

            // Informational report. For now, Linux is where we pay close attention to memory use by Fabric system services as there are still a few issues in that realm..
            var timeToLiveWarning = SetHealthReportTimeToLive();
            var portInformationReport = new HealthReport
            {
                Observer = ObserverName,
                NodeName = NodeName,
                HealthMessage = $"Number of ports in use by Fabric services: {TotalActivePortCountAllSystemServices}\n" +
                                $"Number of ephemeral ports in use by Fabric services: {TotalActiveEphemeralPortCountAllSystemServices}\n" +
                                (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ?
                                    $"Fabric mem use: {this.allMemData.Where(x => x.Id == "Fabric")?.FirstOrDefault()?.AverageDataValue}\n" +
                                    $"FabricGateway mem use: {this.allMemData.Where(x => x.Id == "FabricGateway.exe")?.FirstOrDefault()?.AverageDataValue}\n" +
                                    $"FabricHost mem use: {this.allMemData.Where(x => x.Id == "FabricHost")?.FirstOrDefault()?.AverageDataValue}\n" : string.Empty),

                State = HealthState.Ok,
                HealthReportTimeToLive = timeToLiveWarning,
            };

            HealthReporter.ReportHealthToServiceFabric(portInformationReport);

            // Reset ports counters.
            TotalActivePortCountAllSystemServices = 0;
            TotalActiveEphemeralPortCountAllSystemServices = 0;

            // CPU
            ProcessResourceDataList(
                this.allCpuData,
                CpuErrorUsageThresholdPct,
                CpuWarnUsageThresholdPct);

            // Memory
            ProcessResourceDataList(
                this.allMemData,
                MemErrorUsageThresholdMb,
                MemWarnUsageThresholdMb);

            // Ports - Active TCP
            ProcessResourceDataList(
               this.allActiveTcpPortData,
               ActiveTcpPortCountError,
               ActiveTcpPortCountWarning);

            // Ports - Ephemeral
            ProcessResourceDataList(
               this.allEphemeralTcpPortData,
               ActiveEphemeralPortCountError,
               ActiveEphemeralPortCountWarning);

            // Windows Event Log
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && ObserverManager.ObserverWebAppDeployed
                && this.monitorWinEventLog)
            {
                // SF Eventlog Errors?
                // Write this out to a new file, for use by the web front end log viewer.
                // Format = HTML.
                int count = this.evtRecordList.Count();
                var logPath = Path.Combine(ObserverLogger.LogFolderBasePath, "EventVwrErrors.txt");

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

                    _ = ObserverLogger.TryWriteLogFile(logPath, sb.ToString());
                    _ = sb.Clear();
                }

                // Clean up.
                if (count > 0)
                {
                    this.evtRecordList.Clear();
                }
            }

            ClearDataContainers();

            return Task.CompletedTask;
        }

        private void ClearDataContainers()
        {
            this.allActiveTcpPortData?.Clear();
            this.allActiveTcpPortData = null;
            this.allCpuData?.Clear();
            this.allCpuData = null;
            this.allEphemeralTcpPortData?.Clear();
            this.allEphemeralTcpPortData = null;
            this.allMemData?.Clear();
            this.allMemData = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                this.evtRecordList?.Clear();
                this.evtRecordList = null;
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
                    Token.ThrowIfCancellationRequested();
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
                    Token.ThrowIfCancellationRequested();
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
                    Token.ThrowIfCancellationRequested();
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
                    Token.ThrowIfCancellationRequested();
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
                    Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }

            if (disposing)
            {
                this.disposed = true;
            }
        }

        private static Process[] GetDotnetProcessesByFirstArgument(string argument)
        {
            List<Process> result = new List<Process>();
            Process[] processes = Process.GetProcessesByName("dotnet");

            for (int i = 0; i < processes.Length; ++i)
            {
                Process p = processes[i];
                try
                {
                    string cmdline = File.ReadAllText($"/proc/{p.Id}/cmdline");
                    string[] parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 1 && string.Equals(argument, parts[1], StringComparison.Ordinal))
                    {
                        result.Add(p);
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // It is possible that the process already exited.
                }
            }

            return result.ToArray();
        }

        private void Initialize()
        {
            if (this.stopwatch == null)
            {
                this.stopwatch = new Stopwatch();
            }

            Token.ThrowIfCancellationRequested();

            this.stopwatch.Start();

            SetThresholdSFromConfiguration();

            // CPU data
            if (this.allCpuData == null)
            {
                this.allCpuData = new List<FabricResourceUsageData<int>>(this.processWatchList.Count);

                foreach (var proc in this.processWatchList)
                {
                    this.allCpuData.Add(
                        new FabricResourceUsageData<int>(
                            ErrorWarningProperty.TotalCpuTime,
                            proc,
                            DataCapacity,
                            UseCircularBuffer));
                }
            }

            // Memory data
            if (this.allMemData == null)
            {
                this.allMemData = new List<FabricResourceUsageData<float>>(this.processWatchList.Count);

                foreach (var proc in this.processWatchList)
                {
                    this.allMemData.Add(
                        new FabricResourceUsageData<float>(
                            ErrorWarningProperty.TotalMemoryConsumptionMb,
                            proc,
                            DataCapacity,
                            UseCircularBuffer));
                }
            }

            // Ports
            if (this.allActiveTcpPortData == null)
            {
                this.allActiveTcpPortData = new List<FabricResourceUsageData<int>>(this.processWatchList.Count);

                foreach (var proc in this.processWatchList)
                {
                    this.allActiveTcpPortData.Add(
                        new FabricResourceUsageData<int>(
                            ErrorWarningProperty.TotalActivePorts,
                            proc,
                            DataCapacity,
                            UseCircularBuffer));
                }
            }

            if (this.allEphemeralTcpPortData == null)
            {
                this.allEphemeralTcpPortData = new List<FabricResourceUsageData<int>>(this.processWatchList.Count);

                foreach (var proc in this.processWatchList)
                {
                    this.allEphemeralTcpPortData.Add(
                        new FabricResourceUsageData<int>(
                            ErrorWarningProperty.TotalEphemeralPorts,
                            proc,
                            DataCapacity,
                            UseCircularBuffer));
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && this.monitorWinEventLog
                && this.evtRecordList == null)
            {
                this.evtRecordList = new List<EventRecord>();
            }
        }

        private void SetThresholdSFromConfiguration()
        {
            /* Error thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuError = GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError))
            {
                _ = int.TryParse(cpuError, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverCpuErrorLimitPct}.");
                }

                CpuErrorUsageThresholdPct = threshold;
            }

            var memError = GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverMemoryErrorLimitMb);

            if (!string.IsNullOrEmpty(memError))
            {
                _ = int.TryParse(memError, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverMemoryErrorLimitMb}.");
                }

                MemErrorUsageThresholdMb = threshold;
            }

            // Ports
            var activeTcpPortsError = GetSettingParameterValue(
                     ObserverConstants.FabricSystemObserverConfigurationSectionName,
                     ObserverConstants.FabricSystemObserverNetworkErrorActivePorts);

            if (!string.IsNullOrEmpty(activeTcpPortsError))
            {
                _ = int.TryParse(activeTcpPortsError, out int threshold);
                ActiveTcpPortCountError = threshold;
            }

            var activeEphemeralPortsError = GetSettingParameterValue(
                    ObserverConstants.FabricSystemObserverConfigurationSectionName,
                    ObserverConstants.FabricSystemObserverNetworkErrorEphemeralPorts);

            if (!string.IsNullOrEmpty(activeEphemeralPortsError))
            {
                _ = int.TryParse(activeEphemeralPortsError, out int threshold);
                ActiveEphemeralPortCountError = threshold;
            }

            /* Warning thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuWarn = GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn))
            {
                _ = int.TryParse(cpuWarn, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverCpuWarningLimitPct}.");
                }

                CpuWarnUsageThresholdPct = threshold;
            }

            var memWarn = GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverMemoryWarningLimitMb);

            if (!string.IsNullOrEmpty(memWarn))
            {
                _ = int.TryParse(memWarn, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} MB is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverMemoryWarningLimitMb}.");
                }

                MemWarnUsageThresholdMb = threshold;
            }

            // Ports
            var activeTcpPortsWarning = GetSettingParameterValue(
                     ObserverConstants.FabricSystemObserverConfigurationSectionName,
                     ObserverConstants.FabricSystemObserverNetworkWarningActivePorts);

            if (!string.IsNullOrEmpty(activeTcpPortsWarning))
            {
                _ = int.TryParse(activeTcpPortsWarning, out int threshold);
                ActiveTcpPortCountWarning = threshold;
            }

            var activeEphemeralPortsWarning = GetSettingParameterValue(
                    ObserverConstants.FabricSystemObserverConfigurationSectionName,
                    ObserverConstants.FabricSystemObserverNetworkWarningEphemeralPorts);

            if (!string.IsNullOrEmpty(activeEphemeralPortsWarning))
            {
                _ = int.TryParse(activeEphemeralPortsWarning, out int threshold);
                ActiveEphemeralPortCountWarning = threshold;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            // Monitor Windows event log for SF and System Error/Critical events?
            // This can be noisy. Use wisely.
            var watchEvtLog = GetSettingParameterValue(
                ObserverConstants.FabricSystemObserverConfigurationSectionName,
                ObserverConstants.FabricSystemObserverMonitorWindowsEventLog);

            if (!string.IsNullOrEmpty(watchEvtLog) && bool.TryParse(watchEvtLog, out bool watchEl))
            {
                this.monitorWinEventLog = watchEl;
            }
        }

        private void GetProcessInfo(string procName)
        {
            // This is to support differences between Linux and Windows dotnet process naming pattern.
            // Default value is what Windows expects for proc name. In linux, the procname is an argument (typically) dotnet command.
            string dotnetArg = procName;
            Process[] processes = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && procName.Contains("dotnet "))
            {
                dotnetArg = $"{procName.Replace("dotnet ", string.Empty)}";
                processes = GetDotnetProcessesByFirstArgument(dotnetArg);
            }
            else
            {
                processes = Process.GetProcessesByName(procName);
            }

            if (processes.Length == 0)
            {
                return;
            }

            Stopwatch timer = new Stopwatch();

            foreach (var process in processes)
            {
                CpuUsage cpuUsage = new CpuUsage();

                try
                {
                    Token.ThrowIfCancellationRequested();

                    // Warm up the perf counters.
                    _ = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(process.Id);

                    // Ports
                    int activePortCount = OperatingSystemInfoProvider.Instance.GetActivePortCount(process.Id, FabricServiceContext);
                    TotalActivePortCountAllSystemServices += activePortCount;
                    int activeEphemeralPortCount = OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount(process.Id, FabricServiceContext);
                    TotalActiveEphemeralPortCountAllSystemServices += activeEphemeralPortCount;
                    
                    this.allActiveTcpPortData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(activePortCount);
                    this.allEphemeralTcpPortData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(activeEphemeralPortCount);

                    TimeSpan duration = TimeSpan.FromSeconds(5);

                    if (MonitorDuration > TimeSpan.MinValue)
                    {
                        duration = MonitorDuration;
                    }

                    timer.Start();

                    while (!process.HasExited && timer.Elapsed <= duration)
                    {
                        Token.ThrowIfCancellationRequested();

                        try
                        {
                            // CPU Time for service process.
                            int cpu = (int)cpuUsage.GetCpuUsagePercentageProcess(process);
                            this.allCpuData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(cpu);

                            // Private Working Set for service process.
                            float mem = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(process.Id);
                            this.allMemData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(mem);

                            Thread.Sleep(250);
                        }
                        catch (Exception e)
                        {
                            WriteToLogWithLevel(
                                ObserverName,
                                $"Can't observe {process} details:{Environment.NewLine}{e}",
                                LogLevel.Warning);

                            throw;
                        }
                    }
                }
                catch (Win32Exception)
                {
                    // This will always be the case if FabricObserver.exe is not running as Admin or LocalSystem.
                    // It's OK. Just means that the elevated process (like FabricHost.exe) won't be observed.
                    WriteToLogWithLevel(
                        ObserverName,
                        $"Can't observe {process.ProcessName} due to it's privilege level. FabricObserver must be running as System or Admin for this specific task.",
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

        private void ProcessResourceDataList<T>(
            IReadOnlyCollection<FabricResourceUsageData<T>> data,
            T thresholdError,
            T thresholdWarning)
                where T : struct
        {
            foreach (var dataItem in data)
            {
                Token.ThrowIfCancellationRequested();

                if (dataItem.Data.Count == 0 || dataItem.AverageDataValue <= 0)
                {
                    continue;
                }

                if (CsvFileLogger != null && CsvFileLogger.EnableCsvLogging)
                {
                    var fileName = "FabricSystemServices_" + NodeName;
                    var propertyName = data.First().Property;

                    // Log average data value to long-running store (CSV).
                    string dataLogMonitorType = propertyName;

                    switch (propertyName)
                    {
                        case ErrorWarningProperty.TotalMemoryConsumptionMb:
                            dataLogMonitorType = "Working Set %";
                            break;

                        case ErrorWarningProperty.TotalCpuTime:
                            dataLogMonitorType = "% CPU Time";
                            break;
                    }

                    CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Average", Math.Round(dataItem.AverageDataValue, 2));
                    CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Peak", Math.Round(Convert.ToDouble(dataItem.MaxDataValue)));
                }

                // This function will clear Data items in list (will call Clear() on the supplied FabricResourceUsageData instance's Data field..)
                ProcessResourceDataReportHealth(
                    dataItem,
                    thresholdError,
                    thresholdWarning,
                    SetHealthReportTimeToLive(),
                    HealthReportType.Application);
            }
        }
    }
}
