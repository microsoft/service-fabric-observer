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
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using ConfigSettings = FabricObserver.Observers.MachineInfoModel.ConfigSettings;

namespace FabricObserver.Observers
{
    // This observer monitors the behavior of user SF service processes
    // and signals Warning and Error based on user-supplied resource thresholds
    // in AppObserver.config.json
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    public class AppObserver : ObserverBase
    {
        // Health Report data containers - For use in analysis to determine health state.
        // These lists are cleared after each healthy iteration.
        private readonly List<FabricResourceUsageData<double>> AllAppCpuData;
        private readonly List<FabricResourceUsageData<float>> AllAppMemDataMb;
        private readonly List<FabricResourceUsageData<double>> AllAppMemDataPercent;
        private readonly List<FabricResourceUsageData<int>> AllAppTotalActivePortsData;
        private readonly List<FabricResourceUsageData<int>> AllAppEphemeralPortsData;
        private readonly List<FabricResourceUsageData<float>> AllAppHandlesData;
        private readonly Stopwatch stopwatch;

        // userTargetList is the list of ApplicationInfo objects representing app/app types supplied in configuration.
        private readonly List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        private readonly List<ApplicationInfo> deployedTargetList;
        private readonly ConfigSettings configSettings;
        private string fileName;

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
            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new List<ApplicationInfo>();
            AllAppCpuData = new List<FabricResourceUsageData<double>>();
            AllAppMemDataMb = new List<FabricResourceUsageData<float>>();
            AllAppMemDataPercent = new List<FabricResourceUsageData<double>>();
            AllAppTotalActivePortsData = new List<FabricResourceUsageData<int>>();
            AllAppEphemeralPortsData = new List<FabricResourceUsageData<int>>();
            AllAppHandlesData = new List<FabricResourceUsageData<float>>();
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
                HealthReporter.ReportFabricObserverServiceHealth(
                                FabricServiceContext.ServiceName.OriginalString,
                                ObserverName,
                                HealthState.Warning,
                                "This observer was unable to initialize correctly due to missing configuration info.");

                stopwatch.Stop();
                stopwatch.Reset();

                return;
            }

            await MonitorDeployedAppsAsync(token).ConfigureAwait(false);
            await ReportAsync(token).ConfigureAwait(true);

            // The time it took to run this observer.
            stopwatch.Stop();
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
            token.ThrowIfCancellationRequested();

            if (deployedTargetList.Count == 0)
            {
                return Task.CompletedTask;
            }

            var healthReportTimeToLive = GetHealthReportTimeToLive();

            // App-specific reporting.
            foreach (var app in deployedTargetList)
            {
                token.ThrowIfCancellationRequested();

                // Process data for reporting.
                foreach (var repOrInst in ReplicaOrInstanceList)
                {
                    token.ThrowIfCancellationRequested();

                    if (!string.IsNullOrWhiteSpace(app.TargetAppType)
                        && !string.Equals(
                            repOrInst.ApplicationTypeName,
                            app.TargetAppType,
                            StringComparison.CurrentCultureIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(app.TargetApp)
                        && !string.Equals(
                            repOrInst.ApplicationName.OriginalString,
                            app.TargetApp,
                            StringComparison.CurrentCultureIgnoreCase))
                    {
                        continue;
                    }

                    string processName = null;
                    int processId = 0;

                    try
                    {
                        using Process p = Process.GetProcessById((int)repOrInst.HostProcessId);

                        // If the process is no longer running, then don't report on it.
                        if (p.HasExited)
                        {
                            continue;
                        }

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
                        CsvFileLogger.LogData(
                                fileName,
                                id,
                                "ProcessId",
                                "",
                                processId);

                        // Log resource usage data to CSV files.
                        LogAllAppResourceDataToCsv(id);
                    }

                    // CPU
                    if (AllAppCpuData.Any(x => x.Id == id))
                    {
                        ProcessResourceDataReportHealth(
                            AllAppCpuData.FirstOrDefault(x => x.Id == id),
                            app.CpuErrorLimitPercent,
                            app.CpuWarningLimitPercent,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);
                    }

                    // Memory MB
                    if (AllAppMemDataMb.Any(x => x.Id == id))
                    {
                        ProcessResourceDataReportHealth(
                            AllAppMemDataMb.FirstOrDefault(x => x.Id == id),
                            app.MemoryErrorLimitMb,
                            app.MemoryWarningLimitMb,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);
                    }

                    // Memory Percent
                    if (AllAppMemDataPercent.Any(x => x.Id == id))
                    {
                        ProcessResourceDataReportHealth(
                            AllAppMemDataPercent.FirstOrDefault(x => x.Id == id),
                            app.MemoryErrorLimitPercent,
                            app.MemoryWarningLimitPercent,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);
                    }

                    // TCP Ports - Active
                    if (AllAppTotalActivePortsData.Any(x => x.Id == id))
                    {
                        ProcessResourceDataReportHealth(
                            AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == id),
                            app.NetworkErrorActivePorts,
                            app.NetworkWarningActivePorts,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst);
                    }

                    // TCP Ports - Ephemeral (port numbers fall in the dynamic range)
                    if (AllAppEphemeralPortsData.Any(x => x.Id == id))
                    {
                        ProcessResourceDataReportHealth(
                            AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == id),
                            app.NetworkErrorEphemeralPorts,
                            app.NetworkWarningEphemeralPorts,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst);
                    }

                    // Allocated (in use) Handles
                    if (AllAppHandlesData.Any(x => x.Id == id))
                    {
                        ProcessResourceDataReportHealth(
                            AllAppHandlesData.FirstOrDefault(x => x.Id == id),
                            app.ErrorOpenFileHandles,
                            app.WarningOpenFileHandles,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst);
                    }
                }
            }

            return Task.CompletedTask;
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
            ReplicaOrInstanceList ??= new List<ReplicaOrInstanceMonitoringInfo>();
            
            configSettings.Initialize(
                            FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(
                                ObserverConstants.ObserverConfigurationPackageName)?.Settings,
                                ConfigurationSectionName,
                                "AppObserverDataFileName");
            
            // Unit tests may have null path and filename, thus the null equivalence operations.
            var appObserverConfigFileName = Path.Combine(ConfigPackagePath ?? string.Empty, configSettings.AppObserverConfigFileName ?? string.Empty);

            if (!File.Exists(appObserverConfigFileName))
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                    LogLevel.Information);

                return false;
            }

            // This code runs each time ObserveAsync is called,
            // so clear app list and deployed replica/instance list in case a new app has been added to watch list.
            if (userTargetList.Count > 0)
            {
                userTargetList.Clear();
                ReplicaOrInstanceList.Clear();
            }

            if (deployedTargetList.Count > 0)
            {
                deployedTargetList.Clear();
            }

            await using Stream stream = new FileStream(appObserverConfigFileName, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (stream.Length > 0 && JsonHelper.IsJson<List<ApplicationInfo>>(await File.ReadAllTextAsync(appObserverConfigFileName)))
            {
                userTargetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
            }

            // Are any of the config-supplied apps deployed?.
            if (userTargetList.Count == 0)
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                    LogLevel.Information);

                return false;
            }

            // Support for specifying single configuration item for all or * applications.
            if (userTargetList != null && userTargetList.Any(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*"))
            {
                ApplicationInfo application = userTargetList.Find(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*");

                // Let's make sure that we page through app lists that are huge (like 4MB result set (that's a lot of apps)).
                var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(NodeName)
                {
                    IncludeHealthState = false,
                    MaxResults = 150
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
                }

                foreach (var app in apps)
                {
                    Token.ThrowIfCancellationRequested();
 
                    if (app.ApplicationName.OriginalString == "fabric:/System")
                    {
                        continue;
                    }

                    // App filtering: AppExcludeList, AppIncludeList. This is only useful when you are observing All/* applications for a range of thresholds.
                    if (!string.IsNullOrWhiteSpace(application.AppExcludeList) && application.AppExcludeList.Contains(app.ApplicationName.OriginalString))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(application.AppIncludeList) && !application.AppIncludeList.Contains(app.ApplicationName.OriginalString))
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
                        existingAppConfig.DumpProcessOnError = application.DumpProcessOnError != existingAppConfig.DumpProcessOnError ? application.DumpProcessOnError : existingAppConfig.DumpProcessOnError;
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
                userTargetList.Remove(application);
                apps.Clear();
                apps = null;
            }

            int settingsFail = 0;

            foreach (var application in userTargetList)
            {
                Token.ThrowIfCancellationRequested();

                Uri appUri = null;

                if (string.IsNullOrWhiteSpace(application.TargetApp) && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(
                        FabricServiceContext.ServiceName.ToString(),
                        ObserverName,
                        HealthState.Warning,
                        $"InitializeAsync() | {application.TargetApp}: Required setting, target, is not set.");

                    settingsFail++;
                    continue;
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
                        HealthReporter.ReportFabricObserverServiceHealth(
                            FabricServiceContext.ServiceName.ToString(),
                            ObserverName,
                            HealthState.Warning,
                            $"InitializeAsync() | {application.TargetApp}: Invalid TargetApp value. Value must be a valid Uri string of format \"fabric:/MyApp\", for example.");

                        settingsFail++;
                        continue;
                    }
                }

                // No required settings supplied for deployed application(s).
                if (settingsFail == userTargetList.Count)
                {
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

            foreach (var rep in ReplicaOrInstanceList)
            {
                Token.ThrowIfCancellationRequested();
                
                try
                {
                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring. https://github.com/gittorre/ContainerObserver
                    using Process p = Process.GetProcessById((int)rep.HostProcessId);

                    if (p.ProcessName == "Fabric")
                    {
                        continue;
                    }

                    ObserverLogger.LogInfo($"Will observe resource consumption by {rep.ServiceName?.OriginalString}({rep.HostProcessId}) on Node {NodeName}.");
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
                {
                }
            }

            return true;
        }

        private async Task MonitorDeployedAppsAsync(CancellationToken token)
        {
            Process currentProcess = null;

            foreach (var repOrInst in ReplicaOrInstanceList)
            {
                token.ThrowIfCancellationRequested();

                var timer = new Stopwatch();
                int processId = (int)repOrInst.HostProcessId;
                var cpuUsage = new CpuUsage();
                bool checkCpu = false, checkMemMb = false, checkMemPct = false, checkAllPorts = false, checkEphemeralPorts = false, checkHandles = false;
                var application = deployedTargetList?.Find(
                                    app => app?.TargetApp?.ToLower() == repOrInst.ApplicationName?.OriginalString.ToLower() ||
                                    !string.IsNullOrWhiteSpace(app?.TargetAppType) &&
                                    app.TargetAppType?.ToLower() == repOrInst.ApplicationTypeName?.ToLower());
                
                if (application?.TargetApp == null && application?.TargetAppType == null)
                {
                    continue;
                }

                try
                {
                    // App level.
                    currentProcess = Process.GetProcessById(processId);
                    string procName = currentProcess.ProcessName;

                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring.
                    if (procName == "Fabric")
                    {
                        continue;
                    }

                    string appNameOrType = GetAppNameOrType(repOrInst);
                    string id = $"{appNameOrType}:{procName}";
                    
                    token.ThrowIfCancellationRequested();

                    // Add new resource data structures for each app service process where the metric is specified in configuration for related observation.
                    if (AllAppCpuData.All(list => list.Id != id) && (application.CpuErrorLimitPercent > 0 || application.CpuWarningLimitPercent > 0))
                    {
                        AllAppCpuData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, id, DataCapacity, UseCircularBuffer));
                    }

                    if (AllAppCpuData.Any(list => list.Id == id))
                    {
                        checkCpu = true;
                    }

                    if (AllAppMemDataMb.All(list => list.Id != id) && (application.MemoryErrorLimitMb > 0 || application.MemoryWarningLimitMb > 0))
                    {
                        AllAppMemDataMb.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, id, DataCapacity, UseCircularBuffer));
                    }

                    if (AllAppMemDataMb.Any(list => list.Id == id))
                    {
                        checkMemMb = true;
                    }

                    if (AllAppMemDataPercent.All(list => list.Id != id) && (application.MemoryErrorLimitPercent > 0 || application.MemoryWarningLimitPercent > 0))
                    {
                        AllAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id, DataCapacity, UseCircularBuffer));
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

                    // Measure Total and Ephemeral ports.
                    if (checkAllPorts)
                    {
                        AllAppTotalActivePortsData.FirstOrDefault(x => x.Id == id).Data.Add(OperatingSystemInfoProvider.Instance.GetActiveTcpPortCount(currentProcess.Id, FabricServiceContext));
                    }

                    if (checkEphemeralPorts)
                    {
                        AllAppEphemeralPortsData.FirstOrDefault(x => x.Id == id).Data.Add(OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount(currentProcess.Id, FabricServiceContext));
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

                    // No need to proceed further if no cpu/mem/file handles thresholds are specified in configuration.
                    if (!checkCpu && !checkMemMb && !checkMemPct && !checkHandles)
                    {
                        continue;
                    }

                    /* CPU and Memory Usage */

                    TimeSpan duration = TimeSpan.FromSeconds(3);

                    if (MonitorDuration > TimeSpan.MinValue)
                    {
                        duration = MonitorDuration;
                    }

                    /* Warm up counters. */

                    if (checkCpu)
                    {
                        _ = cpuUsage.GetCpuUsagePercentageProcess(currentProcess);
                    }

                    if (checkHandles)
                    {
                        _ = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(currentProcess.Id, FabricServiceContext);
                    }

                    if (checkMemMb || checkMemPct)
                    {
                        _ = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(currentProcess.Id);
                    }

                    timer.Start();

                    while (!currentProcess.HasExited && timer.Elapsed.Seconds <= duration.Seconds)
                    {
                        token.ThrowIfCancellationRequested();

                        if (checkCpu)
                        {
                            // CPU (all cores).
                            double cpu = cpuUsage.GetCpuUsagePercentageProcess(currentProcess);

                            if (cpu >= 0)
                            {
                                if (cpu > 100)
                                {
                                    cpu = 100;
                                }

                                AllAppCpuData.FirstOrDefault(x => x.Id == id).Data.Add(cpu);
                            }
                        }

                        float processMem = 0;

                        if (checkMemMb || checkMemPct)
                        {
                            processMem = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(currentProcess.Id);
                        }

                        if (checkMemMb)
                        {
                            // Memory (private working set (process)).
                            AllAppMemDataMb.FirstOrDefault(x => x.Id == id).Data.Add(processMem);
                        }

                        if (checkMemPct)
                        {
                            // Memory (percent in use (total)).
                            var (TotalMemory, _) = OperatingSystemInfoProvider.Instance.TupleGetTotalPhysicalMemorySizeAndPercentInUse();

                            if (TotalMemory > 0)
                            {
                                double usedPct = Math.Round((double)(processMem * 100) / (TotalMemory * 1024), 2);
                                AllAppMemDataPercent.FirstOrDefault(x => x.Id == id).Data.Add(Math.Round(usedPct, 1));
                            }
                        }

                        if (checkHandles)
                        {
                            float handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(currentProcess.Id, FabricServiceContext);

                            if (handles > -1)
                            {
                                AllAppHandlesData.FirstOrDefault(x => x.Id == id).Data.Add(handles);
                            }
                        }

                        await Task.Delay(250, Token);
                    }

                    timer.Stop();
                    timer.Reset();
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                {
#if DEBUG
                    WriteToLogWithLevel(
                        ObserverName,
                        $"MonitorDeployedAppsAsync: failed to find current service process or target process is running at a higher privilege than FO for {repOrInst.ApplicationName?.OriginalString ?? repOrInst.ApplicationTypeName}{Environment.NewLine}{e}",
                        LogLevel.Warning);
#endif
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    WriteToLogWithLevel(
                        ObserverName,
                        $"Unhandled exception in MonitorDeployedAppsAsync:{Environment.NewLine}{e}",
                        LogLevel.Warning);

                    // Fix the bug..
                    throw;
                }
                finally
                {
                    currentProcess?.Dispose();
                    currentProcess = null;
                }
            }
        }

        private async Task SetDeployedApplicationReplicaOrInstanceListAsync(Uri applicationNameFilter = null, string applicationType = null)
        {
            var deployedApps = new List<DeployedApplication>();

            if (applicationNameFilter != null)
            {
                var app = await FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(NodeName, applicationNameFilter).ConfigureAwait(false);
                deployedApps.AddRange(app.ToList());
            }
            else if (!string.IsNullOrWhiteSpace(applicationType))
            {
                // Let's make sure that we page through app lists that are huge (like 4MB result set (that's a lot of apps)).
                var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(NodeName)
                {
                    IncludeHealthState = false,
                    MaxResults = 150
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
                deployedApps = appList.ToList();

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

                    deployedApps.AddRange(appList.ToList());
                }

                deployedApps = deployedApps.Where(a => a.ApplicationTypeName == applicationType).ToList();
            }

            foreach (var deployedApp in deployedApps)
            {
                Token.ThrowIfCancellationRequested();

                List<string> filteredServiceList = null;

                // Filter service list if ServiceExcludeList/ServiceIncludeList config setting is non-empty.
                var serviceFilter = userTargetList.Find(x => (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                                || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())
                                                                && (!string.IsNullOrWhiteSpace(x.ServiceExcludeList) || !string.IsNullOrWhiteSpace(x.ServiceIncludeList)));

                ServiceFilterType filterType = ServiceFilterType.None;
                
                if (serviceFilter != null)
                {
                    if (!string.IsNullOrWhiteSpace(serviceFilter.ServiceExcludeList))
                    {
                        filteredServiceList = serviceFilter.ServiceExcludeList.Replace(" ", string.Empty).Split(',').ToList();
                        filterType = ServiceFilterType.Exclude;
                    }
                    else if (!string.IsNullOrWhiteSpace(serviceFilter.ServiceIncludeList))
                    {
                        filteredServiceList = serviceFilter.ServiceIncludeList.Replace(" ", string.Empty).Split(',').ToList();
                        filterType = ServiceFilterType.Include;
                    }
                }

                var replicasOrInstances = await GetDeployedPrimaryReplicaAsync(
                                                    deployedApp.ApplicationName,
                                                    filteredServiceList,
                                                    filterType,
                                                    applicationType).ConfigureAwait(true);

                ReplicaOrInstanceList.AddRange(replicasOrInstances);

                deployedTargetList.AddRange(userTargetList.Where(
                                            x => (x.TargetApp != null || x.TargetAppType != null)
                                                 && (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                     || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())));
            }
        }

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedPrimaryReplicaAsync(
                                                                    Uri appName,
                                                                    List<string> serviceFilterList = null,
                                                                    ServiceFilterType filterType = ServiceFilterType.None,
                                                                    string appTypeName = null)
        {
            var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () => FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName),
                                                Token);

            var replicaMonitoringList = new List<ReplicaOrInstanceMonitoringInfo>();

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
                        IReadOnlyCollection<string> filterList,
                        ServiceFilterType filterType,
                        string appTypeName,
                        DeployedServiceReplicaList deployedReplicaList,
                        ref List<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList)
        {
            foreach (var deployedReplica in deployedReplicaList)
            {
                Token.ThrowIfCancellationRequested();

                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica {ReplicaRole: ReplicaRole.Primary} statefulReplica:
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
                        break;
                    }
                }

                if (replicaInfo != null)
                {
                    replicaMonitoringList.Add(replicaInfo);
                }
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