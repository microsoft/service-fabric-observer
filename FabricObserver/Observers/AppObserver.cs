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
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppMemDataMb;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppMemDataPercent;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppTotalActivePortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppEphemeralPortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppHandlesData;
        // userTargetList is the list of ApplicationInfo objects representing app/app types supplied in configuration.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        private ConcurrentQueue<ApplicationInfo> deployedTargetList;
        private readonly ConfigSettings configSettings;
        private string fileName;
        private readonly Stopwatch stopwatch;
        private readonly object lockObj = new object();

        public int MaxChildProcTelemetryDataCount
        {
            get; set;
        }

        public bool EnableChildProcessMonitoring
        {
            get; set;
        }

        public ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList
        {
            get; set;
        }

        public string ConfigPackagePath
        {
            get; set;
        }

        public bool EnableProcessDumps
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
            CleanUp();
            RunDuration = stopwatch.Elapsed;
           
            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration {(ObserverManager.ParallelOptions.MaxDegreeOfParallelism == -1 ? "with" : "without")} " +
                                       $"Parallel (Processors: {Environment.ProcessorCount}):{RunDuration}");
            }

            stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            if (deployedTargetList.IsEmpty)
            {
                return Task.CompletedTask;
            }

            TimeSpan healthReportTimeToLive = GetHealthReportTimeToLive();

            _ = Parallel.For (0, ReplicaOrInstanceList.Count, ObserverManager.ParallelOptions, (i, state) =>
            {
                token.ThrowIfCancellationRequested();

                // For use in process family tree monitoring.
                ConcurrentQueue<ChildProcessTelemetryData> childProcessTelemetryDataList = null;

                if (!ReplicaOrInstanceList.TryDequeue(out ReplicaOrInstanceMonitoringInfo repOrInst))
                {
                    return;
                }

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
                    using Process p = Process.GetProcessById((int)repOrInst.HostProcessId);
                    processName = p.ProcessName;
                    processId = p.Id;
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                {
                    return;
                }
                
                string appNameOrType = GetAppNameOrType(repOrInst);
                var id = $"{appNameOrType}:{processName}";

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

                    // Parent's and aggregated (summed) spawned process data (if any).
                    ProcessResourceDataReportHealth(
                        parentFrud,
                        app.CpuErrorLimitPercent,
                        app.CpuWarningLimitPercent,
                        healthReportTimeToLive,
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
                        healthReportTimeToLive,
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
                        healthReportTimeToLive,
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
                        healthReportTimeToLive,
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
                        healthReportTimeToLive,
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
                            healthReportTimeToLive,
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
                var parentDataAvg = Math.Round(parentFrud.AverageDataValue, 0);
                var (childProcInfo, Sum) = ProcessChildFrudsGetDataSum(fruds, repOrInst, app, token);
                double sumAllValues = Sum + parentDataAvg;
                childProcInfo.Metric = metric;
                childProcInfo.Value = sumAllValues;
                childProcessTelemetryDataList.Enqueue(childProcInfo);
                parentFrud.Data.Clear();
                parentFrud.Data.Add((T)Convert.ChangeType(sumAllValues, typeof(T)));
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
                            sumValues += Math.Round(value, 0);

                            if (IsEtwEnabled || IsTelemetryEnabled)
                            {
                                var childProcInfo = new ChildProcessInfo { ProcessName = childProcName, Value = value };
                                childProcessInfoData.ChildProcessInfo.Add(childProcInfo);
                            }

                            // Windows process dump support for descendant/child processes \\

                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && app.DumpProcessOnError && EnableProcessDumps)
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
            ReplicaOrInstanceList = new ConcurrentQueue<ReplicaOrInstanceMonitoringInfo>();
            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new ConcurrentQueue<ApplicationInfo>();

            /* Child/Descendant proc monitoring config */
            if (bool.TryParse(
                     GetSettingParameterValue(
                        ConfigurationSectionName,
                        ObserverConstants.EnableChildProcessMonitoringParameter), out bool enableDescendantMonitoring))
            {
                EnableChildProcessMonitoring = enableDescendantMonitoring;
            }

            if (int.TryParse(
                       GetSettingParameterValue(
                          ConfigurationSectionName,
                          ObserverConstants.MaxChildProcTelemetryDataCountParameter), out int maxChildProcs))
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

            configSettings.Initialize(
                            FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(
                                                                                 ObserverConstants.ObserverConfigurationPackageName)?.Settings,
                                                                                 ConfigurationSectionName,
                                                                                 "AppObserverDataFileName");

            // Unit tests may have null path and filename, thus the null equivalence operations.
            var appObserverConfigFileName = Path.Combine(ConfigPackagePath ?? string.Empty, configSettings.AppObserverConfigFileName ?? string.Empty);

            if (!File.Exists(appObserverConfigFileName))
            {
                ObserverLogger.LogWarning($"Will not observe resource consumption on node {NodeName} as no configuration file has been supplied.");
                return false;
            }

            bool isJson = JsonHelper.IsJson<List<ApplicationInfo>>(await File.ReadAllTextAsync(appObserverConfigFileName));

            if (!isJson)
            {
                string message = "AppObserver's JSON configuration file is malformed. Please fix the JSON and redeploy FabricObserver if you want AppObserver to monitor service processes.";
                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = EnableVerboseLogging,
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
                    EmitLogEvent = EnableVerboseLogging,
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
                            WarningOpenFileHandles = application.WarningOpenFileHandles
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
                ApplicationInfo application = userTargetList.ElementAt(i);

                if (string.IsNullOrWhiteSpace(application.TargetApp) && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    ObserverLogger.LogWarning($"InitializeAsync: Required setting, targetApp or targetAppType, is not set in AppObserver.config.json.");

                    settingsFail++;
                    continue;
                }

                // No required settings supplied for deployed application(s).
                if (settingsFail == userTargetList.Count)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(application.TargetApp))
                {
                    try
                    {
                        if (!application.TargetApp.StartsWith("fabric:/"))
                        {
                            application.TargetApp = application.TargetApp.Insert(0, "fabric:/");
                        }

                        if (application.TargetApp.Contains(" "))
                        {
                            application.TargetApp = application.TargetApp.Replace(" ", string.Empty);
                        }

                        appUri = new Uri(application.TargetApp);
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

            // For use in internal telemetry.
            MonitoredServiceProcessCount = repCount;
            MonitoredAppCount = deployedTargetList.Count;

            if (!EnableVerboseLogging)
            {
                return true;
            }

            for (int i = 0; i < repCount; ++i)
            {
                Token.ThrowIfCancellationRequested();

                var rep = ReplicaOrInstanceList.ElementAt(i);

                try
                {
                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring. https://github.com/gittorre/ContainerObserver
                    using Process p = Process.GetProcessById((int)rep.HostProcessId);

                    if (p.ProcessName == "Fabric")
                    {
                        continue;
                    }

                    // This will throw Win32Exception if process is running at higher elevation than FO.
                    // If it is not, then this would mean the process has exited so move on to next process.
                    if (p.HasExited)
                    {
                        continue;
                    }

                    ObserverLogger.LogInfo($"Will observe resource consumption by {rep.ServiceName?.OriginalString}({rep.HostProcessId}) (and child procs, if any) on Node {NodeName}.");
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException || e is Win32Exception)
                {
                    
                }
            }

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
            AllAppCpuData ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppMemDataMb ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
            AllAppMemDataPercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppTotalActivePortsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppEphemeralPortsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppHandlesData ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
            var exceptions = new ConcurrentQueue<Exception>();

            _ = Parallel.For(0, ReplicaOrInstanceList.Count, ObserverManager.ParallelOptions, (i, state) =>
            {
                token.ThrowIfCancellationRequested();

                var repOrInst = ReplicaOrInstanceList.ElementAt(i);
                var timer = new Stopwatch();
                int parentPid = (int)repOrInst.HostProcessId;
                bool checkCpu = false, checkMemMb = false, checkMemPct = false, checkAllPorts = false, checkEphemeralPorts = false, checkHandles = false;
                var application = deployedTargetList?.First(
                                    app => app?.TargetApp?.ToLower() == repOrInst.ApplicationName?.OriginalString.ToLower() ||
                                    !string.IsNullOrWhiteSpace(app?.TargetAppType) &&
                                    app.TargetAppType?.ToLower() == repOrInst.ApplicationTypeName?.ToLower());

                ConcurrentDictionary<string, int> procs = null;

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

                        // This is strange and can happen during a redeployment.
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && parentProc?.ProcessName == "Idle")
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
                                                            HealthState.Warning,
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
                                                    Level = "Warning",
                                                    Message = message,
                                                    ObserverName,
                                                    ServiceName = repOrInst?.ServiceName?.OriginalString
                                                });
                            }
                        }

                        return;
                    }

                    string parentProcName = parentProc?.ProcessName;

                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring.
                    if (parentProcName == null || parentProcName == "Fabric")
                    {
                        return;
                    }

                    string appNameOrType = GetAppNameOrType(repOrInst);
                    string id = $"{appNameOrType}:{parentProcName}";

                    if (UseCircularBuffer)
                    {
                        capacity = DataCapacity > 0 ? DataCapacity : 5;
                    }
                    else if (MonitorDuration > TimeSpan.MinValue)
                    {
                        capacity = (int)MonitorDuration.TotalSeconds * 4;
                    }

                    if (!AllAppCpuData.ContainsKey(id) && (application.CpuErrorLimitPercent > 0 || application.CpuWarningLimitPercent > 0))
                    {
                        _ = AllAppCpuData.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppCpuData.ContainsKey(id))
                    {
                        checkCpu = true;
                    }

                    if (!AllAppMemDataMb.ContainsKey(id) && (application.MemoryErrorLimitMb > 0 || application.MemoryWarningLimitMb > 0))
                    {
                        _ = AllAppMemDataMb.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppMemDataMb.ContainsKey(id))
                    {
                        checkMemMb = true;
                    }

                    if (!AllAppMemDataPercent.ContainsKey(id) && (application.MemoryErrorLimitPercent > 0 || application.MemoryWarningLimitPercent > 0))
                    {
                        _ = AllAppMemDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppMemDataPercent.ContainsKey(id))
                    {
                        checkMemPct = true;
                    }

                    if (!AllAppTotalActivePortsData.ContainsKey(id) && (application.NetworkErrorActivePorts > 0 || application.NetworkWarningActivePorts > 0))
                    {
                        _ = AllAppTotalActivePortsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, id, 1, false));
                    }

                    if (AllAppTotalActivePortsData.ContainsKey(id))
                    {
                        checkAllPorts = true;
                    }

                    if (!AllAppEphemeralPortsData.ContainsKey(id) && (application.NetworkErrorEphemeralPorts > 0 || application.NetworkWarningEphemeralPorts > 0))
                    {
                        _ = AllAppEphemeralPortsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, id, 1, false));
                    }

                    if (AllAppEphemeralPortsData.ContainsKey(id))
                    {
                        checkEphemeralPorts = true;
                    }

                    if (!AllAppHandlesData.ContainsKey(id) && (application.ErrorOpenFileHandles > 0 || application.WarningOpenFileHandles > 0))
                    {
                        _ = AllAppHandlesData.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.TotalFileHandles, id, 1, false));
                    }

                    if (AllAppHandlesData.ContainsKey(id))
                    {
                        checkHandles = true;
                    }

                    /* In order to provide accurate resource usage of an SF service process we need to also account for
                       any processes (children) that the service process (parent) created/spawned. */

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

                    ComputeResourceUsage(
                            capacity,
                            parentPid,
                            checkCpu,
                            checkMemMb,
                            checkMemPct,
                            checkAllPorts,
                            checkEphemeralPorts,
                            checkHandles,
                            procs,
                            id,
                            token);
                }
                catch (AggregateException e) when (e.InnerException is OperationCanceledException || e.InnerException is TaskCanceledException)
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
#if DEBUG
            ObserverLogger.LogInfo($"MonitorDeployedAppsAsync execution time: {execTimer.Elapsed}");
#endif
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
                            ConcurrentDictionary<string, int> procs,
                            string id,
                            CancellationToken token)
        {
            _ = Parallel.ForEach(procs, (proc, state) =>
            {
                int procId = proc.Value;
                string procName = proc.Key;
                TimeSpan maxDuration = TimeSpan.FromSeconds(1);
                CpuUsage cpuUsage = new CpuUsage();

                if (MonitorDuration > TimeSpan.MinValue)
                {
                    maxDuration = MonitorDuration;
                }

                // Handles/FDs
                if (checkHandles)
                {
                    float handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(procId, FabricServiceContext);

                    if (handles > -1)
                    {
                        if (procId == parentPid)
                        {
                            AllAppHandlesData[id].Data.Add(handles);
                        }
                        else
                        {
                            if (!AllAppHandlesData.ContainsKey($"{id}:{procName}"))
                            {
                                _ = AllAppHandlesData.TryAdd($"{id}:{procName}", new FabricResourceUsageData<float>(ErrorWarningProperty.TotalFileHandles, $"{id}:{procName}", capacity, UseCircularBuffer));
                            }
                            AllAppHandlesData[$"{id}:{procName}"].Data.Add(handles);
                        }
                    }
                }

                // Total TCP ports usage
                if (checkAllPorts)
                {
                    // Parent process (the service process).
                    if (procId == parentPid)
                    {
                        AllAppTotalActivePortsData[id].Data.Add(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, FabricServiceContext));
                    }
                    else
                    {
                    // Child procs spawned by the parent service process.
                    if (!AllAppTotalActivePortsData.ContainsKey($"{id}:{procName}"))
                        {
                            _ = AllAppTotalActivePortsData.TryAdd($"{id}:{procName}", new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, $"{id}:{procName}", capacity, UseCircularBuffer));
                        }
                        AllAppTotalActivePortsData[$"{id}:{procName}"].Data.Add(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, FabricServiceContext));
                    }
                }

                // Ephemeral TCP ports usage
                if (checkEphemeralPorts)
                {
                    if (procId == parentPid)
                    {
                        AllAppEphemeralPortsData[id].Data.Add(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, FabricServiceContext));
                    }
                    else
                    {
                        if (!AllAppEphemeralPortsData.ContainsKey($"{id}:{procName}"))
                        {
                            _ = AllAppEphemeralPortsData.TryAdd($"{id}:{procName}", new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, $"{id}:{procName}", capacity, UseCircularBuffer));
                        }
                        AllAppEphemeralPortsData[$"{id}:{procName}"].Data.Add(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, FabricServiceContext));
                    }
                }

                // No need to proceed further if no cpu/mem thresholds are specified in configuration.
                if (!checkCpu && !checkMemMb && !checkMemPct)
                {
                    state.Stop();
                }

                if (checkCpu && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _ = cpuUsage.GetCpuUsagePercentageProcess(procId);  
                }

                // Monitor Duration applies to the code below.
                var timer = Stopwatch.StartNew();

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
                                AllAppCpuData[id].Data.Add(cpu);
                            }
                            else
                            {
                                if (!AllAppCpuData.ContainsKey($"{id}:{procName}"))
                                {
                                    _ = AllAppCpuData.TryAdd($"{id}:{procName}", new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, $"{id}:{procName}", capacity, UseCircularBuffer));
                                }
                                AllAppCpuData[$"{id}:{procName}"].Data.Add(cpu);
                            }
                        }
                    }

                    // Memory \\

                    // private working set.
                    if (checkMemMb)
                    {
                        float processMem = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, true);

                        if (procId == parentPid)
                        {
                            AllAppMemDataMb[id].Data.Add(processMem);
                        }
                        else
                        {
                            if (!AllAppMemDataMb.ContainsKey($"{id}:{procName}"))
                            {
                                _ = AllAppMemDataMb.TryAdd($"{id}:{procName}", new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, $"{id}:{procName}", capacity, UseCircularBuffer));
                            }
                            AllAppMemDataMb[$"{id}:{procName}"].Data.Add(processMem);
                        }
                    }

                    // percent in use (of total).
                    if (checkMemPct)
                    {
                        float processMem = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, true);

                        var (TotalMemoryGb, _, _) = OSInfoProvider.Instance.TupleGetMemoryInfo();

                        if (TotalMemoryGb > 0)
                        {
                            double usedPct = Math.Round((double)(processMem * 100) / (TotalMemoryGb * 1024), 2);

                            if (procId == parentPid)
                            {
                                AllAppMemDataPercent[id].Data.Add(Math.Round(usedPct, 1));
                            }
                            else
                            {
                                if (!AllAppMemDataPercent.ContainsKey($"{id}:{procName}"))
                                {
                                    _ = AllAppMemDataPercent.TryAdd($"{id}:{procName}", new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, $"{id}:{procName}", capacity, UseCircularBuffer));
                                }
                                AllAppMemDataPercent[$"{id}:{procName}"].Data.Add(Math.Round(usedPct, 1));
                            }
                        }
                    }

                    Thread.Sleep(150);
                }
            });
        }

        private async Task SetDeployedApplicationReplicaOrInstanceListAsync(Uri applicationNameFilter = null, string applicationType = null)
        {
            var deployedApps = new List<DeployedApplication>();

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

                var replicasOrInstances = await GetDeployedPrimaryReplicaAsync(deployedApp.ApplicationName, filteredServiceList, filterType, applicationType);

                foreach (var rep in replicasOrInstances)
                {
                    ReplicaOrInstanceList.Enqueue(rep);
                }
               
                var targets = userTargetList.Where(x => (x.TargetApp != null || x.TargetAppType != null)
                                                            && (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                                || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower()));
                foreach (var target in targets)
                {
                    deployedTargetList.Enqueue(target);
                }
            }

            deployedApps.Clear();
            deployedApps = null;
        }

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedPrimaryReplicaAsync(
                                                                     Uri appName,
                                                                     string[] serviceFilterList = null,
                                                                     ServiceFilterType filterType = ServiceFilterType.None,
                                                                     string appTypeName = null)
        {
            var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName),
                                                Token);

            var replicaMonitoringList = new List<ReplicaOrInstanceMonitoringInfo>(deployedReplicaList.Count);

            SetInstanceOrReplicaMonitoringList(
                    appName,
                    serviceFilterList,
                    filterType,
                    appTypeName,
                    deployedReplicaList,
                    replicaMonitoringList);

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
            for (int i = 0; i < deployedReplicaList.Count; ++i)
            {
                Token.ThrowIfCancellationRequested();

                var deployedReplica = deployedReplicaList[i];
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica statefulReplica when statefulReplica.ReplicaRole == ReplicaRole.Primary:
                    {
                        if (filterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = filterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    continue;
                            }
                        }

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statefulReplica.HostProcessId,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ServiceName = statefulReplica.ServiceName
                        };

                        /* In order to provide accurate resource usage of an SF service process we need to also account for
                        any processes (children) that the service process (parent) created/spawned. */

                        if (EnableChildProcessMonitoring)
                        {
                            var childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                                ObserverLogger.LogInfo($"{replicaInfo.ServiceName}:{Environment.NewLine}Child procs (name, id): {string.Join(" ", replicaInfo.ChildProcesses)}");
                            }
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
                                    continue;
                            }
                        }

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statelessInstance.HostProcessId,
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ServiceName = statelessInstance.ServiceName
                        };

                        if (EnableChildProcessMonitoring)
                        {
                            var childProcs = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId);

                            if (childProcs != null && childProcs.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childProcs;
                                ObserverLogger.LogInfo($"{replicaInfo.ServiceName}:{Environment.NewLine}Child procs (name, id): {string.Join(" ", replicaInfo.ChildProcesses)}");
                            }
                        }

                        break;
                    }
                }

                if (replicaInfo != null)
                {
                    replicaMonitoringList.Add(replicaInfo);
                }
            }
        }

        private void CleanUp()
        {
            deployedTargetList?.Clear();
            deployedTargetList = null;

            userTargetList?.Clear();
            userTargetList = null;

            ReplicaOrInstanceList?.Clear();
            ReplicaOrInstanceList = null;

            if (AllAppCpuData != null && AllAppCpuData.All(frud => frud.Value != null && !frud.Value.ActiveErrorOrWarning))
            {
                AllAppCpuData?.Clear();
                AllAppCpuData = null;
            }

            if (AllAppEphemeralPortsData != null && AllAppEphemeralPortsData.All(frud => frud.Value != null && !frud.Value.ActiveErrorOrWarning))
            {
                AllAppEphemeralPortsData?.Clear();
                AllAppEphemeralPortsData = null;
            }

            if (AllAppHandlesData != null && AllAppHandlesData.All(frud => frud.Value != null && !frud.Value.ActiveErrorOrWarning))
            {
                AllAppHandlesData?.Clear();
                AllAppHandlesData = null;
            }

            if (AllAppMemDataMb != null && AllAppMemDataMb.All(frud => frud.Value != null && !frud.Value.ActiveErrorOrWarning))
            {
                AllAppMemDataMb?.Clear();
                AllAppMemDataMb = null;
            }

            if (AllAppMemDataPercent != null && AllAppMemDataPercent.All(frud => frud.Value != null && !frud.Value.ActiveErrorOrWarning))
            {
                AllAppMemDataPercent?.Clear();
                AllAppMemDataPercent = null;
            }

            if (AllAppTotalActivePortsData != null && AllAppTotalActivePortsData.All(frud => frud.Value != null && !frud.Value.ActiveErrorOrWarning))
            {
                AllAppTotalActivePortsData?.Clear();
                AllAppTotalActivePortsData = null;
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
                     Math.Round(AllAppHandlesData.First(x => x.Key == appName).Value.MaxDataValue));
            }

            DataTableFileLogger.Flush();
        }
    }
}