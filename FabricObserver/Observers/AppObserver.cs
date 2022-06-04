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
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.Utilities.ServiceFabric;
using Newtonsoft.Json;

namespace FabricObserver.Observers
{
    // This observer monitors the behavior of user SF service processes (and their children) and signals Warning and Error based on user-supplied resource thresholds
    // in AppObserver.config.json. This observer will also emit telemetry (ETW, LogAnalytics/AppInsights) if enabled in Settings.xml (ObserverManagerConfiguration) and ApplicationManifest.xml (AppObserverEnableEtw).
    public sealed class AppObserver : ObserverBase
    {
        private const double KvsLvidsWarningPercentage = 75.0;
        private readonly bool _isWindows;
        private readonly object _lock = new object();

        // These are the concurrent data containers that hold all monitoring data for all application targets for specific metrics.
        // In the case where machine has capable CPU configuration and AppObserverEnableConcurrentMonitoring is enabled, these ConcurrentDictionaries
        // will be read by and written to by multiple threads. In the case where concurrency is not possible (or not enabled), they will sort of act as "normal"
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

        // Stores process id (key) / process name pairs for all monitored service processes.
        private ConcurrentDictionary<int, string> _processInfoDictionary;

        // _userTargetList is the list of ApplicationInfo objects representing app/app types supplied in user configuration (AppObserver.config.json).
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<ApplicationInfo> _userTargetList;

        // _deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied target list.
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<ApplicationInfo> _deployedTargetList;

        // _deployedApps is the List of all apps currently deployed on the local Fabric node.
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<DeployedApplication> _deployedApps;

        private readonly Stopwatch _stopwatch;
        private readonly object lockObj = new object();
        private FabricClientUtilities _client;
        private ParallelOptions _parallelOptions;
        private string _fileName;
        private int _appCount;
        private int _serviceCount;
        private bool _checkPrivateWorkingSet;
        private NativeMethods.SafeObjectHandle _handleToProcSnapshot = null;
        
        private NativeMethods.SafeObjectHandle HandleToProcessSnapshot
        {
            get
            {
                // This is only useful for Windows.
                if (!_isWindows)
                {
                    return null;
                }

                if (_handleToProcSnapshot == null)
                {
                    lock (lockObj)
                    {
                        if (_handleToProcSnapshot == null)
                        {
                            _handleToProcSnapshot = NativeMethods.CreateToolhelp32Snapshot((uint)NativeMethods.CreateToolhelp32SnapshotFlags.TH32CS_SNAPPROCESS, 0);
                            if (_handleToProcSnapshot.IsInvalid)
                            {
                                throw new Win32Exception(
                                    $"HandleToProcessSnapshot: Failed to get process snapshot with error code {Marshal.GetLastWin32Error()}");
                            }
                        }
                    }
                }
                return _handleToProcSnapshot;
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

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public AppObserver(StatelessServiceContext context) : base(null, context)
        {
            _stopwatch = new Stopwatch();
            _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            Token = token;
            _stopwatch.Start();
            bool initialized = await InitializeAsync().ConfigureAwait(false);

            if (!initialized)
            {
                ObserverLogger.LogWarning("AppObserver was unable to initialize correctly due to misconfiguration. " +
                                          "Please check your AppObserver configuration settings.");
                _stopwatch.Stop();
                _stopwatch.Reset();
                CleanUp();
                LastRunDateTime = DateTime.Now;
                return;
            }

            ParallelLoopResult result = await MonitorDeployedAppsAsync(token).ConfigureAwait(false);
            
            if (result.IsCompleted)
            {
                await ReportAsync(token).ConfigureAwait(false);
            }

            _stopwatch.Stop();
            RunDuration = _stopwatch.Elapsed;
           
            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration {(_parallelOptions.MaxDegreeOfParallelism > 1 ? "with" : "without")} " +
                                       $"Parallel (Processors: {Environment.ProcessorCount} MaxDegreeOfParallelism: {_parallelOptions.MaxDegreeOfParallelism}):{RunDuration}");
            }

            CleanUp();
            _stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }
       
        public override Task ReportAsync(CancellationToken token)
        {
            if (_deployedTargetList.Count == 0)
            {
                return Task.CompletedTask;
            }

            //DEBUG
            //var stopwatch = Stopwatch.StartNew();
            TimeSpan TTL = GetHealthReportTimeToLive();

            // This will run sequentially (with 1 thread) if the underlying CPU config does not meet the requirements for concurrency (e.g., if logical procs < 4).
            _ = Parallel.For (0, ReplicaOrInstanceList.Count, _parallelOptions, (i, state) =>
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

                if (!_deployedTargetList.Any(
                         a => (a.TargetApp != null && a.TargetApp == repOrInst.ApplicationName.OriginalString) ||
                              (a.TargetAppType != null && a.TargetAppType == repOrInst.ApplicationTypeName)))
                {
                    return;
                }

                app = _deployedTargetList.First(
                        a => (a.TargetApp != null && a.TargetApp == repOrInst.ApplicationName.OriginalString) ||
                                (a.TargetAppType != null && a.TargetAppType == repOrInst.ApplicationTypeName));
                

                // process serviceIncludeList config items for a single app.
                if (app?.ServiceIncludeList != null)
                {
                    // Ensure the service is the one we are looking for.
                    if (_deployedTargetList.Any(
                            a => a.ServiceIncludeList != null &&
                                    a.ServiceIncludeList.Contains(repOrInst.ServiceName.OriginalString.Remove(0, repOrInst.ApplicationName.OriginalString.Length + 1))))
                    {
                        // It could be the case that user config specifies multiple inclusion lists for a single app/type in user configuration. We want the correct service here.
                        app = _deployedTargetList.First(
                                a => a.ServiceIncludeList != null &&
                                a.ServiceIncludeList.Contains(repOrInst.ServiceName.OriginalString.Remove(0, repOrInst.ApplicationName.OriginalString.Length + 1)));
                    }
                }
                
                try
                {
                    processId = (int)repOrInst.HostProcessId;
                    
                    if (!_processInfoDictionary.ContainsKey(processId))
                    {
                        // process with process id processId not in dictionary (which would mean it wasn't monitored due some issue that happened well before this code runs).
                        // Continue..
                        return;
                    }

                    processName = _processInfoDictionary[processId];

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
                    lock (_lock)
                    {
                        _fileName = $"{processName}{(CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives ? "_" + DateTime.UtcNow.ToString("o") : string.Empty)}";

                        // BaseLogDataLogFolderPath is set in ObserverBase or a default one is created by CsvFileLogger.
                        // This means a new folder will be added to the base path.
                        if (CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives)
                        {
                            CsvFileLogger.DataLogFolder = processName;
                        }

                        // Log pid..
                        CsvFileLogger.LogData(_fileName, id, "ProcessId", "", processId);

                        // Log resource usage data to CSV files.
                        LogAllAppResourceDataToCsv(id);
                    }
                }

                // CPU - Parent process
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // Memory MB - Parent process
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // Memory Percent - Parent process
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // TCP Ports - Active - Parent process
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // TCP Ports Total - Ephemeral (port numbers fall in the dynamic range) - Parent process
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // TCP Ports Percentage - Ephemeral (port numbers fall in the dynamic range) - Parent process
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // Allocated (in use) Handles - Parent process
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // Threads - Parent process
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
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
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }           

                // Child proc info telemetry.
                if (hasChildProcs && MaxChildProcTelemetryDataCount > 0)
                {
                    if (IsEtwEnabled)
                    {
                        var data = new
                        {
                            ChildProcessTelemetryData = JsonConvert.SerializeObject(childProcessTelemetryDataList.ToList())
                        };

                        ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, data);
                    }

                    if (IsTelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportMetricAsync(childProcessTelemetryDataList.ToList(), token);
                    }
                }
           });

            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"ReportAsync run duration with parallel: {stopwatch.Elapsed}");
            return Task.CompletedTask;
        }

        // This runs each time ObserveAsync is run to ensure that any new app targets and config changes will
        // be up to date.
        public async Task<bool> InitializeAsync()
        {
            ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            _userTargetList = new List<ApplicationInfo>();
            _deployedTargetList = new List<ApplicationInfo>();

            // NodeName is passed here to not break unit tests, which include a mock service fabric context..
            _client = new FabricClientUtilities(NodeName);
            _deployedApps = await _client.GetAllDeployedAppsAsync(Token).ConfigureAwait(false);

            // DEBUG
            //var stopwatch = Stopwatch.StartNew();

            // Set properties with Application Parameter settings (housed in ApplicationManifest.xml) for this run.
            SetPropertiesFromApplicationSettings();

            // Process JSON object configuration settings (housed in [AppObserver.config].json) for this run.
            if (await ProcessJSONConfigAsync().ConfigureAwait(false) == false)
            {
                return false;
            }

            // Filter JSON targetApp setting format; try and fix malformed values, if possible.
            FilterTargetAppFormat();

            // Support for specifying single configuration JSON object for all applications.
            await ProcessGlobalThresholdSettingsAsync().ConfigureAwait(false);

            int settingsFail = 0;

            for (int i = 0; i < _userTargetList.Count; i++)
            {
                Token.ThrowIfCancellationRequested();

                ApplicationInfo application = _userTargetList[i];
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
                    continue;
                }

                // No required settings supplied for deployed application(s).
                if (settingsFail == _userTargetList.Count)
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

                if (!string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    await SetDeployedApplicationReplicaOrInstanceListAsync(null, application.TargetAppType).ConfigureAwait(false);
                }
                else
                {
                    await SetDeployedApplicationReplicaOrInstanceListAsync(appUri).ConfigureAwait(false);
                }
            }

            int repCount = ReplicaOrInstanceList.Count;

            // internal diagnostic telemetry \\

            // Do not emit the same service count data over and over again.
            if (repCount != _serviceCount)
            {
                MonitoredServiceProcessCount = repCount;
                _serviceCount = repCount;
            }
            else
            {
                MonitoredServiceProcessCount = 0;
            }

            // Do not emit the same app count data over and over again.
            if (_deployedTargetList.Count != _appCount)
            {
                MonitoredAppCount = _deployedTargetList.Count;
                _appCount = _deployedTargetList.Count;
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
            for (int i = 0; i < _deployedTargetList.Count; i++)
            {
                ObserverLogger.LogInfo($"AppObserver settings applied to {_deployedTargetList[i].TargetApp}:{Environment.NewLine}{_deployedTargetList[i]}");
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
            if (_userTargetList == null || _userTargetList.Count == 0)
            {
                return;
            }

            if (!_userTargetList.Any(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*"))
            {
                return;
            }

            ApplicationInfo application = _userTargetList.First(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*");

            for (int i = 0; i < _deployedApps.Count; i++)
            {
                Token.ThrowIfCancellationRequested();

                var app = _deployedApps[i];

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
                if (_userTargetList.Any(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName))
                {
                    var existingAppConfig = _userTargetList.FindAll(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName);

                    if (existingAppConfig == null || existingAppConfig.Count == 0)
                    {
                        continue;
                    }

                    for (int j = 0; j < existingAppConfig.Count; j++)
                    {
                        // Service include/exclude lists
                        existingAppConfig[j].ServiceExcludeList = string.IsNullOrWhiteSpace(existingAppConfig[j].ServiceExcludeList) && !string.IsNullOrWhiteSpace(application.ServiceExcludeList) ? application.ServiceExcludeList : existingAppConfig[j].ServiceExcludeList;
                        existingAppConfig[j].ServiceIncludeList = string.IsNullOrWhiteSpace(existingAppConfig[j].ServiceIncludeList) && !string.IsNullOrWhiteSpace(application.ServiceIncludeList) ? application.ServiceIncludeList : existingAppConfig[j].ServiceIncludeList;

                        // Memory
                        existingAppConfig[j].MemoryErrorLimitMb = existingAppConfig[j].MemoryErrorLimitMb == 0 && application.MemoryErrorLimitMb > 0 ? application.MemoryErrorLimitMb : existingAppConfig[j].MemoryErrorLimitMb;
                        existingAppConfig[j].MemoryWarningLimitMb = existingAppConfig[j].MemoryWarningLimitMb == 0 && application.MemoryWarningLimitMb > 0 ? application.MemoryWarningLimitMb : existingAppConfig[j].MemoryWarningLimitMb;
                        existingAppConfig[j].MemoryErrorLimitPercent = existingAppConfig[j].MemoryErrorLimitPercent == 0 && application.MemoryErrorLimitPercent > 0 ? application.MemoryErrorLimitPercent : existingAppConfig[j].MemoryErrorLimitPercent;
                        existingAppConfig[j].MemoryWarningLimitPercent = existingAppConfig[j].MemoryWarningLimitPercent == 0 && application.MemoryWarningLimitPercent > 0 ? application.MemoryWarningLimitPercent : existingAppConfig[j].MemoryWarningLimitPercent;

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

                        // Handles
                        existingAppConfig[j].ErrorOpenFileHandles = existingAppConfig[j].ErrorOpenFileHandles == 0 && application.ErrorOpenFileHandles > 0 ? application.ErrorOpenFileHandles : existingAppConfig[j].ErrorOpenFileHandles;
                        existingAppConfig[j].WarningOpenFileHandles = existingAppConfig[j].WarningOpenFileHandles == 0 && application.WarningOpenFileHandles > 0 ? application.WarningOpenFileHandles : existingAppConfig[j].WarningOpenFileHandles;

                        // Threads
                        existingAppConfig[j].ErrorThreadCount = existingAppConfig[j].ErrorThreadCount == 0 && application.ErrorThreadCount > 0 ? application.ErrorThreadCount : existingAppConfig[j].ErrorThreadCount;
                        existingAppConfig[j].WarningThreadCount = existingAppConfig[j].WarningThreadCount == 0 && application.WarningThreadCount > 0 ? application.WarningThreadCount : existingAppConfig[j].WarningThreadCount;

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
                        ErrorOpenFileHandles = application.ErrorOpenFileHandles,
                        WarningOpenFileHandles = application.WarningOpenFileHandles,
                        ErrorThreadCount = application.ErrorThreadCount,
                        WarningThreadCount = application.WarningThreadCount
                    };

                    _userTargetList.Add(appConfig);
                }
            }

            // Remove the All/* config item.
             _ = _userTargetList.Remove(application);
        }

        private void FilterTargetAppFormat()
        {
            for (int i = 0; i < _userTargetList.Count; i++)
            {
                var target = _userTargetList[i];

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
                        _userTargetList.RemoveAt(i);

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
        }

        private async Task<bool> ProcessJSONConfigAsync()
        {
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
            _userTargetList.AddRange(appInfo);

            // Does the configuration have any objects (targets) defined?
            if (_userTargetList.Count == 0)
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

            return true;
        }

        /// <summary>
        /// Set properties with Application Parameter settings supplied by user.
        /// </summary>
        private void SetPropertiesFromApplicationSettings()
        {
            // Config path.
            if (JsonConfigPath == null)
            {
                JsonConfigPath =
                    Path.Combine(ConfigPackage.Path, GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.ConfigurationFileName));
            }

            // Private working set monitoring.
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MonitorPrivateWorkingSet), out bool monitorWsPriv))
            {
                _checkPrivateWorkingSet = monitorWsPriv;
            }

            /* Child/Descendant proc monitoring config */
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableChildProcessMonitoringParameter), out bool enableDescendantMonitoring))
            {
                EnableChildProcessMonitoring = enableDescendantMonitoring;
            }

            if (int.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxChildProcTelemetryDataCountParameter), out int maxChildProcs))
            {
                MaxChildProcTelemetryDataCount = maxChildProcs;
            }

            /* dumpProcessOnError config */
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableProcessDumpsParameter), out bool enableDumps))
            {
                EnableProcessDumps = enableDumps;

                if (string.IsNullOrWhiteSpace(DumpsPath) && enableDumps)
                {
                    SetDumpPath();
                }
            }

            if (Enum.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.DumpTypeParameter), out DumpType dumpType))
            {
                DumpType = dumpType;
            }

            if (int.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxDumpsParameter), out int maxDumps))
            {
                MaxDumps = maxDumps;
            }

            if (TimeSpan.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxDumpsTimeWindowParameter), out TimeSpan dumpTimeWindow))
            {
                MaxDumpsTimeWindow = dumpTimeWindow;
            }

            // Concurrency/Parallelism support. The minimum requirement is 4 logical processors, regardless of user setting.
            if (Environment.ProcessorCount >= 4 && bool.TryParse(
                    GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableConcurrentMonitoring), out bool enableConcurrency))
            {
                EnableConcurrentMonitoring = enableConcurrency;
            }

            // Effectively, sequential.
            int maxDegreeOfParallelism = 1;

            if (EnableConcurrentMonitoring)
            {
                // Default to using [1/4 of available logical processors ~* 2] threads if MaxConcurrentTasks setting is not supplied.
                // So, this means around 10 - 11 threads (or less) could be used if processor count = 20. This is only being done to limit the impact
                // FabricObserver has on the resources it monitors and alerts on...
                maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));
                
                // If user configures MaxConcurrentTasks setting, then use that value instead.
                if (int.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxConcurrentTasks), out int maxTasks))
                {
                    maxDegreeOfParallelism = maxTasks;
                }
            }

            _parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            // KVS LVID Monitoring - Windows-only.
            if (_isWindows && bool.TryParse(
                    GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableKvsLvidMonitoringParameter), out bool enableLvidMonitoring))
            {
                // Observers that monitor LVIDs should ensure the static ObserverManager.CanInstallLvidCounter is true before attempting to monitor LVID usage.
                EnableKvsLvidMonitoring = enableLvidMonitoring && ObserverManager.IsLvidCounterEnabled;
            }
        }

        private void ProcessChildProcs<T>(
                        ConcurrentDictionary<string, FabricResourceUsageData<T>> fruds,
                        ConcurrentQueue<ChildProcessTelemetryData> childProcessTelemetryDataList, 
                        ReplicaOrInstanceMonitoringInfo repOrInst, 
                        ApplicationInfo app, 
                        FabricResourceUsageData<T> parentFrud, 
                        CancellationToken token) where T : struct
        {
            token.ThrowIfCancellationRequested();

            if (childProcessTelemetryDataList == null)
            {
                return;
            }

            try
            { 
                string metric = parentFrud.Property;
                var parentDataAvg = parentFrud.AverageDataValue;
                var (childProcInfo, Sum) = ProcessChildFrudsGetDataSum(fruds, repOrInst, app, token);
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
        }

        private (ChildProcessTelemetryData childProcInfo, double Sum) ProcessChildFrudsGetDataSum<T>(
                                                                         ConcurrentDictionary<string, FabricResourceUsageData<T>> fruds,
                                                                         ReplicaOrInstanceMonitoringInfo repOrInst,
                                                                         ApplicationInfo app,
                                                                         CancellationToken token) where T : struct
        {
            var childProcs = repOrInst.ChildProcesses;

            if (childProcs == null || childProcs.Count == 0 || token.IsCancellationRequested)
            {
                return (null, 0);
            }

            double sumValues = 0;
            string metric = string.Empty;
            var childProcessInfoData = new ChildProcessTelemetryData
            {
                ApplicationName = repOrInst.ApplicationName.OriginalString,
                ServiceName = repOrInst.ServiceName.OriginalString,
                NodeName = NodeName,
                ProcessId = (int)repOrInst.HostProcessId,
                PartitionId = repOrInst.PartitionId.ToString(),
                ReplicaId = repOrInst.ReplicaOrInstanceId.ToString(),
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

                    if (fruds.Any(x => x.Key.Contains(childProcName)))
                    {
                        var childFruds = fruds.Where(x => x.Key.Contains(childProcName)).ToList();
                        metric = childFruds[0].Value.Property;

                        for (int j = 0; j < childFruds.Count; ++j)
                        {
                            token.ThrowIfCancellationRequested();

                            var frud = childFruds[j];
                            double value = frud.Value.AverageDataValue;
                            sumValues += value;

                            if (IsEtwEnabled || IsTelemetryEnabled)
                            {
                                var childProcInfo = new ChildProcessInfo { ProcessName = childProcName, Value = value };
                                childProcessInfoData.ChildProcessInfo.Add(childProcInfo);
                            }

                            // Windows process dump support for descendant/child processes \\

                            if (_isWindows && app.DumpProcessOnError && EnableProcessDumps)
                            {
                                string prop = frud.Value.Property;
                                bool dump = false;

                                switch (prop)
                                {
                                    case ErrorWarningProperty.CpuTime:
                                        // Test error threshold breach for supplied metric.
                                        if (frud.Value.IsUnhealthy(app.CpuErrorLimitPercent))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.MemoryConsumptionMb:
                                        if (frud.Value.IsUnhealthy(app.MemoryErrorLimitMb))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.MemoryConsumptionPercentage:
                                        if (frud.Value.IsUnhealthy(app.MemoryErrorLimitPercent))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.ActiveTcpPorts:
                                        if (frud.Value.IsUnhealthy(app.NetworkErrorActivePorts))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.ActiveEphemeralPorts:
                                        if (frud.Value.IsUnhealthy(app.NetworkErrorEphemeralPorts))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.ActiveEphemeralPortsPercentage:
                                        if (frud.Value.IsUnhealthy(app.NetworkErrorEphemeralPortsPercent))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.AllocatedFileHandles:
                                        if (frud.Value.IsUnhealthy(app.ErrorOpenFileHandles))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.ThreadCount:
                                        if (frud.Value.IsUnhealthy(app.ErrorThreadCount))
                                        {
                                            dump = true;
                                        }
                                        break;
                                }

                                lock (_lock)
                                {
                                    if (dump)
                                    {
                                        _ = DumpWindowsServiceProcess(childPid, childProcName, prop);
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
                    ObserverLogger.LogWarning($"ProcessChildFrudsGetDataSum - Failure processing descendants:{Environment.NewLine}{e}");
                }
            }

            // Order List<ChildProcessInfo> by Value descending.
            childProcessInfoData.ChildProcessInfo = childProcessInfoData.ChildProcessInfo.OrderByDescending(v => v.Value).ToList();

            // Cap size of List<ChildProcessInfo> to MaxChildProcTelemetryDataCount.
            if (childProcessInfoData.ChildProcessInfo.Count >= MaxChildProcTelemetryDataCount)
            {
                childProcessInfoData.ChildProcessInfo = childProcessInfoData.ChildProcessInfo.Take(MaxChildProcTelemetryDataCount).ToList();
            }

            return (childProcessInfoData, sumValues);
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
                DumpsPath = Path.Combine(ObserverLogger.LogFolderBasePath, ObserverName, "MemoryDumps");
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
            
            // Windows-only LVID usage monitoring for Stateful KVS-based services (e.g., Actors).
            if (EnableKvsLvidMonitoring)
            {
                AllAppKvsLvidsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            }

            _processInfoDictionary ??= new ConcurrentDictionary<int, string>();

            // DEBUG
            //var threadData = new ConcurrentQueue<int>();

            ParallelLoopResult result = Parallel.For (0, ReplicaOrInstanceList.Count, _parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                }

                // DEBUG
                //threadData.Enqueue(Thread.CurrentThread.ManagedThreadId);
                var repOrInst = ReplicaOrInstanceList[i];
                var timer = new Stopwatch();
                int parentPid = (int)repOrInst.HostProcessId;
                bool checkCpu = false;
                bool checkMemMb = false;
                bool checkMemPct = false;
                bool checkAllPorts = false;
                bool checkEphemeralPorts = false;
                bool checkPercentageEphemeralPorts = false;
                bool checkHandles = false;
                bool checkThreads = false;
                bool checkLvids = false;
                var application = _deployedTargetList?.First(
                                    app => app?.TargetApp?.ToLower() == repOrInst.ApplicationName?.OriginalString.ToLower() ||
                                    !string.IsNullOrWhiteSpace(app?.TargetAppType) &&
                                    app.TargetAppType?.ToLower() == repOrInst.ApplicationTypeName?.ToLower());
                
                ConcurrentDictionary<string, int> procs;

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
                        parentProc = Process.GetProcessById(parentPid);

                        // On Windows, this will throw a Win32Exception (NativeErrorCode = 5) if target process is running at a higher user privilege than FO.
                        // If it is not, then this would mean the process has exited so move on to next process.
                        if (parentProc.HasExited)
                        {
                            return;
                        }

                        // net core's ProcessManager.EnsureState is a CPU bottleneck on Windows.
                        if (!_isWindows)
                        {
                            parentProcName = parentProc.ProcessName;
                        }
                        else
                        {
                            parentProcName = NativeMethods.GetProcessNameFromId(parentPid);
                        }

                        // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                        // Please use ContainerObserver for SF container app service monitoring.
                        if (parentProcName == null || parentProcName == "Fabric")
                        {
                            return;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                    {
                        if (!_isWindows || ObserverManager.ObserverFailureHealthStateLevel == HealthState.Unknown)
                        {
                            return;
                        }

                        if (e is Win32Exception exception && (exception.NativeErrorCode == 5 || exception.NativeErrorCode == 6 ))
                        {
                            string message = $"{repOrInst?.ServiceName?.OriginalString} is running as Admin or System user on Windows and can't be monitored by FabricObserver, which is running as Network Service.{Environment.NewLine}" +
                                             $"Please configure FabricObserver to run as Admin or System user on Windows to solve this problem and/or determine if {repOrInst?.ServiceName?.OriginalString} really needs to run as Admin or System user on Windows.";

                            var healthReport = new Utilities.HealthReport
                            {
                                ServiceName = ServiceName,
                                EmitLogEvent = EnableVerboseLogging,
                                HealthMessage = message,
                                HealthReportTimeToLive = GetHealthReportTimeToLive(),
                                Property = $"UserAccount({parentProc.ProcessName})",
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
                                        $"UserAccountPrivilege({parentProc.ProcessName})",
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
                                        Property = $"UserAccountPrivilege({parentProc.ProcessName})",
                                        Level = Enum.GetName(typeof(HealthState), ObserverManager.ObserverFailureHealthStateLevel),
                                        Message = message,
                                        ObserverName,
                                        ServiceName = repOrInst?.ServiceName?.OriginalString
                                    });
                            }
                        }

                        return;
                    }

                    /* In order to provide accurate resource usage of an SF service process we need to also account for
                       any processes that the service process (parent) created/spawned (children). */

                    procs = new ConcurrentDictionary<string, int>();

                    // Add parent to the process tree list since we want to monitor all processes in the family. If there are no child processes,
                    // then only the parent process will be in this dictionary..
                    _ = procs.TryAdd(parentProcName, parentPid);

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

                            _ = procs.TryAdd(repOrInst.ChildProcesses[k].procName, repOrInst.ChildProcesses[k].Pid);
                        }
                    }

                    foreach (var proc in procs)
                    {
                        if (token.IsCancellationRequested)
                        {
                            state.Stop();
                        }

                        _ = _processInfoDictionary.TryAdd(proc.Value, proc.Key);
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

                    // Memory Mb
                    if (application.MemoryErrorLimitMb > 0 || application.MemoryWarningLimitMb > 0)
                    {
                        _ = AllAppMemDataMb.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.MemoryConsumptionMb, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppMemDataMb.ContainsKey(id))
                    {
                        checkMemMb = true;
                    }

                    // Memory percent
                    if (application.MemoryErrorLimitPercent > 0 || application.MemoryWarningLimitPercent > 0)
                    {
                        _ = AllAppMemDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.MemoryConsumptionPercentage, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppMemDataPercent.ContainsKey(id))
                    {
                        checkMemPct = true;
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

                    // Compute the resource usage of the family of processes (each proc in the family tree). This is also parallelized and has real perf benefits when 
                    // a service process has mulitple descendants.
                    ComputeResourceUsage(
                            capacity,
                            parentPid,
                            checkCpu,
                            checkMemMb,
                            checkMemPct,
                            checkAllPorts,
                            checkEphemeralPorts,
                            checkPercentageEphemeralPorts,
                            checkHandles,
                            checkThreads,
                            checkLvids,
                            procs,
                            id,
                            repOrInst,
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

            // DEBUG 
            //int threadcount = threadData.Distinct().Count();
            ObserverLogger.LogInfo($"MonitorDeployedAppsAsync Execution time: {execTimer.Elapsed}"); //Threads: {threadcount}");
            return Task.FromResult(result);
        }

        private void ComputeResourceUsage(
                            int capacity,
                            int parentPid,
                            bool checkCpu,
                            bool checkMemMb,
                            bool checkMemPct,
                            bool checkAllPorts,
                            bool checkEphemeralPorts,
                            bool checkPercentageEphemeralPorts,
                            bool checkHandles,
                            bool checkThreads,
                            bool checkLvids,
                            ConcurrentDictionary<string, int> processDictionary,
                            string id,
                            ReplicaOrInstanceMonitoringInfo repOrInst,
                            CancellationToken token)
        {
            _ = Parallel.For (0, processDictionary.Count, _parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                }

                var index = processDictionary.ElementAt(i);
                string procName = index.Key;
                int procId = index.Value;
                
                // Make sure the process ID still maps to the process name.
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
                    float handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(procId, _isWindows ? null : CodePackage?.Path);

                    if (handles > 0F)
                    {
                        if (procId == parentPid)
                        {
                            AllAppHandlesData[id].AddData(handles);
                        }
                        else
                        {
                            _ = AllAppHandlesData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<float>(ErrorWarningProperty.AllocatedFileHandles, $"{id}:{procName}{procId}", capacity, false, EnableConcurrentMonitoring));
                            AllAppHandlesData[$"{id}:{procName}{procId}"].AddData(handles);
                        }
                    }
                }

                // Threads
                if (checkThreads)
                {
                    int threads = ProcessInfoProvider.GetProcessThreadCount(procId);

                    if (threads > 0)
                    {
                        // Parent process (the service process).
                        if (procId == parentPid)
                        {
                            AllAppThreadsData[id].AddData(threads);
                        }
                        else // Child proc spawned by the parent service process.
                        {
                            _ = AllAppThreadsData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<int>(ErrorWarningProperty.ThreadCount, $"{id}:{procName}{procId}", capacity, false, EnableConcurrentMonitoring));
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
                        _ = AllAppTotalActivePortsData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<int>(ErrorWarningProperty.ActiveTcpPorts, $"{id}:{procName}{procId}", capacity, false, EnableConcurrentMonitoring));
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
                        _ = AllAppEphemeralPortsData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<int>(ErrorWarningProperty.ActiveEphemeralPorts, $"{id}:{procName}{procId}", capacity, false, EnableConcurrentMonitoring));
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
                        _ = AllAppEphemeralPortsDataPercent.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<double>(ErrorWarningProperty.ActiveEphemeralPortsPercentage, $"{id}:{procName}{procId}", capacity, false, EnableConcurrentMonitoring));
                        AllAppEphemeralPortsDataPercent[$"{id}:{procName}{procId}"].AddData(usedPct);
                    }
                }

                // KVS LVIDs
                if (_isWindows && checkLvids && repOrInst.HostProcessId == procId && repOrInst.ServiceKind == ServiceKind.Stateful)
                {
                    var lvidPct = ProcessInfoProvider.Instance.GetProcessKvsLvidsUsagePercentage(procName, procId);

                    // ProcessGetCurrentKvsLvidsUsedPercentage internally handles exceptions and will always return -1 when it fails.
                    if (lvidPct > -1)
                    {
                        if (procId == parentPid)
                        {
                            AllAppKvsLvidsData[id].AddData(lvidPct);
                        }
                        else
                        {
                            _ = AllAppKvsLvidsData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<double>(ErrorWarningProperty.KvsLvidsPercent, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
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

                // Process working set (total or private, based on user configuration)
                // ***NOTE***: If you have several service processes of the same name, Private Working set measurement will add significant processing time to AppObserver.
                // Consider not enabling Private Set memory. In that case, the Full Working set will measured (Private + Shared), computed using a native API call (fast) via COM interop (PInvoke).
                if (checkMemMb)
                {
                    float processMemMb = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, _checkPrivateWorkingSet ? procName : null, _checkPrivateWorkingSet);

                    if (procId == parentPid)
                    {
                        AllAppMemDataMb[id].AddData(processMemMb);
                    }
                    else
                    {
                        _ = AllAppMemDataMb.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<float>(ErrorWarningProperty.MemoryConsumptionMb, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                        AllAppMemDataMb[$"{id}:{procName}{procId}"].AddData(processMemMb);
                    }
                }

                // Process memory, percent in use (of machine total).
                if (checkMemPct)
                {
                    float processMemMb = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, _checkPrivateWorkingSet ? procName : null, _checkPrivateWorkingSet);
                    var (TotalMemoryGb, _, _) = OSInfoProvider.Instance.TupleGetSystemMemoryInfo();

                    if (TotalMemoryGb > 0)
                    {
                        double usedPct = (double)(processMemMb * 100) / (TotalMemoryGb * 1024);

                        if (procId == parentPid)
                        {
                            AllAppMemDataPercent[id].AddData(Math.Round(usedPct, 2));
                        }
                        else
                        {
                            _ = AllAppMemDataPercent.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<double>(ErrorWarningProperty.MemoryConsumptionPercentage, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                            AllAppMemDataPercent[$"{id}:{procName}{procId}"].AddData(Math.Round(usedPct, 2));
                        }
                    }
                }

                // CPU \\

                CpuUsage cpuUsage = checkCpu ? new CpuUsage() : null;
                Stopwatch timer = Stopwatch.StartNew();

                while (timer.Elapsed <= maxDuration)
                {
                    if (token.IsCancellationRequested)
                    {
                        state.Stop();
                    }

                    // CPU (all cores) \\

                    if (checkCpu && cpuUsage != null)
                    {
                        double cpu = cpuUsage.GetCpuUsagePercentageProcess(procId);

                        if (cpu >= 0)
                        {
                            if (cpu > 100)
                            {
                                cpu = 100;
                            }

                            if (procId == parentPid)
                            {
                                AllAppCpuData[id].AddData(cpu);
                            }
                            else
                            {
                                _ = AllAppCpuData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<double>(ErrorWarningProperty.CpuTime, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                                AllAppCpuData[$"{id}:{procName}{procId}"].AddData(cpu);
                            }
                        }
                    }

                    Thread.Sleep(50);
                }

                timer.Stop();
                timer = null;
            });
        }

        private bool EnsureProcess(string procName, int procId)
        {
            // net core's ProcessManager.EnsureState is a CPU bottleneck on Windows.
            if (!_isWindows)
            {
                using var proc = Process.GetProcessById(procId);
                return proc.ProcessName == procName;
            }

            return NativeMethods.GetProcessNameFromId(procId) == procName;
        }

        private async Task SetDeployedApplicationReplicaOrInstanceListAsync(Uri applicationNameFilter = null, string applicationType = null)
        {
            List<DeployedApplication> deployedApps = _deployedApps;

            // DEBUG
            //var stopwatch = Stopwatch.StartNew();

            try
            {
                if (applicationNameFilter != null)
                {
                    deployedApps = deployedApps.FindAll(a => a.ApplicationName.Equals(applicationNameFilter));
                }
                else if (!string.IsNullOrWhiteSpace(applicationType))
                {
                    deployedApps = deployedApps.FindAll(a => a.ApplicationTypeName == applicationType);
                }
            }
            catch (ArgumentException ae)
            {
                ObserverLogger.LogWarning($"SetDeployedApplicationReplicaOrInstanceListAsync: Unable to process replica information:{Environment.NewLine}{ae}");
                return;
            }

            foreach (var userTarget in _userTargetList)
            {
                for (int i = 0; i < deployedApps.Count; i++)
                {
                    Token.ThrowIfCancellationRequested();

                    try
                    {
                        Token.ThrowIfCancellationRequested();

                        // TargetAppType supplied in user config, so set TargetApp on deployedApp instance by searching for it in the currently deployed application list.
                        if (userTarget.TargetAppType != null)
                        {
                            if (deployedApps[i].ApplicationTypeName != userTarget.TargetAppType)
                            {
                                continue;
                            }

                            userTarget.TargetApp = deployedApps[i].ApplicationName.OriginalString;
                        }

                        if (userTarget.TargetApp == null)
                        {
                            continue;
                        }

                        if (deployedApps[i].ApplicationName.OriginalString != userTarget.TargetApp)
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

                        List<ReplicaOrInstanceMonitoringInfo> replicasOrInstances = await GetDeployedPrimaryReplicaAsync(
                                                                                            new Uri(userTarget.TargetApp),
                                                                                            filteredServiceList,
                                                                                            filterType,
                                                                                            applicationType).ConfigureAwait(false);

                        if (replicasOrInstances?.Count > 0)
                        {
                            ReplicaOrInstanceList.AddRange(replicasOrInstances);

                            var targets = _userTargetList.Where(x => x.TargetApp != null && x.TargetApp == userTarget.TargetApp
                                                                  || x.TargetAppType != null && x.TargetAppType == userTarget.TargetAppType);

                            if (userTarget.TargetApp != null && !_deployedTargetList.Any(r => r.TargetApp == userTarget.TargetApp))
                            {
                                _deployedTargetList.AddRange(targets);
                            }
                        }

                        replicasOrInstances.Clear();
                        replicasOrInstances = null;
                    }
                    catch (Exception e) when (e is ArgumentException || e is FabricException || e is Win32Exception)
                    {
                        ObserverLogger.LogWarning(
                            $"SetDeployedApplicationReplicaOrInstanceListAsync: Unable to process replica information for {userTarget}{Environment.NewLine}{e}");
                    }
                }
            }

            deployedApps.Clear();
            deployedApps = null;
            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"SetDeployedApplicationReplicaOrInstanceListAsync for {applicationNameFilter?.OriginalString} run duration: {stopwatch.Elapsed}");
        }

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedPrimaryReplicaAsync(
                                                                     Uri appName,
                                                                     string[] serviceFilterList = null,
                                                                     ServiceFilterType filterType = ServiceFilterType.None,
                                                                     string appTypeName = null)
        {
            // DEBUG
            //var stopwatch = Stopwatch.StartNew();
            var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName, null, null, ConfigurationSettings.AsyncTimeout, Token),
                                                Token);

            //ObserverLogger.LogInfo($"QueryManager.GetDeployedReplicaListAsync for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
            var replicaMonitoringList = new ConcurrentQueue<ReplicaOrInstanceMonitoringInfo>();

            SetInstanceOrReplicaMonitoringList(
               appName,
               serviceFilterList,
               filterType,
               appTypeName,
               deployedReplicaList,
               replicaMonitoringList);

            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"GetDeployedPrimaryReplicaAsync for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
            return replicaMonitoringList.ToList();
        }

        private void SetInstanceOrReplicaMonitoringList(
                        Uri appName,
                        string[] filterList,
                        ServiceFilterType filterType,
                        string appTypeName,
                        DeployedServiceReplicaList deployedReplicaList,
                        ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList)
        {
            
            // DEBUG
            //var stopwatch = Stopwatch.StartNew();
            _ = Parallel.For (0, deployedReplicaList.Count, _parallelOptions, (i, state) =>
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
                            ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                            ServicePackageActivationMode = string.IsNullOrWhiteSpace(statefulReplica.ServicePackageActivationId) ? 
                                System.Fabric.Description.ServicePackageActivationMode.SharedProcess : System.Fabric.Description.ServicePackageActivationMode.ExclusiveProcess,
                            ReplicaStatus = statefulReplica.ReplicaStatus
                        };

                        /* In order to provide accurate resource usage of an SF service process we need to also account for
                        any processes (children) that the service process (parent) created/spawned. */

                        if (EnableChildProcessMonitoring && replicaInfo?.HostProcessId > 0)
                        {
                            // DEBUG
                            //var sw = Stopwatch.StartNew();
                            List<(string ProcName, int Pid)> childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId, HandleToProcessSnapshot);

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
                            ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                            ServicePackageActivationMode = string.IsNullOrWhiteSpace(statelessInstance.ServicePackageActivationId) ?
                                System.Fabric.Description.ServicePackageActivationMode.SharedProcess : System.Fabric.Description.ServicePackageActivationMode.ExclusiveProcess,
                            ReplicaStatus = statelessInstance.ReplicaStatus
                        };

                        if (EnableChildProcessMonitoring && replicaInfo?.HostProcessId > 0)
                        {
                            // DEBUG
                            //var sw = Stopwatch.StartNew();
                            List<(string ProcName, int Pid)> childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId, HandleToProcessSnapshot);

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

                if (replicaInfo?.HostProcessId > 0 && !ReplicaOrInstanceList.Any(r => r.ServiceName == replicaInfo.ServiceName))
                {
                    replicaMonitoringList.Enqueue(replicaInfo);
                }
            });
            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"SetInstanceOrReplicaMonitoringList for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
        }

        private void LogAllAppResourceDataToCsv(string appName)
        {
            if (!EnableCsvLogging)
            {
                return;
            }

            // CPU Time
            if (AllAppCpuData.ContainsKey(appName))
            {
                CsvFileLogger.LogData(
                    _fileName,
                    appName,
                    ErrorWarningProperty.CpuTime,
                    "Average",
                    Math.Round(AllAppCpuData.First(x => x.Key == appName).Value.AverageDataValue));

                CsvFileLogger.LogData(
                    _fileName,
                    appName,
                    ErrorWarningProperty.CpuTime,
                    "Peak",
                    Math.Round(AllAppCpuData.First(x => x.Key == appName).Value.MaxDataValue));
            }

            // Memory - MB
            if (AllAppMemDataMb.ContainsKey(appName))
            {
                CsvFileLogger.LogData(
                    _fileName,
                    appName,
                    ErrorWarningProperty.MemoryConsumptionMb,
                    "Average",
                    Math.Round(AllAppMemDataMb.First(x => x.Key == appName).Value.AverageDataValue));

                CsvFileLogger.LogData(
                    _fileName,
                    appName,
                    ErrorWarningProperty.MemoryConsumptionMb,
                    "Peak",
                    Math.Round(Convert.ToDouble(AllAppMemDataMb.First(x => x.Key == appName).Value.MaxDataValue)));
            }

            if (AllAppMemDataPercent.ContainsKey(appName))
            {
                CsvFileLogger.LogData(
                   _fileName,
                   appName,
                   ErrorWarningProperty.MemoryConsumptionPercentage,
                   "Average",
                   Math.Round(AllAppMemDataPercent.First(x => x.Key == appName).Value.AverageDataValue));

                CsvFileLogger.LogData(
                    _fileName,
                    appName,
                    ErrorWarningProperty.MemoryConsumptionPercentage,
                    "Peak",
                    Math.Round(Convert.ToDouble(AllAppMemDataPercent.FirstOrDefault(x => x.Key == appName).Value.MaxDataValue)));
            }

            if (AllAppTotalActivePortsData.ContainsKey(appName))
            {
                // Network
                CsvFileLogger.LogData(
                    _fileName,
                    appName,
                    ErrorWarningProperty.ActiveTcpPorts,
                    "Total",
                    Math.Round(Convert.ToDouble(AllAppTotalActivePortsData.First(x => x.Key == appName).Value.MaxDataValue)));
            }

            if (AllAppEphemeralPortsData.ContainsKey(appName))
            {
                // Network
                CsvFileLogger.LogData(
                    _fileName,
                    appName,
                    ErrorWarningProperty.ActiveEphemeralPorts,
                    "Total",
                    Math.Round(Convert.ToDouble(AllAppEphemeralPortsData.First(x => x.Key == appName).Value.MaxDataValue)));
            }

            if (AllAppHandlesData.ContainsKey(appName))
            {
                // Handles
                CsvFileLogger.LogData(
                     _fileName,
                     appName,
                     ErrorWarningProperty.AllocatedFileHandles,
                     "Total",
                     AllAppHandlesData.First(x => x.Key == appName).Value.MaxDataValue);
            }

            DataTableFileLogger.Flush();
        }

        private void CleanUp()
        {
            _deployedTargetList?.Clear();
            _deployedTargetList = null;

            _userTargetList?.Clear();
            _userTargetList = null;

            ReplicaOrInstanceList?.Clear();
            ReplicaOrInstanceList = null;

            _processInfoDictionary?.Clear();
            _processInfoDictionary = null;

            _deployedApps?.Clear();
            _deployedApps = null;

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

            if (AllAppKvsLvidsData != null && AllAppKvsLvidsData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppKvsLvidsData?.Clear();
                AllAppKvsLvidsData = null;
            }

            if (_isWindows)
            {
                _handleToProcSnapshot?.Dispose();
                _handleToProcSnapshot = null;
            }
        }
    }
}