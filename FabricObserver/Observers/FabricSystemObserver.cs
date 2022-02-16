// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Concurrent;
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
    // FabricSystemObserver monitors all Fabric system service processes across various resource usage metrics: 
    // CPU Time, Private Workingset, Ephemeral and Total Active TCP ports, File Handles, Threads.
    public class FabricSystemObserver : ObserverBase
    {
        private const double KvsLvidsWarningPercentage = 75.0;
        private readonly List<string> processWatchList;
        private readonly bool isWindows;
        private Stopwatch stopwatch;
        private bool checkPrivateWorkingSet;

        // Health Report data container - For use in analysis to determine health state.
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> allCpuData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> allMemData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> allActiveTcpPortData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> allEphemeralTcpPortData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> allHandlesData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> allThreadsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> allAppKvsLvidsData;

        // Windows only. (EventLog).
        private List<EventRecord> evtRecordList = null;
        private bool monitorWinEventLog;
        private readonly object lockObj = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricSystemObserver"/> class.
        /// </summary>
        public FabricSystemObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            // Linux
            if (!isWindows)
            {
                processWatchList = new List<string>
                {
                    "Fabric",
                    "FabricDCA.dll",
                    "FabricDnsService",
                    "FabricCAS.dll",
                    "FabricFAS.dll",
                    "FabricGateway.exe",
                    "FabricHost",
                    "FabricIS.dll",
                    "FabricRM.exe",
                    "FabricUS.dll"
                };
            }
            else
            {
                // Windows
                processWatchList = new List<string>
                {
                    "Fabric",
                    "FabricApplicationGateway",
                    "FabricDCA",
                    "FabricDnsService",
                    "FabricFAS",
                    "FabricGateway",
                    "FabricHost",
                    "FabricIS",
                    "FabricRM"
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

        public float TotalAllocatedHandlesAllSystemServices
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

        public int AllocatedHandlesWarning
        {
            get; set;
        }

        public int AllocatedHandlesError
        {
            get; set;
        }

        public bool EnableConcurrentMonitoring
        {
            get; set;
        } = false;

        public bool EnableKvsLvidMonitoring
        {
            get; set;
        } = false;

        public ParallelOptions ParallelOptions
        {
            get; set;
        }

        public int ThreadCountError 
        { 
            get; set; 
        }

        public int ThreadCountWarning 
        { 
            get; set; 
        }

        public int TotalThreadsAllSystemServices 
        { 
            get; set; 
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            Token = token;

            if (Token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                ComputeResourceUsage();
            }
            catch (AggregateException e) when (!(e.InnerException is OperationCanceledException || e.InnerException is TaskCanceledException))
            {
                ObserverLogger.LogError( $"Unhandled exception in ObserveAsync:{Environment.NewLine}{e}");

                // Fix the bug..
                throw;
            }

            if (isWindows && IsObserverWebApiAppDeployed && monitorWinEventLog)
            {
                ReadServiceFabricWindowsEventLog();
            }

            await ReportAsync(token).ConfigureAwait(true);

            // The time it took to run this observer to completion.
            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;

            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration: {RunDuration}");
            }

            CleanUp();
            stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        private void ComputeResourceUsage()
        {
            Initialize();

            _ = Parallel.ForEach(processWatchList, ParallelOptions, (procName, state) =>
            {
                Token.ThrowIfCancellationRequested();

                try
                {
                    string dotnet = string.Empty;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && procName.EndsWith(".dll"))
                    {
                        dotnet = "dotnet ";
                    }

                    GetProcessInfoAsync($"{dotnet}{procName}").GetAwaiter().GetResult();
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                {
                    return;
                }
            });
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                Token.ThrowIfCancellationRequested();

                string info = string.Empty;

                if (allMemData != null)
                {
                    info += $"Fabric memory: {allMemData["Fabric"].AverageDataValue} MB{Environment.NewLine}" +
                            $"FabricDCA memory: {allMemData.FirstOrDefault(x => x.Key.Contains("FabricDCA")).Value.AverageDataValue} MB{Environment.NewLine}" +
                            $"FabricGateway memory: {allMemData.FirstOrDefault(x => x.Key.Contains("FabricGateway")).Value.AverageDataValue} MB{Environment.NewLine}" +

                            // On Windows, FO runs as NetworkUser by default and therefore can't monitor FabricHost process, which runs as System.
                            (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ?
                                $"FabricHost memory: {allMemData["FabricHost"].AverageDataValue} MB{Environment.NewLine}" : string.Empty);
                }

                if (allHandlesData != null)
                {
                    info += $"Fabric file handles: {allHandlesData["Fabric"].AverageDataValue}{Environment.NewLine}" +
                            $"FabricDCA file handles: {allHandlesData.FirstOrDefault(x => x.Key.Contains("FabricDCA")).Value.AverageDataValue}{Environment.NewLine}" +
                            $"FabricGateway file handles: {allHandlesData.FirstOrDefault(x => x.Key.Contains("FabricGateway")).Value.AverageDataValue}{Environment.NewLine}" +

                            // On Windows, FO runs as NetworkUser by default and therefore can't monitor FabricHost process, which runs as System. 
                            (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ?
                                $"FabricHost file handles: {allHandlesData["FabricHost"]?.AverageDataValue}{Environment.NewLine}" : string.Empty);
                }

                if (allThreadsData != null)
                {
                    info += $"Fabric threads: {allThreadsData["Fabric"].AverageDataValue}{Environment.NewLine}" +
                            $"FabricDCA threads: {allThreadsData.FirstOrDefault(x => x.Key.Contains("FabricDCA")).Value.AverageDataValue}{Environment.NewLine}" +
                            $"FabricGateway threads: {allThreadsData.FirstOrDefault(x => x.Key.Contains("FabricGateway")).Value.AverageDataValue}{Environment.NewLine}" +

                            // On Windows, FO runs as NetworkUser by default and therefore can't monitor FabricHost process, which runs as System. 
                            (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ?
                                $"FabricHost threads: {allThreadsData["FabricHost"]?.AverageDataValue}" : string.Empty);
                }

                // Informational report.
                TimeSpan timeToLiveWarning = GetHealthReportTimeToLive();
                var informationReport = new HealthReport
                {
                    Observer = ObserverName,
                    NodeName = NodeName,
                    HealthMessage = $"TCP ports in use by Fabric System services: {TotalActivePortCountAllSystemServices}{Environment.NewLine}" +
                                    $"Ephemeral TCP ports in use by Fabric System services: {TotalActiveEphemeralPortCountAllSystemServices}{Environment.NewLine}" +
                                    $"File handles in use by Fabric System services: {TotalAllocatedHandlesAllSystemServices}{Environment.NewLine}" +
                                    $"Threads in use by Fabric System services: {TotalThreadsAllSystemServices}{Environment.NewLine}{info}",

                    State = HealthState.Ok,
                    HealthReportTimeToLive = timeToLiveWarning,
                    ReportType = HealthReportType.Node
                };

                HealthReporter.ReportHealthToServiceFabric(informationReport);

                // Reset local tracking counters.
                TotalActivePortCountAllSystemServices = 0;
                TotalActiveEphemeralPortCountAllSystemServices = 0;
                TotalAllocatedHandlesAllSystemServices = 0;
                TotalThreadsAllSystemServices = 0;

                // CPU
                if (CpuErrorUsageThresholdPct > 0 || CpuWarnUsageThresholdPct > 0)
                {
                    ProcessResourceDataList(allCpuData, CpuErrorUsageThresholdPct, CpuWarnUsageThresholdPct);
                }

                // Memory
                if (MemErrorUsageThresholdMb > 0 || MemWarnUsageThresholdMb > 0)
                {
                    ProcessResourceDataList(allMemData, MemErrorUsageThresholdMb, MemWarnUsageThresholdMb);
                }

                // Ports - Active TCP
                if (ActiveTcpPortCountError > 0 || ActiveTcpPortCountWarning > 0)
                {
                    ProcessResourceDataList(allActiveTcpPortData, ActiveTcpPortCountError, ActiveTcpPortCountWarning);
                }

                // Ports - Ephemeral
                if (ActiveEphemeralPortCountError > 0 || ActiveEphemeralPortCountWarning > 0)
                {
                    ProcessResourceDataList(allEphemeralTcpPortData, ActiveEphemeralPortCountError, ActiveEphemeralPortCountWarning);
                }

                // Handles
                if (AllocatedHandlesError > 0 || AllocatedHandlesWarning > 0)
                {
                    ProcessResourceDataList(allHandlesData, AllocatedHandlesError, AllocatedHandlesWarning);
                }

                // Threads
                if (ThreadCountError > 0 || ThreadCountWarning > 0)
                {
                    ProcessResourceDataList(allThreadsData, ThreadCountError, ThreadCountWarning);
                }

                // KVS LVIDs - Windows-only (EnableKvsLvidMonitoring will always be false otherwise)
                if (EnableKvsLvidMonitoring && allAppKvsLvidsData.Count > 0)
                {
                    ProcessResourceDataList(allAppKvsLvidsData, 0, KvsLvidsWarningPercentage);
                }

                // No need to progress on Linux.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return Task.CompletedTask;
                }

                // Windows Event Log
                if (IsObserverWebApiAppDeployed && monitorWinEventLog)
                {
                    // SF Eventlog Errors?
                    // Write this out to a new file, for use by the web front end log viewer.
                    // Format = HTML.
                    int count = evtRecordList.Count();
                    var logPath = Path.Combine(ObserverLogger.LogFolderBasePath, "EventVwrErrors.txt");

                    // Remove existing file.
                    if (File.Exists(logPath))
                    {
                        try
                        {
                            File.Delete(logPath);
                        }
                        catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
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

                        foreach (var evt in evtRecordList.Distinct())
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
                        evtRecordList.Clear();
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogError($"Unhandled exception in ReportAsync:{Environment.NewLine}{e}");

                // Fix the bug..
                throw;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// ReadServiceFabricWindowsEventLog().
        /// </summary>
        private void ReadServiceFabricWindowsEventLog()
        {
            if (!isWindows)
            {
                return;
            }

            string sfOperationalLogSource = "Microsoft-ServiceFabric/Operational";
            string sfAdminLogSource = "Microsoft-ServiceFabric/Admin";
            string systemLogSource = "System";
            string sfLeaseAdminLogSource = "Microsoft-ServiceFabric-Lease/Admin";
            string sfLeaseOperationalLogSource = "Microsoft-ServiceFabric-Lease/Operational";

            var range2Days = DateTime.UtcNow.AddDays(-1);
            var format = range2Days.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00K", CultureInfo.InvariantCulture);
            var datexQuery = $"*[System/TimeCreated/@SystemTime >='{format}']";

            // Critical and Errors only.
            string xQuery = "*[System/Level <= 2] and " + datexQuery;

            // SF Admin Event Store.
            var evtLogQuery = new EventLogQuery(sfAdminLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }

            // SF Operational Event Store.
            evtLogQuery = new EventLogQuery(sfOperationalLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }

            // SF Lease Admin Event Store.
            evtLogQuery = new EventLogQuery(sfLeaseAdminLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }

            // SF Lease Operational Event Store.
            evtLogQuery = new EventLogQuery(sfLeaseOperationalLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }

            // System Event Store.
            evtLogQuery = new EventLogQuery(systemLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }
        }

        private Process[] GetDotnetLinuxProcessesByFirstArgument(string argument)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new PlatformNotSupportedException("This function should only be called on Linux platforms.");
            }

            var result = new List<Process>();
            var processes = Process.GetProcessesByName("dotnet");

            for (int i = 0; i < processes.Length; ++i)
            {
                Token.ThrowIfCancellationRequested();

                Process process = processes[i];

                try
                {
                    string cmdline = File.ReadAllText($"/proc/{process.Id}/cmdline");

                    // dotnet /mnt/sfroot/_App/__FabricSystem_App4294967295/US.Code.Current/FabricUS.dll 
                    if (cmdline.Contains("/mnt/sfroot/_App/"))
                    {
                        string bin = cmdline[(cmdline.LastIndexOf("/", StringComparison.Ordinal) + 1)..];

                        if (string.Equals(argument, bin, StringComparison.InvariantCulture))
                        {
                            result.Add(process);
                        }
                    }
                    else if (cmdline.Contains("Fabric"))
                    {
                        // dotnet FabricDCA.dll
                        string[] parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length > 1 && string.Equals(argument, parts[1], StringComparison.Ordinal))
                        {
                            result.Add(process);
                        }
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
            Token.ThrowIfCancellationRequested();
            
            // fabric:/System
            MonitoredAppCount = 1;
            MonitoredServiceProcessCount = processWatchList.Count;
            int frudCapacity = 4;

            if (UseCircularBuffer)
            {
                frudCapacity = DataCapacity > 0 ? DataCapacity : 5;
            }
            else if (MonitorDuration > TimeSpan.MinValue)
            {
                frudCapacity = (int)MonitorDuration.TotalSeconds * 4;
            }

            stopwatch ??= new Stopwatch();
            stopwatch.Start();
            SetThresholdSFromConfiguration();

            // CPU data
            if (allCpuData == null && (CpuErrorUsageThresholdPct > 0 || CpuWarnUsageThresholdPct > 0))
            {
                allCpuData = new ConcurrentDictionary<string, FabricResourceUsageData<int>>();

                foreach (var proc in processWatchList)
                {
                    _ = allCpuData.TryAdd(
                                    proc,
                                    new FabricResourceUsageData<int>(
                                            ErrorWarningProperty.TotalCpuTime,
                                            proc,
                                            frudCapacity,
                                            UseCircularBuffer,
                                            EnableConcurrentMonitoring));
                }
            }

            // Memory data
            if (allMemData == null && (MemErrorUsageThresholdMb > 0 || MemWarnUsageThresholdMb > 0))
            {
                allMemData = new ConcurrentDictionary<string, FabricResourceUsageData<float>>();

                foreach (var proc in processWatchList)
                {
                    _ = allMemData.TryAdd(
                                    proc,
                                    new FabricResourceUsageData<float>(
                                            ErrorWarningProperty.TotalMemoryConsumptionMb,
                                            proc,
                                            frudCapacity,
                                            UseCircularBuffer,
                                            EnableConcurrentMonitoring));
                }
            }

            // Ports
            if (allActiveTcpPortData == null && (ActiveTcpPortCountError > 0 || ActiveTcpPortCountWarning > 0))
            {
                allActiveTcpPortData = new ConcurrentDictionary<string, FabricResourceUsageData<int>>();

                foreach (var proc in processWatchList)
                {
                    _ = allActiveTcpPortData.TryAdd(
                                              proc,
                                              new FabricResourceUsageData<int>(
                                                    ErrorWarningProperty.TotalActivePorts,
                                                    proc,
                                                    frudCapacity,
                                                    UseCircularBuffer,
                                                    EnableConcurrentMonitoring));
                }
            }

            if (allEphemeralTcpPortData == null && (ActiveEphemeralPortCountError > 0 || ActiveEphemeralPortCountWarning > 0))
            {
                allEphemeralTcpPortData = new ConcurrentDictionary<string, FabricResourceUsageData<int>>();

                foreach (var proc in processWatchList)
                {
                    _ = allEphemeralTcpPortData.TryAdd(
                                                 proc,
                                                 new FabricResourceUsageData<int>(
                                                        ErrorWarningProperty.TotalEphemeralPorts,
                                                        proc,
                                                        frudCapacity,
                                                        UseCircularBuffer,
                                                        EnableConcurrentMonitoring));
                }
            }

            // Handles
            if (allHandlesData == null && (AllocatedHandlesError > 0 || AllocatedHandlesWarning > 0))
            {
                allHandlesData = new ConcurrentDictionary<string, FabricResourceUsageData<float>>();

                foreach (var proc in processWatchList)
                {
                    _ = allHandlesData.TryAdd(
                                        proc,
                                        new FabricResourceUsageData<float>(
                                                ErrorWarningProperty.TotalFileHandles,
                                                proc,
                                                frudCapacity,
                                                UseCircularBuffer,
                                                EnableConcurrentMonitoring));
                }
            }

            // Threads
            if (allThreadsData == null && (ThreadCountError > 0 || ThreadCountWarning > 0))
            {
                allThreadsData = new ConcurrentDictionary<string, FabricResourceUsageData<int>>();

                foreach (var proc in processWatchList)
                {
                    _ = allThreadsData.TryAdd(
                                        proc,
                                        new FabricResourceUsageData<int>(
                                                ErrorWarningProperty.TotalThreadCount,
                                                proc,
                                                frudCapacity,
                                                UseCircularBuffer,
                                                EnableConcurrentMonitoring));
                }
            }

            // KVS LVIDs - Windows-only (EnableKvsLvidMonitoring will always be false otherwise)
            if (EnableKvsLvidMonitoring && allAppKvsLvidsData == null)
            {
                allAppKvsLvidsData = new ConcurrentDictionary<string, FabricResourceUsageData<double>>();

                foreach (var proc in processWatchList)
                {
                    if (proc != "Fabric" && proc != "FabricRM")
                    {
                        continue;
                    }

                    _ = allAppKvsLvidsData.TryAdd(
                                        proc,
                                        new FabricResourceUsageData<double>(
                                                ErrorWarningProperty.TotalKvsLvidsPercent,
                                                proc,
                                                frudCapacity,
                                                UseCircularBuffer,
                                                EnableConcurrentMonitoring));
                }
            }

            if (isWindows && monitorWinEventLog && evtRecordList == null)
            {
                evtRecordList = new List<EventRecord>();
            }
        }

        private void SetThresholdSFromConfiguration()
        {
            /* Error thresholds */
            
            Token.ThrowIfCancellationRequested();

            // CPU Time
            var cpuError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverCpuErrorLimitPct);

            if (!string.IsNullOrWhiteSpace(cpuError))
            {
                _ = int.TryParse(cpuError, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverCpuErrorLimitPct}.");
                }

                CpuErrorUsageThresholdPct = threshold;
            }

            /* Memory - Private or Full Working Set */
            var privateWS = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MonitorPrivateWorkingSet);
            
            if (!string.IsNullOrWhiteSpace(privateWS))
            {
                _ = bool.TryParse(privateWS, out bool privWs);
               checkPrivateWorkingSet = privWs;
            }

            // Memory - Working Set MB
            var memError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverMemoryErrorLimitMb);

            if (!string.IsNullOrWhiteSpace(memError))
            {
                _ = int.TryParse(memError, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverMemoryErrorLimitMb}.");
                }

                MemErrorUsageThresholdMb = threshold;
            }

            // All TCP Ports
            var activeTcpPortsError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverNetworkErrorActivePorts);

            if (!string.IsNullOrWhiteSpace(activeTcpPortsError))
            {
                _ = int.TryParse(activeTcpPortsError, out int threshold);
                ActiveTcpPortCountError = threshold;
            }

            // Ephemeral TCP Ports
            var activeEphemeralPortsError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverNetworkErrorEphemeralPorts);

            if (!string.IsNullOrWhiteSpace(activeEphemeralPortsError))
            {
                _ = int.TryParse(activeEphemeralPortsError, out int threshold);
                ActiveEphemeralPortCountError = threshold;
            }

            // File Handles
            var handlesError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverErrorHandles);

            if (!string.IsNullOrWhiteSpace(handlesError))
            {
                _ = int.TryParse(handlesError, out int threshold);
                AllocatedHandlesError = threshold;
            }

            // Threads
            var threadCountError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverErrorThreadCount);

            if (!string.IsNullOrWhiteSpace(threadCountError))
            {
                _ = int.TryParse(threadCountError, out int threshold);
                ThreadCountError = threshold;
            }

            /* Warning thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuWarn = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverCpuWarningLimitPct);

            if (!string.IsNullOrWhiteSpace(cpuWarn))
            {
                _ = int.TryParse(cpuWarn, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverCpuWarningLimitPct}.");
                }

                CpuWarnUsageThresholdPct = threshold;
            }

            var memWarn = GetSettingParameterValue( ConfigurationSectionName, ObserverConstants.FabricSystemObserverMemoryWarningLimitMb);

            if (!string.IsNullOrWhiteSpace(memWarn))
            {
                _ = int.TryParse(memWarn, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} MB is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverMemoryWarningLimitMb}.");
                }

                MemWarnUsageThresholdMb = threshold;
            }

            // Ports
            var activeTcpPortsWarning = GetSettingParameterValue( ConfigurationSectionName, ObserverConstants.FabricSystemObserverNetworkWarningActivePorts);

            if (!string.IsNullOrWhiteSpace(activeTcpPortsWarning))
            {
                _ = int.TryParse(activeTcpPortsWarning, out int threshold);
                ActiveTcpPortCountWarning = threshold;
            }

            var activeEphemeralPortsWarning = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverNetworkWarningEphemeralPorts);

            if (!string.IsNullOrWhiteSpace(activeEphemeralPortsWarning))
            {
                _ = int.TryParse(activeEphemeralPortsWarning, out int threshold);
                ActiveEphemeralPortCountWarning = threshold;
            }

            // File Handles
            var handlesWarning = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverWarningHandles);

            if (!string.IsNullOrWhiteSpace(handlesWarning))
            {
                _ = int.TryParse(handlesWarning, out int threshold);
                AllocatedHandlesWarning = threshold;
            }

            // Threads
            var threadCountWarning = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverWarningThreadCount);
            
            if (!string.IsNullOrWhiteSpace(threadCountWarning))
            {
                _ = int.TryParse(threadCountWarning, out int threshold);
                ThreadCountWarning = threshold;
            }

            // Concurrency/Parallelism support.
            if (Environment.ProcessorCount >= 4 && bool.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableConcurrentMonitoring), out bool enableConcurrency))
            {
                EnableConcurrentMonitoring = enableConcurrency;
            }

            // Default to using [1/4 of available logical processors ~* 2] threads if MaxConcurrentTasks setting is not supplied.
            // So, this means around 10 - 11 threads (or less) could be used if processor count = 20. This is only being done to limit the impact
            // FabricObserver has on the resources it monitors and alerts on...
            int maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));
            if (int.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxConcurrentTasks), out int maxTasks))
            {
                maxDegreeOfParallelism = maxTasks;
            }

            ParallelOptions = new ParallelOptions
            {
                // Parallelism only makes sense for capable CPU configurations. The minimum requirement is 4 logical processors; which would map to more than 1 available core.
                MaxDegreeOfParallelism = EnableConcurrentMonitoring  ? maxDegreeOfParallelism : 1,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            // KVS LVID Monitoring - Windows-only.
            if (isWindows && bool.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableKvsLvidMonitoringParameter), out bool enableLvidMonitoring))
            {
                // Observers that monitor LVIDs should ensure the static ObserverManager.CanInstallLvidCounter is true before attempting to monitor LVID usage.
                EnableKvsLvidMonitoring = enableLvidMonitoring && ObserverManager.IsLvidCounterEnabled;
            }

            // Monitor Windows event log for SF and System Error/Critical events?
            // This can be noisy. Use wisely. Return if running on Linux.
            if (!isWindows)
            {
                return;
            }

            var watchEvtLog = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverMonitorWindowsEventLog);
            if (!string.IsNullOrWhiteSpace(watchEvtLog) && bool.TryParse(watchEvtLog, out bool watchEl))
            {
                monitorWinEventLog = watchEl;
            }
        }

        private async Task GetProcessInfoAsync(string procName)
        {
            // This is to support differences between Linux and Windows dotnet process naming pattern.
            // Default value is what Windows expects for proc name. In linux, the procname is an argument (typically) of a dotnet command.
            string dotnetArg = procName;
            Process[] processes;

            if (!isWindows && procName.Contains("dotnet"))
            {
                dotnetArg = $"{procName.Replace("dotnet ", string.Empty)}";
                processes = GetDotnetLinuxProcessesByFirstArgument(dotnetArg);
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

            for (int i = 0; i < processes.Length; ++i)
            {
                Token.ThrowIfCancellationRequested();

                Process process = processes[i];

                try
                { 
                    // Ports - Active TCP All
                    int activePortCount = OSInfoProvider.Instance.GetActiveTcpPortCount(process.Id, FabricServiceContext);
                    TotalActivePortCountAllSystemServices += activePortCount;
                    
                    if (ActiveTcpPortCountError > 0 || ActiveTcpPortCountWarning > 0)
                    {
                        allActiveTcpPortData[dotnetArg].AddData(activePortCount);
                    }

                    // Ports - Active TCP Ephemeral
                    int activeEphemeralPortCount = OSInfoProvider.Instance.GetActiveEphemeralPortCount(process.Id, FabricServiceContext);
                    TotalActiveEphemeralPortCountAllSystemServices += activeEphemeralPortCount;
                    
                    if (ActiveEphemeralPortCountError > 0 || ActiveEphemeralPortCountWarning > 0)
                    {
                        allEphemeralTcpPortData[dotnetArg].AddData(activeEphemeralPortCount);
                    }

                    // Allocated Handles
                    float handles;

                    if (isWindows)
                    {
                        handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(process.Id);
                    }
                    else
                    {
                        handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(process.Id, FabricServiceContext);
                    }

                    TotalAllocatedHandlesAllSystemServices += handles;

                    // Threads
                    int threads = 0;

                    if (isWindows)
                    {
                        try
                        {
                            threads = NativeMethods.GetProcessThreadCount(process.Id);
                        }
                        catch (Win32Exception we)
                        {
                            ObserverLogger.LogInfo($"{we}");
                        }
                    }
                    else
                    {
                        threads = ProcessInfoProvider.GetProcessThreadCount(process.Id);
                    }

                    TotalThreadsAllSystemServices += threads;
                    
                    // No need to proceed further if there are no configuration settings for CPU, Memory, Handles thresholds.
                    // Returning here is correct as supplied thresholds apply to all system services.
                    if (CpuErrorUsageThresholdPct <= 0 && CpuWarnUsageThresholdPct <= 0 && MemErrorUsageThresholdMb <= 0 && MemWarnUsageThresholdMb <= 0
                        && AllocatedHandlesError <= 0 && AllocatedHandlesWarning <= 0 && ThreadCountError <= 0 && ThreadCountWarning <= 0 && !EnableKvsLvidMonitoring)
                    {
                        return;
                    }

                    // Handles/FDs
                    if (AllocatedHandlesError > 0 || AllocatedHandlesWarning > 0)
                    {
                        allHandlesData[dotnetArg].AddData(handles);
                    }

                    // Threads
                    if (ThreadCountError > 0 || ThreadCountWarning > 0)
                    {
                        allThreadsData[dotnetArg].AddData(threads);
                    }

                    // KVS LVIDs
                    if (EnableKvsLvidMonitoring && (dotnetArg == "Fabric" || dotnetArg == "FabricRM"))
                    {
                        double lvidPct = ProcessInfoProvider.Instance.GetProcessKvsLvidsUsagePercentage(dotnetArg);

                        // ProcessGetCurrentKvsLvidsUsedPercentage internally handles exceptions and will always return -1 when it fails.
                        if (lvidPct > -1)
                        {
                            allAppKvsLvidsData[dotnetArg].AddData(lvidPct);
                        }
                    }

                    CpuUsage cpuUsage = new CpuUsage();
                    TimeSpan duration = TimeSpan.FromSeconds(1);

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
                            if (CpuErrorUsageThresholdPct > 0 || CpuWarnUsageThresholdPct > 0)
                            {
                                int cpu = (int)cpuUsage.GetCpuUsagePercentageProcess(process.Id);
                                allCpuData[dotnetArg].AddData(cpu);
                            }

                            // Memory MB
                            if (MemErrorUsageThresholdMb > 0 || MemWarnUsageThresholdMb > 0)
                            {
                                float processMem = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(process.Id, checkPrivateWorkingSet ? dotnetArg : null, checkPrivateWorkingSet);
                                allMemData[dotnetArg].AddData(processMem);
                            }

                            await Task.Delay(250, Token).ConfigureAwait(true);
                        }
                        catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                        {
                            ObserverLogger.LogWarning($"Unhandled Exception thrown in GetProcessInfoAsync:{Environment.NewLine}{e}");

                            // Fix the bug..
                            throw;
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                {
                    // This will be a Win32Exception or InvalidOperationException if FabricObserver.exe is not running as Admin or LocalSystem on Windows.
                    // It's OK. Just means that the elevated process (like FabricHost.exe) won't be observed. 
                    // It is generally *not* worth running FO process as a Windows elevated user just for this scenario. On Linux, FO always should be run as normal user, not root.
#if DEBUG
                    ObserverLogger.LogWarning($"Can't observe {procName} due to it's privilege level. " +
                                              $"FabricObserver must be running as System or Admin on Windows for this specific task.");
#endif       
                    continue;
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    ObserverLogger.LogError("Unhandled exception in GetProcessInfoAsync:{Environment.NewLine}{e}");

                    // Fix the bug..
                    throw;
                }
                finally
                {
                    process?.Dispose();
                }

                timer.Stop();
                timer.Reset();

                await Task.Delay(150, Token).ConfigureAwait(false);
            }
        }

        private void ProcessResourceDataList<T>(
                            ConcurrentDictionary<string, FabricResourceUsageData<T>> data,
                            T thresholdError,
                            T thresholdWarning)
                                where T : struct
        {
            string fileName = null;
            TimeSpan TTL = GetHealthReportTimeToLive();

            if (EnableCsvLogging)
            {
                fileName = $"FabricSystemServices{(CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives ? "_" + DateTime.UtcNow.ToString("o") : string.Empty)}";
            }

            _ = Parallel.ForEach (data, ParallelOptions, (state) =>
            {
                Token.ThrowIfCancellationRequested();

                var dataItem = state.Value;

                if (dataItem.Data.Count() == 0 || dataItem.AverageDataValue <= 0)
                {
                    return;
                }

                if (EnableCsvLogging)
                {
                    var propertyName = dataItem.Property;

                    /* Log average data value to long-running store (CSV).*/

                    var dataLogMonitorType = propertyName switch
                    {
                        ErrorWarningProperty.TotalCpuTime => "% CPU Time",
                        ErrorWarningProperty.TotalMemoryConsumptionMb => "Working Set %",
                        ErrorWarningProperty.TotalActivePorts => "Active TCP Ports",
                        ErrorWarningProperty.TotalEphemeralPorts => "Active Ephemeral Ports",
                        ErrorWarningProperty.TotalFileHandlesPct => "Allocated (in use) File Handles %",
                        ErrorWarningProperty.TotalThreadCount => "Threads",
                        _ => propertyName
                    };

                    // Log pid
                    try
                    {
                        int procId = -1;
                        Process[] p = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                            ? GetDotnetLinuxProcessesByFirstArgument(dataItem.Id)
                            : Process.GetProcessesByName(dataItem.Id);

                        if (p.Length > 0)
                        {
                            procId = p.First().Id;
                        }

                        if (procId > 0)
                        {
                            lock (lockObj)
                            {
                                CsvFileLogger.LogData(fileName, dataItem.Id, "ProcessId", "", procId);
                            }
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                    {

                    }

                    lock (lockObj)
                    {
                        CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Average", dataItem.AverageDataValue);
                        CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Peak", Convert.ToDouble(dataItem.MaxDataValue));
                    }
                }

                ProcessResourceDataReportHealth(
                        dataItem,
                        thresholdError,
                        thresholdWarning,
                        TTL,
                        HealthReportType.Application); 
            });
        }

        private void CleanUp()
        {
            if (allCpuData != null && !allCpuData.Any(frud => frud.Value.ActiveErrorOrWarning))
            {
                allCpuData?.Clear();
                allCpuData = null;
            }

            if (allEphemeralTcpPortData != null && !allEphemeralTcpPortData.Any(frud => frud.Value.ActiveErrorOrWarning))
            {
                allEphemeralTcpPortData?.Clear();
                allEphemeralTcpPortData = null;
            }

            if (allHandlesData != null && !allHandlesData.Any(frud => frud.Value.ActiveErrorOrWarning))
            {
                allHandlesData?.Clear();
                allHandlesData = null;
            }

            if (allMemData != null && !allMemData.Any(frud => frud.Value.ActiveErrorOrWarning))
            {
                allMemData?.Clear();
                allMemData = null;
            }

            if (allActiveTcpPortData != null && !allActiveTcpPortData.Any(frud => frud.Value.ActiveErrorOrWarning))
            {
                allActiveTcpPortData?.Clear();
                allActiveTcpPortData = null;
            }

            if (allThreadsData != null && !allThreadsData.Any(frud => frud.Value.ActiveErrorOrWarning))
            {
                allThreadsData?.Clear();
                allThreadsData = null;
            }

            if (allAppKvsLvidsData != null && !allAppKvsLvidsData.Any(frud => frud.Value.ActiveErrorOrWarning))
            {
                allAppKvsLvidsData?.Clear();
                allAppKvsLvidsData = null;
            }
        }
    }
}
