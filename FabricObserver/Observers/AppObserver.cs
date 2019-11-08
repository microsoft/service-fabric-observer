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
using FabricObserver.Model;
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
        private List<FabricResourceUsageData<float>> allAppDiskReadsData;
        private List<FabricResourceUsageData<float>> allAppDiskWritesData;
        private List<FabricResourceUsageData<int>> allAppTotalActivePortsData;
        private List<FabricResourceUsageData<int>> allAppEphemeralPortsData;
        private List<ReplicaMonitoringInfo> replicaOrInstanceList;
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
            this.allAppDiskReadsData = new List<FabricResourceUsageData<float>>();
            this.allAppDiskWritesData = new List<FabricResourceUsageData<float>>();
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

        // Initialize() runs each time ObserveAsync is run to ensure
        // that any new app targets and config changes will
        // be up to date across observer loop iterations...
        private bool Initialize()
        {
            if (this.replicaOrInstanceList == null)
            {
                this.replicaOrInstanceList = new List<ReplicaMonitoringInfo>();
            }

            // Is this a unit test run?
            if (this.IsTestRun)
            {
                this.replicaOrInstanceList.Add(new ReplicaMonitoringInfo
                {
                    ApplicationName = new Uri("fabric:/TestApp"),
                    Partitionid = Guid.NewGuid(),
                    ReplicaHostProcessId = 0,
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
                if (stream.Length > 40
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
                if (string.IsNullOrWhiteSpace(application.Target))
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

                this.ObserverLogger.LogInfo($"Will observe resource consumption by {application.Target} " +
                                       $"on Node {this.NodeName}.");
            }

            return true;
        }

        private async Task MonitorAppAsync(ApplicationInfo application)
        {
            var repOrInstList = await this.GetDeployedApplicationReplicaOrInstanceListAsync(new Uri(application.Target)).ConfigureAwait(true);

            if (repOrInstList.Count == 0)
            {
                return;
            }

            Process currentProcess = null;

            foreach (var repOrInst in repOrInstList)
            {
                this.Token.ThrowIfCancellationRequested();

                int processid = (int)repOrInst.ReplicaHostProcessId;
                var cpuUsage = new CpuUsage();

                try
                {
                    // App level...
                    currentProcess = Process.GetProcessById(processid);

                    this.Token.ThrowIfCancellationRequested();

                    if (currentProcess == null)
                    {
                        continue;
                    }

                    var procName = currentProcess.ProcessName;

                    // Add new resource data structures for each app service process...
                    var id = $"{application.Target.Replace("fabric:/", string.Empty)}:{procName}";

                    if (!this.allAppCpuData.Any(list => list.Id == id))
                    {
                        this.allAppCpuData.Add(new FabricResourceUsageData<int>("CPU Time", id));
                        this.allAppDiskReadsData.Add(new FabricResourceUsageData<float>("IO Read Bytes/sec", id));
                        this.allAppDiskWritesData.Add(new FabricResourceUsageData<float>("IO Write Bytes/sec", id));
                        this.allAppMemDataMB.Add(new FabricResourceUsageData<long>("Memory Consumption MB", id));
                        this.allAppMemDataPercent.Add(new FabricResourceUsageData<double>("Memory Consumption %", id));
                        this.allAppTotalActivePortsData.Add(new FabricResourceUsageData<int>("Total Active Ports", id));
                        this.allAppEphemeralPortsData.Add(new FabricResourceUsageData<int>("Ephemeral Ports", id));
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

                        // Disk/Network/Etc... IO (per-process bytes read/write per sec)
                        this.allAppDiskReadsData.FirstOrDefault(x => x.Id == id)
                            .Data.Add(this.diskUsage.PerfCounterGetDiskIOInfo(
                                currentProcess.ProcessName,
                                "Process",
                                "IO Read Bytes/sec") / 1000);

                        this.allAppDiskWritesData.FirstOrDefault(x => x.Id == id)
                            .Data.Add(this.diskUsage.PerfCounterGetDiskIOInfo(
                                currentProcess.ProcessName,
                                "Process",
                                "IO Write Bytes/sec") / 1000);
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
                    if (e is Win32Exception)
                    {
                        this.WriteToLogWithLevel(
                            this.ObserverName,
                            $"MonitorAsync failed to find current service process for {application.Target}",
                            LogLevel.Information);
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

        private async Task<List<ReplicaMonitoringInfo>> GetDeployedApplicationReplicaOrInstanceListAsync(Uri applicationNameFilter)
        {
            var deployedApps = await this.FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(this.NodeName, applicationNameFilter).ConfigureAwait(true);
            var currentReplicaInfoList = new List<ReplicaMonitoringInfo>();

            foreach (var deployedApp in deployedApps)
            {
                var serviceList = await this.FabricClientInstance.QueryManager.GetServiceListAsync(deployedApp.ApplicationName).ConfigureAwait(true);
                ServiceList filteredServiceList = null;

                var app = this.targetList.Where(x => x.Target.ToLower() == deployedApp.ApplicationName.OriginalString.ToLower()
                                                     && (!string.IsNullOrEmpty(x.ServiceExcludeList)
                                                     || !string.IsNullOrEmpty(x.ServiceIncludeList)))?.FirstOrDefault();
                if (app != null)
                {
                    filteredServiceList = new ServiceList();

                    if (!string.IsNullOrEmpty(app.ServiceExcludeList))
                    {
                        string[] list = app.ServiceExcludeList.Split(',');

                        // Excludes...?
                        foreach (var service in serviceList)
                        {
                            if (!list.Any(l => service.ServiceName.OriginalString.Contains(l)))
                            {
                                filteredServiceList.Add(service);
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(app.ServiceIncludeList))
                    {
                        string[] list = app.ServiceIncludeList.Split(',');

                        // Includes...?
                        foreach (var service in serviceList)
                        {
                            if (list.Any(l => service.ServiceName.OriginalString.Contains(l)))
                            {
                                filteredServiceList.Add(service);
                            }
                        }
                    }
                }

                var replicasOrInstances = await this.GetDeployedPrimaryReplicaAsync(deployedApp.ApplicationName, filteredServiceList ?? serviceList).ConfigureAwait(true);

                currentReplicaInfoList.AddRange(replicasOrInstances);

                // This is for reporting...
                this.replicaOrInstanceList.AddRange(replicasOrInstances);
            }

            return currentReplicaInfoList;
        }

        private async Task<List<ReplicaMonitoringInfo>> GetDeployedPrimaryReplicaAsync(Uri appName, ServiceList services)
        {
            var deployedReplicaList = await this.FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(this.NodeName, appName).ConfigureAwait(true);
            var replicaMonitoringList = new List<ReplicaMonitoringInfo>();

            foreach (var deployedReplica in deployedReplicaList)
            {
                if (deployedReplica is DeployedStatefulServiceReplica statefulReplica)
                {
                    if (statefulReplica.ReplicaRole == ReplicaRole.Primary
                        && services.Any(s => s.ServiceName == statefulReplica.ServiceName))
                    {
                        var replicaInfo = new ReplicaMonitoringInfo()
                        {
                            ApplicationName = appName,
                            ReplicaHostProcessId = statefulReplica.HostProcessId,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            Partitionid = statefulReplica.Partitionid,
                        };

                        replicaMonitoringList.Add(replicaInfo);

                        continue;
                    }
                }

                if (deployedReplica is DeployedStatelessServiceInstance statelessReplica
                    && services.Any(s => s.ServiceName == statelessReplica.ServiceName))
                {
                    var replicaInfo = new ReplicaMonitoringInfo()
                    {
                        ApplicationName = appName,
                        ReplicaHostProcessId = statelessReplica.HostProcessId,
                        ReplicaOrInstanceId = statelessReplica.InstanceId,
                        Partitionid = statelessReplica.Partitionid,
                    };

                    replicaMonitoringList.Add(replicaInfo);

                    continue;
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
                foreach (var app in this.targetList)
                {
                    this.Token.ThrowIfCancellationRequested();

                    // Process data for reporting...
                    foreach (var replicaOrInstance in this.replicaOrInstanceList.Where(x => x.ApplicationName.OriginalString == app.Target))
                    {
                        this.Token.ThrowIfCancellationRequested();

                        Process p = Process.GetProcessById((int)replicaOrInstance.ReplicaHostProcessId);

                        // If the process is no longer running, then don't report on it...
                        if (p == null)
                        {
                            continue;
                        }

                        var id = $"{app.Target.Replace("fabric:/", string.Empty)}:{p.ProcessName}";

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
                            app.Target,
                            replicaOrInstance,
                            app.DumpProcessOnError);

                        // Memory
                        this.ProcessResourceDataReportHealth(
                            this.allAppMemDataMB.Where(x => x.Id == id).FirstOrDefault(),
                            app.MemoryErrorLimitMB,
                            app.MemoryWarningLimitMB,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            app.Target,
                            replicaOrInstance,
                            app.DumpProcessOnError);

                        this.ProcessResourceDataReportHealth(
                            this.allAppMemDataPercent.Where(x => x.Id == id).FirstOrDefault(),
                            app.MemoryErrorLimitPercent,
                            app.MemoryWarningLimitPercent,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            app.Target,
                            replicaOrInstance,
                            app.DumpProcessOnError);

                        // DiskIO
                        this.ProcessResourceDataReportHealth(
                            this.allAppDiskReadsData.Where(x => x.Id == id).FirstOrDefault(),
                            app.DiskIOErrorReadsPerSecMS,
                            app.DiskIOWarningReadsPerSecMS,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            app.Target,
                            replicaOrInstance);

                        this.ProcessResourceDataReportHealth(
                            this.allAppDiskWritesData.Where(x => x.Id == id).FirstOrDefault(),
                            app.DiskIOErrorWritesPerSecMS,
                            app.DiskIOWarningWritesPerSecMS,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            app.Target,
                            replicaOrInstance);

                        // Ports
                        this.ProcessResourceDataReportHealth(
                            this.allAppTotalActivePortsData.Where(x => x.Id == id).FirstOrDefault(),
                            app.NetworkErrorActivePorts,
                            app.NetworkWarningActivePorts,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            app.Target,
                            replicaOrInstance);

                        // Ports
                        this.ProcessResourceDataReportHealth(
                            this.allAppEphemeralPortsData.Where(x => x.Id == id).FirstOrDefault(),
                            app.NetworkErrorEphemeralPorts,
                            app.NetworkWarningEphemeralPorts,
                            timeToLiveWarning,
                            HealthReportType.Application,
                            app.Target,
                            replicaOrInstance);
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

            // IO Read Bytes/s
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "IO Read Bytes/sec",
                "Average",
                Math.Round(this.allAppDiskReadsData.Where(x => x.Id == appName)
                                                    .FirstOrDefault().AverageDataValue));
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "IO Read Bytes/sec",
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppDiskReadsData.Where(x => x.Id == appName)
                                                                     .FirstOrDefault().MaxDataValue)));

            // IO Write Bytes/s
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "IO Write Bytes/sec",
                "Average",
                Math.Round(this.allAppDiskWritesData.Where(x => x.Id == appName)
                                                    .FirstOrDefault().AverageDataValue));
            this.CsvFileLogger.LogData(
                fileName,
                appName,
                "IO Write Bytes/sec",
                "Peak",
                Math.Round(Convert.ToDouble(this.allAppDiskWritesData.Where(x => x.Id == appName)
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