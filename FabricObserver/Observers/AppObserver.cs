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
using System.Runtime.Remoting.Channels;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Model;
using FabricObserver.Observers.Utilities;
using FabricObserver.Utilities;

namespace FabricObserver
{
    // This observer monitors the behavior of user SF service processes
    // and signals Warning and Error based on user-supplied resource thresholds
    // in AppObserver.config.json
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    public class AppObserver : ObserverBase
    {
        private readonly string configPackagePath;
        private List<ApplicationInfo> targetList = new List<ApplicationInfo>();

        // Health Report data containers - For use in analysis to determine health state...
        // These lists are cleared after each healthy iteration.
        private List<FabricResourceUsageData<int>> allAppCpuData;
        private List<FabricResourceUsageData<long>> allAppMemDataMB;
        private List<FabricResourceUsageData<double>> allAppMemDataPercent;
        private List<FabricResourceUsageData<int>> allAppTotalActivePortsData;
        private List<FabricResourceUsageData<int>> allAppEphemeralPortsData;
        private List<ReplicaOrInstanceMonitoringInfo> replicaOrInstanceList;
        private WindowsPerfCounters perfCounters = null;
        private DiskUsage diskUsage = null;
        private bool disposed = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="AppObserver"/> class.
        /// </summary>
        public AppObserver()
            : base(ObserverConstants.AppObserverName)
        {
            this.configPackagePath = ConfigSettings.ConfigPackagePath;
            this.allAppCpuData = new List<FabricResourceUsageData<int>>();
            this.allAppMemDataMB = new List<FabricResourceUsageData<long>>();
            this.allAppMemDataPercent = new List<FabricResourceUsageData<double>>();
            this.allAppTotalActivePortsData = new List<FabricResourceUsageData<int>>();
            this.allAppEphemeralPortsData = new List<FabricResourceUsageData<int>>();
        }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example...
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            bool initialized = this.Initialize();
            this.Token = token;

            if (!initialized || token.IsCancellationRequested)
            {
                this.Token.ThrowIfCancellationRequested();

                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    "This observer was unable to initialize correctly due to missing configuration info...");

                return;
            }

            try
            {
                this.perfCounters = new WindowsPerfCounters();
                this.diskUsage = new DiskUsage();

                foreach (var app in this.targetList)
                {
                    this.Token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(app.Target)
                        && string.IsNullOrWhiteSpace(app.TargetType))
                    {
                        continue;
                    }

                    await this.MonitorAppAsync(app).ConfigureAwait(true);
                }

                await this.ReportAsync(token).ConfigureAwait(true);
                this.LastRunDateTime = DateTime.Now;
            }
            finally
            {
                // Clean up...
                this.diskUsage?.Dispose();
                this.diskUsage = null;
                this.perfCounters?.Dispose();
                this.perfCounters = null;
            }
        }

        private static string GetAppNameOrType(ReplicaOrInstanceMonitoringInfo repOrInst)
        {
            string appNameOrType = null;

            // targetType specified as AppTargetType name, which means monitor all apps of specified type...
            if (!string.IsNullOrWhiteSpace(repOrInst.ApplicationTypeName))
            {
                appNameOrType = repOrInst.ApplicationTypeName;
            }
            else
            {
                // target specified as app URI string... (generally the case)
                appNameOrType = repOrInst.ApplicationName.OriginalString.Replace("fabric:/", string.Empty);
            }

            return appNameOrType;
        }

        // Initialize() runs each time ObserveAsync is run to ensure
        // that any new app targets and config changes will
        // be up to date across observer loop iterations...
        private bool Initialize()
        {
            if (this.replicaOrInstanceList == null)
            {
                this.replicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            }

            // Is this a unit test run?
            if (this.IsTestRun)
            {
                this.replicaOrInstanceList.Add(new ReplicaOrInstanceMonitoringInfo
                {
                    ApplicationName = new Uri("fabric:/TestApp"),
                    Partitionid = Guid.NewGuid(),
                    HostProcessId = 0,
                    ReplicaOrInstanceId = default(long),
                });

                return true;
            }

            ConfigSettings.Initialize(this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(ObserverConstants.ObserverConfigurationPackageName)?.Settings, ObserverConstants.AppObserverConfigurationSectionName, "AppObserverDataFileName");
            var appObserverConfigFileName = Path.Combine(this.configPackagePath, ConfigSettings.AppObserverDataFileName);

            if (!File.Exists(appObserverConfigFileName))
            {
                this.WriteToLogWithLevel(
                    this.ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied... | {this.NodeName}",
                    LogLevel.Information);

                return false;
            }

            // this code runs each time ObserveAsync is called,
            // so clear app list and deployed replica/instance list in case a new app has been added to watch list...
            if (this.targetList.Count > 0)
            {
                this.targetList.Clear();
                this.replicaOrInstanceList.Clear();
            }

            using (Stream stream = new FileStream(appObserverConfigFileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 42
                    && JsonHelper.IsJson<List<ApplicationInfo>>(File.ReadAllText(appObserverConfigFileName)))
                {
                    this.targetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
                }
            }

            // Are any of the config-supplied apps deployed?...
            if (this.targetList.Count == 0)
            {
                this.WriteToLogWithLevel(
                    this.ObserverName,
                    $"Will not observe resource consumption as no configuration parameters have been supplied... | {this.NodeName}",
                    LogLevel.Information);

                return false;
            }

            int settingsFail = 0;

            foreach (var application in this.targetList)
            {
                if (string.IsNullOrWhiteSpace(application.Target)
                    && string.IsNullOrWhiteSpace(application.TargetType))
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.ToString(),
                        this.ObserverName,
                        HealthState.Warning,
                        $"Initialize() | {application.Target}: Required setting, target, is not set...");

                    settingsFail++;

                    continue;
                }

                // No required settings supplied for deployed application(s)...
                if (settingsFail == this.targetList.Count)
                {
                    return false;
                }

                this.ObserverLogger.LogInfo(
                    $"Will observe resource consumption by {application.Target ?? application.TargetType} " +
                    $"on Node {this.NodeName}.");
            }

            return true;
        }

        private async Task MonitorAppAsync(ApplicationInfo application)
        {
            List<ReplicaOrInstanceMonitoringInfo> repOrInstList = null;

            if (!string.IsNullOrEmpty(application.TargetType))
            {
                repOrInstList = await this.GetDeployedApplicationReplicaOrInstanceListAsync(null, application.TargetType).ConfigureAwait(true);
            }
            else
            {
                repOrInstList = await this.GetDeployedApplicationReplicaOrInstanceListAsync(new Uri(application.Target)).ConfigureAwait(true);
            }

            if (repOrInstList.Count == 0)
            {
                this.ObserverLogger.LogInfo("No target or targetType specified.");
                return;
            }

            Process currentProcess = null;

            foreach (var repOrInst in repOrInstList)
            {
                this.Token.ThrowIfCancellationRequested();

                int processid = (int)repOrInst.HostProcessId;
                var cpuUsage = new CpuUsage();

                try
                {
                    // App level...
                    currentProcess = Process.GetProcessById(processid);

                    this.Token.ThrowIfCancellationRequested();

                    var procName = currentProcess.ProcessName;
                    string appNameOrType = GetAppNameOrType(repOrInst);

                    var id = $"{appNameOrType}:{procName}";

                    // Add new resource data structures for each app service process...
                    if (!this.allAppCpuData.Any(list => list.Id == id))
                    {
                        this.allAppCpuData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalCpuTime, id));
                        this.allAppMemDataMB.Add(new FabricResourceUsageData<long>(ErrorWarningProperty.TotalMemoryConsumptionMB, id));
                        this.allAppMemDataPercent.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionPct, id));
                        this.allAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalActivePorts, id));
                        this.allAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(ErrorWarningProperty.TotalEphemeralPorts, id));
                    }

                    // CPU (all cores)...
                    int i = Environment.ProcessorCount + 10;

                    while (!currentProcess.HasExited && i > 0)
                    {
                        this.Token.ThrowIfCancellationRequested();

                        int cpu = cpuUsage.GetCpuUsageProcess(currentProcess);

                        if (cpu >= 0)
                        {
                            this.allAppCpuData.FirstOrDefault(x => x.Id == id).Data.Add(cpu);
                        }

                        // Memory (private working set (process))...
                        var mem = this.perfCounters.PerfCounterGetProcessPrivateWorkingSetMB(currentProcess.ProcessName);
                        this.allAppMemDataMB.FirstOrDefault(x => x.Id == id).Data.Add((long)mem);

                        // Memory (percent in use (total))...
                        var memInfo = ObserverManager.TupleGetTotalPhysicalMemorySizeAndPercentInUse();
                        long totalMem = memInfo.Item1;

                        if (totalMem > -1)
                        {
                            double usedPct = Math.Round(((double)(mem * 100)) / (totalMem * 1024), 2);
                            this.allAppMemDataPercent.FirstOrDefault(x => x.Id == id).Data.Add(usedPct);
                        }

                        --i;

                        Thread.Sleep(250);
                    }

                    // Total and Ephemeral ports....
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
                            $"MonitorAsync failed to find current service process for {application.Target}/n{e.ToString()}",
                            LogLevel.Information);

                        continue;
                    }
                    else
                    {
                        if (!(e is OperationCanceledException))
                        {
                            this.WriteToLogWithLevel(
                                this.ObserverName,
                                $"Unhandled exception in MonitorAsync: \n {e.ToString()}",
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
            DeployedApplicationList deployedApps = null;

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
                        var app = deployedApps[i];

                        if (app.ApplicationTypeName?.ToLower() != applicationType.ToLower())
                        {
                            deployedApps.Remove(app);
                        }
                    }
                }
            }

            var currentReplicaInfoList = new List<ReplicaOrInstanceMonitoringInfo>();

            foreach (var deployedApp in deployedApps)
            {
                List<string> filteredServiceList = null;

                var appFilter = this.targetList.Where(x => (x.Target != null || x.TargetType != null)
                                                     && (x.Target?.ToLower() == deployedApp.ApplicationName?.OriginalString.ToLower()
                                                         || x.TargetType?.ToLower() == deployedApp.ApplicationTypeName?.ToLower())
                                                     && (!string.IsNullOrEmpty(x.ServiceExcludeList)
                                                     || !string.IsNullOrEmpty(x.ServiceIncludeList)))?.FirstOrDefault();

                // Filter service list if include/exclude service(s) config setting is supplied...
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

                // This is for reporting...
                this.replicaOrInstanceList.AddRange(replicasOrInstances);
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

            foreach (var deployedReplica in deployedReplicaList)
            {
                if (deployedReplica is DeployedStatefulServiceReplica statefulReplica
                    && statefulReplica.ReplicaRole == ReplicaRole.Primary)
                {
                    var replicaInfo = new ReplicaOrInstanceMonitoringInfo()
                    {
                        ApplicationName = appName,
                        ApplicationTypeName = appTypeName,
                        HostProcessId = statefulReplica.HostProcessId,
                        ReplicaOrInstanceId = statefulReplica.ReplicaId,
                        Partitionid = statefulReplica.Partitionid,
                    };

                    if (serviceFilterList == null
                        || filterType == ServiceFilterType.None)
                    {
                        replicaMonitoringList.Add(replicaInfo);
                        continue;
                    }

                    // Service filtering?...
                    if (filterType == ServiceFilterType.Exclude
                        && !serviceFilterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower())))
                    {
                        replicaMonitoringList.Add(replicaInfo);
                    }
                    else if (filterType == ServiceFilterType.Include
                        && serviceFilterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower())))
                    {
                        replicaMonitoringList.Add(replicaInfo);
                    }
                }
                else if (deployedReplica is DeployedStatelessServiceInstance statelessInstance)
                {
                    var replicaInfo = new ReplicaOrInstanceMonitoringInfo()
                    {
                        ApplicationName = appName,
                        ApplicationTypeName = appTypeName,
                        HostProcessId = statelessInstance.HostProcessId,
                        ReplicaOrInstanceId = statelessInstance.InstanceId,
                        Partitionid = statelessInstance.Partitionid,
                    };

                    if (serviceFilterList == null
                        || filterType == ServiceFilterType.None)
                    {
                        replicaMonitoringList.Add(replicaInfo);
                        continue;
                    }

                    // Service filtering?...
                    if (filterType == ServiceFilterType.Exclude
                        && !serviceFilterList.Any(s => statelessInstance.ServiceName.OriginalString.ToLower().Contains(s.ToLower())))
                    {
                        replicaMonitoringList.Add(replicaInfo);
                    }
                    else if (filterType == ServiceFilterType.Include
                             && serviceFilterList.Any(s => statelessInstance.ServiceName.OriginalString.ToLower().Contains(s.ToLower())))
                    {
                        replicaMonitoringList.Add(replicaInfo);
                    }
                }
            }

            return replicaMonitoringList;
        }

        /// <inheritdoc/>
        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                this.Token.ThrowIfCancellationRequested();
                var timeToLiveWarning = this.SetTimeToLiveWarning();

                // App-specific reporting...
                foreach (var app in this.targetList.Where(x => !string.IsNullOrWhiteSpace(x.Target)
                                                               || !string.IsNullOrWhiteSpace(x.TargetType)))
                {
                    this.Token.ThrowIfCancellationRequested();

                    // Process data for reporting...
                    foreach (var repOrInst in this.replicaOrInstanceList.Where(x => (!string.IsNullOrWhiteSpace(app.Target) && x.ApplicationName.OriginalString == app.Target)
                                                                                    || (!string.IsNullOrWhiteSpace(app.TargetType) && x.ApplicationTypeName == app.TargetType)))
                    {
                        this.Token.ThrowIfCancellationRequested();

                        Process p = null;

                        try
                        {
                            p = Process.GetProcessById((int)repOrInst.HostProcessId);

                            // If the process is no longer running, then don't report on it...
                            if (p != null && p.HasExited)
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

                        // Log (csv) CPU/Mem/DiskIO per app...
                        if (this.CsvFileLogger.EnableCsvLogging || this.IsTelemetryEnabled)
                        {
                            this.LogAllAppResourceDataToCsv(id);
                        }

                        // CPU
                        this.ProcessResourceDataReportHealth(
                            this.allAppCpuData.Where(x => x.Id == id).FirstOrDefault(),
                            app.CpuErrorLimitPct,
                            app.CpuWarningLimitPct,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        // Memory
                        this.ProcessResourceDataReportHealth(
                            this.allAppMemDataMB.Where(x => x.Id == id).FirstOrDefault(),
                            app.MemoryErrorLimitMB,
                            app.MemoryWarningLimitMB,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        this.ProcessResourceDataReportHealth(
                            this.allAppMemDataPercent.Where(x => x.Id == id).FirstOrDefault(),
                            app.MemoryErrorLimitPercent,
                            app.MemoryWarningLimitPercent,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            repOrInst,
                            app.DumpProcessOnError);

                        // Ports
                        this.ProcessResourceDataReportHealth(
                            this.allAppTotalActivePortsData.Where(x => x.Id == id).FirstOrDefault(),
                            app.NetworkErrorActivePorts,
                            app.NetworkWarningActivePorts,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            repOrInst);

                        // Ports
                        this.ProcessResourceDataReportHealth(
                            this.allAppEphemeralPortsData.Where(x => x.Id == id).FirstOrDefault(),
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
                this.WriteToLogWithLevel(
                    this.ObserverName,
                    $"Unhandled exception in ReportAsync: \n{e.ToString()}",
                    LogLevel.Error);

                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }

            if (disposing)
            {
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
        }

        private void LogAllAppResourceDataToCsv(string appName)
        {
            if (!this.CsvFileLogger.EnableCsvLogging && !this.IsTelemetryEnabled)
            {
                return;
            }

            var fileName = appName.Replace(":", string.Empty) +
                           this.NodeName;

            // CPU Time
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "% CPU Time",
                "Average",
                Math.Round(this.allAppCpuData.Where(x => x.Id == appName)
                                                    .FirstOrDefault().AverageDataValue));
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "% CPU Time",
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppCpuData.Where(x => x.Id == appName)
                                                                     .FirstOrDefault().MaxDataValue)));

            // Memory
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "Memory (Working set) MB",
                "Average",
                Math.Round(this.allAppMemDataMB.Where(x => x.Id == appName)
                                                    .FirstOrDefault().AverageDataValue));
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "Memory (Working set) MB",
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppMemDataMB.Where(x => x.Id == appName)
                                                                     .FirstOrDefault().MaxDataValue)));
            this.CsvFileLogger.LogData(
               fileName,
               appName,
               "Memory (Percent in use)",
               "Average",
               Math.Round(this.allAppMemDataPercent.Where(x => x.Id == appName)
                                                   .FirstOrDefault().AverageDataValue));
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "Memory (Percent in use)",
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppMemDataPercent.Where(x => x.Id == appName)
                                                                     .FirstOrDefault().MaxDataValue)));

            // Network
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "Active Ports",
                "Total",
                Math.Round(Convert.ToDouble(this.allAppTotalActivePortsData.Where(x => x.Id == appName)
                                                                   .FirstOrDefault().MaxDataValue)));
            DataTableFileLogger.Flush();
        }
    }
}