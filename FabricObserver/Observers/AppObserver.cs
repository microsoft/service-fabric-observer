// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FabricObserver.Interfaces;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.Utilities.ServiceFabric;

namespace FabricObserver.Observers
{
    // This observer monitors the behavior of user SF service processes (and their children) and signals Warning and Error based on user-supplied resource thresholds
    // in AppObserver.config.json. This observer will also emit telemetry (ETW, LogAnalytics/AppInsights) if enabled in Settings.xml (ObserverManagerConfiguration) and ApplicationManifest.xml (AppObserverEnableEtw).
    public sealed class AppObserver : ObserverBase
    {
        private const double KvsLvidsWarningPercentage = 75.0;
        private const double MaxRGMemoryInUsePercent = 90.0;
        private const int MaxSameNamedProcesses = 50;

        // These are the concurrent data structures that hold all monitoring data for all application service targets for specific metrics.
        // In the case where machine has capable CPU configuration and AppObserverEnableConcurrentMonitoring is enabled, these ConcurrentDictionaries
        // will be read from and written to by multiple threads. In the case where concurrency is not possible (or not enabled), they will sort of act as "normal"
        // Dictionaries (not precisely) since the monitoring loop will always be sequential (exactly one thread, so no internal locking) and there will not be *any* concurrent reads/writes.
        // The modest cost in memory allocation in the sequential processing case is not an issue here.
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppMemDataMb;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppMemDataPercent;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppTotalActivePortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppEphemeralPortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppEphemeralPortsDataPercent;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppHandlesData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppThreadsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppKvsLvidsData;

        // Windows-only
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppPrivateBytesDataMb;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppPrivateBytesDataPercent;

        // Windows-only for now.
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppRGMemoryUsagePercent;

        // Stores process id (key) / process name pairs for all monitored service processes.
        private ConcurrentDictionary<int, string> processInfoDictionary;

        // _userTargetList is the list of ApplicationInfo objects representing app/app types supplied in user configuration (AppObserver.config.json).
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<ApplicationInfo> userTargetList;

        // _deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied target list.
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<ApplicationInfo> deployedTargetList;

        // _deployedApps is the List of all apps currently deployed on the local Fabric node.
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<DeployedApplication> deployedApps;

        private readonly Stopwatch stopwatch;
        private readonly object lockObj = new object();
        private FabricClientUtilities fabricClientUtilities;
        private ParallelOptions parallelOptions;
        private string fileName;
        private int appCount;
        private int serviceCount;
        private NativeMethods.SafeObjectHandle handleToProcSnapshot = null;
        
        private NativeMethods.SafeObjectHandle Win32HandleToProcessSnapshot
        {
            get
            {
                // This is only useful for Windows.
                if (!IsWindows)
                {
                    return null;
                }

                if (handleToProcSnapshot == null)
                {
                    lock (lockObj)
                    {
                        if (handleToProcSnapshot == null)
                        {
                            handleToProcSnapshot = 
                                NativeMethods.CreateToolhelp32Snapshot((uint)NativeMethods.CreateToolhelp32SnapshotFlags.TH32CS_SNAPPROCESS, 0);
                            
                            if (handleToProcSnapshot.IsInvalid)
                            {
                                string message = $"HandleToProcessSnapshot: Failed to get process snapshot with error code {Marshal.GetLastWin32Error()}";
                                ObserverLogger.LogWarning(message);
                                throw new Win32Exception(message);
                            }
                        }
                    }
                }

                return handleToProcSnapshot;
            }
        }

        // ReplicaOrInstanceList is the List of all replicas or instances that will be monitored during the current run.
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        public List<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList;

        public int MaxChildProcTelemetryDataCount
        {
            get; set;
        }

        public bool EnableChildProcessMonitoring
        {
            get; set;
        }

        public string JsonConfigPath
        {
            get; set;
        }

        public bool EnableConcurrentMonitoring
        {
            get; set;
        }

        public bool EnableProcessDumps
        {
            get; set;
        }

        public bool EnableKvsLvidMonitoring
        {
            get; set;
        }

        public int OperationalHealthEvents
        {
            get; set;
        }

        public bool CheckPrivateWorkingSet
        {
            get; set;
        }

        public bool MonitorResourceGovernanceLimits
        {
            get; set;
        }

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public AppObserver(StatelessServiceContext context) : base(null, context)
        {
            stopwatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            ObserverLogger.LogInfo($"Started ObserveAsync.");

            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                ObserverLogger.LogInfo($"RunInterval ({RunInterval}) has not elapsed. Exiting.");
                return;
            }

            Token = token;
            stopwatch.Start();

            try
            {
                bool initialized = await InitializeAsync();

                if (!initialized)
                {
                    ObserverLogger.LogWarning("AppObserver was unable to initialize correctly due to misconfiguration. " +
                                              "Please check your AppObserver configuration settings.");
                    stopwatch.Stop();
                    stopwatch.Reset();
                    CleanUp();
                    LastRunDateTime = DateTime.Now;
                    return;
                }
            }
            catch (Exception e)
            {
                ObserverLogger.LogWarning($"DEBUG: InitializeAsync failure:{Environment.NewLine}{e}");
                throw;
            }

            ParallelLoopResult result = await MonitorDeployedAppsAsync(token);
            
            if (result.IsCompleted)
            {
                await ReportAsync(token);
            }

            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;
           
            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration ({ReplicaOrInstanceList?.Count} service processes observed) {(parallelOptions.MaxDegreeOfParallelism == 1 ? "without" : "with")} " +
                                       $"Parallel Processing (Processors: {Environment.ProcessorCount} MaxDegreeOfParallelism: {parallelOptions.MaxDegreeOfParallelism}): {RunDuration}.");
            }

            CleanUp();
            stopwatch.Reset();
            ObserverLogger.LogInfo($"Completed ObserveAsync.");
            LastRunDateTime = DateTime.Now;
        }
       
        public override Task ReportAsync(CancellationToken token)
        {
            if (deployedTargetList.Count == 0)
            {
                return Task.CompletedTask;
            }

            ObserverLogger.LogInfo($"Started ReportAsync.");

            //DEBUG
            //var stopwatch = Stopwatch.StartNew();
            TimeSpan TTL = GetHealthReportTimeToLive();

            // This will run sequentially (with 1 thread) if the underlying CPU config does not meet the requirements for concurrency (e.g., if logical procs < 4).
            _ = Parallel.For (0, ReplicaOrInstanceList.Count, parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                }

                var repOrInst = ReplicaOrInstanceList[i];
                
                if (repOrInst.HostProcessId < 1)
                {
                    return;
                }

                // For use in process family tree monitoring.
                ConcurrentQueue<ChildProcessTelemetryData> childProcessTelemetryDataList = null;

                string processName = null;
                int processId = 0;
                ApplicationInfo app = null;
                bool hasChildProcs = EnableChildProcessMonitoring && repOrInst.ChildProcesses != null;

                if (hasChildProcs)
                {
                    childProcessTelemetryDataList = new ConcurrentQueue<ChildProcessTelemetryData>();
                }

                if (!deployedTargetList.Any(
                         a => (a.TargetApp != null && a.TargetApp == repOrInst.ApplicationName.OriginalString) ||
                              (a.TargetAppType != null && a.TargetAppType == repOrInst.ApplicationTypeName)))
                {
                    return;
                }

                app = deployedTargetList.First(
                        a => (a.TargetApp != null && a.TargetApp == repOrInst.ApplicationName.OriginalString) ||
                                (a.TargetAppType != null && a.TargetAppType == repOrInst.ApplicationTypeName));
                

                // process serviceIncludeList config items for a single app.
                if (app?.ServiceIncludeList != null)
                {
                    // Ensure the service is the one we are looking for.
                    if (deployedTargetList.Any(
                            a => a.ServiceIncludeList != null &&
                                    a.ServiceIncludeList.Contains(repOrInst.ServiceName.OriginalString.Remove(0, repOrInst.ApplicationName.OriginalString.Length + 1))))
                    {
                        // It could be the case that user config specifies multiple inclusion lists for a single app/type in user configuration. We want the correct service here.
                        app = deployedTargetList.First(
                                a => a.ServiceIncludeList != null &&
                                a.ServiceIncludeList.Contains(repOrInst.ServiceName.OriginalString.Remove(0, repOrInst.ApplicationName.OriginalString.Length + 1)));
                    }
                }
                
                try
                {
                    processId = (int)repOrInst.HostProcessId;

                    // Make sure the process id was monitored.
                    if (!processInfoDictionary.ContainsKey(processId))
                    {
                        return;
                    }

                    processName = processInfoDictionary[processId];

                    // Make sure the target process still exists, otherwise why report on it (it was ephemeral as far as this run of AO is concerned).
                    if (!EnsureProcess(processName, processId))
                    {
                        return;
                    }
                }
                catch (Exception e) when (e is ArgumentException)
                {
                    return;
                }

                string appNameOrType = GetAppNameOrType(repOrInst);
                var id = $"{appNameOrType}:{processName}{processId}";

                // Locally Log (csv) CPU/Mem/FileHandles/Ports per app service process.
                if (EnableCsvLogging)
                {
                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring.
                    if (processName == "Fabric")
                    {
                        return;
                    }

                    // This lock is required.
                    lock (lockObj)
                    {
                        fileName = $"{processName}{(CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives ? "_" + DateTime.UtcNow.ToString("o") : string.Empty)}";

                        // BaseLogDataLogFolderPath is set in ObserverBase or a default one is created by CsvFileLogger.
                        // This means a new folder will be added to the base path.
                        if (CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives)
                        {
                            CsvFileLogger.DataLogFolder = processName;
                        }

                        // Log pid..
                        CsvFileLogger.LogData(fileName, id, "ProcessId", "", processId);

                        // Log resource usage data to CSV files.
                        LogAllAppResourceDataToCsv(id);
                    }
                }

                // CPU Time (Percent)
                if (AllAppCpuData.ContainsKey(id))
                {
                    var parentFrud = AllAppCpuData[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppCpuData, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    // Parent's and aggregated (summed) descendant process data (if any).
                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.CpuErrorLimitPercent,
                        app.CpuWarningLimitPercent,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }

                // Working Set (MB)
                if (AllAppMemDataMb.ContainsKey(id))
                {
                    var parentFrud = AllAppMemDataMb[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppMemDataMb, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.MemoryErrorLimitMb,
                        app.MemoryWarningLimitMb,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }

                // Working Set (Percent)
                if (AllAppMemDataPercent.ContainsKey(id))
                {
                    var parentFrud = AllAppMemDataPercent[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppMemDataPercent, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.MemoryErrorLimitPercent,
                        app.MemoryWarningLimitPercent,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }

                // Private Bytes (MB)
                if (AllAppPrivateBytesDataMb.ContainsKey(id))
                {
                    var parentFrud = AllAppPrivateBytesDataMb[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppPrivateBytesDataMb, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    if (app.WarningPrivateBytesMb > 0 || app.ErrorPrivateBytesMb > 0)
                    {
                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.ErrorPrivateBytesMb,
                            app.WarningPrivateBytesMb,
                            TTL,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId);
                    }
                }

                // Private Bytes (Percent)
                if (AllAppPrivateBytesDataPercent.ContainsKey(id))
                {
                    var parentFrud = AllAppPrivateBytesDataPercent[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppPrivateBytesDataPercent, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    if (app.WarningPrivateBytesPercent > 0 || app.ErrorPrivateBytesPercent > 0)
                    {
                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.ErrorPrivateBytesPercent,
                            app.WarningPrivateBytesPercent,
                            TTL,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId);
                    }
                }

                // RG Memory Monitoring (Private Bytes Percent)
                if (AllAppRGMemoryUsagePercent.ContainsKey(id))
                {
                    var parentFrud = AllAppRGMemoryUsagePercent[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppRGMemoryUsagePercent, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }
             
                    ProcessResourceDataReportHealth(
                        parentFrud,
                        thresholdError: 0, // Only Warning Threshold is supported for RG reporting.
                        thresholdWarning: app.WarningRGMemoryLimitPercent > 0 ? app.WarningRGMemoryLimitPercent : MaxRGMemoryInUsePercent, // Default: 90%
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        false, // Not supported
                        false, // Not supported
                        processId);
                }

                // TCP Ports - Active
                if (AllAppTotalActivePortsData.ContainsKey(id))
                {
                    var parentFrud = AllAppTotalActivePortsData[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppTotalActivePortsData, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.NetworkErrorActivePorts,
                        app.NetworkWarningActivePorts,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }

                // TCP Ports Total - Ephemeral (port numbers fall in the dynamic range)
                if (AllAppEphemeralPortsData.ContainsKey(id))
                {
                    var parentFrud = AllAppEphemeralPortsData[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppEphemeralPortsData, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.NetworkErrorEphemeralPorts,
                        app.NetworkWarningEphemeralPorts,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }

                // TCP Ports Percentage - Ephemeral (port numbers fall in the dynamic range)
                if (AllAppEphemeralPortsDataPercent.ContainsKey(id))
                {
                    var parentFrud = AllAppEphemeralPortsDataPercent[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppEphemeralPortsDataPercent, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.NetworkErrorEphemeralPortsPercent,
                        app.NetworkWarningEphemeralPortsPercent,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }

                // Allocated (in use) Handles
                if (AllAppHandlesData.ContainsKey(id))
                {
                    var parentFrud = AllAppHandlesData[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppHandlesData, childProcessTelemetryDataList, repOrInst, app, parentFrud, token); 
                    }

                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.ErrorOpenFileHandles,
                        app.WarningOpenFileHandles,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }

                // Threads
                if (AllAppThreadsData.ContainsKey(id))
                {
                    var parentFrud = AllAppThreadsData[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppThreadsData, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.ErrorThreadCount,
                        app.WarningThreadCount,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }

                // KVS LVIDs - Windows-only (EnableKvsLvidMonitoring will always be false otherwise)
                if (EnableKvsLvidMonitoring && AllAppKvsLvidsData != null && AllAppKvsLvidsData.ContainsKey(id))
                {
                    var parentFrud = AllAppKvsLvidsData[id];

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(AllAppKvsLvidsData, childProcessTelemetryDataList, repOrInst, app, parentFrud, token);
                    }

                    // FO will warn if the stateful (Actor, for example) service process has used 75% or greater of available LVIDs. This is not configurable (and a temporary feature).
                    ProcessResourceDataReportHealth(
                        parentFrud,
                        0,
                        KvsLvidsWarningPercentage,
                        TTL,
                        EntityType.Service,
                        processName,
                        repOrInst,
                        app.DumpProcessOnError && EnableProcessDumps,
                        app.DumpProcessOnWarning && EnableProcessDumps,
                        processId);
                }           

                // Child proc info telemetry.
                if (hasChildProcs && MaxChildProcTelemetryDataCount > 0 && childProcessTelemetryDataList.Count > 0)
                {
                    if (IsEtwEnabled)
                    {
                        ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, childProcessTelemetryDataList.ToList());
                    }

                    if (IsTelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportMetricAsync(childProcessTelemetryDataList.ToList(), token);
                    }
                }
           });

            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"ReportAsync run duration with parallel: {stopwatch.Elapsed}");
            ObserverLogger.LogInfo($"Completed ReportAsync.");
            return Task.CompletedTask;
        }

        // This runs each time ObserveAsync is run to ensure that any new app targets and config changes will
        // be up to date.
        public async Task<bool> InitializeAsync()
        {
            ObserverLogger.LogInfo($"Initializing AppObserver.");
            ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new List<ApplicationInfo>();

            // NodeName is passed here to not break unit tests, which include a mock service fabric context..
            fabricClientUtilities = new FabricClientUtilities(NodeName);
            deployedApps = await fabricClientUtilities.GetAllDeployedAppsAsync(Token);

            // DEBUG - Perf
            //var stopwatch = Stopwatch.StartNew();

            // Set properties with Application Parameter settings (housed in ApplicationManifest.xml) for this run.
            SetPropertiesFromApplicationSettings();

            // Process JSON object configuration settings (housed in [AppObserver.config].json) for this run.
            if (!await ProcessJSONConfigAsync())
            {
                return false;
            }

            // Filter JSON targetApp setting format; try and fix malformed values, if possible.
            FilterTargetAppFormat();

            // Support for specifying single configuration JSON object for all applications.
            await ProcessGlobalThresholdSettingsAsync();

            int settingsFail = 0;

            for (int i = 0; i < userTargetList.Count; i++)
            {
                Token.ThrowIfCancellationRequested();

                ApplicationInfo application = userTargetList[i];
                Uri appUri = null;

                try
                {
                    if (application.TargetApp != null)
                    {
                        appUri = new Uri(application.TargetApp);
                    }
                }
                catch (UriFormatException)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(application.TargetApp) && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    settingsFail++;

                    // No required settings supplied for deployed application(s).
                    if (settingsFail == userTargetList.Count)
                    {
                        string message = "No required settings supplied for deployed applications in AppObserver.config.json. " +
                                         "You must supply either a targetApp or targetAppType setting.";

                        var healthReport = new Utilities.HealthReport
                        {
                            AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                            EmitLogEvent = true,
                            HealthMessage = message,
                            HealthReportTimeToLive = GetHealthReportTimeToLive(),
                            Property = "AppMisconfiguration",
                            EntityType = EntityType.Application,
                            State = HealthState.Warning,
                            NodeName = NodeName,
                            Observer = ObserverConstants.AppObserverName,
                        };

                        // Generate a Service Fabric Health Report.
                        HealthReporter.ReportHealthToServiceFabric(healthReport);

                        // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                        if (IsTelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportHealthAsync(
                                    "AppMisconfiguration",
                                    HealthState.Warning,
                                    message,
                                    ObserverName,
                                    Token);
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(
                                ObserverConstants.FabricObserverETWEventName,
                                new
                                {
                                    Property = "AppMisconfiguration",
                                    Level = "Warning",
                                    Message = message,
                                    ObserverName
                                });
                        }

                        OperationalHealthEvents++;
                        return false;
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    await SetDeployedReplicaOrInstanceListAsync(null, application.TargetAppType);
                }
                else
                {
                    await SetDeployedReplicaOrInstanceListAsync(appUri);
                }
            }

            int repCount = ReplicaOrInstanceList.Count;

            // internal diagnostic telemetry \\

            // Do not emit the same service count data over and over again.
            if (repCount != serviceCount)
            {
                MonitoredServiceProcessCount = repCount;
                serviceCount = repCount;
            }
            else
            {
                MonitoredServiceProcessCount = 0;
            }

            // Do not emit the same app count data over and over again.
            if (deployedTargetList.Count != appCount)
            {
                MonitoredAppCount = deployedTargetList.Count;
                appCount = deployedTargetList.Count;
            }
            else
            {
                MonitoredAppCount = 0;
            }

            if (!EnableVerboseLogging)
            {
                return true;
            }
#if DEBUG
            for (int i = 0; i < deployedTargetList.Count; i++)
            {
                Token.ThrowIfCancellationRequested();
                ObserverLogger.LogInfo($"AppObserver settings applied to {deployedTargetList[i].TargetApp}:{Environment.NewLine}{deployedTargetList[i]}");
            }
#endif
            for (int i = 0; i < repCount; ++i)
            {
                Token.ThrowIfCancellationRequested();

                var rep = ReplicaOrInstanceList[i];
                ObserverLogger.LogInfo($"Will observe resource consumption by {rep.ServiceName?.OriginalString}({rep.HostProcessId}) on Node {NodeName}.");
            }

            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"InitializeAsync run duration: {stopwatch.Elapsed}");

            return true;
        }

        private async Task ProcessGlobalThresholdSettingsAsync()
        {
            if (userTargetList == null || userTargetList.Count == 0)
            {
                return;
            }

            if (!userTargetList.Any(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*"))
            {
                return;
            }

            ObserverLogger.LogInfo($"Started processing of global (*/all) settings from appObserver.config.json.");

            ApplicationInfo application = userTargetList.First(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*");

            for (int i = 0; i < deployedApps.Count; i++)
            {
                Token.ThrowIfCancellationRequested();

                var app = deployedApps[i];

                // Make sure deployed app is not a containerized app.
                var codepackages = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () => FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(
                                                    NodeName,
                                                    app.ApplicationName,
                                                    null,
                                                    null,
                                                    ConfigurationSettings.AsyncTimeout,
                                                    Token), Token);

                if (codepackages.Count == 0)
                {
                    continue;
                }

                int containerHostCount = codepackages.Count(c => c.HostType == HostType.ContainerHost);

                // Ignore containerized apps. ContainerObserver is designed for those types of services.
                if (containerHostCount > 0)
                {
                    continue;
                }

                if (app.ApplicationName.OriginalString == "fabric:/System")
                {
                    continue;
                }

                // Multiple code packages
                if (codepackages.Count > 1)
                {
                    foreach (var codepackage in codepackages)
                    {
                        int procId = (int)codepackage.EntryPoint.ProcessId;
                        string procName = NativeMethods.GetProcessNameFromId(procId);
                    }
                }

                // App filtering: AppExcludeList, AppIncludeList. This is only useful when you are observing All/* applications for a range of thresholds.
                if (!string.IsNullOrWhiteSpace(application.AppExcludeList) && application.AppExcludeList.Contains(app.ApplicationName.OriginalString.Replace("fabric:/", string.Empty)))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(application.AppIncludeList) && !application.AppIncludeList.Contains(app.ApplicationName.OriginalString.Replace("fabric:/", string.Empty)))
                {
                    continue;
                }

                // Don't create a brand new entry for an existing (specified in configuration) app target/type. Just update the appConfig instance with data supplied in the All/* apps config entry.
                // Note that if you supply a conflicting setting (where you specify a threshold for a specific app target config item and also in a global config item), then the target-specific setting will be used.
                // E.g., if you supply a memoryWarningLimitMb threshold for an app named fabric:/MyApp and also supply a memoryWarningLimitMb threshold for all apps ("targetApp" : "All"),
                // then the threshold specified for fabric:/MyApp will remain in place for that app target. So, target specificity overrides any global setting.
                if (userTargetList.Any(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName))
                {
                    var existingAppConfig = userTargetList.FindAll(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName);

                    if (existingAppConfig == null || existingAppConfig.Count == 0)
                    {
                        continue;
                    }

                    for (int j = 0; j < existingAppConfig.Count; j++)
                    {
                        // Service include/exclude lists
                        existingAppConfig[j].ServiceExcludeList = string.IsNullOrWhiteSpace(existingAppConfig[j].ServiceExcludeList) && !string.IsNullOrWhiteSpace(application.ServiceExcludeList) ? application.ServiceExcludeList : existingAppConfig[j].ServiceExcludeList;
                        existingAppConfig[j].ServiceIncludeList = string.IsNullOrWhiteSpace(existingAppConfig[j].ServiceIncludeList) && !string.IsNullOrWhiteSpace(application.ServiceIncludeList) ? application.ServiceIncludeList : existingAppConfig[j].ServiceIncludeList;

                        // Memory - Working Set (MB)
                        existingAppConfig[j].MemoryErrorLimitMb = existingAppConfig[j].MemoryErrorLimitMb == 0 && application.MemoryErrorLimitMb > 0 ? application.MemoryErrorLimitMb : existingAppConfig[j].MemoryErrorLimitMb;
                        existingAppConfig[j].MemoryWarningLimitMb = existingAppConfig[j].MemoryWarningLimitMb == 0 && application.MemoryWarningLimitMb > 0 ? application.MemoryWarningLimitMb : existingAppConfig[j].MemoryWarningLimitMb;

                        // Memory - Working Set (Percent)
                        existingAppConfig[j].MemoryErrorLimitPercent = existingAppConfig[j].MemoryErrorLimitPercent == 0 && application.MemoryErrorLimitPercent > 0 ? application.MemoryErrorLimitPercent : existingAppConfig[j].MemoryErrorLimitPercent;
                        existingAppConfig[j].MemoryWarningLimitPercent = existingAppConfig[j].MemoryWarningLimitPercent == 0 && application.MemoryWarningLimitPercent > 0 ? application.MemoryWarningLimitPercent : existingAppConfig[j].MemoryWarningLimitPercent;

                        // Memory - Private Bytes (MB)
                        existingAppConfig[j].ErrorPrivateBytesMb = existingAppConfig[j].ErrorPrivateBytesMb == 0 && application.ErrorPrivateBytesMb > 0 ? application.ErrorPrivateBytesMb : existingAppConfig[j].ErrorPrivateBytesMb;
                        existingAppConfig[j].WarningPrivateBytesMb = existingAppConfig[j].WarningPrivateBytesMb == 0 && application.WarningPrivateBytesMb > 0 ? application.WarningPrivateBytesMb : existingAppConfig[j].WarningPrivateBytesMb;

                        // Memory - Private Bytes (Percent)
                        existingAppConfig[j].ErrorPrivateBytesPercent = existingAppConfig[j].ErrorPrivateBytesPercent == 0 && application.ErrorPrivateBytesPercent > 0 ? application.ErrorPrivateBytesPercent : existingAppConfig[j].ErrorPrivateBytesPercent;
                        existingAppConfig[j].WarningPrivateBytesPercent = existingAppConfig[j].WarningPrivateBytesPercent == 0 && application.WarningPrivateBytesPercent > 0 ? application.WarningPrivateBytesPercent : existingAppConfig[j].WarningPrivateBytesPercent;

                        // CPU
                        existingAppConfig[j].CpuErrorLimitPercent = existingAppConfig[j].CpuErrorLimitPercent == 0 && application.CpuErrorLimitPercent > 0 ? application.CpuErrorLimitPercent : existingAppConfig[j].CpuErrorLimitPercent;
                        existingAppConfig[j].CpuWarningLimitPercent = existingAppConfig[j].CpuWarningLimitPercent == 0 && application.CpuWarningLimitPercent > 0 ? application.CpuWarningLimitPercent : existingAppConfig[j].CpuWarningLimitPercent;

                        // Active TCP Ports
                        existingAppConfig[j].NetworkErrorActivePorts = existingAppConfig[j].NetworkErrorActivePorts == 0 && application.NetworkErrorActivePorts > 0 ? application.NetworkErrorActivePorts : existingAppConfig[j].NetworkErrorActivePorts;
                        existingAppConfig[j].NetworkWarningActivePorts = existingAppConfig[j].NetworkWarningActivePorts == 0 && application.NetworkWarningActivePorts > 0 ? application.NetworkWarningActivePorts : existingAppConfig[j].NetworkWarningActivePorts;

                        // Active Ephemeral Ports
                        existingAppConfig[j].NetworkErrorEphemeralPorts = existingAppConfig[j].NetworkErrorEphemeralPorts == 0 && application.NetworkErrorEphemeralPorts > 0 ? application.NetworkErrorEphemeralPorts : existingAppConfig[j].NetworkErrorEphemeralPorts;
                        existingAppConfig[j].NetworkWarningEphemeralPorts = existingAppConfig[j].NetworkWarningEphemeralPorts == 0 && application.NetworkWarningEphemeralPorts > 0 ? application.NetworkWarningEphemeralPorts : existingAppConfig[j].NetworkWarningEphemeralPorts;
                        existingAppConfig[j].NetworkErrorEphemeralPortsPercent = existingAppConfig[j].NetworkErrorEphemeralPortsPercent == 0 && application.NetworkErrorEphemeralPortsPercent > 0 ? application.NetworkErrorEphemeralPortsPercent : existingAppConfig[j].NetworkErrorEphemeralPortsPercent;
                        existingAppConfig[j].NetworkWarningEphemeralPortsPercent = existingAppConfig[j].NetworkWarningEphemeralPortsPercent == 0 && application.NetworkWarningEphemeralPortsPercent > 0 ? application.NetworkWarningEphemeralPortsPercent : existingAppConfig[j].NetworkWarningEphemeralPortsPercent;

                        // DumpOnError
                        existingAppConfig[j].DumpProcessOnError = application.DumpProcessOnError == existingAppConfig[j].DumpProcessOnError ? application.DumpProcessOnError : existingAppConfig[j].DumpProcessOnError;

                        // DumpOnWarning
                        existingAppConfig[j].DumpProcessOnWarning = application.DumpProcessOnWarning == existingAppConfig[j].DumpProcessOnWarning ? application.DumpProcessOnWarning : existingAppConfig[j].DumpProcessOnWarning;

                        // Handles
                        existingAppConfig[j].ErrorOpenFileHandles = existingAppConfig[j].ErrorOpenFileHandles == 0 && application.ErrorOpenFileHandles > 0 ? application.ErrorOpenFileHandles : existingAppConfig[j].ErrorOpenFileHandles;
                        existingAppConfig[j].WarningOpenFileHandles = existingAppConfig[j].WarningOpenFileHandles == 0 && application.WarningOpenFileHandles > 0 ? application.WarningOpenFileHandles : existingAppConfig[j].WarningOpenFileHandles;

                        // Threads
                        existingAppConfig[j].ErrorThreadCount = existingAppConfig[j].ErrorThreadCount == 0 && application.ErrorThreadCount > 0 ? application.ErrorThreadCount : existingAppConfig[j].ErrorThreadCount;
                        existingAppConfig[j].WarningThreadCount = existingAppConfig[j].WarningThreadCount == 0 && application.WarningThreadCount > 0 ? application.WarningThreadCount : existingAppConfig[j].WarningThreadCount;

                        // RGMemoryLimitPercent
                        existingAppConfig[j].WarningRGMemoryLimitPercent = existingAppConfig[j].WarningRGMemoryLimitPercent == 0 && application.WarningRGMemoryLimitPercent > 0 ? application.WarningRGMemoryLimitPercent : existingAppConfig[j].WarningRGMemoryLimitPercent;
                    }
                }
                else
                {
                    var appConfig = new ApplicationInfo
                    {
                        TargetApp = app.ApplicationName.OriginalString,
                        TargetAppType = null,
                        AppExcludeList = application.AppExcludeList,
                        AppIncludeList = application.AppIncludeList,
                        ServiceExcludeList = application.ServiceExcludeList,
                        ServiceIncludeList = application.ServiceIncludeList,
                        CpuErrorLimitPercent = application.CpuErrorLimitPercent,
                        CpuWarningLimitPercent = application.CpuWarningLimitPercent,
                        MemoryErrorLimitMb = application.MemoryErrorLimitMb,
                        MemoryWarningLimitMb = application.MemoryWarningLimitMb,
                        MemoryErrorLimitPercent = application.MemoryErrorLimitPercent,
                        MemoryWarningLimitPercent = application.MemoryWarningLimitPercent,
                        NetworkErrorActivePorts = application.NetworkErrorActivePorts,
                        NetworkWarningActivePorts = application.NetworkWarningActivePorts,
                        NetworkErrorEphemeralPorts = application.NetworkErrorEphemeralPorts,
                        NetworkWarningEphemeralPorts = application.NetworkWarningEphemeralPorts,
                        NetworkErrorEphemeralPortsPercent = application.NetworkErrorEphemeralPortsPercent,
                        NetworkWarningEphemeralPortsPercent = application.NetworkWarningEphemeralPortsPercent,
                        DumpProcessOnError = application.DumpProcessOnError,
                        DumpProcessOnWarning = application.DumpProcessOnWarning,
                        ErrorOpenFileHandles = application.ErrorOpenFileHandles,
                        WarningOpenFileHandles = application.WarningOpenFileHandles,
                        ErrorThreadCount = application.ErrorThreadCount,
                        WarningThreadCount = application.WarningThreadCount,
                        ErrorPrivateBytesMb = application.ErrorPrivateBytesMb,
                        WarningPrivateBytesMb = application.WarningPrivateBytesMb,
                        ErrorPrivateBytesPercent = application.ErrorPrivateBytesPercent,
                        WarningPrivateBytesPercent = application.WarningPrivateBytesPercent,
                        WarningRGMemoryLimitPercent = application.WarningRGMemoryLimitPercent
                    };

                    userTargetList.Add(appConfig);
                }
            }

            // Remove the All/* config item.
             _ = userTargetList.Remove(application);
            ObserverLogger.LogInfo($"Completed processing of global (*/all) settings from appObserver.config.json.");
        }

        private void FilterTargetAppFormat()
        {
            ObserverLogger.LogInfo($"Evaluating targetApp format. Will attempt to correct malformed values, if any.");
            for (int i = 0; i < userTargetList.Count; i++)
            {
                var target = userTargetList[i];

                // We are only filtering/fixing targetApp string format.
                if (string.IsNullOrWhiteSpace(target.TargetApp))
                {
                    continue;
                }

                if (target.TargetApp == "*" || target.TargetApp.ToLower() == "all")
                {
                    continue;
                }

                try
                {
                    /* Try and fix malformed app names, if possible. */

                    if (!target.TargetApp.StartsWith("fabric:/"))
                    {
                        target.TargetApp = target.TargetApp.Insert(0, "fabric:/");
                    }

                    if (target.TargetApp.Contains("://"))
                    {
                        target.TargetApp = target.TargetApp.Replace("://", ":/");
                    }

                    if (target.TargetApp.Contains(" "))
                    {
                        target.TargetApp = target.TargetApp.Replace(" ", string.Empty);
                    }

                    if (!Uri.IsWellFormedUriString(target.TargetApp, UriKind.RelativeOrAbsolute))
                    {
                        userTargetList.RemoveAt(i);

                        string msg = $"FilterTargetAppFormat: Unsupported TargetApp value: {target.TargetApp}. " +
                                     "Value must be a valid Uri string of format \"fabric:/MyApp\" OR just \"MyApp\". Ignoring targetApp.";

                        var healthReport = new Utilities.HealthReport
                        {
                            AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                            EmitLogEvent = true,
                            HealthMessage = msg,
                            HealthReportTimeToLive = GetHealthReportTimeToLive(),
                            Property = "UnsupportedTargetAppValue",
                            EntityType = EntityType.Application,
                            State = HealthState.Warning,
                            NodeName = NodeName,
                            Observer = ObserverConstants.AppObserverName
                        };

                        // Generate a Service Fabric Health Report.
                        HealthReporter.ReportHealthToServiceFabric(healthReport);

                        if (IsTelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportHealthAsync(
                                    "UnsupportedTargetAppValue",
                                    HealthState.Warning,
                                    msg,
                                    ObserverName,
                                    Token);
                        }

                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(
                                ObserverConstants.FabricObserverETWEventName,
                                new
                                {
                                    Property = "UnsupportedTargetAppValue",
                                    Level = "Warning",
                                    Message = msg,
                                    ObserverName
                                });
                        }

                        OperationalHealthEvents++;
                    }
                }
                catch (ArgumentException)
                {

                }
            }
            ObserverLogger.LogInfo($"Completed targetApp evaluation.");
        }

        private async Task<bool> ProcessJSONConfigAsync()
        {
            ObserverLogger.LogInfo($"Processing Json configuration.");
            if (!File.Exists(JsonConfigPath))
            {
                string message = $"Will not observe resource consumption on node {NodeName} as no configuration file has been supplied.";
                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = true,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTimeToLive(),
                    Property = "MissingAppConfiguration",
                    EntityType = EntityType.Application,
                    State = HealthState.Warning,
                    NodeName = NodeName,
                    Observer = ObserverConstants.AppObserverName,
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "MissingAppConfiguration",
                            HealthState.Warning,
                            message,
                            ObserverName,
                            Token);
                }

                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Property = "MissingAppConfiguration",
                            Level = "Warning",
                            Message = message,
                            ObserverName
                        });
                }

                OperationalHealthEvents++;
                IsUnhealthy = true;
                return false;
            }

            bool isJson = JsonHelper.IsJson<List<ApplicationInfo>>(await File.ReadAllTextAsync(JsonConfigPath));

            if (!isJson)
            {
                string message = "AppObserver's JSON configuration file is malformed. Please fix the JSON and redeploy FabricObserver if you want AppObserver to monitor service processes.";
                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = true,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTimeToLive(),
                    Property = "JsonValidation",
                    EntityType = EntityType.Application,
                    State = HealthState.Warning,
                    NodeName = NodeName,
                    Observer = ObserverConstants.AppObserverName,
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "JsonValidation",
                            HealthState.Warning,
                            message,
                            ObserverName,
                            Token);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Property = "JsonValidation",
                            Level = "Warning",
                            Message = message,
                            ObserverName
                        });
                }

                OperationalHealthEvents++;
                IsUnhealthy = true;
                return false;
            }

            await using Stream stream = new FileStream(JsonConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var appInfo = JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream);
            userTargetList.AddRange(appInfo);

            // Does the configuration have any objects (targets) defined?
            if (userTargetList.Count == 0)
            {
                string message = $"Please add targets to AppObserver's JSON configuration file and redeploy FabricObserver if you want AppObserver to monitor service processes.";

                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = true,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTimeToLive(),
                    Property = "Misconfiguration",
                    EntityType = EntityType.Application,
                    State = HealthState.Warning,
                    NodeName = NodeName,
                    Observer = ObserverConstants.AppObserverName,
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);
                CurrentWarningCount++;

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "Misconfiguration",
                            HealthState.Warning,
                            message,
                            ObserverName,
                            Token);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Property = "Misconfiguration",
                            Level = "Warning",
                            Message = message,
                            ObserverName
                        });
                }

                OperationalHealthEvents++;
                IsUnhealthy = true;
                return false;
            }
            ObserverLogger.LogInfo($"Completed processing Json configuration.");
            return true;
        }

        /// <summary>
        /// Set properties with Application Parameter settings supplied by user.
        /// </summary>
        private void SetPropertiesFromApplicationSettings()
        {
            ObserverLogger.LogInfo($"Setting properties from application parameters.");
            
            // TODO Right another TEST....
            // Config path.
            if (JsonConfigPath == null)
            {
                JsonConfigPath =
                    Path.Combine(ConfigPackage.Path, GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.ConfigurationFileNameParameter));

                ObserverLogger.LogInfo(JsonConfigPath);
            }

            ObserverLogger.LogInfo($"MonitorPrivateWorkingSet");
            // Private working set monitoring.
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MonitorPrivateWorkingSetParameter), out bool monitorWsPriv))
            {
                CheckPrivateWorkingSet = monitorWsPriv;
            }

            ObserverLogger.LogInfo($"MonitorResourceGovernanceLimits");
            // Monitor RG limits. Windows-only.
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MonitorResourceGovernanceLimitsParameter), out bool monitorRG))
            {
                MonitorResourceGovernanceLimits = IsWindows && monitorRG;
            }

            ObserverLogger.LogInfo($"EnableChildProcessMonitoringParameter");
            /* Child/Descendant proc monitoring config */
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableChildProcessMonitoringParameter), out bool enableDescendantMonitoring))
            {
                EnableChildProcessMonitoring = enableDescendantMonitoring;
            }

            ObserverLogger.LogInfo($"MaxChildProcTelemetryDataCountParameter");
            if (int.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxChildProcTelemetryDataCountParameter), out int maxChildProcs))
            {
                MaxChildProcTelemetryDataCount = maxChildProcs;
            }

            ObserverLogger.LogInfo($"EnableProcessDumpsParameter");
            /* dumpProcessOnError/dumpProcessOnWarning config */
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableProcessDumpsParameter), out bool enableDumps))
            {
                EnableProcessDumps = enableDumps;

                if (string.IsNullOrWhiteSpace(DumpsPath) && enableDumps)
                {
                    SetDumpPath();
                }
            }

            ObserverLogger.LogInfo($"DumpTypeParameter");
            if (Enum.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.DumpTypeParameter), out DumpType dumpType))
            {
                DumpType = dumpType;
            }

            ObserverLogger.LogInfo($"MaxDumpsParameter");
            if (int.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxDumpsParameter), out int maxDumps))
            {
                MaxDumps = maxDumps;
            }

            ObserverLogger.LogInfo($"MaxDumpsTimeWindowParameter");
            if (TimeSpan.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxDumpsTimeWindowParameter), out TimeSpan dumpTimeWindow))
            {
                MaxDumpsTimeWindow = dumpTimeWindow;
            }

            ObserverLogger.LogInfo($"EnableConcurrentMonitoring");
            // Concurrency/Parallelism support. The minimum requirement is 4 logical processors, regardless of user setting.
            if (Environment.ProcessorCount >= 4 && bool.TryParse(
                    GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableConcurrentMonitoringParameter), out bool enableConcurrency))
            {
                EnableConcurrentMonitoring = enableConcurrency;
            }

            ObserverLogger.LogInfo($"MaxConcurrentTasks");
            // Effectively, sequential.
            int maxDegreeOfParallelism = 1;

            if (EnableConcurrentMonitoring)
            {
                // Default to using [1/4 of available logical processors ~* 2] threads if MaxConcurrentTasks setting is not supplied.
                // So, this means around 10 - 11 threads (or less) could be used if processor count = 20. This is only being done to limit the impact
                // FabricObserver has on the resources it monitors and alerts on.
                maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));
                
                // If user configures MaxConcurrentTasks setting, then use that value instead.
                if (int.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxConcurrentTasksParameter), out int maxTasks))
                {
                    if (maxTasks == -1 || maxTasks > 0)
                    {
                        maxDegreeOfParallelism = maxTasks;
                    }
                }
            }

            ObserverLogger.LogInfo($"ParallelOps");
            parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            ObserverLogger.LogInfo($"EnableKvsLvidMonitoringParameter");
            // KVS LVID Monitoring - Windows-only.
            if (IsWindows && bool.TryParse(
                    GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableKvsLvidMonitoringParameter), out bool enableLvidMonitoring))
            {
                // Observers that monitor LVIDs should ensure the static ObserverManager.CanInstallLvidCounter is true before attempting to monitor LVID usage.
                EnableKvsLvidMonitoring = enableLvidMonitoring && ObserverManager.IsLvidCounterEnabled;
            }
            ObserverLogger.LogInfo($"Completed setting properties from application parameters.");
        }

        private void ProcessChildProcs<T>(
                        ConcurrentDictionary<string, FabricResourceUsageData<T>> childFruds,
                        ConcurrentQueue<ChildProcessTelemetryData> childProcessTelemetryDataList, 
                        ReplicaOrInstanceMonitoringInfo repOrInst, 
                        ApplicationInfo appInfo, 
                        FabricResourceUsageData<T> parentFrud, 
                        CancellationToken token) where T : struct
        {
            token.ThrowIfCancellationRequested();

            if (childProcessTelemetryDataList == null)
            {
                return;
            }

            ObserverLogger.LogInfo($"Started ProcessChildProcs.");
            try
            { 
                var (childProcInfo, Sum) = TupleProcessChildFruds(childFruds, repOrInst, appInfo, token);
                
                if (childProcInfo == null)
                {
                    return;
                }

                string metric = parentFrud.Property;
                var parentDataAvg = parentFrud.AverageDataValue;
                double sumAllValues = Sum + parentDataAvg;
                childProcInfo.Metric = metric;
                childProcInfo.Value = sumAllValues;
                childProcessTelemetryDataList.Enqueue(childProcInfo);
                parentFrud.ClearData();
                parentFrud.AddData((T)Convert.ChangeType(sumAllValues, typeof(T)));
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogWarning($"ProcessChildProcs - Failure processing descendants:{Environment.NewLine}{e}");
            }
            ObserverLogger.LogInfo($"Completed ProcessChildProcs.");
        }

        private (ChildProcessTelemetryData childProcInfo, double Sum) TupleProcessChildFruds<T>(
                                                                         ConcurrentDictionary<string, FabricResourceUsageData<T>> fruds,
                                                                         ReplicaOrInstanceMonitoringInfo repOrInst,
                                                                         ApplicationInfo app,
                                                                         CancellationToken token) where T : struct
        {
            ObserverLogger.LogInfo($"Started TupleProcessChildFruds.");
            var childProcs = repOrInst.ChildProcesses;

            if (childProcs == null || childProcs.Count == 0 || token.IsCancellationRequested)
            {
                return (null, 0);
            }

            double sumValues = 0;
            string metric = string.Empty;
            string procStartTime = string.Empty;

            if (IsWindows)
            {
                try
                {
                    procStartTime = NativeMethods.GetProcessStartTime((int)repOrInst.HostProcessId).ToString("o");
                }
                catch (Exception e) when (e is ArgumentException || e is FormatException || e is Win32Exception)
                {
                    ObserverLogger.LogInfo($"Can't get process start time for {repOrInst.HostProcessId}: {e.Message}");
                    // Just don't set the ChildProcessTelemetryData.ProcessStartTime field. Don't exit the function.
                }
            }
            else
            {
                try
                {
                    using (Process proc = Process.GetProcessById((int)repOrInst.HostProcessId))
                    {
                        procStartTime = proc.StartTime.ToString("o");
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is FormatException || e is InvalidOperationException || e is NotSupportedException || e is Win32Exception)
                {
                    ObserverLogger.LogInfo($"Can't get process start time for {repOrInst.HostProcessId}: {e.Message}");
                    // Just don't set the ChildProcessTelemetryData.ProcessStartTime field. Don't exit the function.
                }
            }

            var childProcessInfoData = new ChildProcessTelemetryData
            {
                ApplicationName = repOrInst.ApplicationName.OriginalString,
                ServiceName = repOrInst.ServiceName.OriginalString,
                NodeName = NodeName,
                ProcessId = (int)repOrInst.HostProcessId,
                ProcessName = IsWindows ? NativeMethods.GetProcessNameFromId((int)repOrInst.HostProcessId) : Process.GetProcessById((int)repOrInst.HostProcessId)?.ProcessName,
                ProcessStartTime = procStartTime,
                PartitionId = repOrInst.PartitionId.ToString(),
                ReplicaId = repOrInst.ReplicaOrInstanceId,
                ChildProcessCount = childProcs.Count,
                ChildProcessInfo = new List<ChildProcessInfo>()
            };

            for (int i = 0; i < childProcs.Count; ++i)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    int childPid = childProcs[i].Pid;
                    string childProcName = childProcs[i].procName;
                    string startTime;

                    if (!EnsureProcess(childProcName, childPid))
                    {
                        continue;
                    }

                    if (IsWindows)
                    {
                        startTime = NativeMethods.GetProcessStartTime(childPid).ToString("o");
                    }
                    else
                    {
                        using Process p = Process.GetProcessById(childPid);
                        startTime = p.StartTime.ToString("o");
                    }

                    if (fruds.Any(x => x.Key.Contains(childPid.ToString())))
                    {
                        var childFruds = fruds.Where(x => x.Key.Contains(childPid.ToString())).ToList();
                        metric = childFruds[0].Value.Property;

                        for (int j = 0; j < childFruds.Count; ++j)
                        {
                            token.ThrowIfCancellationRequested();

                            var frud = childFruds[j];
                            double value = frud.Value.AverageDataValue;
                            sumValues += value;

                            if (IsEtwEnabled || IsTelemetryEnabled)
                            {
                                var childProcInfo = new ChildProcessInfo { ProcessId = childPid, ProcessName = childProcName, ProcessStartTime = startTime, Value = value };
                                childProcessInfoData.ChildProcessInfo.Add(childProcInfo);
                            }

                            // Windows process dump support for descendant/child processes \\

                            if (IsWindows && EnableProcessDumps && (app.DumpProcessOnError || app.DumpProcessOnWarning))
                            {
                                string prop = frud.Value.Property;
                                bool dump = false;

                                switch (prop)
                                {
                                    case ErrorWarningProperty.CpuTime:
                                        // Test error/warning threshold breach for supplied metric.
                                        if (frud.Value.IsUnhealthy(app.CpuErrorLimitPercent) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.CpuWarningLimitPercent)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.MemoryConsumptionMb:
                                        if (frud.Value.IsUnhealthy(app.MemoryErrorLimitMb) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.MemoryWarningLimitMb)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.MemoryConsumptionPercentage:
                                        if (frud.Value.IsUnhealthy(app.MemoryErrorLimitPercent) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.MemoryWarningLimitPercent)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.PrivateBytesMb:
                                        if (frud.Value.IsUnhealthy(app.ErrorPrivateBytesMb) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.WarningPrivateBytesMb)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.PrivateBytesPercent:
                                        if (frud.Value.IsUnhealthy(app.ErrorPrivateBytesPercent) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.WarningPrivateBytesPercent)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.ActiveTcpPorts:
                                        if (frud.Value.IsUnhealthy(app.NetworkErrorActivePorts) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.NetworkWarningActivePorts)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.ActiveEphemeralPorts:
                                        if (frud.Value.IsUnhealthy(app.NetworkErrorEphemeralPorts) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.NetworkWarningEphemeralPorts)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.ActiveEphemeralPortsPercentage:
                                        if (frud.Value.IsUnhealthy(app.NetworkErrorEphemeralPortsPercent) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.NetworkWarningEphemeralPortsPercent)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.AllocatedFileHandles:
                                        if (frud.Value.IsUnhealthy(app.ErrorOpenFileHandles) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.WarningOpenFileHandles)))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.ThreadCount:
                                        if (frud.Value.IsUnhealthy(app.ErrorThreadCount) || (app.DumpProcessOnWarning && frud.Value.IsUnhealthy(app.WarningThreadCount)))
                                        {
                                            dump = true;
                                        }
                                        break;
                                }

                                lock (lockObj)
                                {
                                    if (dump)
                                    {
                                        ObserverLogger.LogInfo($"Starting dump code path for {repOrInst.HostProcessName}/{childProcName}/{childPid}.");
                                        // Make sure the child process is still the one we're looking for.
                                        if (EnsureProcess(childProcName, childPid))
                                        {
                                            // DumpWindowsServiceProcess logs failure. Log success here with parent/child info.
                                            if (DumpWindowsServiceProcess(childPid, childProcName, prop))
                                            {
                                                ObserverLogger.LogInfo($"Successfully dumped {repOrInst.HostProcessName}/{childProcName}/{childPid}.");
                                            }
                                        }
                                        else
                                        {
                                            ObserverLogger.LogInfo($"Will not dump child process: {childProcName}({childPid}) is no longer running.");
                                        }
                                        ObserverLogger.LogInfo($"Completed dump code path for {repOrInst.HostProcessName}/{childProcName}/{childPid}.");
                                    }
                                }
                            }

                            // Remove child FRUD from FRUDs.
                            _ = fruds.TryRemove(frud.Key, out _);
                        }

                        childFruds?.Clear();
                        childFruds = null;
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    ObserverLogger.LogWarning($"Failure processing descendant information: {e.Message}");
                    continue;
                }
            }

            try
            {
                // Order List<ChildProcessInfo> by Value descending.
                childProcessInfoData.ChildProcessInfo = childProcessInfoData.ChildProcessInfo.OrderByDescending(v => v.Value).ToList();

                // Cap size of List<ChildProcessInfo> to MaxChildProcTelemetryDataCount.
                if (childProcessInfoData.ChildProcessInfo.Count >= MaxChildProcTelemetryDataCount)
                {
                    childProcessInfoData.ChildProcessInfo = childProcessInfoData.ChildProcessInfo.Take(MaxChildProcTelemetryDataCount).ToList();
                }
                ObserverLogger.LogInfo($"Successfully completed TupleProcessChildFruds...");
                return (childProcessInfoData, sumValues);
            }
            catch (ArgumentException ae)
            {
                ObserverLogger.LogWarning($"TupleProcessChildFruds - Failure processing descendants:{Environment.NewLine}{ae.Message}");
            }

            ObserverLogger.LogInfo($"Completed TupleProcessChildFruds with Warning.");
            return (null, 0);
        }

        private static string GetAppNameOrType(ReplicaOrInstanceMonitoringInfo repOrInst)
        {
            // targetType specified as TargetAppType name, which means monitor all apps of specified type.
            var appNameOrType = !string.IsNullOrWhiteSpace(repOrInst.ApplicationTypeName) ? repOrInst.ApplicationTypeName : repOrInst.ApplicationName.OriginalString.Replace("fabric:/", string.Empty);
            return appNameOrType;
        }

        private void SetDumpPath()
        {
            try
            {
                DumpsPath = Path.Combine(ObserverLogger.LogFolderBasePath, ObserverName, ObserverConstants.ProcessDumpFolderNameParameter);
                Directory.CreateDirectory(DumpsPath);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is NotSupportedException || e is UnauthorizedAccessException)
            {
                ObserverLogger.LogWarning($"Unable to create dump directory {DumpsPath}.");
                return;
            }
        }

        private Task<ParallelLoopResult> MonitorDeployedAppsAsync(CancellationToken token)
        {
            Stopwatch execTimer = Stopwatch.StartNew();
            ObserverLogger.LogInfo("Starting MonitorDeployedAppsAsync.");
            int capacity = ReplicaOrInstanceList.Count;
            var exceptions = new ConcurrentQueue<Exception>();
            AllAppCpuData ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppMemDataMb ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
            AllAppMemDataPercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppTotalActivePortsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppEphemeralPortsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppEphemeralPortsDataPercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppHandlesData ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
            AllAppThreadsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();

            // Windows only.
            if (IsWindows)
            {
                AllAppPrivateBytesDataMb ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
                AllAppPrivateBytesDataPercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
                AllAppRGMemoryUsagePercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();

                // LVID usage monitoring for Stateful KVS-based services (e.g., Actors).
                if (EnableKvsLvidMonitoring)
                {
                    AllAppKvsLvidsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
                }
            }

            processInfoDictionary ??= new ConcurrentDictionary<int, string>();

            // DEBUG - Perf
            //var threadData = new ConcurrentQueue<int>();

            ParallelLoopResult result = Parallel.For (0, ReplicaOrInstanceList.Count, parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                }

                // DEBUG - Perf
                //threadData.Enqueue(Thread.CurrentThread.ManagedThreadId);
                var repOrInst = ReplicaOrInstanceList[i];
                var timer = new Stopwatch();
                int parentPid = (int)repOrInst.HostProcessId;
                bool checkCpu = false;
                bool checkMemMb = false;
                bool checkMemPct = false;
                bool checkMemPrivateBytesPct = false;
                bool checkMemPrivateBytes = false;
                bool checkAllPorts = false;
                bool checkEphemeralPorts = false;
                bool checkPercentageEphemeralPorts = false;
                bool checkHandles = false;
                bool checkThreads = false;
                bool checkLvids = false;
                var application = deployedTargetList?.First(
                                    app => app?.TargetApp?.ToLower() == repOrInst.ApplicationName?.OriginalString.ToLower() ||
                                    !string.IsNullOrWhiteSpace(app?.TargetAppType) &&
                                    app.TargetAppType?.ToLower() == repOrInst.ApplicationTypeName?.ToLower());
                
                double rgMemoryPercentThreshold = 0.0;
                ConcurrentDictionary<int, string> procs;

                if (application?.TargetApp == null && application?.TargetAppType == null)
                {
                    // return in a parallel loop is equivalent to a standard loop's continue.
                    return;
                }

                try
                {
                    string parentProcName = null;
                    Process parentProc = null;

                    try
                    {
                        if (!IsWindows)
                        {
                            parentProc = Process.GetProcessById(parentPid);

                            if (parentProc.HasExited)
                            {
                                return;
                            }

                            parentProcName = parentProc.ProcessName; 
                        }
                        else
                        {
                            // Has the process exited?
                            if (NativeMethods.GetProcessExitTime(parentPid) != DateTime.MinValue)
                            {
                                return;
                            }

                            // On Windows, this will throw a Win32Exception if target process is running at a higher user privilege than FO, handled below.
                            parentProcName = NativeMethods.GetProcessNameFromId(parentPid);
                        }

                        // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                        // Please use ContainerObserver for SF container app service monitoring.
                        if (parentProcName == null || parentProcName == "Fabric")
                        {
                            return;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException || e is Win32Exception)
                    {
                        if (!IsWindows || ObserverManager.ObserverFailureHealthStateLevel == HealthState.Unknown)
                        {
                            return;
                        }

                        if (e is Win32Exception exception)
                        {
                            if (exception.NativeErrorCode == 5 || exception.NativeErrorCode == 6)
                            {
                                string serviceName = repOrInst?.ServiceName?.OriginalString;
                                string message = $"{serviceName} is running as Admin or System user on Windows and can't be monitored by FabricObserver, which is running as Network Service. " +
                                                 $"Please configure FabricObserver to run as Admin or System user on Windows to solve this problem and/or determine if {serviceName} really needs to run as Admin or System user on Windows.";

                                string property = $"RestrictedAccess({serviceName})";
                                var healthReport = new Utilities.HealthReport
                                {
                                    ServiceName = ServiceName,
                                    EmitLogEvent = EnableVerboseLogging,
                                    HealthMessage = message,
                                    HealthReportTimeToLive = GetHealthReportTimeToLive(),
                                    Property = property,
                                    EntityType = EntityType.Service,
                                    State = ObserverManager.ObserverFailureHealthStateLevel,
                                    NodeName = NodeName,
                                    Observer = ObserverName
                                };

                                // Generate a Service Fabric Health Report.
                                HealthReporter.ReportHealthToServiceFabric(healthReport);

                                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                                if (IsTelemetryEnabled)
                                {
                                    _ = TelemetryClient?.ReportHealthAsync(
                                            property,
                                            ObserverManager.ObserverFailureHealthStateLevel,
                                            message,
                                            ObserverName,
                                            token,
                                            repOrInst?.ServiceName?.OriginalString);
                                }

                                // ETW.
                                if (IsEtwEnabled)
                                {
                                    ObserverLogger.LogEtw(
                                        ObserverConstants.FabricObserverETWEventName,
                                        new
                                        {
                                            Property = property,
                                            Level = Enum.GetName(typeof(HealthState), ObserverManager.ObserverFailureHealthStateLevel),
                                            Message = message,
                                            ObserverName,
                                            ServiceName = repOrInst?.ServiceName?.OriginalString
                                        });
                                }
                            }
                        }

                        return;
                    }

                    /* In order to provide accurate resource usage of an SF service process we need to also account for
                       any processes that the service process (parent) created/spawned (children). */

                    procs = new ConcurrentDictionary<int, string>();

                    // Add parent to the process tree list since we want to monitor all processes in the family. If there are no child processes,
                    // then only the parent process will be in this dictionary..
                    _ = procs.TryAdd(parentPid, parentProcName);

                    if (repOrInst.ChildProcesses != null && repOrInst.ChildProcesses.Count > 0)
                    {
                        for (int k = 0; k < repOrInst.ChildProcesses.Count; ++k)
                        {
                            if (token.IsCancellationRequested)
                            {
                                state.Stop();
                            }

                            // Make sure the child process still exists. Descendant processes are often ephemeral.
                            if (!EnsureProcess(repOrInst.ChildProcesses[k].procName, repOrInst.ChildProcesses[k].Pid))
                            {
                                continue;
                            }

                            _ = procs.TryAdd(repOrInst.ChildProcesses[k].Pid, repOrInst.ChildProcesses[k].procName);
                        }
                    }

                    foreach (var proc in procs)
                    {
                        if (token.IsCancellationRequested)
                        {
                            state.Stop();
                        }

                        _ = processInfoDictionary.TryAdd(proc.Key, proc.Value);
                    }

                    string appNameOrType = GetAppNameOrType(repOrInst);
                    string id = $"{appNameOrType}:{parentProcName}{parentPid}";

                    if (UseCircularBuffer)
                    {
                        capacity = DataCapacity > 0 ? DataCapacity : 5;
                    }
                    else if (MonitorDuration > TimeSpan.MinValue)
                    {
                        capacity = MonitorDuration.Seconds * 4;
                    }

                    // CPU
                    if (application.CpuErrorLimitPercent > 0 || application.CpuWarningLimitPercent > 0)
                    {
                        _ = AllAppCpuData.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.CpuTime, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppCpuData.ContainsKey(id))
                    {
                        checkCpu = true;
                    }

                    // Memory - Working Set MB.
                    if (application.MemoryErrorLimitMb > 0 || application.MemoryWarningLimitMb > 0)
                    {
                        _ = AllAppMemDataMb.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.MemoryConsumptionMb, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppMemDataMb.ContainsKey(id))
                    {
                        checkMemMb = true;
                    }

                    // Memory - Working Set Percent.
                    if (application.MemoryErrorLimitPercent > 0 || application.MemoryWarningLimitPercent > 0)
                    {
                        _ = AllAppMemDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.MemoryConsumptionPercentage, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppMemDataPercent.ContainsKey(id))
                    {
                        checkMemPct = true;
                    }

                    // Memory - Private Bytes MB. Windows-only.
                    if (IsWindows && application.ErrorPrivateBytesMb > 0 || application.WarningPrivateBytesMb > 0)
                    {
                        _ = AllAppPrivateBytesDataMb.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.PrivateBytesMb, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (IsWindows && AllAppPrivateBytesDataMb != null && AllAppPrivateBytesDataMb.ContainsKey(id))
                    {
                        checkMemPrivateBytes = true;
                    }

                    // Memory - Private Bytes (Percent). Windows-only.
                    if (IsWindows && application.ErrorPrivateBytesPercent > 0 || application.WarningPrivateBytesPercent > 0)
                    {
                        _ = AllAppPrivateBytesDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.PrivateBytesPercent, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (IsWindows && AllAppPrivateBytesDataPercent != null && AllAppPrivateBytesDataPercent.ContainsKey(id))
                    {
                        checkMemPrivateBytesPct = true;
                    }

                    // Memory - RG monitoring. Windows-only for now.
                    if (MonitorResourceGovernanceLimits && repOrInst.RGMemoryEnabled && repOrInst.RGAppliedMemoryLimitMb > 0)
                    {
                        _ = AllAppRGMemoryUsagePercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.RGMemoryUsagePercent, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (IsWindows && AllAppRGMemoryUsagePercent != null && AllAppRGMemoryUsagePercent.ContainsKey(id))
                    {
                        rgMemoryPercentThreshold = application.WarningRGMemoryLimitPercent;

                        if (rgMemoryPercentThreshold > 0)
                        {
                            if (rgMemoryPercentThreshold < 1)
                            {
                                rgMemoryPercentThreshold = application.WarningRGMemoryLimitPercent * 100.0; // decimal to double.
                            }
                        }
                        else
                        {
                            rgMemoryPercentThreshold = MaxRGMemoryInUsePercent; // Default: 90%.
                        }
                    }

                    // Active TCP Ports
                    if (application.NetworkErrorActivePorts > 0 || application.NetworkWarningActivePorts > 0)
                    {
                        _ = AllAppTotalActivePortsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.ActiveTcpPorts, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppTotalActivePortsData.ContainsKey(id))
                    {
                        checkAllPorts = true;
                    }

                    // Ephemeral TCP Ports - Total number.
                    if (application.NetworkErrorEphemeralPorts > 0 || application.NetworkWarningEphemeralPorts > 0)
                    {
                        _ = AllAppEphemeralPortsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.ActiveEphemeralPorts, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppEphemeralPortsData.ContainsKey(id))
                    {
                        checkEphemeralPorts = true;
                    }

                    // Ephemeral TCP Ports - Percentage in use of total available.
                    if (application.NetworkErrorEphemeralPortsPercent > 0 || application.NetworkWarningEphemeralPortsPercent > 0)
                    {
                        _ = AllAppEphemeralPortsDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.ActiveEphemeralPortsPercentage, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppEphemeralPortsDataPercent.ContainsKey(id))
                    {
                        checkPercentageEphemeralPorts = true;
                    }

                    // File Handles
                    if (application.ErrorOpenFileHandles > 0 || application.WarningOpenFileHandles > 0)
                    {
                        _ = AllAppHandlesData.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.AllocatedFileHandles, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppHandlesData.ContainsKey(id))
                    {
                        checkHandles = true;
                    }

                    // Threads
                    if (application.ErrorThreadCount > 0 || application.WarningThreadCount > 0)
                    {
                        _ = AllAppThreadsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.ThreadCount, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppThreadsData.ContainsKey(id))
                    {
                        checkThreads = true;
                    }

                    // KVS LVIDs percent (Windows-only)
                    // Note: This is a non-configurable Windows monitor and will be removed when SF ships with the latest version of ESE.
                    if (EnableKvsLvidMonitoring && AllAppKvsLvidsData != null && repOrInst.ServiceKind == ServiceKind.Stateful)
                    {
                        _ = AllAppKvsLvidsData.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.KvsLvidsPercent, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppKvsLvidsData != null && AllAppKvsLvidsData.ContainsKey(id))
                    {
                        checkLvids = true;
                    }

                    // For Windows: Regardless of user setting, if there are more than 50 service processes with the same name, then FO will employ Win32 API (fast, lightweight).
                    bool usePerfCounter = IsWindows && ReplicaOrInstanceList.Count(p => p.HostProcessName == parentProcName) < MaxSameNamedProcesses;

                    // Compute the resource usage of the family of processes (each proc in the family tree). This is also parallelized and has real perf benefits when 
                    // a service process has mulitple descendants.
                    ComputeResourceUsage(
                        capacity,
                        parentPid,
                        checkCpu,
                        checkMemMb,
                        checkMemPct,
                        checkMemPrivateBytesPct,
                        checkMemPrivateBytes,
                        checkAllPorts,
                        checkEphemeralPorts,
                        checkPercentageEphemeralPorts,
                        checkHandles,
                        checkThreads,
                        checkLvids,
                        procs,
                        id,
                        repOrInst,
                        usePerfCounter,
                        rgMemoryPercentThreshold,
                        token);
                }
                catch (Exception e)
                {
                    exceptions.Enqueue(e);
                }
           });

            if (!exceptions.IsEmpty)
            {
                throw new AggregateException(exceptions);
            }

            // DEBUG - Perf 
            //int threadcount = threadData.Distinct().Count();
            ObserverLogger.LogInfo("Completed MonitorDeployedAppsAsync.");
            ObserverLogger.LogInfo($"MonitorDeployedAppsAsync Execution time: {execTimer.Elapsed}"); //Threads: {threadcount}");
            return Task.FromResult(result);
        }

        private void ComputeResourceUsage(
                        int capacity,
                        int parentPid,
                        bool checkCpu,
                        bool checkMemMb,
                        bool checkMemPct,
                        bool checkMemPrivateBytesPct,
                        bool checkMemPrivateBytes,
                        bool checkAllPorts,
                        bool checkEphemeralPorts,
                        bool checkPercentageEphemeralPorts,
                        bool checkHandles,
                        bool checkThreads,
                        bool checkLvids,
                        ConcurrentDictionary<int, string> processDictionary,
                        string id,
                        ReplicaOrInstanceMonitoringInfo repOrInst,
                        bool usePerfCounter,
                        double rgMemoryPercentThreshold,
                        CancellationToken token)
        {
            _ = Parallel.For (0, processDictionary.Count, parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                }

                var index = processDictionary.ElementAt(i);
                string procName = index.Value;
                int procId = index.Key;
                
                // Make sure this is still the process we're looking for.
                if (!EnsureProcess(procName, procId))
                {
                    return;
                }

                TimeSpan maxDuration = TimeSpan.FromSeconds(1);

                if (MonitorDuration > TimeSpan.MinValue)
                {
                    maxDuration = MonitorDuration;
                }

                // Handles/FDs
                if (checkHandles)
                {
                    float handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(procId, IsWindows ? null : CodePackage?.Path);

                    if (handles > 0F)
                    {
                        if (procId == parentPid)
                        {
                            AllAppHandlesData[id].AddData(handles);
                        }
                        else
                        {
                            _ = AllAppHandlesData.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<float>(
                                        ErrorWarningProperty.AllocatedFileHandles,
                                        $"{id}:{procName}{procId}", 
                                        capacity,
                                        false,
                                        EnableConcurrentMonitoring));

                            AllAppHandlesData[$"{id}:{procName}{procId}"].AddData(handles);
                        }
                    }
                }

                // Threads
                if (checkThreads)
                {
                    int threads = 0;

                    if (!IsWindows)
                    {
                        // Lightweight on Linux..
                        threads = ProcessInfoProvider.GetProcessThreadCount(procId);
                    }
                    else
                    {
                        // Much faster, less memory.. employs Win32's PSSCaptureSnapshot/PSSQuerySnapshot.
                        threads = NativeMethods.GetProcessThreadCount(procId);
                    }

                    if (threads > 0)
                    {
                        // Parent process (the service process).
                        if (procId == parentPid)
                        {
                            AllAppThreadsData[id].AddData(threads);
                        }
                        else // Child proc spawned by the parent service process.
                        {
                            _ = AllAppThreadsData.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<int>(
                                        ErrorWarningProperty.ThreadCount,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        false,
                                        EnableConcurrentMonitoring));

                            AllAppThreadsData[$"{id}:{procName}{procId}"].AddData(threads);
                        }
                    }
                }

                // Total TCP ports usage
                if (checkAllPorts)
                {
                    // Parent process (the service process).
                    if (procId == parentPid)
                    {
                        AllAppTotalActivePortsData[id].AddData(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, CodePackage?.Path));
                    }
                    else // Child proc spawned by the parent service process.
                    {
                        _ = AllAppTotalActivePortsData.TryAdd(
                            $"{id}:{procName}{procId}",
                            new FabricResourceUsageData<int>(
                                ErrorWarningProperty.ActiveTcpPorts,
                                $"{id}:{procName}{procId}",
                                capacity,
                                false,
                                EnableConcurrentMonitoring));

                        AllAppTotalActivePortsData[$"{id}:{procName}{procId}"].AddData(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, CodePackage?.Path));
                    }
                }

                // Ephemeral TCP ports usage - Raw count.
                if (checkEphemeralPorts)
                {
                    if (procId == parentPid)
                    {
                        AllAppEphemeralPortsData[id].AddData(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, CodePackage?.Path));
                    }
                    else
                    {
                        _ = AllAppEphemeralPortsData.TryAdd(
                                $"{id}:{procName}{procId}",
                                new FabricResourceUsageData<int>(
                                    ErrorWarningProperty.ActiveEphemeralPorts,
                                    $"{id}:{procName}{procId}",
                                    capacity,
                                    false,
                                    EnableConcurrentMonitoring));

                        AllAppEphemeralPortsData[$"{id}:{procName}{procId}"].AddData(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, CodePackage?.Path));
                    }
                }

                // Ephemeral TCP ports usage - Percentage.
                if (checkPercentageEphemeralPorts)
                {
                    double usedPct = OSInfoProvider.Instance.GetActiveEphemeralPortCountPercentage(procId, CodePackage?.Path);

                    if (procId == parentPid)
                    {
                        AllAppEphemeralPortsDataPercent[id].AddData(usedPct);
                    }
                    else
                    {
                        _ = AllAppEphemeralPortsDataPercent.TryAdd(
                                $"{id}:{procName}{procId}",
                                new FabricResourceUsageData<double>(
                                    ErrorWarningProperty.ActiveEphemeralPortsPercentage,
                                    $"{id}:{procName}{procId}",
                                    capacity,
                                    false,
                                    EnableConcurrentMonitoring));

                        AllAppEphemeralPortsDataPercent[$"{id}:{procName}{procId}"].AddData(usedPct);
                    }
                }

                // KVS LVIDs
                if (IsWindows && checkLvids && repOrInst.HostProcessId == procId && repOrInst.ServiceKind == ServiceKind.Stateful)
                {
                    var lvidPct = ProcessInfoProvider.Instance.GetProcessKvsLvidsUsagePercentage(procName, Token, procId);

                    // ProcessGetCurrentKvsLvidsUsedPercentage internally handles exceptions and will always return -1 when it fails.
                    if (lvidPct > -1)
                    {
                        if (procId == parentPid)
                        {
                            AllAppKvsLvidsData[id].AddData(lvidPct);
                        }
                        else
                        {
                            _ = AllAppKvsLvidsData.TryAdd(
                                    $"{id}:{procName}{procId}", 
                                    new FabricResourceUsageData<double>(
                                        ErrorWarningProperty.KvsLvidsPercent, 
                                        $"{id}:{procName}{procId}", 
                                        capacity, 
                                        UseCircularBuffer, 
                                        EnableConcurrentMonitoring));

                            AllAppKvsLvidsData[$"{id}:{procName}{procId}"].AddData(lvidPct);
                        }
                    }
                }

                // No need to proceed further if no cpu/mem thresholds are specified in configuration.
                if (!checkCpu && !checkMemMb && !checkMemPct)
                {
                    state.Stop();
                }

                // Memory \\

                // Private Bytes (MB) - Windows only.
                if (IsWindows && checkMemPrivateBytes)
                {
                    float memPb = ProcessInfoProvider.Instance.GetProcessPrivateBytesMb(procId);
                    
                    if (procId == parentPid)
                    {
                        AllAppPrivateBytesDataMb[id].AddData(memPb);
                    }
                    else
                    {
                        _ = AllAppPrivateBytesDataMb.TryAdd(
                               $"{id}:{procName}{procId}",
                               new FabricResourceUsageData<float>(
                                       ErrorWarningProperty.PrivateBytesMb,
                                       $"{id}:{procName}{procId}",
                                       capacity,
                                       UseCircularBuffer,
                                       EnableConcurrentMonitoring));
                        
                        AllAppPrivateBytesDataMb[$"{id}:{procName}{procId}"].AddData(memPb);
                    }
                }

                // RG Memory (Percent) Monitoring - Windows-only (MonitorResourceGovernanceLimits will always be false for Linux for the time being).
                if (MonitorResourceGovernanceLimits && repOrInst.RGMemoryEnabled && rgMemoryPercentThreshold > 0)
                {
                    float memPb = ProcessInfoProvider.Instance.GetProcessPrivateBytesMb(procId);

                    if (procId == parentPid)
                    {
                        if (repOrInst.RGAppliedMemoryLimitMb > 0)
                        {
                            double pct = ((double)memPb / repOrInst.RGAppliedMemoryLimitMb) * 100;
                            AllAppRGMemoryUsagePercent[id].AddData(pct);
                        }
                    }
                    else
                    {
                        if (repOrInst.RGAppliedMemoryLimitMb > 0)
                        {
                            _ = AllAppRGMemoryUsagePercent.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<double>(
                                            ErrorWarningProperty.RGMemoryUsagePercent,
                                            $"{id}:{procName}{procId}",
                                            capacity,
                                            UseCircularBuffer,
                                            EnableConcurrentMonitoring));

                            double pct = ((double)memPb / repOrInst.RGAppliedMemoryLimitMb) * 100;
                            AllAppRGMemoryUsagePercent[$"{id}:{procName}{procId}"].AddData(pct);
                        }
                    }
                }

                // Working Set.
                if (checkMemMb)
                {
                    if (procId == parentPid)
                    {
                        if (usePerfCounter)
                        {
                            _ = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, procName, Token, usePerfCounter);
                            Thread.Sleep(100);
                        }

                        AllAppMemDataMb[id].AddData(ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, procName, Token, usePerfCounter));
                    }
                    else
                    {
                        _ = AllAppMemDataMb.TryAdd(
                                $"{id}:{procName}{procId}",
                                new FabricResourceUsageData<float>(
                                        ErrorWarningProperty.MemoryConsumptionMb,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                        if (usePerfCounter)
                        {
                            _ = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, procName, Token, usePerfCounter);
                            Thread.Sleep(100);
                        }

                        AllAppMemDataMb[$"{id}:{procName}{procId}"].AddData(ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, procName, Token, usePerfCounter));
                    }
                }

                // Working Set (Percent).
                if (checkMemPct)
                {
                    float processMemMb = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, procName, Token, usePerfCounter);
                    var (TotalMemoryGb, _, _) = OSInfoProvider.Instance.TupleGetSystemPhysicalMemoryInfo();

                    if (TotalMemoryGb > 0)
                    {
                        double usedPct = (float)(processMemMb * 100) / (TotalMemoryGb * 1024);

                        if (procId == parentPid)
                        {
                            AllAppMemDataPercent[id].AddData(Math.Round(usedPct, 2));
                        }
                        else
                        {
                            _ = AllAppMemDataPercent.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<double>(
                                        ErrorWarningProperty.MemoryConsumptionPercentage,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                            AllAppMemDataPercent[$"{id}:{procName}{procId}"].AddData(Math.Round(usedPct, 2));
                        }
                    }
                }

                // Private Bytes (Percent) - Windows only.
                if (IsWindows && checkMemPrivateBytesPct)
                {
                    float processPrivateBytesMb = ProcessInfoProvider.Instance.GetProcessPrivateBytesMb(procId);
                    var (CommitLimitGb, _) = OSInfoProvider.Instance.TupleGetSystemCommittedMemoryInfo();

                    if (CommitLimitGb > 0)
                    {
                        double usedPct = (float)(processPrivateBytesMb * 100) / (CommitLimitGb * 1024);

                        if (procId == parentPid)
                        {
                            AllAppPrivateBytesDataPercent[id].AddData(Math.Round(usedPct, 2));
                        }
                        else
                        {
                            _ = AllAppPrivateBytesDataPercent.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<double>(
                                        ErrorWarningProperty.PrivateBytesPercent,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                            AllAppPrivateBytesDataPercent[$"{id}:{procName}{procId}"].AddData(Math.Round(usedPct, 2));
                        }
                    }
                }

                // CPU \\

                ICpuUsage cpuUsage;

                if (IsWindows)
                {
                    cpuUsage = new CpuUsageWin32();
                }
                else
                {
                    cpuUsage = new CpuUsageProcess();
                }

                Stopwatch timer = Stopwatch.StartNew();

                while (timer.Elapsed <= maxDuration)
                {
                    if (token.IsCancellationRequested)
                    {
                        state.Stop();
                    }

                    // CPU (all cores) \\

                    if (checkCpu)
                    {
                        double cpu = 0;
                        cpu = cpuUsage.GetCurrentCpuUsagePercentage(procId, IsWindows ? procName : null);

                        if (procId == parentPid)
                        {
                            AllAppCpuData[id].AddData(cpu);
                        }
                        else
                        {
                            _ = AllAppCpuData.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<double>(
                                        ErrorWarningProperty.CpuTime,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                            AllAppCpuData[$"{id}:{procName}{procId}"].AddData(cpu);
                        }
                    }

                    Thread.Sleep(50);
                }

                timer.Stop();
                timer = null;
            });
        }

        private async Task SetDeployedReplicaOrInstanceListAsync(Uri applicationNameFilter = null, string applicationType = null)
        {
            ObserverLogger.LogInfo("Starting SetDeployedReplicaOrInstanceListAsync.");
            List<DeployedApplication> depApps = null;

            // DEBUG - Perf
            //var stopwatch = Stopwatch.StartNew();
            try
            {
                if (applicationNameFilter != null)
                {
                    depApps = deployedApps.FindAll(a => a.ApplicationName.Equals(applicationNameFilter));
                }
                else if (!string.IsNullOrWhiteSpace(applicationType))
                {
                    depApps = deployedApps.FindAll(a => a.ApplicationTypeName == applicationType);
                }
                else
                {
                    depApps = deployedApps;
                }
            }
            catch (ArgumentException ae)
            {
                ObserverLogger.LogWarning($"SetDeployedReplicaOrInstanceListAsync: Unable to process replica information:{Environment.NewLine}{ae}");
                return;
            }

            foreach (var userTarget in userTargetList)
            {
                for (int i = 0; i < depApps.Count; i++)
                {
                    Token.ThrowIfCancellationRequested();

                    try
                    {
                        Token.ThrowIfCancellationRequested();

                        // TargetAppType supplied in user config, so set TargetApp on deployedApp instance by searching for it in the currently deployed application list.
                        if (userTarget.TargetAppType != null)
                        {
                            if (depApps[i].ApplicationTypeName != userTarget.TargetAppType)
                            {
                                continue;
                            }

                            userTarget.TargetApp = depApps[i].ApplicationName.OriginalString;
                        }

                        if (userTarget.TargetApp == null)
                        {
                            continue;
                        }

                        if (depApps[i].ApplicationName.OriginalString != userTarget.TargetApp)
                        {
                            continue;
                        }

                        string[] filteredServiceList = null;
                        ServiceFilterType filterType = ServiceFilterType.None;

                        // Filter serviceInclude/Exclude config.
                        if (!string.IsNullOrWhiteSpace(userTarget.ServiceExcludeList))
                        {
                            filteredServiceList = userTarget.ServiceExcludeList.Replace(" ", string.Empty).Split(',');
                            filterType = ServiceFilterType.Exclude;
                        }
                        else if (!string.IsNullOrWhiteSpace(userTarget.ServiceIncludeList))
                        {
                            filteredServiceList = userTarget.ServiceIncludeList.Replace(" ", string.Empty).Split(',');
                            filterType = ServiceFilterType.Include;
                        }

                        List<ReplicaOrInstanceMonitoringInfo> replicasOrInstances = await GetDeployedReplicasAsync(
                                                                                            new Uri(userTarget.TargetApp),
                                                                                            filteredServiceList,
                                                                                            filterType,
                                                                                            applicationType);

                        if (replicasOrInstances?.Count > 0)
                        {
                            /* TOTHINK: Filter out SharedProcess replicas to only include a single element in the ReplicaOrInstanceList list;
                            // so, only one SharedHost activated service entry with the same host process id should be added to the global replica list,
                            // which is used in multiple code paths by AppObserver.
                            foreach (var rep in replicasOrInstances)
                            {
                                if (!ReplicaOrInstanceList.Any(r => r.HostProcessId == rep.HostProcessId))
                                {
                                    ReplicaOrInstanceList.Add(rep);
                                }
                            }*/

                            ReplicaOrInstanceList.AddRange(replicasOrInstances);

                            var targets = userTargetList.Where(x => x.TargetApp != null && x.TargetApp == userTarget.TargetApp
                                                                 || x.TargetAppType != null && x.TargetAppType == userTarget.TargetAppType);

                            if (userTarget.TargetApp != null && !deployedTargetList.Any(r => r.TargetApp == userTarget.TargetApp))
                            {
                                deployedTargetList.AddRange(targets);
                            }

                            replicasOrInstances.Clear();
                        }

                        replicasOrInstances = null;
                    }
                    catch (Exception e) when (e is ArgumentException || e is FabricException || e is Win32Exception)
                    {
                        ObserverLogger.LogWarning(
                            $"SetDeployedReplicaOrInstanceListAsync: Unable to process replica information for {userTarget}{Environment.NewLine}{e}");
                    }
                }
            }

            depApps?.Clear();
            depApps = null;
            ObserverLogger.LogInfo("Completed SetDeployedReplicaOrInstanceListAsync.");
            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"SetDeployedReplicaOrInstanceListAsync for {applicationNameFilter?.OriginalString} run duration: {stopwatch.Elapsed}");
        }

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedReplicasAsync(
                                                                     Uri appName,
                                                                     string[] serviceFilterList = null,
                                                                     ServiceFilterType filterType = ServiceFilterType.None,
                                                                     string appTypeName = null)
        {
            ObserverLogger.LogInfo("Starting GetDeployedReplicasAsync.");
            // DEBUG - Perf
            //var stopwatch = Stopwatch.StartNew();
            var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(
                                                        NodeName, appName, null, null, ConfigurationSettings.AsyncTimeout, Token),
                                                Token);

            //ObserverLogger.LogInfo($"QueryManager.GetDeployedReplicaListAsync for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
            var replicaMonitoringList = new ConcurrentQueue<ReplicaOrInstanceMonitoringInfo>();
            string appType = appTypeName;

            if (string.IsNullOrWhiteSpace(appType))
            {
                try
                {
                    if (deployedApps.Any(app => app.ApplicationName == appName))
                    {
                        appType = deployedApps.First(app => app.ApplicationName == appName).ApplicationTypeName;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                {

                }
            }

            SetInstanceOrReplicaMonitoringList(
                appName,
                appType,
                serviceFilterList,
                filterType,
                deployedReplicaList,
                replicaMonitoringList);

            ObserverLogger.LogInfo("Completed GetDeployedReplicasAsync.");
            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"GetDeployedReplicasAsync for {appName.OriginalString} run duration: {stopwatch.Elapsed}");

            return replicaMonitoringList.ToList();
        }

        private void SetInstanceOrReplicaMonitoringList(
                        Uri appName,
                        string appTypeName,
                        string[] filterList,
                        ServiceFilterType filterType,
                        DeployedServiceReplicaList deployedReplicaList,
                        ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList)
        {
            ObserverLogger.LogInfo("Starting SetInstanceOrReplicaMonitoringList.");
            // DEBUG - Perf
            //var stopwatch = Stopwatch.StartNew();
            
            _ = Parallel.For (0, deployedReplicaList.Count, parallelOptions, (i, state) =>
            {
                Token.ThrowIfCancellationRequested();

                var deployedReplica = deployedReplicaList[i];
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica statefulReplica when statefulReplica.ReplicaRole == ReplicaRole.Primary || statefulReplica.ReplicaRole == ReplicaRole.ActiveSecondary:
                    {
                        if (filterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = filterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    return;
                            }
                        }

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statefulReplica.HostProcessId,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ReplicaRole = statefulReplica.ReplicaRole,
                            ServiceKind = statefulReplica.ServiceKind,
                            ServiceName = statefulReplica.ServiceName,
                            ServiceManifestName = statefulReplica.ServiceManifestName,
                            ServiceTypeName = statefulReplica.ServiceTypeName,
                            ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                            ServicePackageActivationMode = string.IsNullOrWhiteSpace(statefulReplica.ServicePackageActivationId) ?
                                            ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                            ReplicaStatus = statefulReplica.ReplicaStatus
                        };

                        /* In order to provide accurate resource usage of an SF service process we need to also account for
                        any processes (children) that the service process (parent) created/spawned. */

                        if (EnableChildProcessMonitoring && replicaInfo?.HostProcessId > 0)
                        {
                            // DEBUG - Perf
                            //var sw = Stopwatch.StartNew();
                            List<(string ProcName, int Pid)> childPids;

                            if (IsWindows)
                            {
                                childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId, Win32HandleToProcessSnapshot);
                            }
                            else
                            {
                                childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId);
                            }

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                                ObserverLogger.LogInfo($"{replicaInfo?.ServiceName}:{Environment.NewLine}Child procs (name, id): {string.Join(" ", replicaInfo.ChildProcesses)}");
                            }
                            //sw.Stop();
                            //ObserverLogger.LogInfo($"EnableChildProcessMonitoring block run duration: {sw.Elapsed}");
                        }
                        break;
                    }
                    case DeployedStatelessServiceInstance statelessInstance:
                    {
                        if (filterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = filterList.Any(s => statelessInstance.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    return;
                            }
                        }

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statelessInstance.HostProcessId,
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ReplicaRole = ReplicaRole.None,
                            ServiceKind = statelessInstance.ServiceKind,
                            ServiceName = statelessInstance.ServiceName,
                            ServiceManifestName = statelessInstance.ServiceManifestName,
                            ServiceTypeName = statelessInstance.ServiceTypeName,
                            ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                            ServicePackageActivationMode = string.IsNullOrWhiteSpace(statelessInstance.ServicePackageActivationId) ?
                                            ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                            ReplicaStatus = statelessInstance.ReplicaStatus
                        };

                        if (EnableChildProcessMonitoring && replicaInfo?.HostProcessId > 0)
                        {
                            // DEBUG - Perf
                            //var sw = Stopwatch.StartNew();
                            List<(string ProcName, int Pid)> childPids;

                            if (IsWindows)
                            {
                                childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId, Win32HandleToProcessSnapshot);
                            }
                            else
                            {
                                childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId);
                            }

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                                ObserverLogger.LogInfo($"{replicaInfo?.ServiceName}:{Environment.NewLine}Child procs (name, id): {string.Join(" ", replicaInfo.ChildProcesses)}");
                            }
                            //sw.Stop();
                            //ObserverLogger.LogInfo($"EnableChildProcessMonitoring block run duration: {sw.Elapsed}");
                        }
                        break;
                    }
                }

                // TOTHINK: Filter out SharedProcess replicas if one is already present in the list ssince they are all hosted in the same process,
                // and FO can only operate at the process level for resource monitoring.
                /*if (replicaMonitoringList.Any(
                        r => r.ServicePackageActivationMode == ServicePackageActivationMode.SharedProcess
                          && r.HostProcessId == replicaInfo.HostProcessId))
                {
                    // return in a parallel loop is equivalent to continue in a sequential loop.
                    return;
                }*/

                ProcessServiceConfiguration(appTypeName, deployedReplica.CodePackageName, ref replicaInfo);

                if (replicaInfo?.HostProcessId > 0 && !ReplicaOrInstanceList.Any(r => r.ServiceName.Equals(replicaInfo.ServiceName)))
                {
                    if (IsWindows)
                    {
                        replicaInfo.HostProcessName = NativeMethods.GetProcessNameFromId((int)replicaInfo.HostProcessId);
                    }
                    else // Linux
                    {
                        try
                        {
                            using (Process p = Process.GetProcessById((int)replicaInfo.HostProcessId))
                            {
                                replicaInfo.HostProcessName = p.ProcessName;
                            }
                        }
                        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
                        {

                        }
                    }
                    
                    // If Fabric is the hosting process, then this is a Guest Executable or helper code package.
                    if (replicaInfo.HostProcessName != "Fabric")
                    {
                        replicaMonitoringList.Enqueue(replicaInfo);
                    }
                }

                ProcessMultipleHelperCodePackages(appName, appTypeName, deployedReplica, ref replicaMonitoringList, replicaInfo.HostProcessName == "Fabric");
            });
            ObserverLogger.LogInfo("Completed SetInstanceOrReplicaMonitoringList.");
            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"SetInstanceOrReplicaMonitoringList for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
        }

        private void ProcessServiceConfiguration(string appTypeName, string codepackageName, ref ReplicaOrInstanceMonitoringInfo replicaInfo)
        {
            // ResourceGovernance/AppTypeVer/ServiceTypeVer.
            ObserverLogger.LogInfo($"Starting ProcessServiceConfiguration check for {replicaInfo.ServiceName.OriginalString}.");

            if (string.IsNullOrWhiteSpace(appTypeName))
            {
                return;
            }

            try
            {
                string appTypeVersion = null;

                var appList =
                    FabricClientInstance.QueryManager.GetApplicationListAsync(replicaInfo.ApplicationName, ConfigurationSettings.AsyncTimeout, Token)?.Result;

                if (appList?.Count > 0)
                {
                    try
                    {
                        if (appList.Any(app => app.ApplicationTypeName == appTypeName))
                        {
                            appTypeVersion = appList.First(app => app.ApplicationTypeName == appTypeName).ApplicationTypeVersion;
                            replicaInfo.ApplicationTypeVersion = appTypeVersion;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                    {
                        
                    }

                    if (!string.IsNullOrWhiteSpace(appTypeVersion))
                    {
                        // RG - Windows-only. Linux is not supported yet.
                        if (IsWindows)
                        {
                            string appManifest = FabricClientInstance.ApplicationManager.GetApplicationManifestAsync(
                                                    appTypeName, appTypeVersion, ConfigurationSettings.AsyncTimeout, Token)?.Result;

                            if (!string.IsNullOrWhiteSpace(appManifest) && appManifest.Contains($"<{ObserverConstants.RGPolicyNodeName} "))
                            {
                                (replicaInfo.RGMemoryEnabled, replicaInfo.RGAppliedMemoryLimitMb) =
                                    fabricClientUtilities.TupleGetMemoryResourceGovernanceInfo(appManifest, replicaInfo.ServiceManifestName, codepackageName); 
                            }
                        }

                        // ServiceTypeVersion
                        var serviceList =
                            FabricClientInstance.QueryManager.GetServiceListAsync(
                                replicaInfo.ApplicationName, replicaInfo.ServiceName, ConfigurationSettings.AsyncTimeout, Token)?.Result;

                        if (serviceList?.Count > 0)
                        {
                            try
                            {
                                Uri serviceName = replicaInfo.ServiceName;

                                if (serviceList.Any(s => s.ServiceName == serviceName))
                                {
                                    replicaInfo.ServiceTypeVersion = serviceList.First(s => s.ServiceName == serviceName).ServiceManifestVersion;
                                }
                            }
                            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                            {

                            }
                        }
                    }
                }
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is TimeoutException || e is XmlException)
            {
                ObserverLogger.LogWarning($"Handled: Failed to process Service configuration for {replicaInfo.ServiceName.OriginalString} with exception '{e.Message}'");
                // move along
            }
            ObserverLogger.LogInfo($"Completed ProcessServiceConfiguration for {replicaInfo.ServiceName.OriginalString}.");
        }

        private void ProcessMultipleHelperCodePackages(
                        Uri appName,
                        string appTypeName,
                        DeployedServiceReplica deployedReplica,
                        ref ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> repsOrInstancesInfo,
                        bool isHostedByFabric)
        {
            ObserverLogger.LogInfo($"Starting ProcessMultipleHelperCodePackages for {deployedReplica.ServiceName} (isHostedByFabric = {isHostedByFabric})");
            try
            {
                DeployedCodePackageList codepackages = FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(
                                                        NodeName,
                                                        appName,
                                                        deployedReplica.ServiceManifestName,
                                                        null,
                                                        ConfigurationSettings.AsyncTimeout,
                                                        Token).Result;

                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                // Check for multiple code packages or GuestExecutable service (Fabric is the host).
                if (codepackages.Count < 2 && !isHostedByFabric)
                {
                    ObserverLogger.LogInfo($"Completed ProcessMultipleHelperCodePackages.");
                    return;
                }

                foreach (var codepackage in codepackages)
                {
                    // If the code package does not belong to a deployed replica, then this is the droid we're looking for (a helper code package or guest executable).
                    if (codepackage.CodePackageName == deployedReplica.CodePackageName)
                    {
                        continue;
                    }

                    int procId = (int)codepackage.EntryPoint.ProcessId; // The actual process id of the helper or guest executable binary.
                    string procName = null;

                    // Process class is a CPU bottleneck on Windows.
                    if (IsWindows)
                    {
                        procName = NativeMethods.GetProcessNameFromId(procId);
                    }
                    else // Linux
                    {
                        using (var proc = Process.GetProcessById(procId))
                        {
                            try
                            {
                                procName = proc.ProcessName;
                            }
                            catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException || e is ArgumentException)
                            {
                                ObserverLogger.LogInfo($"ProcessMultipleHelperCodePackages::GetProcessById(Linux): Handled Exception: {e.Message}");
                            }
                        }
                    }

                    // Make sure procName lookup worked and if so that it is still the process we're looking for.
                    if (string.IsNullOrWhiteSpace(procName) || !EnsureProcess(procName, procId))
                    {
                        continue;
                    }

                    // This ensures that support for multiple CodePackages and GuestExecutable services fit naturally into AppObserver's *existing* implementation.
                    replicaInfo = new ReplicaOrInstanceMonitoringInfo
                    {
                        ApplicationName = appName,
                        ApplicationTypeName = appTypeName,
                        HostProcessId = procId,
                        HostProcessName = procName,
                        ReplicaOrInstanceId = deployedReplica is DeployedStatefulServiceReplica replica ?
                                                replica.ReplicaId : ((DeployedStatelessServiceInstance)deployedReplica).InstanceId,
                        PartitionId = deployedReplica.Partitionid,
                        ReplicaRole = deployedReplica is DeployedStatefulServiceReplica rep ? rep.ReplicaRole : ReplicaRole.None,
                        ServiceKind = deployedReplica.ServiceKind,
                        ServiceName = deployedReplica.ServiceName,
                        ServiceManifestName = codepackage.ServiceManifestName,
                        ServiceTypeName = deployedReplica.ServiceTypeName,
                        ServicePackageActivationId = string.IsNullOrWhiteSpace(codepackage.ServicePackageActivationId) ?
                                                        deployedReplica.ServicePackageActivationId : codepackage.ServicePackageActivationId,
                        ServicePackageActivationMode = string.IsNullOrWhiteSpace(codepackage.ServicePackageActivationId) ?
                                                        ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                        ReplicaStatus = deployedReplica is DeployedStatefulServiceReplica r ?
                                            r.ReplicaStatus : ((DeployedStatelessServiceInstance)deployedReplica).ReplicaStatus,
                    };

                    // If Helper binaries launch child processes, AppObserver will monitor them, too.
                    if (EnableChildProcessMonitoring && procId > 0)
                    {
                        // DEBUG - Perf
                        //var sw = Stopwatch.StartNew();
                        List<(string ProcName, int Pid)> childPids;

                        if (IsWindows)
                        {
                            childPids = ProcessInfoProvider.Instance.GetChildProcessInfo(procId, Win32HandleToProcessSnapshot);
                        }
                        else
                        {
                            childPids = ProcessInfoProvider.Instance.GetChildProcessInfo(procId);
                        }

                        if (childPids != null && childPids.Count > 0)
                        {
                            replicaInfo.ChildProcesses = childPids;
                            ObserverLogger.LogInfo($"{replicaInfo?.ServiceName}:{Environment.NewLine}Child procs (name, id): {string.Join(" ", replicaInfo.ChildProcesses)}");
                        }
                        //sw.Stop();
                        //ObserverLogger.LogInfo($"EnableChildProcessMonitoring block run duration: {sw.Elapsed}");
                    }

                    // ResourceGovernance/AppTypeVer/ServiceTypeVer.
                    ProcessServiceConfiguration(appTypeName, codepackage.CodePackageName, ref replicaInfo);
                        
                    if (replicaInfo != null && replicaInfo.HostProcessId > 0 && !repsOrInstancesInfo.Any(r => r.HostProcessId == replicaInfo.HostProcessId))
                    {
                        repsOrInstancesInfo.Enqueue(replicaInfo);
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FabricException || e is TaskCanceledException || e is TimeoutException)
            {
                ObserverLogger.LogInfo($"ProcessMultipleHelperCodePackages: Handled Exception: {e.Message}");
            }
            ObserverLogger.LogInfo($"Completed ProcessMultipleHelperCodePackages.");
        }

        private void LogAllAppResourceDataToCsv(string appName)
        {
            if (!EnableCsvLogging)
            {
                return;
            }

            try
            {
                // CPU Time
                if (AllAppCpuData != null && AllAppCpuData.ContainsKey(appName))
                {
                    CsvFileLogger.LogData(
                        fileName,
                        appName,
                        ErrorWarningProperty.CpuTime,
                        "Average",
                        Math.Round(AllAppCpuData.First(x => x.Key == appName).Value.AverageDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        appName,
                        ErrorWarningProperty.CpuTime,
                        "Peak",
                        Math.Round(AllAppCpuData.First(x => x.Key == appName).Value.MaxDataValue));
                }

                // Memory - Working set \\

                if (AllAppMemDataMb != null && AllAppMemDataMb.ContainsKey(appName))
                {
                    CsvFileLogger.LogData(
                        fileName,
                        appName,
                        ErrorWarningProperty.MemoryConsumptionMb,
                        "Average",
                        Math.Round(AllAppMemDataMb.First(x => x.Key == appName).Value.AverageDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        appName,
                        ErrorWarningProperty.MemoryConsumptionMb,
                        "Peak",
                        Math.Round(Convert.ToDouble(AllAppMemDataMb.First(x => x.Key == appName).Value.MaxDataValue)));
                }

                if (AllAppMemDataPercent != null && AllAppMemDataPercent.ContainsKey(appName))
                {
                    CsvFileLogger.LogData(
                       fileName,
                       appName,
                       ErrorWarningProperty.MemoryConsumptionPercentage,
                       "Average",
                       Math.Round(AllAppMemDataPercent.First(x => x.Key == appName).Value.AverageDataValue));

                    CsvFileLogger.LogData(
                        fileName,
                        appName,
                        ErrorWarningProperty.MemoryConsumptionPercentage,
                        "Peak",
                        Math.Round(Convert.ToDouble(AllAppMemDataPercent.FirstOrDefault(x => x.Key == appName).Value.MaxDataValue)));
                }

                // Memory - Private Bytes \\

                if (IsWindows)
                {
                    if (AllAppPrivateBytesDataMb != null && AllAppPrivateBytesDataMb.ContainsKey(appName))
                    {
                        if (AllAppPrivateBytesDataMb.Any(x => x.Key == appName))
                        {
                            CsvFileLogger.LogData(
                                fileName,
                                appName,
                                ErrorWarningProperty.PrivateBytesMb,
                                "Average",
                                Math.Round(AllAppPrivateBytesDataMb.First(x => x.Key == appName).Value.AverageDataValue));

                            CsvFileLogger.LogData(
                                fileName,
                                appName,
                                ErrorWarningProperty.PrivateBytesMb,
                                "Peak",
                                Math.Round(Convert.ToDouble(AllAppPrivateBytesDataMb.First(x => x.Key == appName).Value.MaxDataValue)));
                        }
                    }

                    if (AllAppPrivateBytesDataPercent != null && AllAppPrivateBytesDataPercent.ContainsKey(appName))
                    {
                        if (AllAppPrivateBytesDataPercent.Any(x => x.Key == appName))
                        {
                            CsvFileLogger.LogData(
                               fileName,
                               appName,
                               ErrorWarningProperty.PrivateBytesPercent,
                               "Average",
                               Math.Round(AllAppPrivateBytesDataPercent.First(x => x.Key == appName).Value.AverageDataValue));

                            CsvFileLogger.LogData(
                                fileName,
                                appName,
                                ErrorWarningProperty.PrivateBytesPercent,
                                "Peak",
                                Math.Round(Convert.ToDouble(AllAppPrivateBytesDataPercent.FirstOrDefault(x => x.Key == appName).Value.MaxDataValue)));
                        }
                    }
                }

                // Ports \\

                if (AllAppTotalActivePortsData != null && AllAppTotalActivePortsData.ContainsKey(appName))
                {
                    if (AllAppTotalActivePortsData.Any(x => x.Key == appName))
                    {
                        CsvFileLogger.LogData(
                            fileName,
                            appName,
                            ErrorWarningProperty.ActiveTcpPorts,
                            "Total",
                            Math.Round(Convert.ToDouble(AllAppTotalActivePortsData.First(x => x.Key == appName).Value.MaxDataValue)));
                    }
                }

                if (AllAppEphemeralPortsData != null && AllAppEphemeralPortsData.ContainsKey(appName))
                {
                    if (AllAppEphemeralPortsData.Any(x => x.Key == appName))
                    {
                        CsvFileLogger.LogData(
                            fileName,
                            appName,
                            ErrorWarningProperty.ActiveEphemeralPorts,
                            "Total",
                            Math.Round(Convert.ToDouble(AllAppEphemeralPortsData.First(x => x.Key == appName).Value.MaxDataValue)));
                    }
                }

                // Handles
                if (AllAppHandlesData != null && AllAppHandlesData.ContainsKey(appName))
                {
                    if (AllAppHandlesData.Any(x => x.Key == appName))
                    {
                        CsvFileLogger.LogData(
                             fileName,
                             appName,
                             ErrorWarningProperty.AllocatedFileHandles,
                             "Total",
                             AllAppHandlesData.First(x => x.Key == appName).Value.MaxDataValue);
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                ObserverLogger.LogWarning($"Failure generating CSV data: {e.Message}");
            }

            DataTableFileLogger.Flush();
        }

        private void CleanUp()
        {
            ObserverLogger.LogInfo("Starting CleanUp...");
            deployedTargetList?.Clear();
            deployedTargetList = null;

            userTargetList?.Clear();
            userTargetList = null;

            ReplicaOrInstanceList?.Clear();
            ReplicaOrInstanceList = null;

            processInfoDictionary?.Clear();
            processInfoDictionary = null;

            deployedApps?.Clear();
            deployedApps = null;

            if (AllAppCpuData != null && AllAppCpuData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppCpuData?.Clear();
                AllAppCpuData = null;
            }

            if (AllAppEphemeralPortsData != null && AllAppEphemeralPortsData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppEphemeralPortsData?.Clear();
                AllAppEphemeralPortsData = null;
            }

            if (AllAppEphemeralPortsDataPercent != null && AllAppEphemeralPortsDataPercent.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppEphemeralPortsDataPercent?.Clear();
                AllAppEphemeralPortsDataPercent = null;
            }

            if (AllAppHandlesData != null && AllAppHandlesData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppHandlesData?.Clear();
                AllAppHandlesData = null;
            }

            if (AllAppMemDataMb != null && AllAppMemDataMb.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppMemDataMb?.Clear();
                AllAppMemDataMb = null;
            }

            if (AllAppMemDataPercent != null && AllAppMemDataPercent.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppMemDataPercent?.Clear();
                AllAppMemDataPercent = null;
            }

            if (AllAppTotalActivePortsData != null && AllAppTotalActivePortsData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppTotalActivePortsData?.Clear();
                AllAppTotalActivePortsData = null;
            }

            if (AllAppThreadsData != null && AllAppThreadsData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppThreadsData?.Clear();
                AllAppThreadsData = null;
            }

            // Windows-only cleanup.
            if (IsWindows)
            {
                if (AllAppKvsLvidsData != null && AllAppKvsLvidsData.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppKvsLvidsData?.Clear();
                    AllAppKvsLvidsData = null;
                }

                if (AllAppPrivateBytesDataMb != null && AllAppPrivateBytesDataMb.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppPrivateBytesDataMb?.Clear();
                    AllAppPrivateBytesDataMb = null;
                }

                if (AllAppPrivateBytesDataPercent != null && AllAppPrivateBytesDataPercent.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppPrivateBytesDataPercent?.Clear();
                    AllAppPrivateBytesDataPercent = null;
                }

                if (AllAppRGMemoryUsagePercent != null && AllAppRGMemoryUsagePercent.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppRGMemoryUsagePercent?.Clear();
                    AllAppRGMemoryUsagePercent = null;
                }

                handleToProcSnapshot?.Dispose();
                GC.KeepAlive(handleToProcSnapshot);
                handleToProcSnapshot = null;
            }
            ObserverLogger.LogInfo("Completed CleanUp...");
        }
    }
}