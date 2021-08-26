// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
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
        // Health Report data containers - For use in analysis to determine health state.
        // These lists are cleared after each healthy iteration.
        private List<FabricResourceUsageData<double>> AllAppCpuData;
        private List<FabricResourceUsageData<float>> AllAppMemDataMb;
        private List<FabricResourceUsageData<double>> AllAppMemDataPercent;
        private List<FabricResourceUsageData<int>> AllAppTotalActivePortsData;
        private List<FabricResourceUsageData<int>> AllAppEphemeralPortsData;
        private List<FabricResourceUsageData<float>> AllAppHandlesData;

        // userTargetList is the list of ApplicationInfo objects representing app/app types supplied in configuration.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        private List<ApplicationInfo> deployedTargetList;
        private readonly ConfigSettings configSettings;
        private string fileName;
        private readonly Stopwatch stopwatch;

        public int MaxChildProcTelemetryDataCount
        {
            get; set;
        }

        public bool EnableChildProcessMonitoring
        {
            get; set;
        }

        public List<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList
        {
            get; set;
        }

        public string ConfigPackagePath
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
            // If set, this observer will only run during the supplied interval.
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

            // The time it took to run this observer.
            stopwatch.Stop();
            CleanUp();
            RunDuration = stopwatch.Elapsed;

            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration: {RunDuration}");
            }

            stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            if (deployedTargetList.Count == 0)
            {
                return Task.CompletedTask;
            }

            // For use in process family tree monitoring.
            List<ChildProcessTelemetryData> childProcessTelemetryDataList = null;
            TimeSpan healthReportTimeToLive = GetHealthReportTimeToLive();

            for (int i = 0; i < ReplicaOrInstanceList.Count; ++i)
            {
                token.ThrowIfCancellationRequested();

                var repOrInst = ReplicaOrInstanceList[i];
                string processName = null;
                int processId = 0;
                ApplicationInfo app = null;
                bool hasChildProcs = EnableChildProcessMonitoring && repOrInst.ChildProcesses != null;
                
                if (hasChildProcs)
                {
                    childProcessTelemetryDataList = new List<ChildProcessTelemetryData>();
                }

                app = deployedTargetList.Find(
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
                    continue;
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
                        continue;
                    }

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

                // CPU - Parent process
                if (AllAppCpuData.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppCpuData.FirstOrDefault(x => x.Id == id);

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(ref AllAppCpuData, ref childProcessTelemetryDataList, repOrInst, ref app, ref parentFrud, token);
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
                if (AllAppMemDataMb.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppMemDataMb.FirstOrDefault(x => x.Id == id);

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(ref AllAppMemDataMb, ref childProcessTelemetryDataList, repOrInst, ref app, ref parentFrud, token);
                    }

                    // Parent's and aggregated (summed) spawned process data (if any).
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
                if (AllAppMemDataPercent.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppMemDataPercent.FirstOrDefault(x => x.Id == id);

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(ref AllAppMemDataPercent, ref childProcessTelemetryDataList, repOrInst, ref app, ref parentFrud, token);
                    }

                    // Parent's and aggregated (summed) spawned process data (if any).
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
                if (AllAppTotalActivePortsData.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == id);
                    
                    if (hasChildProcs)
                    {
                        ProcessChildProcs(ref AllAppTotalActivePortsData, ref childProcessTelemetryDataList, repOrInst, ref app, ref parentFrud, token);
                    }

                    // Parent's and aggregated (summed) spawned process data (if any).
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
                if (AllAppEphemeralPortsData.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == id);

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(ref AllAppEphemeralPortsData, ref childProcessTelemetryDataList, repOrInst, ref app, ref parentFrud, token);
                    }

                    // Parent's and aggregated (summed) spawned process data (if any).
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
                if (AllAppHandlesData.Any(x => x.Id == id))
                {
                    var parentFrud = AllAppHandlesData.FirstOrDefault(x => x.Id == id);

                    if (hasChildProcs)
                    {
                        ProcessChildProcs(ref AllAppHandlesData, ref childProcessTelemetryDataList, repOrInst, ref app, ref parentFrud, token);
                    }

                    // Parent's and aggregated (summed) spawned process data (if any).
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
                if (IsEtwEnabled && hasChildProcs && MaxChildProcTelemetryDataCount > 0)
                {
                    var data = new
                    {
                        ChildProcessTelemetryData = JsonConvert.SerializeObject(childProcessTelemetryDataList)
                    };

                    ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, data);
                }

                if (IsTelemetryEnabled && hasChildProcs && MaxChildProcTelemetryDataCount > 0)
                {
                    _ = TelemetryClient?.ReportMetricAsync(childProcessTelemetryDataList, token);
                }

                childProcessTelemetryDataList = null;
            }

            return Task.CompletedTask;
        }

        private void ProcessChildProcs<T>(
                            ref List<FabricResourceUsageData<T>> fruds,
                            ref List<ChildProcessTelemetryData> childProcessTelemetryDataList, 
                            ReplicaOrInstanceMonitoringInfo repOrInst, 
                            ref ApplicationInfo app, 
                            ref FabricResourceUsageData<T> parentFrud, 
                            CancellationToken token) where T : struct
        {
            token.ThrowIfCancellationRequested();

            try
            {
                string metric = parentFrud.Property;
                var parentDataAvg = Math.Round(parentFrud.AverageDataValue, 0);
                var (childProcInfo, Sum) = ProcessChildFrudsGetDataSum(ref fruds, repOrInst, ref app, token);
                double sumAllValues = Sum + parentDataAvg;
                childProcInfo.Metric = metric;
                childProcInfo.Value = sumAllValues;
                childProcessTelemetryDataList.Add(childProcInfo);
                parentFrud.Data.Clear();
                parentFrud.Data.Add((T)Convert.ChangeType(sumAllValues, typeof(T)));
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogWarning($"Error processing child processes:{Environment.NewLine}{e}");
            }
        }

        private (ChildProcessTelemetryData childProcInfo, double Sum) ProcessChildFrudsGetDataSum<T>(
                                                                        ref List<FabricResourceUsageData<T>> fruds,
                                                                        ReplicaOrInstanceMonitoringInfo repOrInst,
                                                                        ref ApplicationInfo app,
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
                    if (fruds.Any(x => x.Id.Contains(childProcName)))
                    {
                        var childFruds = fruds.Where(x => x.Id.Contains(childProcName)).ToList();
                        metric = childFruds[0].Property;

                        for (int j = 0; j < childFruds.Count; ++j)
                        {
                            token.ThrowIfCancellationRequested();
                            
                            var frud = childFruds[j];
                            double value = frud.AverageDataValue;
                            sumValues += Math.Round(value, 0);

                            if (IsEtwEnabled || IsTelemetryEnabled)
                            {
                                var childProcInfo = new ChildProcessInfo { ProcessName = childProcName, Value = value };
                                childProcessInfoData.ChildProcessInfo.Add(childProcInfo); 
                            }

                            // Windows process dump support for descendant/child processes \\

                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && app.DumpProcessOnError && EnableProcessDumps)
                            {
                                string prop = frud.Property;

                                switch (prop)
                                {
                                    case ErrorWarningProperty.TotalCpuTime:
                                        if (frud.IsUnhealthy(app.CpuErrorLimitPercent))
                                        {
                                            DumpWindowsServiceProcess(childPid, childProcName, ErrorWarningProperty.TotalCpuTime);
                                            app.DumpProcessOnError = false;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalMemoryConsumptionMb:
                                        if (frud.IsUnhealthy(app.MemoryErrorLimitMb))
                                        {
                                            DumpWindowsServiceProcess(childPid, childProcName, ErrorWarningProperty.TotalMemoryConsumptionMb);
                                            app.DumpProcessOnError = false;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalMemoryConsumptionPct:
                                        if (frud.IsUnhealthy(app.MemoryErrorLimitPercent))
                                        {
                                            DumpWindowsServiceProcess(childPid, childProcName, ErrorWarningProperty.TotalMemoryConsumptionPct);
                                            app.DumpProcessOnError = false;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalActivePorts:
                                        if (frud.IsUnhealthy(app.NetworkErrorActivePorts))
                                        {
                                            DumpWindowsServiceProcess(childPid, childProcName, ErrorWarningProperty.TotalActivePorts);
                                            app.DumpProcessOnError = false;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalEphemeralPorts:
                                        if (frud.IsUnhealthy(app.NetworkErrorEphemeralPorts))
                                        {
                                            DumpWindowsServiceProcess(childPid, childProcName, ErrorWarningProperty.TotalEphemeralPorts);
                                            app.DumpProcessOnError = false;
                                        }
                                        break;

                                    case ErrorWarningProperty.TotalFileHandles:
                                        if (frud.IsUnhealthy(app.ErrorOpenFileHandles))
                                        {
                                            DumpWindowsServiceProcess(childPid, childProcName, ErrorWarningProperty.TotalFileHandles);
                                            app.DumpProcessOnError = false;
                                        }
                                        break;
                                }
                            }

                            // Remove child FRUD from ref FRUD.
                            fruds.Remove(frud);
                        }

                        childFruds?.Clear();
                        childFruds = null;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is Win32Exception || e is InvalidOperationException)
                {
                    continue;
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    ObserverLogger.LogWarning($"Error processing child processes:{Environment.NewLine}{e}");
                    continue;
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
            userTargetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
            
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
                ApplicationInfo application = userTargetList.Find(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*");

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
                        var existingAppConfig = userTargetList.Find(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName);

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
                ApplicationInfo application = userTargetList[i];

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

            for (int i = 0; i < repCount; ++i)
            {
                Token.ThrowIfCancellationRequested();

                var rep = ReplicaOrInstanceList[i];

                try
                {
                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring. https://github.com/gittorre/ContainerObserver
                    using Process p = Process.GetProcessById((int)rep.HostProcessId);

                    if (p.ProcessName == "Fabric")
                    {
                        MonitoredServiceProcessCount--;
                        continue;
                    }

                    // This will throw Win32Exception if process is running at higher elevation than FO.
                    // If it is not, then this would mean the process has exited so move on to next process.
                    if (p.HasExited)
                    {
                        MonitoredServiceProcessCount--;
                        continue;
                    }

                    ObserverLogger.LogInfo($"Will observe resource consumption by {rep.ServiceName?.OriginalString}({rep.HostProcessId}) (and child procs, if any) on Node {NodeName}.");
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException || e is Win32Exception)
                {
                    MonitoredServiceProcessCount--;
                }
            }

            return true;
        }

        private async Task MonitorDeployedAppsAsync(CancellationToken token)
        {
            int capacity = ReplicaOrInstanceList.Count;
            AllAppCpuData ??= new List<FabricResourceUsageData<double>>(capacity);
            AllAppMemDataMb ??= new List<FabricResourceUsageData<float>>(capacity);
            AllAppMemDataPercent ??= new List<FabricResourceUsageData<double>>(capacity);
            AllAppTotalActivePortsData ??= new List<FabricResourceUsageData<int>>(capacity);
            AllAppEphemeralPortsData ??= new List<FabricResourceUsageData<int>>(capacity);
            AllAppHandlesData ??= new List<FabricResourceUsageData<float>>(capacity);

            for (int i = 0; i < ReplicaOrInstanceList.Count; ++i)
            {
                token.ThrowIfCancellationRequested();

                var repOrInst = ReplicaOrInstanceList[i];
                var timer = new Stopwatch();
                int parentPid = (int)repOrInst.HostProcessId;
                var cpuUsage = new CpuUsage();
                bool checkCpu = false, checkMemMb = false, checkMemPct = false, checkAllPorts = false, checkEphemeralPorts = false, checkHandles = false;
                var application = deployedTargetList?.Find(
                                    app => app?.TargetApp?.ToLower() == repOrInst.ApplicationName?.OriginalString.ToLower() ||
                                    !string.IsNullOrWhiteSpace(app?.TargetAppType) &&
                                    app.TargetAppType?.ToLower() == repOrInst.ApplicationTypeName?.ToLower());

                List<(string procName, int Pid)> procTree = null;

                if (application?.TargetApp == null && application?.TargetAppType == null)
                {
                    continue;
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
                            continue;
                        }

                        // This will throw Win32Exception if process is running at higher elevation than FO.
                        // If it is not, then this would mean the process has exited so move on to next process.
                        if (parentProc.HasExited)
                        {
                            continue;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || ObserverManager.ObserverFailureHealthStateLevel == HealthState.Unknown)
                        {
                            continue;
                        }

                        if (e is Win32Exception exception && exception.NativeErrorCode == 5 || e.Message.ToLower().Contains("access is denied"))
                        {
                            string message = $"{repOrInst?.ServiceName?.OriginalString} is running as Admin or System user on Windows.{Environment.NewLine}" +
                                             $"You must also run FabricObserver as Admin user or System user on Windows if you want to monitor services that run as Admin or System user on Windows.";

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

                        continue;
                    }

                    string parentProcName = parentProc?.ProcessName;

                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring.
                    if (parentProcName == null || parentProcName == "Fabric")
                    {
                        continue;
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

                    // Add new resource data structures for each app service process where the metric is specified in configuration for related observation.
                    if (AllAppCpuData.All(list => list.Id != id) && (application.CpuErrorLimitPercent > 0 || application.CpuWarningLimitPercent > 0))
                    {
                        AllAppCpuData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppCpuData.Any(list => list.Id == id))
                    {
                        checkCpu = true;
                    }

                    if (AllAppMemDataMb.All(list => list.Id != id) && (application.MemoryErrorLimitMb > 0 || application.MemoryWarningLimitMb > 0))
                    {
                        AllAppMemDataMb.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppMemDataMb.Any(list => list.Id == id))
                    {
                        checkMemMb = true;
                    }

                    if (AllAppMemDataPercent.All(list => list.Id != id) && (application.MemoryErrorLimitPercent > 0 || application.MemoryWarningLimitPercent > 0))
                    {
                        AllAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id, capacity, UseCircularBuffer));
                    }

                    if (AllAppMemDataPercent.Any(list => list.Id == id))
                    {
                        checkMemPct = true;
                    }

                    if (AllAppTotalActivePortsData.All(list => list.Id != id) && (application.NetworkErrorActivePorts > 0 || application.NetworkWarningActivePorts > 0))
                    {
                        AllAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, id, 1));
                    }

                    if (AllAppTotalActivePortsData.Any(list => list.Id == id))
                    {
                        checkAllPorts = true;
                    }

                    if (AllAppEphemeralPortsData.All(list => list.Id != id) && (application.NetworkErrorEphemeralPorts > 0 || application.NetworkWarningEphemeralPorts > 0))
                    {
                        AllAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, id, 1));
                    }

                    if (AllAppEphemeralPortsData.Any(list => list.Id == id))
                    {
                        checkEphemeralPorts = true;
                    }

                    // File Handles (FD on linux)
                    if (AllAppHandlesData.All(list => list.Id != id) && (application.ErrorOpenFileHandles > 0 || application.WarningOpenFileHandles > 0))
                    {
                        AllAppHandlesData.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalFileHandles, id, 1));
                    }

                    if (AllAppHandlesData.Any(list => list.Id == id))
                    {
                        checkHandles = true;
                    }

                    // Get list of child processes of parentProc should they exist.
                    // In order to provide accurate resource usage of an SF service process we need to also account for
                    // any processes (children) that the service process (parent) created/spawned.
                    procTree = new List<(string procName, int Pid)>
                    {
                        // Add parent to the process tree list since we want to monitor all processes in the family. If there are no child processes,
                        // then only the parent process will be in this list.
                        (parentProc.ProcessName, parentProc.Id)
                    };

                    if (repOrInst.ChildProcesses != null && repOrInst.ChildProcesses.Count > 0)
                    {
                        procTree.AddRange(repOrInst.ChildProcesses);
                    }

                    for (int j = 0; j < procTree.Count; ++j)
                    {
                        int procId = procTree[j].Pid;
                        string procName = procTree[j].procName;
                        TimeSpan duration = TimeSpan.FromSeconds(1);

                        if (MonitorDuration > TimeSpan.MinValue)
                        {
                            duration = MonitorDuration;
                        }

                        // No need to proceed further if no cpu/mem/file handles thresholds are specified in configuration.
                        if (!checkCpu && !checkMemMb && !checkMemPct && !checkHandles)
                        {
                            continue;
                        }

                        /* Warm up Windows perf counters. */

                        if (checkCpu)
                        {
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                _ = cpuUsage.GetCpuUsagePercentageProcess(procId);
                            }
                        }

                        // Handles/FDs
                        if (checkHandles)
                        {
                            float handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(procId, FabricServiceContext);

                            if (handles > -1)
                            {
                                if (procId == parentPid)
                                {
                                    AllAppHandlesData.FirstOrDefault(x => x.Id == id).Data.Add(handles);
                                }
                                else
                                {
                                    if (!AllAppHandlesData.Any(x => x.Id == $"{id}:{procName}"))
                                    {
                                        AllAppHandlesData.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalFileHandles, $"{id}:{procName}", capacity, UseCircularBuffer));
                                    }
                                    AllAppHandlesData.FirstOrDefault(x => x.Id == $"{id}:{procName}").Data.Add(handles);
                                }
                            }
                        }

                        // Total TCP ports usage
                        if (checkAllPorts)
                        {
                            // Parent process (the service process).
                            if (procId == parentPid)
                            {
                                AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == id).Data.Add(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, FabricServiceContext));
                            }
                            else
                            {
                                // Child procs spawned by the parent service process.
                                if (!AllAppTotalActivePortsData.Any(x => x.Id == $"{id}:{procName}"))
                                {
                                    AllAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, $"{id}:{procName}", capacity, UseCircularBuffer));
                                }
                                AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == $"{id}:{procName}").Data.Add(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, FabricServiceContext));
                            }
                        }

                        // Ephemeral TCP ports usage
                        if (checkEphemeralPorts)
                        {
                            if (procId == parentPid)
                            {
                                AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == id).Data.Add(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, FabricServiceContext));
                            }
                            else
                            {
                                if (!AllAppEphemeralPortsData.Any(x => x.Id == $"{id}:{procName}"))
                                {
                                    AllAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, $"{id}:{procName}", capacity, UseCircularBuffer));
                                }
                                AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == $"{id}:{procName}").Data.Add(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, FabricServiceContext));
                            }
                        }

                        // Monitor Duration applies to the code below.
                        timer.Start();

                        while (timer.Elapsed.Seconds <= duration.Seconds)
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
                                        AllAppCpuData.FirstOrDefault(x => x.Id == id).Data.Add(cpu);
                                    }
                                    else
                                    {
                                        if (!AllAppCpuData.Any(x => x.Id == $"{id}:{procName}"))
                                        {
                                            AllAppCpuData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, $"{id}:{procName}", capacity, UseCircularBuffer));
                                        }
                                        AllAppCpuData.FirstOrDefault(x => x.Id == $"{id}:{procName}").Data.Add(cpu);
                                    }
                                }
                            }

                            // Memory \\

                            float processMem = 0;

                            // private working set.
                            if (checkMemMb)
                            {
                                processMem = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, true);

                                if (procId == parentPid)
                                {
                                    AllAppMemDataMb.FirstOrDefault(x => x.Id == id).Data.Add(processMem);
                                }
                                else
                                {
                                    if (!AllAppMemDataMb.Any(x => x.Id == $"{id}:{procName}"))
                                    {
                                        AllAppMemDataMb.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, $"{id}:{procName}", capacity, UseCircularBuffer));
                                    }
                                    AllAppMemDataMb.FirstOrDefault(x => x.Id == $"{id}:{procName}").Data.Add(processMem);
                                }
                            }

                            // percent in use (of total).
                            if (checkMemPct)
                            {
                                if (processMem == 0)
                                {
                                    processMem = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, true);
                                }

                                var (TotalMemoryGb, _, _) = OSInfoProvider.Instance.TupleGetMemoryInfo();

                                if (TotalMemoryGb > 0)
                                {
                                    double usedPct = Math.Round((double)(processMem * 100) / (TotalMemoryGb * 1024), 2);

                                    if (procId == parentPid)
                                    {
                                        AllAppMemDataPercent.FirstOrDefault(x => x.Id == id).Data.Add(Math.Round(usedPct, 1));
                                    }
                                    else
                                    {
                                        if (!AllAppMemDataPercent.Any(x => x.Id == $"{id}:{procName}"))
                                        {
                                            AllAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, $"{id}:{procName}", capacity, UseCircularBuffer));
                                        }
                                        AllAppMemDataPercent.FirstOrDefault(x => x.Id == $"{id}:{procName}").Data.Add(Math.Round(usedPct, 1));
                                    }
                                }
                            }

                            await Task.Delay(250, Token).ConfigureAwait(false);
                        }

                        timer.Stop();
                        timer.Reset();

                        await Task.Delay(250, Token).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    ObserverLogger.LogError($"Unhandled exception in MonitorDeployedAppsAsync:{Environment.NewLine}{e}");

                    // Fix the bug..
                    throw;
                }
            } 
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
                var serviceFilter = userTargetList.Find(x => (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
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

                ReplicaOrInstanceList.AddRange(replicasOrInstances);

                deployedTargetList.AddRange(userTargetList.Where(
                                            x => (x.TargetApp != null || x.TargetAppType != null)
                                                 && (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                     || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())));
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
               ref replicaMonitoringList);

            return replicaMonitoringList;
        }

        private void SetInstanceOrReplicaMonitoringList(
                        Uri appName,
                        string[] filterList,
                        ServiceFilterType filterType,
                        string appTypeName,
                        DeployedServiceReplicaList deployedReplicaList,
                        ref List<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList)
        {
            for (int i = 0; i < deployedReplicaList.Count; ++i)
            {
                Token.ThrowIfCancellationRequested();

                var deployedReplica = deployedReplicaList[i];
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica statefulReplica when statefulReplica.ReplicaRole == ReplicaRole.Primary ||
                                                                             statefulReplica.ReplicaRole == ReplicaRole.ActiveSecondary:
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

                        if (EnableChildProcessMonitoring)
                        {
                            var childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
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

            if (AllAppCpuData != null && AllAppCpuData.All(frud => !frud.ActiveErrorOrWarning))
            {
                AllAppCpuData?.Clear();
                AllAppCpuData = null;
            }

            if (AllAppEphemeralPortsData != null && AllAppEphemeralPortsData.All(frud => !frud.ActiveErrorOrWarning))
            {
                AllAppEphemeralPortsData?.Clear();
                AllAppEphemeralPortsData = null;
            }

            if (AllAppHandlesData != null && AllAppHandlesData.All(frud => !frud.ActiveErrorOrWarning))
            {
                AllAppHandlesData?.Clear();
                AllAppHandlesData = null;
            }

            if (AllAppMemDataMb != null && AllAppMemDataMb.All(frud => !frud.ActiveErrorOrWarning))
            {
                AllAppMemDataMb?.Clear();
                AllAppMemDataMb = null;
            }

            if (AllAppMemDataPercent != null && AllAppMemDataPercent.All(frud => !frud.ActiveErrorOrWarning))
            {
                AllAppMemDataPercent?.Clear();
                AllAppMemDataPercent = null;
            }

            if (AllAppTotalActivePortsData != null && AllAppTotalActivePortsData.All(frud => !frud.ActiveErrorOrWarning))
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
            if (AllAppCpuData.Any(x => x.Id == appName))
            {
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalCpuTime,
                    "Average",
                    Math.Round(AllAppCpuData.Find(x => x.Id == appName).AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalCpuTime,
                    "Peak",
                    Math.Round(AllAppCpuData.FirstOrDefault(x => x.Id == appName).MaxDataValue));
            }

            // Memory - MB
            if (AllAppMemDataMb.Any(x => x.Id == appName))
            {
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionMb,
                    "Average",
                    Math.Round(AllAppMemDataMb.FirstOrDefault(x => x.Id == appName).AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionMb,
                    "Peak",
                    Math.Round(Convert.ToDouble(AllAppMemDataMb.FirstOrDefault(x => x.Id == appName).MaxDataValue)));
            }

            if (AllAppMemDataPercent.Any(x => x.Id == appName))
            {
                CsvFileLogger.LogData(
                   fileName,
                   appName,
                   ErrorWarningProperty.TotalMemoryConsumptionPct,
                   "Average",
                   Math.Round(AllAppMemDataPercent.FirstOrDefault(x => x.Id == appName).AverageDataValue));

                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalMemoryConsumptionPct,
                    "Peak",
                    Math.Round(Convert.ToDouble(AllAppMemDataPercent.FirstOrDefault(x => x.Id == appName).MaxDataValue)));
            }

            if (AllAppTotalActivePortsData.Any(x => x.Id == appName))
            {
                // Network
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalActivePorts,
                    "Total",
                    Math.Round(Convert.ToDouble(AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == appName).MaxDataValue)));
            }

            if (AllAppEphemeralPortsData.Any(x => x.Id == appName))
            {
                // Network
                CsvFileLogger.LogData(
                    fileName,
                    appName,
                    ErrorWarningProperty.TotalEphemeralPorts,
                    "Total",
                    Math.Round(Convert.ToDouble(AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == appName).MaxDataValue)));
            }

            if (AllAppHandlesData.Any(x => x.Id == appName))
            {
                // Handles
                CsvFileLogger.LogData(
                     fileName,
                     appName,
                     ErrorWarningProperty.TotalFileHandles,
                     "Total",
                     Math.Round(AllAppHandlesData.FirstOrDefault(x => x.Id == appName).MaxDataValue));
            }

            DataTableFileLogger.Flush();
        }
    }
}