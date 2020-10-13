// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;

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
        private readonly List<FabricResourceUsageData<double>> allAppCpuData;
        private readonly List<FabricResourceUsageData<float>> allAppMemDataMb;
        private readonly List<FabricResourceUsageData<double>> allAppMemDataPercent;
        private readonly List<FabricResourceUsageData<int>> allAppTotalActivePortsData;
        private readonly List<FabricResourceUsageData<int>> allAppEphemeralPortsData;
        private readonly Stopwatch stopwatch;

        // userTargetList is the list of ApplicationInfo objects representing app/app types supplied in configuration.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        private List<ApplicationInfo> deployedTargetList;
        private bool disposed;

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
        public AppObserver()
        {
            ConfigPackagePath = MachineInfoModel.ConfigSettings.ConfigPackagePath;
            this.allAppCpuData = new List<FabricResourceUsageData<double>>();
            this.allAppMemDataMb = new List<FabricResourceUsageData<float>>();
            this.allAppMemDataPercent = new List<FabricResourceUsageData<double>>();
            this.allAppTotalActivePortsData = new List<FabricResourceUsageData<int>>();
            this.allAppEphemeralPortsData = new List<FabricResourceUsageData<int>>();
            this.userTargetList = new List<ApplicationInfo>();
            this.deployedTargetList = new List<ApplicationInfo>();
            this.stopwatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            this.stopwatch.Start();
            bool initialized = await InitializeAsync();
            Token = token;

            if (!initialized)
            {
                HealthReporter.ReportFabricObserverServiceHealth(
                    FabricServiceContext.ServiceName.OriginalString,
                    ObserverName,
                    HealthState.Warning,
                    "This observer was unable to initialize correctly due to missing configuration info.");

                this.stopwatch.Stop();
                this.stopwatch.Reset();

                return;
            }

            await MonitorDeployedAppsAsync(token).ConfigureAwait(false);

            // The time it took to get to ReportAsync.
            // For use in computing actual HealthReport TTL.
            this.stopwatch.Stop();
            RunDuration = this.stopwatch.Elapsed;
            this.stopwatch.Reset();

            await ReportAsync(token).ConfigureAwait(true);
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                if (this.deployedTargetList.Count == 0)
                {
                    return Task.CompletedTask;
                }

                var healthReportTimeToLive = SetHealthReportTimeToLive();

                // App-specific reporting.
                foreach (var app in this.deployedTargetList)
                {
                    token.ThrowIfCancellationRequested();

                    // Process data for reporting.
                    foreach (var repOrInst in ReplicaOrInstanceList)
                    {
                        token.ThrowIfCancellationRequested();

                        if (!string.IsNullOrEmpty(app.TargetAppType)
                            && !string.Equals(
                                repOrInst.ApplicationTypeName,
                                app.TargetAppType,
                                StringComparison.CurrentCultureIgnoreCase))
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(app.TargetApp)
                            && !string.Equals(
                                repOrInst.ApplicationName.OriginalString,
                                app.TargetApp,
                                StringComparison.CurrentCultureIgnoreCase))
                        {
                            continue;
                        }

                        Process p;

                        try
                        {
                            p = Process.GetProcessById((int)repOrInst.HostProcessId);

                            // If the process is no longer running, then don't report on it.
                            if (p.HasExited)
                            {
                                continue;
                            }
                        }
                        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                        {
                            continue;
                        }

                        string appNameOrType = GetAppNameOrType(repOrInst);

                        var id = $"{appNameOrType}:{p.ProcessName}";

                        // Log (csv) CPU/Mem/DiskIO per app.
                        if (CsvFileLogger != null && CsvFileLogger.EnableCsvLogging)
                        {
                            LogAllAppResourceDataToCsv(id);
                        }

                        // CPU
                        ProcessResourceDataReportHealth(
                            this.allAppCpuData.FirstOrDefault(x => x.Id == id),
                            app.CpuErrorLimitPercent,
                            app.CpuWarningLimitPercent,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        // Memory
                        ProcessResourceDataReportHealth(
                            this.allAppMemDataMb.FirstOrDefault(x => x.Id == id),
                            app.MemoryErrorLimitMb,
                            app.MemoryWarningLimitMb,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        ProcessResourceDataReportHealth(
                            this.allAppMemDataPercent.FirstOrDefault(x => x.Id == id),
                            app.MemoryErrorLimitPercent,
                            app.MemoryWarningLimitPercent,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        // Ports
                        ProcessResourceDataReportHealth(
                            this.allAppTotalActivePortsData.FirstOrDefault(x => x.Id == id),
                            app.NetworkErrorActivePorts,
                            app.NetworkWarningActivePorts,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst);

                        // Ports
                        ProcessResourceDataReportHealth(
                            this.allAppEphemeralPortsData.FirstOrDefault(x => x.Id == id),
                            app.NetworkErrorEphemeralPorts,
                            app.NetworkWarningEphemeralPorts,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst);
                    }
                }

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Unhandled exception in ReportAsync: \n{e}",
                    LogLevel.Error);

                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (this.disposed || !disposing)
            {
                return;
            }

            this.disposed = true;
        }

        private static string GetAppNameOrType(ReplicaOrInstanceMonitoringInfo repOrInst)
        {
            // targetType specified as TargetAppType name, which means monitor all apps of specified type.
            var appNameOrType = !string.IsNullOrWhiteSpace(repOrInst.ApplicationTypeName) ? repOrInst.ApplicationTypeName : repOrInst.ApplicationName.OriginalString.Replace("fabric:/", string.Empty);

            return appNameOrType;
        }

        // Initialize() runs each time ObserveAsync is run to ensure
        // that any new app targets and config changes will
        // be up to date across observer loop iterations.
        private async Task<bool> InitializeAsync()
        {
            if (ReplicaOrInstanceList == null)
            {
                ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            }

            if (!IsTestRun)
            {
                MachineInfoModel.ConfigSettings.Initialize(
                    FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(
                        ObserverConstants.ObserverConfigurationPackageName)?.Settings,
                    ConfigurationSectionName,
                    "AppObserverDataFileName");
            }

            // For unit tests, this path will be an empty string and not generate an exception.
            var appObserverConfigFileName = Path.Combine(
                ConfigPackagePath ?? string.Empty,
                MachineInfoModel.ConfigSettings.AppObserverConfigFileName ?? string.Empty);

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
            if (this.userTargetList.Count > 0)
            {
                this.userTargetList.Clear();
                ReplicaOrInstanceList.Clear();
            }

            if (this.deployedTargetList.Count > 0)
            {
                this.deployedTargetList.Clear();
            }

            using Stream stream = new FileStream(
                appObserverConfigFileName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            if (stream.Length > 0
                && JsonHelper.IsJson<List<ApplicationInfo>>(File.ReadAllText(appObserverConfigFileName)))
            {
                this.userTargetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
            }

            // Are any of the config-supplied apps deployed?.
            if (this.userTargetList.Count == 0)
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                    LogLevel.Information);

                return false;
            }

            int settingSFail = 0;

            foreach (var application in this.userTargetList)
            {
                if (string.IsNullOrWhiteSpace(application.TargetApp)
                    && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(
                        FabricServiceContext.ServiceName.ToString(),
                        ObserverName,
                        HealthState.Warning,
                        $"Initialize() | {application.TargetApp}: Required setting, target, is not set.");

                    settingSFail++;

                    continue;
                }

                // No required settings supplied for deployed application(s).
                if (settingSFail == this.userTargetList.Count)
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(application.TargetAppType))
                {
                    await SetDeployedApplicationReplicaOrInstanceListAsync(
                        null,
                        application.TargetAppType).ConfigureAwait(false);
                }
                else
                {
                    await SetDeployedApplicationReplicaOrInstanceListAsync(new Uri(application.TargetApp))
                        .ConfigureAwait(false);
                }
            }

            foreach (var repOrInst in ReplicaOrInstanceList)
            {
                ObserverLogger.LogInfo(
                    $"Will observe resource consumption by {repOrInst.ApplicationName?.OriginalString} " +
                    $"on Node {NodeName}.");
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

                try
                {
                    // App level.
                    currentProcess = Process.GetProcessById(processId);

                    token.ThrowIfCancellationRequested();

                    var procName = currentProcess.ProcessName;
                    string appNameOrType = GetAppNameOrType(repOrInst);

                    var id = $"{appNameOrType}:{procName}";

                    // Add new resource data structures for each app service process.
                    if (this.allAppCpuData.All(list => list.Id != id))
                    {
                        this.allAppCpuData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, id, DataCapacity, UseCircularBuffer));
                        this.allAppMemDataMb.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, id, DataCapacity, UseCircularBuffer));
                        this.allAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id, DataCapacity, UseCircularBuffer));
                        this.allAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, id, 1));
                        this.allAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, id, 1));
                    }

                    TimeSpan duration = TimeSpan.FromSeconds(15);

                    if (MonitorDuration > TimeSpan.MinValue)
                    {
                        duration = MonitorDuration;
                    }

                    // Warm up the counters.
                    _ = cpuUsage.GetCpuUsagePercentageProcess(currentProcess);
                    _ = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(currentProcess.Id);

                    timer.Start();

                    while (!currentProcess.HasExited && timer.Elapsed.Seconds <= duration.Seconds)
                    {
                        token.ThrowIfCancellationRequested();

                        // CPU (all cores).
                        double cpu = cpuUsage.GetCpuUsagePercentageProcess(currentProcess);

                        if (cpu >= 0)
                        {
                            if (cpu > 100)
                            {
                                cpu = 100;
                            }

                            this.allAppCpuData.FirstOrDefault(x => x.Id == id).Data.Add(cpu);
                        }

                        // Memory (private working set (process)).
                        var processMem = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(currentProcess.Id);
                        this.allAppMemDataMb.FirstOrDefault(x => x.Id == id).Data.Add(processMem);

                        // Memory (percent in use (total)).
                        var (TotalMemory, PercentInUse) = OperatingSystemInfoProvider.Instance.TupleGetTotalPhysicalMemorySizeAndPercentInUse();
                        long totalMem = TotalMemory;

                        if (totalMem > -1)
                        {
                            double usedPct = Math.Round(((double)(processMem * 100)) / (totalMem * 1024), 2);
                            this.allAppMemDataPercent.FirstOrDefault(x => x.Id == id).Data.Add(Math.Round(usedPct, 1));
                        }

                        await Task.Delay(250, Token);
                    }

                    timer.Stop();
                    timer.Reset();

                    // Total and Ephemeral ports..
                    this.allAppTotalActivePortsData.FirstOrDefault(x => x.Id == id)
                        .Data.Add(OperatingSystemInfoProvider.Instance.GetActivePortCount(currentProcess.Id, FabricServiceContext));

                    this.allAppEphemeralPortsData.FirstOrDefault(x => x.Id == id)
                        .Data.Add(OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount(currentProcess.Id, FabricServiceContext));
                }
                catch (Exception e)
                {
#if DEBUG
                    // DEBUG INFO
                    var healthReport = new Utilities.HealthReport
                    {
                        AppName = repOrInst.ApplicationName,
                        HealthMessage = $"Error: {e}\n\n",
                        State = HealthState.Ok,
                        Code = FOErrorWarningCodes.Ok,
                        NodeName = NodeName,
                        Observer = ObserverName,
                        Property = $"{e.Source}",
                        ReportType = HealthReportType.Application,
                    };

                    HealthReporter.ReportHealthToServiceFabric(healthReport);
#endif
                    if (e is Win32Exception || e is ArgumentException || e is InvalidOperationException)
                    {
                        WriteToLogWithLevel(
                            ObserverName,
                            $"MonitorAsync failed to find current service process for {repOrInst.ApplicationName?.OriginalString ?? repOrInst.ApplicationTypeName}/n{e}",
                            LogLevel.Information);
                    }
                    else
                    {
                        if (!(e is OperationCanceledException || e is TaskCanceledException))
                        {
                            WriteToLogWithLevel(
                                ObserverName,
                                $"Unhandled exception in MonitorAsync: \n {e}",
                                LogLevel.Warning);
                        }

                        throw;
                    }
                }
                finally
                {
                    currentProcess?.Dispose();
                    currentProcess = null;
                }
            }
        }

        private async Task SetDeployedApplicationReplicaOrInstanceListAsync(
            Uri applicationNameFilter = null,
            string applicationType = null)
        {
            DeployedApplicationList deployedApps;

            if (applicationNameFilter != null)
            {
                deployedApps = await FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(NodeName, applicationNameFilter).ConfigureAwait(true);
            }
            else
            {
                deployedApps = await FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(NodeName).ConfigureAwait(true);

                if (deployedApps.Count > 0 && !string.IsNullOrEmpty(applicationType))
                {
                    for (int i = 0; i < deployedApps.Count; i++)
                    {
                        if (deployedApps[i].ApplicationTypeName == applicationType)
                        {
                            continue;
                        }

                        deployedApps.Remove(deployedApps[i]);
                        --i;
                    }
                }
            }

            var currentReplicaInfoList = new List<ReplicaOrInstanceMonitoringInfo>();

            foreach (var deployedApp in deployedApps)
            {
                List<string> filteredServiceList = null;

                var appFilter = this.userTargetList.Where(x => (x.TargetApp != null || x.TargetAppType != null)
                                                           && (x.TargetApp?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                               || x.TargetAppType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())
                                                           && (!string.IsNullOrEmpty(x.ServiceExcludeList)
                                                               || !string.IsNullOrEmpty(x.ServiceIncludeList)))?.FirstOrDefault();

                // Filter service list if include/exclude service(s) config setting is supplied.
                var filterType = ServiceFilterType.None;

                if (appFilter != null)
                {
                    if (!string.IsNullOrEmpty(appFilter.ServiceExcludeList))
                    {
                        filteredServiceList = appFilter.ServiceExcludeList.Split(',').ToList();
                        filterType = ServiceFilterType.Exclude;
                    }
                    else if (!string.IsNullOrEmpty(appFilter.ServiceIncludeList))
                    {
                        filteredServiceList = appFilter.ServiceIncludeList.Split(',').ToList();
                        filterType = ServiceFilterType.Include;
                    }
                }

                var replicasOrInstances = await GetDeployedPrimaryReplicaAsync(
                    deployedApp.ApplicationName,
                    filteredServiceList,
                    filterType,
                    applicationType).ConfigureAwait(true);

                ReplicaOrInstanceList.AddRange(replicasOrInstances);

                this.deployedTargetList.AddRange(this.userTargetList.Where(
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
            var deployedReplicaList = await FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName).ConfigureAwait(true);
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
            List<string> serviceFilterList,
            ServiceFilterType filterType,
            string appTypeName,
            DeployedServiceReplicaList deployedReplicaList,
            ref List<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList)
        {
            foreach (var deployedReplica in deployedReplicaList)
            {
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                if (deployedReplica is DeployedStatefulServiceReplica statefulReplica
                    && statefulReplica.ReplicaRole == ReplicaRole.Primary)
                {
                    replicaInfo = new ReplicaOrInstanceMonitoringInfo()
                    {
                        ApplicationName = appName,
                        ApplicationTypeName = appTypeName,
                        HostProcessId = statefulReplica.HostProcessId,
                        ReplicaOrInstanceId = statefulReplica.ReplicaId,
                        PartitionId = statefulReplica.Partitionid,
                        ServiceName = statefulReplica.ServiceName,
                    };

                    if (serviceFilterList != null
                        && filterType != ServiceFilterType.None)
                    {
                        bool isInFilterList = serviceFilterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                        switch (filterType)
                        {
                            case ServiceFilterType.Include when !isInFilterList:
                            case ServiceFilterType.Exclude when isInFilterList:
                                continue;
                        }
                    }
                }
                else if (deployedReplica is DeployedStatelessServiceInstance statelessInstance)
                {
                    replicaInfo = new ReplicaOrInstanceMonitoringInfo()
                    {
                        ApplicationName = appName,
                        ApplicationTypeName = appTypeName,
                        HostProcessId = statelessInstance.HostProcessId,
                        ReplicaOrInstanceId = statelessInstance.InstanceId,
                        PartitionId = statelessInstance.Partitionid,
                        ServiceName = statelessInstance.ServiceName,
                    };

                    if (serviceFilterList != null
                        && filterType != ServiceFilterType.None)
                    {
                        bool isInFilterList = serviceFilterList.Any(s => statelessInstance.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                        switch (filterType)
                        {
                            case ServiceFilterType.Include when !isInFilterList:
                            case ServiceFilterType.Exclude when isInFilterList:
                                continue;
                        }
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
            if (!CsvFileLogger.EnableCsvLogging && !IsTelemetryProviderEnabled)
            {
                return;
            }

            var fileName = $"{appName.Replace(":", string.Empty)}{NodeName}";

            // CPU Time
            CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalCpuTime,
                "Average",
                Math.Round((double)this.allAppCpuData
                    .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalCpuTime,
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppCpuData
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            // Memory
            CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalMemoryConsumptionMb,
                "Average",
                Math.Round(this.allAppMemDataMb
                    .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalMemoryConsumptionMb,
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppMemDataMb
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            CsvFileLogger.LogData(
               fileName,
               appName,
               ErrorWarningProperty.TotalMemoryConsumptionPct,
               "Average",
               Math.Round(this.allAppMemDataPercent
                   .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalMemoryConsumptionPct,
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppMemDataPercent
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            // Network
            CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalActivePorts,
                "Total",
                Math.Round(Convert.ToDouble(this.allAppTotalActivePortsData
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            DataTableFileLogger.Flush();
        }
    }
}