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
        private readonly string configPackagePath;
        private List<ApplicationInfo> targetList = new List<ApplicationInfo>();

        // Health Report data containers - For use in analysis to determine health state.
        // These lists are cleared after each healthy iteration.
        private List<FabricResourceUsageData<int>> allAppCpuData;
        private List<FabricResourceUsageData<long>> allAppMemDataMB;
        private List<FabricResourceUsageData<double>> allAppMemDataPercent;
        private List<FabricResourceUsageData<int>> allAppTotalActivePortsData;
        private List<FabricResourceUsageData<int>> allAppEphemeralPortsData;
        private List<ReplicaOrInstanceMonitoringInfo> replicaOrInstanceList;
        private WindowsPerfCounters perfCounters;
        private DiskUsage diskUsage;
        private bool disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="AppObserver"/> class.
        /// </summary>
        public AppObserver()
            : base(ObserverConstants.AppObserverName)
        {
            configPackagePath = ConfigSettings.ConfigPackagePath;
            allAppCpuData = new List<FabricResourceUsageData<int>>();
            allAppMemDataMB = new List<FabricResourceUsageData<long>>();
            allAppMemDataPercent = new List<FabricResourceUsageData<double>>();
            allAppTotalActivePortsData = new List<FabricResourceUsageData<int>>();
            allAppEphemeralPortsData = new List<FabricResourceUsageData<int>>();
        }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            bool initialized = Initialize();
            Token = token;

            if (!initialized)
            {
                HealthReporter.ReportFabricObserverServiceHealth(
                    FabricServiceContext.ServiceName.OriginalString,
                    ObserverName,
                    HealthState.Warning,
                    "This observer was unable to initialize correctly due to missing configuration info.");

                return;
            }

            try
            {
                perfCounters = new WindowsPerfCounters();
                diskUsage = new DiskUsage();

                foreach (var app in targetList)
                {
                    Token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(app.Target)
                        && string.IsNullOrWhiteSpace(app.TargetType))
                    {
                        continue;
                    }

                    await MonitorAppAsync(app).ConfigureAwait(true);
                }

                await ReportAsync(token).ConfigureAwait(true);
                LastRunDateTime = DateTime.Now;
            }
            finally
            {
                // Clean up.
                diskUsage?.Dispose();
                diskUsage = null;
                perfCounters?.Dispose();
                perfCounters = null;
            }
        }

        private static string GetAppNameOrType(ReplicaOrInstanceMonitoringInfo repOrInst)
        {
            // targetType specified as AppTargetType name, which means monitor all apps of specified type.
            var appNameOrType = !string.IsNullOrWhiteSpace(repOrInst.ApplicationTypeName) ? repOrInst.ApplicationTypeName : repOrInst.ApplicationName.OriginalString.Replace("fabric:/", string.Empty);

            return appNameOrType;
        }

        // Initialize() runs each time ObserveAsync is run to ensure
        // that any new app targets and config changes will
        // be up to date across observer loop iterations.
        private bool Initialize()
        {
            if (replicaOrInstanceList == null)
            {
                replicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            }

            // Is this a unit test run?
            if (IsTestRun)
            {
                replicaOrInstanceList.Add(new ReplicaOrInstanceMonitoringInfo
                {
                    ApplicationName = new Uri("fabric:/TestApp"),
                    PartitionId = Guid.NewGuid(),
                    HostProcessId = 0,
                    ReplicaOrInstanceId = default(long),
                });

                return true;
            }

            ConfigSettings.Initialize(FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(ObserverConstants.ObserverConfigurationPackageName)?.Settings, ObserverConstants.AppObserverConfigurationSectionName, "AppObserverDataFileName");
            var appObserverConfigFileName = Path.Combine(configPackagePath, ConfigSettings.AppObserverDataFileName);

            if (!File.Exists(appObserverConfigFileName))
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                    LogLevel.Information);

                return false;
            }

            // this code runs each time ObserveAsync is called,
            // so clear app list and deployed replica/instance list in case a new app has been added to watch list.
            if (targetList.Count > 0)
            {
                targetList.Clear();
                replicaOrInstanceList.Clear();
            }

            using (Stream stream = new FileStream(appObserverConfigFileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 42
                    && JsonHelper.IsJson<List<ApplicationInfo>>(File.ReadAllText(appObserverConfigFileName)))
                {
                    targetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
                }
            }

            // Are any of the config-supplied apps deployed?.
            if (targetList.Count == 0)
            {
                WriteToLogWithLevel(
                    ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied. | {NodeName}",
                    LogLevel.Information);

                return false;
            }

            int settingsFail = 0;

            foreach (var application in targetList)
            {
                if (string.IsNullOrWhiteSpace(application.Target)
                    && string.IsNullOrWhiteSpace(application.TargetType))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(
                        FabricServiceContext.ServiceName.ToString(),
                        ObserverName,
                        HealthState.Warning,
                        $"Initialize() | {application.Target}: Required setting, target, is not set.");

                    settingsFail++;

                    continue;
                }

                // No required settings supplied for deployed application(s).
                if (settingsFail == targetList.Count)
                {
                    return false;
                }

                ObserverLogger.LogInfo(
                    $"Will observe resource consumption by {application.Target ?? application.TargetType} " +
                    $"on Node {NodeName}.");
            }

            return true;
        }

        private async Task MonitorAppAsync(ApplicationInfo application)
        {
            List<ReplicaOrInstanceMonitoringInfo> repOrInstList;

            if (!string.IsNullOrEmpty(application.TargetType))
            {
                repOrInstList = await GetDeployedApplicationReplicaOrInstanceListAsync(null, application.TargetType).ConfigureAwait(true);
            }
            else
            {
                repOrInstList = await GetDeployedApplicationReplicaOrInstanceListAsync(new Uri(application.Target)).ConfigureAwait(true);
            }

            if (repOrInstList.Count == 0)
            {
                ObserverLogger.LogInfo("No target or targetType specified.");
                return;
            }

            Process currentProcess = null;

            foreach (var repOrInst in repOrInstList)
            {
                Token.ThrowIfCancellationRequested();

                int processid = (int)repOrInst.HostProcessId;
                var cpuUsage = new CpuUsage();

                try
                {
                    // App level.
                    currentProcess = Process.GetProcessById(processid);

                    Token.ThrowIfCancellationRequested();

                    var procName = currentProcess.ProcessName;
                    string appNameOrType = GetAppNameOrType(repOrInst);

                    var id = $"{appNameOrType}:{procName}";

                    // Add new resource data structures for each app service process.
                    if (!allAppCpuData.Any(list => list.Id == id))
                    {
                        allAppCpuData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, id));
                        allAppMemDataMB.Add(new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionMb, id));
                        allAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id));
                        allAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, id));
                        allAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, id));
                    }

                    // CPU (all cores).
                    int i = Environment.ProcessorCount + 10;

                    while (!currentProcess.HasExited && i > 0)
                    {
                        Token.ThrowIfCancellationRequested();

                        int cpu = cpuUsage.GetCpuUsageProcess(currentProcess);

                        if (cpu >= 0)
                        {
                            allAppCpuData.FirstOrDefault(x => x.Id == id).Data.Add(cpu);
                        }

                        // Memory (private working set (process)).
                        var processMem = perfCounters.PerfCounterGetProcessPrivateWorkingSetMb(currentProcess.ProcessName);
                        allAppMemDataMB.FirstOrDefault(x => x.Id == id).Data.Add((long)processMem);

                        // Memory (percent in use (total)).
                        var memInfo = ObserverManager.TupleGetTotalPhysicalMemorySizeAndPercentInUse();
                        long totalMem = memInfo.TotalMemory;

                        if (totalMem > -1)
                        {
                            double usedPct = Math.Round(((double)(processMem * 100)) / (totalMem * 1024), 2);
                            allAppMemDataPercent.FirstOrDefault(x => x.Id == id).Data.Add(usedPct);
                        }

                        --i;

                        Thread.Sleep(250);
                    }

                    // Total and Ephemeral ports..
                    allAppTotalActivePortsData.FirstOrDefault(x => x.Id == id)
                        .Data.Add(NetworkUsage.GetActivePortCount(currentProcess.Id));

                    allAppEphemeralPortsData.FirstOrDefault(x => x.Id == id)
                        .Data.Add(NetworkUsage.GetActiveEphemeralPortCount(currentProcess.Id));
                }
                catch (Exception e)
                {
                    if (e is Win32Exception || e is ArgumentException || e is InvalidOperationException)
                    {
                        WriteToLogWithLevel(
                            ObserverName,
                            $"MonitorAsync failed to find current service process for {application.Target}/n{e}",
                            LogLevel.Information);
                    }
                    else
                    {
                        if (!(e is OperationCanceledException))
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

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedApplicationReplicaOrInstanceListAsync(
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
                        var app = deployedApps[i];

                        if (app.ApplicationTypeName?.ToLower() != applicationType.ToLower())
                        {
                            _ = deployedApps.Remove(app);
                        }
                    }
                }
            }

            var currentReplicaInfoList = new List<ReplicaOrInstanceMonitoringInfo>();

            foreach (var deployedApp in deployedApps)
            {
                List<string> filteredServiceList = null;

                var appFilter = targetList.Where(x => (x.Target != null || x.TargetType != null)
                                                     && (x.Target?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                         || x.TargetType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())
                                                     && (!string.IsNullOrEmpty(x.ServiceExcludeList)
                                                     || !string.IsNullOrEmpty(x.ServiceIncludeList)))
                    .FirstOrDefault();

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

                currentReplicaInfoList.AddRange(replicasOrInstances);

                // This is for reporting.
                replicaOrInstanceList.AddRange(replicasOrInstances);
            }

            return currentReplicaInfoList;
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
                    };

                    if (serviceFilterList != null
                        && filterType != ServiceFilterType.None)
                    {
                        bool isInFilterList = serviceFilterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                        switch (filterType)
                        {
                            // Include
                            case ServiceFilterType.Include when !isInFilterList:
                            // Exclude
                            case ServiceFilterType.Exclude when isInFilterList:
                                continue;
                            case ServiceFilterType.None:
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(filterType), filterType, null);
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

                        // Include
                        if (filterType == ServiceFilterType.Include
                            && !isInFilterList)
                        {
                            continue;
                        }

                        // Exclude
                        if (filterType == ServiceFilterType.Exclude
                            && isInFilterList)
                        {
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
                Token.ThrowIfCancellationRequested();
                var timeToLiveWarning = SetTimeToLiveWarning();

                // App-specific reporting.
                foreach (var app in targetList.Where(x => !string.IsNullOrWhiteSpace(x.Target)
                                                               || !string.IsNullOrWhiteSpace(x.TargetType)))
                {
                    Token.ThrowIfCancellationRequested();

                    // Process data for reporting.
                    foreach (var repOrInst in replicaOrInstanceList.Where(x => (!string.IsNullOrWhiteSpace(app.Target) && x.ApplicationName.OriginalString == app.Target)
                                                                                    || (!string.IsNullOrWhiteSpace(app.TargetType) && x.ApplicationTypeName == app.TargetType)))
                    {
                        Token.ThrowIfCancellationRequested();

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
                        if (CsvFileLogger.EnableCsvLogging || IsTelemetryEnabled)
                        {
                            LogAllAppResourceDataToCsv(id);
                        }

                        // CPU
                        ProcessResourceDataReportHealth(
                            allAppCpuData.Where(x => x.Id == id).FirstOrDefault(),
                            app.CpuErrorLimitPct,
                            app.CpuWarningLimitPct,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        // Memory
                        ProcessResourceDataReportHealth(
                            allAppMemDataMB.FirstOrDefault(x => x.Id == id),
                            app.MemoryErrorLimitMb,
                            app.MemoryWarningLimitMb,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        ProcessResourceDataReportHealth(
                            allAppMemDataPercent.FirstOrDefault(x => x.Id == id),
                            app.MemoryErrorLimitPercent,
                            app.MemoryWarningLimitPercent,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        // Ports
                        ProcessResourceDataReportHealth(
                            allAppTotalActivePortsData.FirstOrDefault(x => x.Id == id),
                            app.NetworkErrorActivePorts,
                            app.NetworkWarningActivePorts,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            repOrInst);

                        // Ports
                        ProcessResourceDataReportHealth(
                            allAppEphemeralPortsData.FirstOrDefault(x => x.Id == id),
                            app.NetworkErrorEphemeralPorts,
                            app.NetworkWarningEphemeralPorts,
                            timeToLiveWarning,
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
            if (disposed || !disposing)
            {
                return;
            }

            if (perfCounters != null)
            {
                perfCounters.Dispose();
                perfCounters = null;
            }

            if (diskUsage != null)
            {
                diskUsage.Dispose();
                diskUsage = null;
            }

            disposed = true;
        }

        private void LogAllAppResourceDataToCsv(string appName)
        {
            if (!CsvFileLogger.EnableCsvLogging && !IsTelemetryEnabled)
            {
                return;
            }

            var fileName = $"{appName.Replace(":", string.Empty)}{NodeName}";

            // CPU Time
            CsvFileLogger.LogData(
                fileName,
                appName,
                "% CPU Time",
                "Average",
                Math.Round(allAppCpuData
                    .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            CsvFileLogger.LogData(
                fileName,
                appName,
                "% CPU Time",
                "Peak",
                Math.Round(Convert.ToDouble(allAppCpuData
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            // Memory
            CsvFileLogger.LogData(
                fileName,
                appName,
                "Memory (Working set) MB",
                "Average",
                Math.Round(allAppMemDataMB
                    .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            CsvFileLogger.LogData(
                fileName,
                appName,
                "Memory (Working set) MB",
                "Peak",
                Math.Round(Convert.ToDouble(allAppMemDataMB
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            CsvFileLogger.LogData(
               fileName,
               appName,
               "Memory (Percent in use)",
               "Average",
               Math.Round(allAppMemDataPercent
                   .FirstOrDefault(x => x.Id == appName).AverageDataValue));

            CsvFileLogger.LogData(
                fileName,
                appName,
                "Memory (Percent in use)",
                "Peak",
                Math.Round(Convert.ToDouble(allAppMemDataPercent
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            // Network
            CsvFileLogger.LogData(
                fileName,
                appName,
                "Active Ports",
                "Total",
                Math.Round(Convert.ToDouble(allAppTotalActivePortsData
                    .FirstOrDefault(x => x.Id == appName).MaxDataValue)));

            DataTableFileLogger.Flush();
        }
    }
}