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
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Newtonsoft.Json;
using ConfigSettings = FabricObserver.Observers.MachineInfoModel.ConfigSettings;

namespace FabricObserver.Observers
{
    // This observer monitors the behavior of user SF service processes (and their children) and signals Warning and Error based on user-supplied resource thresholds
    // in AppObserver.config.json. This observer will also emit telemetry (ETW, LogAnalytics/AppInsights) if enabled in Settings.xml (ObserverManagerConfiguration) and ApplicationManifest.xml (AppObserverEnableEtw).
    public class AppObserver : ObserverBase
    {
        private const double KvsLvidsWarningPercentage = 75.0;
        private readonly bool isWindows;

        // Support for concurrent monitoring.
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppMemDataMb;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppMemDataPercent;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppTotalActivePortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppEphemeralPortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppHandlesData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppThreadsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppKvsLvidsData;
        private ConcurrentDictionary<int, string> processInfo;

        // userTargetList is the list of ApplicationInfo objects representing app/app types supplied in configuration. List<T> is thread-safe for reads.
        // There are no concurrent writes for this List.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        // List<T> is thread-safe for reads. There are no concurrent writes for this List.
        private List<ApplicationInfo> deployedTargetList;
        private readonly ConfigSettings configSettings;
        private string fileName;
        private readonly Stopwatch stopwatch;
        private readonly object lockObj = new object();
        private int appCount;
        private int serviceCount;

        public int MaxChildProcTelemetryDataCount
        {
            get; set;
        }

        public bool EnableChildProcessMonitoring
        {
            get; set;
        }

        // List<T> is thread-safe for reads. There are no concurrent writes for this List.
        public List<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList
        {
            get; set;
        }

        public string ConfigPackagePath
        {
            get; set;
        }

        public bool EnableConcurrentMonitoring
        {
            get; set;
        }

        ParallelOptions ParallelOptions
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

        /// <summary>
        /// Initializes a new instance of the <see cref="AppObserver"/> class.
        /// </summary>
        public AppObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            configSettings = new ConfigSettings(FabricServiceContext);
            ConfigPackagePath = configSettings.ConfigPackagePath;
            stopwatch = new Stopwatch();
            isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            stopwatch.Start();
            bool initialized = await InitializeAsync();
            Token = token;

            if (!initialized)
            {
                ObserverLogger.LogWarning("AppObserver was unable to initialize correctly due to misconfiguration. " +
                                          "Please check your AppObserver configuration settings.");
                stopwatch.Stop();
                stopwatch.Reset();
                return;
            }

            await MonitorDeployedAppsAsync(token);
            await ReportAsync(token);

            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;
           
            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration {(ParallelOptions.MaxDegreeOfParallelism > 1 ? "with" : "without")} " +
                                       $"Parallel (Processors: {Environment.ProcessorCount} MaxDegreeOfParallelism: {ParallelOptions.MaxDegreeOfParallelism}):{RunDuration}");
            }

            CleanUp();
            stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            if (deployedTargetList.Count == 0)
            {
                return Task.CompletedTask;
            }

            var stopwatch = Stopwatch.StartNew();

            TimeSpan TTL = GetHealthReportTimeToLive();

            // This will run sequentially if the underlying CPU config does not meet the requirements for concurrency (e.g., if logical procs < 4).
            _ = Parallel.For(0, ReplicaOrInstanceList.Count, ParallelOptions, (i, state) =>
            {
                token.ThrowIfCancellationRequested();

                var repOrInst = ReplicaOrInstanceList[i];

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

                app = deployedTargetList.First(
                        a => (a.TargetApp != null && a.TargetApp == repOrInst.ApplicationName.OriginalString) ||
                              (a.TargetAppType != null && a.TargetAppType == repOrInst.ApplicationTypeName));
                
                try
                {
                    processId = (int)repOrInst.HostProcessId;
                    processName = processInfo.FirstOrDefault(p => p.Key == processId).Value;
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
                            HealthReportType.Application,
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
                            HealthReportType.Application,
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
                            HealthReportType.Application,
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
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // TCP Ports - Ephemeral (port numbers fall in the dynamic range) - Parent process
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
                            HealthReportType.Application,
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
                            HealthReportType.Application,
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
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps);
                }

                // KVS LVIDs - Parent process
                if (EnableKvsLvidMonitoring && AllAppKvsLvidsData.ContainsKey(id))
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
                            HealthReportType.Application,
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

            stopwatch.Stop();
            //ObserverLogger.LogInfo($"ReportAsync run duration with parallel: {stopwatch.Elapsed}");
            return Task.CompletedTask;
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
                
                // this lock probably isn't necessary.
                lock (lockObj)
                {
                    parentFrud.ClearData();
                    parentFrud.AddData((T)Convert.ChangeType(sumAllValues, typeof(T)));
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogWarning($"Error processing child processes:{Environment.NewLine}{e}");
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

                int childPid = childProcs[i].Pid;
                string childProcName = childProcs[i].procName;

                try
                {
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

                            if (isWindows && app.DumpProcessOnError && EnableProcessDumps)
                            {
                                string prop = frud.Value.Property;
                                bool dump = false;

                                switch (prop)
                                {
                                    case ErrorWarningProperty.TotalCpuTime:
                                        // Test error threshold breach for supplied metric.
                                        if (frud.Value.IsUnhealthy(app.CpuErrorLimitPercent))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalMemoryConsumptionMb:
                                        if (frud.Value.IsUnhealthy(app.MemoryErrorLimitMb))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalMemoryConsumptionPct:
                                        if (frud.Value.IsUnhealthy(app.MemoryErrorLimitPercent))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalActivePorts:
                                        if (frud.Value.IsUnhealthy(app.NetworkErrorActivePorts))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalEphemeralPorts:
                                        if (frud.Value.IsUnhealthy(app.NetworkErrorEphemeralPorts))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalFileHandles:
                                        if (frud.Value.IsUnhealthy(app.ErrorOpenFileHandles))
                                        {
                                            dump = true;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalThreadCount:
                                        if (frud.Value.IsUnhealthy(app.ErrorThreadCount))
                                        {
                                            dump = true;
                                        }
                                        break;
                                }

                                lock (lockObj)
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
                catch (Exception e) when (e is ArgumentException || e is Win32Exception || e is InvalidOperationException)
                {
                    
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    ObserverLogger.LogWarning($"Error processing child processes:{Environment.NewLine}{e}");
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

        // This runs each time ObserveAsync is run to ensure that any new app targets and config changes will
        // be up to date across observer loop iterations.
        private async Task<bool> InitializeAsync()
        {
            ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new List<ApplicationInfo>();

            // DEBUG
            //var stopwatch = Stopwatch.StartNew();

            /* Child/Descendant proc monitoring config */
            if (bool.TryParse( GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableChildProcessMonitoringParameter), out bool enableDescendantMonitoring))
            {
                EnableChildProcessMonitoring = enableDescendantMonitoring;
            }

            if (int.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxChildProcTelemetryDataCountParameter), out int maxChildProcs))
            {
                MaxChildProcTelemetryDataCount = maxChildProcs;
            }

            /* dumpProcessOnError config */
            if (bool.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableProcessDumpsParameter), out bool enableDumps))
            {
                EnableProcessDumps = enableDumps;

                if (string.IsNullOrWhiteSpace(DumpsPath) && enableDumps)
                {
                    SetDumpPath();
                }
            }

            if (Enum.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.DumpTypeParameter), out DumpType dumpType))
            {
                DumpType = dumpType;
            }

            if (int.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxDumpsParameter), out int maxDumps))
            {
                MaxDumps = maxDumps;
            }

            if (TimeSpan.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxDumpsTimeWindowParameter), out TimeSpan dumpTimeWindow))
            {
                MaxDumpsTimeWindow = dumpTimeWindow;
            }

            // Concurrency/Parallelism support.
            if (bool.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableConcurrentMonitoring), out bool enableConcurrency))
            {
                // The minimum requirement is 4 logical processors, regardless of this user setting.
                EnableConcurrentMonitoring = enableConcurrency && Environment.ProcessorCount >= 4;
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
                MaxDegreeOfParallelism = EnableConcurrentMonitoring ? maxDegreeOfParallelism : 1,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            // KVS LVID Monitoring - Windows-only.
            if (bool.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableKvsLvidMonitoringParameter), out bool enableLvidMonitoring))
            {
                EnableKvsLvidMonitoring = enableLvidMonitoring && isWindows;
            }

            configSettings.Initialize(
                            FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(
                                                                                 ObserverConstants.ObserverConfigurationPackageName)?.Settings,
                                                                                 ConfigurationSectionName,
                                                                                 "AppObserverDataFileName");

            // Unit tests may have null path and filename, thus the null equivalence operations.
            var appObserverConfigFileName = Path.Combine(ConfigPackagePath ?? string.Empty, configSettings.AppObserverConfigFileName ?? string.Empty);

            if (!File.Exists(appObserverConfigFileName))
            {
                string message = $"Will not observe resource consumption on node {NodeName} as no configuration file has been supplied.";
                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = true,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTimeToLive(),
                    Property = "MissingAppConfiguration",
                    ReportType = HealthReportType.Application,
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

                return false;
            }

            bool isJson = JsonHelper.IsJson<List<ApplicationInfo>>(await File.ReadAllTextAsync(appObserverConfigFileName));

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
                    ReportType = HealthReportType.Application,
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

                return false;
            }

            await using Stream stream = new FileStream(appObserverConfigFileName, FileMode.Open, FileAccess.Read, FileShare.Read);
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
                    ReportType = HealthReportType.Application,
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

                return false;
            }

            // Support for specifying single configuration item for all or * applications.
            if (userTargetList != null && userTargetList.Any(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*"))
            {
                ApplicationInfo application = userTargetList.First(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*");

                // Get info for 50 apps at a time that are deployed to the same node this FO instance is running on.
                var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(NodeName)
                {
                    IncludeHealthState = false,
                    MaxResults = 50
                };

                var appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                                       deployedAppQueryDesc,
                                                                                       ConfigurationSettings.AsyncTimeout,
                                                                                       Token),
                                            Token);

                // DeployedApplicationList is a wrapper around List, but does not support AddRange.. Thus, cast it ToList and add to the temp list, then iterate through it.
                // In reality, this list will never be greater than, say, 1000 apps deployed to a node, but it's a good idea to be prepared since AppObserver supports
                // all-app service process monitoring with a very simple configuration pattern.
                var apps = appList.ToList();

                // The GetDeployedApplicationPagedList api will set a continuation token value if it knows it did not return all the results in one swoop.
                // Check that it is not null, and make a new query passing back the token it gave you.
                while (appList.ContinuationToken != null)
                {
                    Token.ThrowIfCancellationRequested();
                    
                    deployedAppQueryDesc.ContinuationToken = appList.ContinuationToken;
                    appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                                       deployedAppQueryDesc,
                                                                                       ConfigurationSettings.AsyncTimeout,
                                                                                       Token),
                                            Token);

                    apps.AddRange(appList.ToList());

                    // TODO: Add random wait (ms) impl, include cluster size in calc.
                    await Task.Delay(250, Token);
                }

                for (int i = 0; i < apps.Count; ++i)
                {
                    Token.ThrowIfCancellationRequested();

                    var app = apps[i];

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

                    // Don't create a brand new entry for an existing (specified in configuration) app target/type. Just update the appConfig instance with data supplied in the All//* apps config entry.
                    // Note that if you supply a conflicting setting (where you specify a threshold for a specific app target config item and also in a global config item), then the target-specific setting will be used.
                    // E.g., if you supply a memoryWarningLimitMb threshold for an app named fabric:/MyApp and also supply a memoryWarningLimitMb threshold for all apps ("targetApp" : "All"),
                    // then the threshold specified for fabric:/MyApp will remain in place for that app target. So, target specificity overrides any global setting.
                    if (userTargetList.Any(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName))
                    {
                        var existingAppConfig = userTargetList.First(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName);

                        if (existingAppConfig == null)
                        {
                            continue;
                        }

                        existingAppConfig.ServiceExcludeList = string.IsNullOrWhiteSpace(existingAppConfig.ServiceExcludeList) && !string.IsNullOrWhiteSpace(application.ServiceExcludeList) ? application.ServiceExcludeList : existingAppConfig.ServiceExcludeList;
                        existingAppConfig.ServiceIncludeList = string.IsNullOrWhiteSpace(existingAppConfig.ServiceIncludeList) && !string.IsNullOrWhiteSpace(application.ServiceIncludeList) ? application.ServiceIncludeList : existingAppConfig.ServiceIncludeList;
                        existingAppConfig.MemoryWarningLimitMb = existingAppConfig.MemoryWarningLimitMb == 0 && application.MemoryWarningLimitMb > 0 ? application.MemoryWarningLimitMb : existingAppConfig.MemoryWarningLimitMb;
                        existingAppConfig.MemoryErrorLimitMb = existingAppConfig.MemoryErrorLimitMb == 0 && application.MemoryErrorLimitMb > 0 ? application.MemoryErrorLimitMb : existingAppConfig.MemoryErrorLimitMb;
                        existingAppConfig.MemoryWarningLimitPercent = existingAppConfig.MemoryWarningLimitPercent == 0 && application.MemoryWarningLimitPercent > 0 ? application.MemoryWarningLimitPercent : existingAppConfig.MemoryWarningLimitPercent;
                        existingAppConfig.MemoryErrorLimitPercent = existingAppConfig.MemoryErrorLimitPercent == 0 && application.MemoryErrorLimitPercent > 0 ? application.MemoryErrorLimitPercent : existingAppConfig.MemoryErrorLimitPercent;
                        existingAppConfig.CpuErrorLimitPercent = existingAppConfig.CpuErrorLimitPercent == 0 && application.CpuErrorLimitPercent > 0 ? application.CpuErrorLimitPercent : existingAppConfig.CpuErrorLimitPercent;
                        existingAppConfig.CpuWarningLimitPercent = existingAppConfig.CpuWarningLimitPercent == 0 && application.CpuWarningLimitPercent > 0 ? application.CpuWarningLimitPercent : existingAppConfig.CpuWarningLimitPercent;
                        existingAppConfig.NetworkErrorActivePorts = existingAppConfig.NetworkErrorActivePorts == 0 && application.NetworkErrorActivePorts > 0 ? application.NetworkErrorActivePorts : existingAppConfig.NetworkErrorActivePorts;
                        existingAppConfig.NetworkWarningActivePorts = existingAppConfig.NetworkWarningActivePorts == 0 && application.NetworkWarningActivePorts > 0 ? application.NetworkWarningActivePorts : existingAppConfig.NetworkWarningActivePorts;
                        existingAppConfig.NetworkErrorEphemeralPorts = existingAppConfig.NetworkErrorEphemeralPorts == 0 && application.NetworkErrorEphemeralPorts > 0 ? application.NetworkErrorEphemeralPorts : existingAppConfig.NetworkErrorEphemeralPorts;
                        existingAppConfig.NetworkWarningEphemeralPorts = existingAppConfig.NetworkWarningEphemeralPorts == 0 && application.NetworkWarningEphemeralPorts > 0 ? application.NetworkWarningEphemeralPorts : existingAppConfig.NetworkWarningEphemeralPorts;
                        existingAppConfig.DumpProcessOnError = application.DumpProcessOnError == existingAppConfig.DumpProcessOnError ? application.DumpProcessOnError : existingAppConfig.DumpProcessOnError;
                        existingAppConfig.ErrorOpenFileHandles = existingAppConfig.ErrorOpenFileHandles == 0 && application.ErrorOpenFileHandles > 0 ? application.ErrorOpenFileHandles : existingAppConfig.ErrorOpenFileHandles;
                        existingAppConfig.WarningOpenFileHandles = existingAppConfig.WarningOpenFileHandles == 0 && application.WarningOpenFileHandles > 0 ? application.WarningOpenFileHandles : existingAppConfig.WarningOpenFileHandles;
                        existingAppConfig.ErrorThreadCount = existingAppConfig.ErrorThreadCount == 0 && application.ErrorThreadCount > 0 ? application.ErrorThreadCount : existingAppConfig.ErrorThreadCount;
                        existingAppConfig.WarningThreadCount = existingAppConfig.WarningThreadCount == 0 && application.WarningThreadCount > 0 ? application.WarningThreadCount : existingAppConfig.WarningThreadCount;
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
                            MemoryWarningLimitMb = application.MemoryWarningLimitMb,
                            MemoryErrorLimitMb = application.MemoryErrorLimitMb,
                            MemoryWarningLimitPercent = application.MemoryWarningLimitPercent,
                            MemoryErrorLimitPercent = application.MemoryErrorLimitPercent,
                            CpuErrorLimitPercent = application.CpuErrorLimitPercent,
                            CpuWarningLimitPercent = application.CpuWarningLimitPercent,
                            NetworkErrorActivePorts = application.NetworkErrorActivePorts,
                            NetworkWarningActivePorts = application.NetworkWarningActivePorts,
                            NetworkErrorEphemeralPorts = application.NetworkErrorEphemeralPorts,
                            NetworkWarningEphemeralPorts = application.NetworkWarningEphemeralPorts,
                            DumpProcessOnError = application.DumpProcessOnError,
                            ErrorOpenFileHandles = application.ErrorOpenFileHandles,
                            WarningOpenFileHandles = application.WarningOpenFileHandles,
                            ErrorThreadCount = application.ErrorThreadCount,
                            WarningThreadCount = application.WarningThreadCount
                        };

                        userTargetList.Add(appConfig);
                    }
                }

                // Remove the All or * config item.
                _ = userTargetList.Remove(application);
                apps.Clear();
                apps = null;
            }

            int settingsFail = 0;

            for (int i = 0; i < userTargetList.Count; ++i) 
            {
                Token.ThrowIfCancellationRequested();

                Uri appUri = null;
                ApplicationInfo application = userTargetList[i];

                if (string.IsNullOrWhiteSpace(application.TargetApp) && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    settingsFail++;
                    continue;
                }

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
                        ReportType = HealthReportType.Application,
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

                    return false;
                }

                if (!string.IsNullOrWhiteSpace(application.TargetApp))
                {
                    try
                    {
                        // Try and fix malformed app names, if possible. \\

                        if (application.TargetApp.StartsWith("fabric:/") == false)
                        {
                            application.TargetApp = application.TargetApp.Insert(0, "fabric:/");
                        }

                        if (application.TargetApp.Contains("://"))
                        {
                            application.TargetApp = application.TargetApp.Replace("://", ":/");
                        }

                        if (application.TargetApp.Contains(" "))
                        {
                            application.TargetApp = application.TargetApp.Replace(" ", string.Empty);
                        }

                        appUri = new Uri(application.TargetApp);

                        // Make sure app is deployed and not a containerized app. \\

                        var codepackages = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                              () => FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(
                                                                               NodeName,
                                                                               appUri,
                                                                               null,
                                                                               null,
                                                                               ConfigurationSettings.AsyncTimeout,
                                                                               Token),
                                              Token);

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
                    }
                    catch (FabricException)
                    {
                        // This will happen if the specified app is not found in the codepackage query (so, not deployed). Ignore.
                        continue;
                    }
                    catch (Exception e) when (e is ArgumentException || e is UriFormatException)
                    {
                        ObserverLogger.LogWarning($"InitializeAsync: Unexpected TargetApp value {application.TargetApp}. " +
                                                  $"Value must be a valid Uri string of format \"fabric:/MyApp\" OR just \"MyApp\"");
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    await SetDeployedApplicationReplicaOrInstanceListAsync(null, application.TargetAppType);
                }
                else
                {
                    await SetDeployedApplicationReplicaOrInstanceListAsync(appUri);
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

        private Task MonitorDeployedAppsAsync(CancellationToken token)
        {
            Stopwatch execTimer = Stopwatch.StartNew();
            int capacity = ReplicaOrInstanceList.Count;
            var exceptions = new ConcurrentQueue<Exception>();
            AllAppCpuData ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppMemDataMb ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
            AllAppMemDataPercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppTotalActivePortsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppEphemeralPortsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppHandlesData ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
            AllAppThreadsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppKvsLvidsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            processInfo ??= new ConcurrentDictionary<int, string>();

            // DEBUG
            //var threadData = new ConcurrentQueue<int>();

            _ = Parallel.For(0, ReplicaOrInstanceList.Count, ParallelOptions, (i, state) =>
            {
                token.ThrowIfCancellationRequested();
                
                // DEBUG
                //threadData.Enqueue(Thread.CurrentThread.ManagedThreadId);

                var repOrInst = ReplicaOrInstanceList.ElementAt(i);
                var timer = new Stopwatch();
                int parentPid = (int)repOrInst.HostProcessId;
                bool checkCpu = false, checkMemMb = false, checkMemPct = false, checkAllPorts = false, checkEphemeralPorts = false, checkHandles = false, checkThreads = false, checkLvids = false;
                var application = deployedTargetList?.First(
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
                    Process parentProc = null;

                    try
                    {
                        parentProc = Process.GetProcessById(parentPid);

                        if (isWindows && parentProc?.ProcessName == "Idle")
                        {
                            return;
                        }

                        // This will throw Win32Exception if process is running at higher elevation than FO.
                        // If it is not, then this would mean the process has exited so move on to next process.
                        if (parentProc.HasExited)
                        {
                            return;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || ObserverManager.ObserverFailureHealthStateLevel == HealthState.Unknown)
                        {
                            return;
                        }

                        if (e is Win32Exception exception && exception.NativeErrorCode == 5 || e.Message.ToLower().Contains("access is denied"))
                        {
                            string message = $"{repOrInst?.ServiceName?.OriginalString} is running as Admin or System user on Windows and can't be monitored.{Environment.NewLine}" +
                                             $"Please configure FabricObserver to run as Admin or System user on Windows to solve this problem.";

                            var healthReport = new Utilities.HealthReport
                            {
                                AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                                EmitLogEvent = EnableVerboseLogging,
                                HealthMessage = message,
                                HealthReportTimeToLive = GetHealthReportTimeToLive(),
                                Property = $"UserAccount({parentProc?.ProcessName})",
                                ReportType = HealthReportType.Application,
                                State = ObserverManager.ObserverFailureHealthStateLevel,
                                NodeName = NodeName,
                                Observer = ObserverName,
                            };

                            // Generate a Service Fabric Health Report.
                            HealthReporter.ReportHealthToServiceFabric(healthReport);

                            // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                            if (IsTelemetryEnabled)
                            {
                                _ = TelemetryClient?.ReportHealthAsync(
                                                            $"UserAccountPrivilege({parentProc?.ProcessName})",
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
                                                    Property = $"UserAccountPrivilege({parentProc?.ProcessName})",
                                                    Level = Enum.GetName(typeof(HealthState), ObserverManager.ObserverFailureHealthStateLevel),
                                                    Message = message,
                                                    ObserverName,
                                                    ServiceName = repOrInst?.ServiceName?.OriginalString
                                                });
                            }
                        }

                        return;
                    }

                    if (parentProc == null)
                    {
                        return;
                    }

                    string parentProcName = parentProc.ProcessName;
                    int parentProcId = parentProc.Id;

                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring.
                    if (parentProcName == null || parentProcName == "Fabric")
                    {
                        return;
                    }

                    string appNameOrType = GetAppNameOrType(repOrInst);
                    string id = $"{appNameOrType}:{parentProcName}{parentProcId}";

                    if (UseCircularBuffer)
                    {
                        capacity = DataCapacity > 0 ? DataCapacity : 5;
                    }
                    else if (MonitorDuration > TimeSpan.MinValue)
                    {
                        capacity = MonitorDuration.Seconds * 4;
                    }

                    if (application.CpuErrorLimitPercent > 0 || application.CpuWarningLimitPercent > 0)
                    {
                        _ = AllAppCpuData.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppCpuData.ContainsKey(id))
                    {
                        checkCpu = true;
                    }

                    if (application.MemoryErrorLimitMb > 0 || application.MemoryWarningLimitMb > 0)
                    {
                        _ = AllAppMemDataMb.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppMemDataMb.ContainsKey(id))
                    {
                        checkMemMb = true;
                    }

                    if (application.MemoryErrorLimitPercent > 0 || application.MemoryWarningLimitPercent > 0)
                    {
                        _ = AllAppMemDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppMemDataPercent.ContainsKey(id))
                    {
                        checkMemPct = true;
                    }

                    if (application.NetworkErrorActivePorts > 0 || application.NetworkWarningActivePorts > 0)
                    {
                        _ = AllAppTotalActivePortsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppTotalActivePortsData.ContainsKey(id))
                    {
                        checkAllPorts = true;
                    }

                    if (application.NetworkErrorEphemeralPorts > 0 || application.NetworkWarningEphemeralPorts > 0)
                    {
                        _ = AllAppEphemeralPortsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppEphemeralPortsData.ContainsKey(id))
                    {
                        checkEphemeralPorts = true;
                    }

                    if (application.ErrorOpenFileHandles > 0 || application.WarningOpenFileHandles > 0)
                    {
                        _ = AllAppHandlesData.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.TotalFileHandles, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppHandlesData.ContainsKey(id))
                    {
                        checkHandles = true;
                    }

                    if (application.ErrorThreadCount > 0 || application.WarningThreadCount > 0)
                    {
                        _ = AllAppThreadsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.TotalThreadCount, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppThreadsData.ContainsKey(id))
                    {
                        checkThreads = true;
                    }

                    // This feature (KVS LVIDs percentage in use monitoring) is only available on Windows. This is non-configurable and will be removed when SF ships with the latest version of ESE.
                    if (EnableKvsLvidMonitoring && repOrInst.ServiceKind == ServiceKind.Stateful)
                    {
                        _ = AllAppKvsLvidsData.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.TotalKvsLvidsPercent, id, 1, false, EnableConcurrentMonitoring));
                        checkLvids = true;
                    }

                    /* In order to provide accurate resource usage of an SF service process we need to also account for
                       any processes that the service process (parent) created/spawned (children). */

                    procs = new ConcurrentDictionary<string, int>();

                    // Add parent to the process tree list since we want to monitor all processes in the family. If there are no child processes,
                    // then only the parent process will be in this dictionary..
                    _ = procs.TryAdd(parentProc.ProcessName, parentProc.Id);

                    if (repOrInst.ChildProcesses != null && repOrInst.ChildProcesses.Count > 0)
                    {
                        for (int k = 0; k < repOrInst.ChildProcesses.Count; ++k)
                        {
                            _ = procs.TryAdd(repOrInst.ChildProcesses[k].procName, repOrInst.ChildProcesses[k].Pid);
                        }
                    }

                    foreach (var proc in procs)
                    {
                        _ = processInfo.TryAdd(proc.Value, proc.Key);
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
                            checkHandles,
                            checkThreads,
                            checkLvids,
                            procs,
                            id,
                            token);
                }
                catch (AggregateException e) when (e.InnerExceptions.Any(ex => ex is OperationCanceledException || ex is TaskCanceledException))
                {
                    state.Stop();
                }
                catch (Exception e)
                {
                    exceptions.Enqueue(e);
                }
           });

            if (!exceptions.IsEmpty)
            {
                var aggEx = new AggregateException(exceptions);
                ObserverLogger.LogError($"Unhandled exception in MonitorDeployedAppsAsync:{Environment.NewLine}{aggEx}");
                throw new AggregateException(aggEx);
            }

            // DEBUG 
            //int threadcount = threadData.Distinct().Count();
            ObserverLogger.LogInfo($"MonitorDeployedAppsAsync Execution time: {execTimer.Elapsed}"); //Threads: {threadcount}");
            return Task.CompletedTask;
        }

        private void ComputeResourceUsage(
                            int capacity,
                            int parentPid,
                            bool checkCpu,
                            bool checkMemMb,
                            bool checkMemPct,
                            bool checkAllPorts,
                            bool checkEphemeralPorts,
                            bool checkHandles,
                            bool checkThreads,
                            bool checkLvids,
                            ConcurrentDictionary<string, int> procs,
                            string id,
                            CancellationToken token)
        {
            _ = Parallel.For(0, procs.Count, ParallelOptions, (i, state) =>
            {
                string procName = procs.ElementAt(i).Key;
                int procId = procs[procName];
                
                TimeSpan maxDuration = TimeSpan.FromSeconds(1);
                CpuUsage cpuUsage = new CpuUsage();

                if (MonitorDuration > TimeSpan.MinValue)
                {
                    maxDuration = MonitorDuration;
                }

                // Handles/FDs
                if (checkHandles)
                {
                    float handles = 0F;

                    if (isWindows)
                    {
                        handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(procId, null, ReplicaOrInstanceList.Count >= 50); 
                    }
                    else
                    {
                        handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(procId, FabricServiceContext);
                    }
                  
                    if (handles > 0F)
                    {
                        if (procId == parentPid)
                        {
                            AllAppHandlesData[id].AddData(handles);
                        }
                        else
                        {
                            _ = AllAppHandlesData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<float>(ErrorWarningProperty.TotalFileHandles, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                            AllAppHandlesData[$"{id}:{procName}{procId}"].AddData(handles);
                        }
                    }
                }

                // Threads
                if (checkThreads)
                {
                    int threads = 0;
                    
                    if (isWindows)
                    {
                        try
                        {
                            threads = NativeMethods.GetProcessThreadCount(procId);
                        }
                        catch (Win32Exception)
                        {
                            // Log...?
                        }
                    }
                    else
                    {
                        // Process object is much less expensive on Linux..
                        threads = ProcessInfoProvider.GetProcessThreadCount(procId);
                    }

                    if (threads > 0)
                    {
                        // Parent process (the service process).
                        if (procId == parentPid)
                        {
                            AllAppThreadsData[id].AddData(threads);
                        }
                        else // Child procs spawned by the parent service process.
                        {
                            _ = AllAppThreadsData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<int>(ErrorWarningProperty.TotalFileHandles, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
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
                        AllAppTotalActivePortsData[id].AddData(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, FabricServiceContext));
                    }
                    else // Child procs spawned by the parent service process.
                    {
                        _ = AllAppTotalActivePortsData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                        AllAppTotalActivePortsData[$"{id}:{procName}{procId}"].AddData(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, FabricServiceContext));
                    }
                }

                // Ephemeral TCP ports usage
                if (checkEphemeralPorts)
                {
                    if (procId == parentPid)
                    {
                        AllAppEphemeralPortsData[id].AddData(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, FabricServiceContext));
                    }
                    else
                    {
                        _ = AllAppEphemeralPortsData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                        AllAppEphemeralPortsData[$"{id}:{procName}{procId}"].AddData(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, FabricServiceContext));
                    }
                }

                // KVS LVIDs
                if (isWindows && checkLvids && ReplicaOrInstanceList.Any(r => r.HostProcessId == procId && r.ServiceKind == ServiceKind.Stateful))
                {
                    var lvidPct = ProcessInfoProvider.Instance.ProcessGetCurrentKvsLvidsUsedPercentage(procName);

                    if (procId == parentPid)
                    {
                        AllAppKvsLvidsData[id].AddData(lvidPct);
                    }
                    else
                    {
                        _ = AllAppKvsLvidsData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<double>(ErrorWarningProperty.TotalKvsLvidsPercent, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                        AllAppKvsLvidsData[$"{id}:{procName}{procId}"].AddData(lvidPct);
                    }
                }

                // No need to proceed further if no cpu/mem thresholds are specified in configuration.
                if (!checkCpu && !checkMemMb && !checkMemPct)
                {
                    state.Stop();
                }

                Stopwatch timer = Stopwatch.StartNew();

                while (timer.Elapsed <= maxDuration)
                {
                    token.ThrowIfCancellationRequested();

                    // CPU (all cores) \\

                    if (checkCpu)
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
                                _ = AllAppCpuData.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                                AllAppCpuData[$"{id}:{procName}{procId}"].AddData(cpu);
                            }
                        }
                    }

                    // Memory \\

                    // private working set.
                    if (checkMemMb)
                    {
                        float processMem = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId);
                       
                        if (procId == parentPid)
                        {
                            AllAppMemDataMb[id].AddData(processMem);
                        }
                        else
                        {
                            _ = AllAppMemDataMb.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                            AllAppMemDataMb[$"{id}:{procName}{procId}"].AddData(processMem);
                        }
                    }

                    // percent in use (of total).
                    if (checkMemPct)
                    {
                        float processMem = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId);
                        var (TotalMemoryGb, _, _) = OSInfoProvider.Instance.TupleGetMemoryInfo();

                        if (TotalMemoryGb > 0)
                        {
                            double usedPct = (double)(processMem * 100) / (TotalMemoryGb * 1024);

                            if (procId == parentPid)
                            {
                                AllAppMemDataPercent[id].AddData(Math.Round(usedPct, 2));
                            }
                            else
                            {
                                _ = AllAppMemDataPercent.TryAdd($"{id}:{procName}{procId}", new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, $"{id}:{procName}{procId}", capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                                AllAppMemDataPercent[$"{id}:{procName}{procId}"].AddData(Math.Round(usedPct, 2));
                            }
                        }
                    }
                    Thread.Sleep(150);
                }
                timer.Stop();
                timer = null;
            });
        }

        private async Task SetDeployedApplicationReplicaOrInstanceListAsync(Uri applicationNameFilter = null, string applicationType = null)
        {
            var deployedApps = new List<DeployedApplication>();
            // DEBUG
            //var stopwatch = Stopwatch.StartNew();

            if (applicationNameFilter != null)
            {
                var app = await FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(NodeName, applicationNameFilter);
                deployedApps.AddRange(app.ToList());
            }
            else if (!string.IsNullOrWhiteSpace(applicationType))
            {
                // There is no typename filter (unfortunately), so do a paged query for app data and then filter on supplied typename.
                var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(NodeName)
                {
                    IncludeHealthState = false,
                    MaxResults = 50
                };

                var appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                               deployedAppQueryDesc,
                                                                               ConfigurationSettings.AsyncTimeout,
                                                                               Token),
                                    Token);

                deployedApps = appList.ToList();

                while (appList.ContinuationToken != null)
                {
                    Token.ThrowIfCancellationRequested();

                    deployedAppQueryDesc.ContinuationToken = appList.ContinuationToken;

                    appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                               deployedAppQueryDesc,
                                                                               ConfigurationSettings.AsyncTimeout,
                                                                               Token),
                                    Token);

                    deployedApps.AddRange(appList.ToList());
                    await Task.Delay(250, Token);
                }

                deployedApps = deployedApps.Where(a => a.ApplicationTypeName == applicationType).ToList();

                appList.Clear();
                appList = null;
            }

            for (int i = 0; i < deployedApps.Count; ++i)
            {
                Token.ThrowIfCancellationRequested();

                var deployedApp = deployedApps[i];
                string[] filteredServiceList = null;

                // Filter service list if ServiceExcludeList/ServiceIncludeList config setting is non-empty.
                var serviceFilter = 
                    userTargetList.FirstOrDefault(x => (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                        || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())
                                                        && (!string.IsNullOrWhiteSpace(x.ServiceExcludeList) || !string.IsNullOrWhiteSpace(x.ServiceIncludeList)));

                ServiceFilterType filterType = ServiceFilterType.None;

                if (serviceFilter != null)
                {
                    if (!string.IsNullOrWhiteSpace(serviceFilter.ServiceExcludeList))
                    {
                        filteredServiceList = serviceFilter.ServiceExcludeList.Replace(" ", string.Empty).Split(',');
                        filterType = ServiceFilterType.Exclude;
                    }
                    else if (!string.IsNullOrWhiteSpace(serviceFilter.ServiceIncludeList))
                    {
                        filteredServiceList = serviceFilter.ServiceIncludeList.Replace(" ", string.Empty).Split(',');
                        filterType = ServiceFilterType.Include;
                    }
                }

                List<ReplicaOrInstanceMonitoringInfo> replicasOrInstances = await GetDeployedPrimaryReplicaAsync(deployedApp.ApplicationName, filteredServiceList, filterType, applicationType);

                if (!ReplicaOrInstanceList.Any(r => r.ApplicationName.OriginalString == deployedApp.ApplicationName.OriginalString))
                {
                    ReplicaOrInstanceList.AddRange(replicasOrInstances);

                    var targets = userTargetList.Where(x => (x.TargetApp != null || x.TargetAppType != null)
                                                                && (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                                    || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower()));
                    deployedTargetList.AddRange(targets);
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
                                                () => FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName),
                                                Token);
            //ObserverLogger.LogInfo($"QueryManager.GetDeployedReplicaListAsync for {appName.OriginalString} run duration: {stopwatch.Elapsed}");

            var replicaMonitoringList = new List<ReplicaOrInstanceMonitoringInfo>(deployedReplicaList.Count);

            SetInstanceOrReplicaMonitoringList(
                    appName,
                    serviceFilterList,
                    filterType,
                    appTypeName,
                    deployedReplicaList,
                    replicaMonitoringList);

            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"GetDeployedPrimaryReplicaAsync for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
            return replicaMonitoringList;
        }

        private void SetInstanceOrReplicaMonitoringList(
                                    Uri appName,
                                    string[] filterList,
                                    ServiceFilterType filterType,
                                    string appTypeName,
                                    DeployedServiceReplicaList deployedReplicaList,
                                    List<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList)
        {
            // DEBUG
            //var stopwatch = Stopwatch.StartNew();

            _ = Parallel.For(0, deployedReplicaList.Count, ParallelOptions, (i, state) =>
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
                            ServiceKind = statefulReplica.ServiceKind,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ServiceName = statefulReplica.ServiceName,
                            ServicePackageActivationId = statefulReplica.ServicePackageActivationId
                        };

                        /* In order to provide accurate resource usage of an SF service process we need to also account for
                        any processes (children) that the service process (parent) created/spawned. */

                        if (EnableChildProcessMonitoring)
                        {
                            // DEBUG
                            //var sw = Stopwatch.StartNew();
                            var childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                                ObserverLogger.LogInfo($"{replicaInfo.ServiceName}:{Environment.NewLine}Child procs (name, id): {string.Join(" ", replicaInfo.ChildProcesses)}");
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
                            ServiceKind = statelessInstance.ServiceKind,
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ServiceName = statelessInstance.ServiceName,
                            ServicePackageActivationId = statelessInstance.ServicePackageActivationId
                        };

                        if (EnableChildProcessMonitoring)
                        {
                            // DEBUG
                            //var sw = Stopwatch.StartNew();
                            var childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                                ObserverLogger.LogInfo($"{replicaInfo.ServiceName}:{Environment.NewLine}Child procs (name, id): {string.Join(" ", replicaInfo.ChildProcesses)}");
                            }
                            //sw.Stop();
                            //ObserverLogger.LogInfo($"EnableChildProcessMonitoring block run duration: {sw.Elapsed}");
                        }

                        break;
                    }
                }

                if (replicaInfo != null)
                {
                    replicaMonitoringList.Add(replicaInfo);
                }
            });
            //stopwatch.Stop();
            //ObserverLogger.LogInfo($"SetInstanceOrReplicaMonitoringList for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
        }


        private void CleanUp()
        {
            deployedTargetList?.Clear();
            deployedTargetList = null;

            userTargetList?.Clear();
            userTargetList = null;

            ReplicaOrInstanceList?.Clear();
            ReplicaOrInstanceList = null;

            processInfo?.Clear();
            processInfo = null;

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
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalCpuTime,
                    "Average",
                    Math.Round(AllAppCpuData.First(x => x.Key == appName).Value.AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalCpuTime,
                    "Peak",
                    Math.Round(AllAppCpuData.First(x => x.Key == appName).Value.MaxDataValue));
            }

            // Memory - MB
            if (AllAppMemDataMb.ContainsKey(appName))
            {
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionMb,
                    "Average",
                    Math.Round(AllAppMemDataMb.First(x => x.Key == appName).Value.AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionMb,
                    "Peak",
                    Math.Round(Convert.ToDouble(AllAppMemDataMb.First(x => x.Key == appName).Value.MaxDataValue)));
            }

            if (AllAppMemDataPercent.ContainsKey(appName))
            {
                CsvFileLogger.LogData(
                   fileName,
                   appName,
                   ErrorWarningProperty.TotalMemoryConsumptionPct,
                   "Average",
                   Math.Round(AllAppMemDataPercent.First(x => x.Key == appName).Value.AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionPct,
                    "Peak",
                    Math.Round(Convert.ToDouble(AllAppMemDataPercent.FirstOrDefault(x => x.Key == appName).Value.MaxDataValue)));
            }

            if (AllAppTotalActivePortsData.ContainsKey(appName))
            {
                // Network
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalActivePorts,
                    "Total",
                    Math.Round(Convert.ToDouble(AllAppTotalActivePortsData.First(x => x.Key == appName).Value.MaxDataValue)));
            }

            if (AllAppEphemeralPortsData.ContainsKey(appName))
            {
                // Network
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalEphemeralPorts,
                    "Total",
                    Math.Round(Convert.ToDouble(AllAppEphemeralPortsData.First(x => x.Key == appName).Value.MaxDataValue)));
            }

            if (AllAppHandlesData.ContainsKey(appName))
            {
                // Handles
                CsvFileLogger.LogData(
                     fileName,
                     appName,
                     ErrorWarningProperty.TotalFileHandles,
                     "Total",
                     AllAppHandlesData.First(x => x.Key == appName).Value.MaxDataValue);
            }

            DataTableFileLogger.Flush();
        }

        public double GetMaximumLvidPercentInUseForProcess(string procName)
        {
            // KVS is not supported on Linux.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return -1;
            }

            try
            {
                using var performanceCounter = new PerformanceCounter(
                                                    categoryName: "Windows Fabric Database",
                                                    counterName: "Long-Value Maximum LID",
                                                    instanceName: procName,
                                                    readOnly: true);

                float result = performanceCounter.NextValue();
                long maxLvids = (long)Math.Pow(2, 31);
                double usedPct = (double)(result * 100) / maxLvids;
                return usedPct;
            }
            catch
            {
                return 0;
            }
        }
    }
}