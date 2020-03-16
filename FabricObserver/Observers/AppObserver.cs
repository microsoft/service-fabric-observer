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
        private readonly List<FabricResourceUsageData<int>> allAppCpuData;
        private readonly List<FabricResourceUsageData<float>> allAppMemDataMb;
        private readonly List<FabricResourceUsageData<double>> allAppMemDataPercent;
        private readonly List<FabricResourceUsageData<int>> allAppTotalActivePortsData;
        private readonly List<FabricResourceUsageData<int>> allAppEphemeralPortsData;
        private WindowsPerfCounters perfCounters;
        private DiskUsage diskUsage;
        private bool disposed;
        private readonly Stopwatch stopwatch;
        private readonly List<ApplicationInfo> targetList;

        public List<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList { get; set; }

        public string ConfigPackagePath { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AppObserver"/> class.
        /// </summary>
        public AppObserver()
            : base(ObserverConstants.AppObserverName)
        {
            this.ConfigPackagePath = ConfigSettings.ConfigPackagePath;
            this.allAppCpuData = new List<FabricResourceUsageData<int>>();
            this.allAppMemDataMb = new List<FabricResourceUsageData<float>>();
            this.allAppMemDataPercent = new List<FabricResourceUsageData<double>>();
            this.allAppTotalActivePortsData = new List<FabricResourceUsageData<int>>();
            this.allAppEphemeralPortsData = new List<FabricResourceUsageData<int>>();
            this.targetList = new List<ApplicationInfo>();
            this.stopwatch = new Stopwatch();
        }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            this.stopwatch.Start();
            bool initialized = this.Initialize();
            this.Token = token;

            if (!initialized)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    "This observer was unable to initialize correctly due to missing configuration info.");

                return;
            }

            try
            {
                this.perfCounters = new WindowsPerfCounters();
                this.diskUsage = new DiskUsage();

                foreach (var app in this.targetList)
                {
                    this.Token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(app.TargetApp)
                        && string.IsNullOrWhiteSpace(app.TargetAppType))
                    {
                        continue;
                    }

                    await this.MonitorAppAsync(app).ConfigureAwait(true);
                }

                // The time it took to get to ReportAsync.
                // For use in computing actual HealthReport TTL.
                this.stopwatch.Stop();
                this.RunDuration = this.stopwatch.Elapsed;
                this.stopwatch.Reset();

                await this.ReportAsync(token).ConfigureAwait(true);
                this.LastRunDateTime = DateTime.Now;
            }
            finally
            {
                // Clean up.
                this.diskUsage?.Dispose();
                this.diskUsage = null;
                this.perfCounters?.Dispose();
                this.perfCounters = null;
            }
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
        private bool Initialize()
        {
            if (this.ReplicaOrInstanceList == null)
            {
                this.ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            }

            if (!IsTestRun)
            {
                ConfigSettings.Initialize(
                    this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(
                        ObserverConstants.ObserverConfigurationPackageName)?.Settings,
                    ObserverConstants.AppObserverConfigurationSectionName,
                    "AppObserverDataFileName");
            }

            var appObserverConfigFileName = Path.Combine(
                this.ConfigPackagePath,
                ConfigSettings.AppObserverDataFileName != null ? ConfigSettings.AppObserverDataFileName : string.Empty);

            if (!File.Exists(appObserverConfigFileName))
            {
                this.WriteToLogWithLevel(
                    this.ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {this.NodeName}",
                    LogLevel.Information);

                return false;
            }

            // this code runs each time ObserveAsync is called,
            // so clear app list and deployed replica/instance list in case a new app has been added to watch list.
            if (this.targetList.Count > 0)
            {
                this.targetList.Clear();
                this.ReplicaOrInstanceList.Clear();
            }

            using (Stream stream = new FileStream(
                appObserverConfigFileName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                if (stream.Length > 0
                    && JsonHelper.IsJson<List<ApplicationInfo>>(File.ReadAllText(appObserverConfigFileName)))
                {
                    this.targetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
                }
            }

            // Are any of the config-supplied apps deployed?.
            if (this.targetList.Count == 0)
            {
                this.WriteToLogWithLevel(
                    this.ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {this.NodeName}",
                    LogLevel.Information);

                return false;
            }

            int settingsFail = 0;

            foreach (var application in this.targetList)
            {
                if (string.IsNullOrWhiteSpace(application.TargetApp)
                    && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.ToString(),
                        this.ObserverName,
                        HealthState.Warning,
                        $"Initialize() | {application.TargetApp}: Required setting, target, is not set.");

                    settingsFail++;

                    continue;
                }

                // No required settings supplied for deployed application(s).
                if (settingsFail == this.targetList.Count)
                {
                    return false;
                }

                this.ObserverLogger.LogInfo(
                    $"Will observe resource consumption by {application.TargetApp ?? application.TargetAppType} " +
                    $"on Node {this.NodeName}.");
            }

            return true;
        }

        private async Task MonitorAppAsync(ApplicationInfo application)
        {
            List<ReplicaOrInstanceMonitoringInfo> repOrInstList;

            if (IsTestRun)
            {
                repOrInstList = ReplicaOrInstanceList;
            }
            else
            {
                if (!string.IsNullOrEmpty(application.TargetAppType))
                {
                    repOrInstList = await this
                        .GetDeployedApplicationReplicaOrInstanceListAsync(null, application.TargetAppType)
                        .ConfigureAwait(true);
                }
                else
                {
                    repOrInstList = await this
                        .GetDeployedApplicationReplicaOrInstanceListAsync(new Uri(application.TargetApp))
                        .ConfigureAwait(true);
                }

                if (repOrInstList.Count == 0)
                {
                    this.ObserverLogger.LogInfo("No targetApp or targetAppType specified.");
                    return;
                }
            }

            Process currentProcess = null;

            foreach (var repOrInst in repOrInstList)
            {
                this.Token.ThrowIfCancellationRequested();

                var timer = new Stopwatch();
                int processId = (int)repOrInst.HostProcessId;
                var cpuUsage = new CpuUsage();

                try
                {
                    // App level.
                    currentProcess = Process.GetProcessById(processId);

                    this.Token.ThrowIfCancellationRequested();

                    var procName = currentProcess.ProcessName;
                    string appNameOrType = GetAppNameOrType(repOrInst);

                    var id = $"{appNameOrType}:{procName}";

                    // Add new resource data structures for each app service process.
                    if (this.allAppCpuData.All(list => list.Id != id))
                    {
                        this.allAppCpuData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, id, DataCapacity, UseCircularBuffer));
                        this.allAppMemDataMb.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.TotalMemoryConsumptionMb, id, DataCapacity, UseCircularBuffer));
                        this.allAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id, DataCapacity, UseCircularBuffer));
                        this.allAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, id, 1));
                        this.allAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, id, 1));
                    }

                    TimeSpan duration = TimeSpan.FromSeconds(15);

                    if (this.MonitorDuration > TimeSpan.MinValue)
                    {
                        duration = this.MonitorDuration;
                    }

                    // Warm up the counters.
                    _ = cpuUsage.GetCpuUsageProcess(currentProcess);
                    _ = this.perfCounters.PerfCounterGetProcessPrivateWorkingSetMb(currentProcess.ProcessName);

                    timer.Start();

                    while (!currentProcess.HasExited && timer.Elapsed <= duration)
                    {
                        this.Token.ThrowIfCancellationRequested();

                        // CPU (all cores).
                        int cpu = cpuUsage.GetCpuUsageProcess(currentProcess);

                        if (cpu >= 0)
                        {
                            this.allAppCpuData.FirstOrDefault(x => x.Id == id).Data.Add(cpu);
                        }

                        // Memory (private working set (process)).
                        var processMem = this.perfCounters.PerfCounterGetProcessPrivateWorkingSetMb(currentProcess.ProcessName);
                        this.allAppMemDataMb.FirstOrDefault(x => x.Id == id).Data.Add(processMem);

                        // Memory (percent in use (total)).
                        var memInfo = ObserverManager.TupleGetTotalPhysicalMemorySizeAndPercentInUse();
                        long totalMem = memInfo.TotalMemory;

                        if (totalMem > -1)
                        {
                            double usedPct = Math.Round(((double)(processMem * 100)) / (totalMem * 1024), 2);
                            this.allAppMemDataPercent.FirstOrDefault(x => x.Id == id).Data.Add(Math.Round(usedPct, 1));
                        }

                        Thread.Sleep(250);
                    }

                    timer.Stop();
                    timer.Reset();

                    // Total and Ephemeral ports..
                    this.allAppTotalActivePortsData.FirstOrDefault(x => x.Id == id)
                        .Data.Add(NetworkUsage.GetActivePortCount(currentProcess.Id));

                    this.allAppEphemeralPortsData.FirstOrDefault(x => x.Id == id)
                        .Data.Add(NetworkUsage.GetActiveEphemeralPortCount(currentProcess.Id));
                }
                catch (Exception e)
                {
                    if (e is Win32Exception || e is ArgumentException || e is InvalidOperationException)
                    {
                        this.WriteToLogWithLevel(
                            this.ObserverName,
                            $"MonitorAsync failed to find current service process for {application.TargetApp}/n{e}",
                            LogLevel.Information);
                    }
                    else
                    {
                        if (!(e is OperationCanceledException))
                        {
                            this.WriteToLogWithLevel(
                                this.ObserverName,
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

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedApplicationReplicaOrInstanceListAsync(
            Uri applicationNameFilter = null,
            string applicationType = null)
        {
            DeployedApplicationList deployedApps;

            if (applicationNameFilter != null)
            {
                deployedApps = await this.FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(this.NodeName, applicationNameFilter).ConfigureAwait(true);
            }
            else
            {
                deployedApps = await this.FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(this.NodeName).ConfigureAwait(true);

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

                var appFilter = this.targetList.Where(x => (x.TargetApp != null || x.TargetAppType != null)
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

                var replicasOrInstances = await this.GetDeployedPrimaryReplicaAsync(
                    deployedApp.ApplicationName,
                    filteredServiceList,
                    filterType,
                    applicationType).ConfigureAwait(true);

                currentReplicaInfoList.AddRange(replicasOrInstances);

                // This is for reporting.
                this.ReplicaOrInstanceList.AddRange(replicasOrInstances);
            }

            return currentReplicaInfoList;
        }

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedPrimaryReplicaAsync(
            Uri appName,
            List<string> serviceFilterList = null,
            ServiceFilterType filterType = ServiceFilterType.None,
            string appTypeName = null)
        {
            var deployedReplicaList = await this.FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(this.NodeName, appName).ConfigureAwait(true);
            var replicaMonitoringList = new List<ReplicaOrInstanceMonitoringInfo>();

            this.SetInstanceOrReplicaMonitoringList(
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

        /// <inheritdoc/>
        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                this.Token.ThrowIfCancellationRequested();
                var healthReportTimeToLive = this.SetHealthReportTimeToLive();

                // App-specific reporting.
                foreach (var app in this.targetList)
                {
                    this.Token.ThrowIfCancellationRequested();

                    // Process data for reporting.
                    foreach (var repOrInst in this.ReplicaOrInstanceList)
                    {
                        this.Token.ThrowIfCancellationRequested();

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
                        catch (ArgumentException)
                        {
                            continue;
                        }
                        catch (InvalidOperationException)
                        {
                            continue;
                        }
                        catch (Win32Exception)
                        {
                            continue;
                        }

                        string appNameOrType = GetAppNameOrType(repOrInst);

                        var id = $"{appNameOrType}:{p.ProcessName}";

                        // Log (csv) CPU/Mem/DiskIO per app.
                        if (this.CsvFileLogger.EnableCsvLogging)
                        {
                            this.LogAllAppResourceDataToCsv(id);
                        }

                        // CPU
                        this.ProcessResourceDataReportHealth(
                            this.allAppCpuData.FirstOrDefault(x => x.Id == id),
                            app.CpuErrorLimitPercent,
                            app.CpuWarningLimitPercent,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        // Memory
                        this.ProcessResourceDataReportHealth(
                            this.allAppMemDataMb.FirstOrDefault(x => x.Id == id),
                            app.MemoryErrorLimitMb,
                            app.MemoryWarningLimitMb,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        this.ProcessResourceDataReportHealth(
                            this.allAppMemDataPercent.FirstOrDefault(x => x.Id == id),
                            app.MemoryErrorLimitPercent,
                            app.MemoryWarningLimitPercent,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        // Ports
                        this.ProcessResourceDataReportHealth(
                            this.allAppTotalActivePortsData.FirstOrDefault(x => x.Id == id),
                            app.NetworkErrorActivePorts,
                            app.NetworkWarningActivePorts,
                            healthReportTimeToLive,
                            HealthReportType.Application,
                            repOrInst);

                        // Ports
                        this.ProcessResourceDataReportHealth(
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
                this.WriteToLogWithLevel(
                    this.ObserverName,
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

        private void LogAllAppResourceDataToCsv(string appName)
        {
            if (!this.CsvFileLogger.EnableCsvLogging && !this.IsTelemetryProviderEnabled)
            {
                return;
            }

            var fileName = $"{appName.Replace(":", string.Empty)}{this.NodeName}";

            // CPU Time
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalCpuTime,
                "Average",
                Math.Round((double)this.allAppCpuData
                    .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            this.CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalCpuTime,
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppCpuData
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            // Memory
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalMemoryConsumptionMb,
                "Average",
                Math.Round((double)this.allAppMemDataMb
                    .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            this.CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalMemoryConsumptionMb,
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppMemDataMb
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            this.CsvFileLogger.LogData(
               fileName,
               appName,
               ErrorWarningProperty.TotalMemoryConsumptionPct,
               "Average",
               Math.Round(this.allAppMemDataPercent
                   .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            this.CsvFileLogger.LogData(
                fileName,
                appName,
                ErrorWarningProperty.TotalMemoryConsumptionPct,
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppMemDataPercent
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            // Network
            this.CsvFileLogger.LogData(
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