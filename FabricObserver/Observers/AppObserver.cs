// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Model;
using FabricObserver.Utilities;
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

namespace FabricObserver
{
    // This observer monitors the behavior of user SF service processes
    // and signals Warning and Error based on user-supplied resource thresholds
    // in AppObserver.config.json
    public class AppObserver : ObserverBase
    {
        private readonly string dataPackagePath;
        private readonly List<ApplicationInfo> targetList = new List<ApplicationInfo>();

        // Health Report data containers - For use in analysis to determine health state...
        // These lists are cleared after each healthy iteration.
        private List<FabricResourceUsageData<int>> allAppCpuData;
        private List<FabricResourceUsageData<long>> allAppMemData;
        private List<FabricResourceUsageData<float>> allAppDiskReadsData;
        private List<FabricResourceUsageData<float>> allAppDiskWritesData;
        private List<FabricResourceUsageData<int>> allAppTotalActivePortsData;
        private List<FabricResourceUsageData<int>> allAppEphemeralPortsData;
        private List<ReplicaMonitoringInfo> replicaOrInstanceList;
        private WindowsPerfCounters perfCounters = null;
        private DiskUsage diskUsage = null;

        public AppObserver() : base(ObserverConstants.AppObserverName)
        {
            this.dataPackagePath = FabricServiceContext.CodePackageActivationContext.GetDataPackageObject("Observers.Data")?.Path;
            this.allAppCpuData = new List<FabricResourceUsageData<int>>();
            this.allAppDiskReadsData = new List<FabricResourceUsageData<float>>();
            this.allAppDiskWritesData = new List<FabricResourceUsageData<float>>();
            this.allAppMemData = new List<FabricResourceUsageData<long>>();
            this.allAppTotalActivePortsData = new List<FabricResourceUsageData<int>>();
            this.allAppEphemeralPortsData = new List<FabricResourceUsageData<int>>();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            bool initialized = Initialize();
            Token = token;

            if (!initialized || token.IsCancellationRequested)
            {
                Token.ThrowIfCancellationRequested();

                HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                                 ObserverName,
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
                    Token.ThrowIfCancellationRequested();

                    await MonitorAppAsync(app).ConfigureAwait(true);
                }

                await ReportAsync(token).ConfigureAwait(true);
                LastRunDateTime = DateTime.Now;
            }
            finally
            {
                //Clean up...
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
            if (IsTestRun)
            {
                this.replicaOrInstanceList.Add(new ReplicaMonitoringInfo
                {
                    ApplicationName = new Uri("fabric:/TestApp"),
                    Partitionid = Guid.NewGuid(),
                    ReplicaHostProcessId = 0,
                    ReplicaOrInstanceId = default(long)
                });

                return true;
            }

            ConfigSettings.Initialize(FabricRuntime.GetActivationContext().GetConfigurationPackageObject(ConfigSettings.ConfigPackageName).Settings, ConfigSettings.AppObserverConfiguration, "AppObserverDataFileName");
            var appObserverDataFileName = Path.Combine(this.dataPackagePath, ConfigSettings.AppObserverDataFileName);

            if (!File.Exists(appObserverDataFileName))
            {
                WriteToLogWithLevel(ObserverName,
                                    $"Will not observe resource consumption as no configuration parameters have been supplied... " +
                                    $"| {NodeName}",
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

            using (Stream stream = new FileStream(appObserverDataFileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 40
                    && JsonHelper.IsJson<List<ApplicationInfo>>(File.ReadAllText(appObserverDataFileName)))
                {
                    this.targetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
                }
            }

            // Are any of the config-supplied apps deployed?...
            if (this.targetList.Count == 0)
            {
                WriteToLogWithLevel(ObserverName,
                                    $"Will not observe resource consumption as no configuration parameters have been supplied... " +
                                    $"| {NodeName}",
                                    LogLevel.Information);

                return false;
            }

            int settingsFail = 0;

            foreach (var application in this.targetList)
            {
                if (string.IsNullOrWhiteSpace(application.Target))
                {
                    HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.ToString(),
                                                                     ObserverName,
                                                                     HealthState.Warning,
                                                                     $"Initialize() | {application.Target}: Required setting, target, is not set...");
                    settingsFail++;
                    continue;
                }

                if (settingsFail == this.targetList.Count) // No required settings supplied for deployed application(s)...
                {
                    return false;
                }

                ObserverLogger.LogInfo($"Will observe resource consumption by {application.Target} " +
                                       $"on Node {NodeName}.");

                // If the target is not present in CPU list, then it isn't going to be in any of the other lists...
                if (!this.allAppCpuData.Any(target => target.Name == application.Target))
                {
                    this.allAppCpuData.Add(new FabricResourceUsageData<int>(application.Target));
                    this.allAppDiskReadsData.Add(new FabricResourceUsageData<float>(application.Target));
                    this.allAppDiskWritesData.Add(new FabricResourceUsageData<float>(application.Target));
                    this.allAppMemData.Add(new FabricResourceUsageData<long>(application.Target));
                    this.allAppTotalActivePortsData.Add(new FabricResourceUsageData<int>(application.Target));
                    this.allAppEphemeralPortsData.Add(new FabricResourceUsageData<int>(application.Target));
                }
            }

            return true;
        }

        private async Task MonitorAppAsync(ApplicationInfo application)
        {
            await SetDeployedApplicationReplicaOrInstanceListAsync(new Uri(application.Target)).ConfigureAwait(true);

            Process currentProcess = null;

            foreach (var replicaOrInstance in this.replicaOrInstanceList)
            {
                Token.ThrowIfCancellationRequested();

                int processid = (int)replicaOrInstance.ReplicaHostProcessId;
                var cpuUsage = new CpuUsage();

                try
                {
                    // App level...
                    currentProcess = Process.GetProcessById(processid);

                    Token.ThrowIfCancellationRequested();

                    if (currentProcess == null)
                    {
                        continue;
                    }

                    // CPU (all cores)...
                    int i = Environment.ProcessorCount + 10;

                    while (!currentProcess.HasExited && i > 0)
                    {
                        Token.ThrowIfCancellationRequested();

                        int cpu = cpuUsage.GetCpuUsageProcess(currentProcess);

                        if (cpu >= 0)
                        {
                            this.allAppCpuData.FirstOrDefault(x => x.Name == application.Target).Data.Add(cpu);
                        }

                        // Memory (private working set)...
                        var mem = this.perfCounters.PerfCounterGetProcessPrivateWorkingSetMB(currentProcess.ProcessName);
                        this.allAppMemData.FirstOrDefault(x => x.Name == application.Target).Data.Add((long)mem);

                        // Disk/Network/Etc... IO (per-process bytes read/write per sec)
                        this.allAppDiskReadsData.FirstOrDefault(x => x.Name == application.Target)
                            .Data.Add(this.diskUsage.PerfCounterGetDiskIOInfo(currentProcess.ProcessName,
                                                                              "Process",
                                                                              "IO Read Bytes/sec") / 1000);

                        this.allAppDiskWritesData.FirstOrDefault(x => x.Name == application.Target)
                            .Data.Add(this.diskUsage.PerfCounterGetDiskIOInfo(currentProcess.ProcessName,
                                                                              "Process",
                                                                              "IO Write Bytes/sec") / 1000);
                        --i;

                        Thread.Sleep(250);
                    }

                    // Total and Ephemeral ports....
                    this.allAppTotalActivePortsData.FirstOrDefault(x => x.Name == application.Target)
                        .Data.Add(NetworkUsage.GetActivePortCount(currentProcess.Id));

                    this.allAppEphemeralPortsData.FirstOrDefault(x => x.Name == application.Target)
                        .Data.Add(NetworkUsage.GetActiveEphemeralPortCount(currentProcess.Id));
                }
                catch (Exception e)
                {
                    if (e is Win32Exception)
                    {
                        WriteToLogWithLevel(ObserverName,
                                            "MonitorAsync failed to find current service process for " +
                                            application.Target, LogLevel.Information);
                    }
                    else
                    {
                        if (!(e is OperationCanceledException))
                        {
                            WriteToLogWithLevel(ObserverName,
                                                "Unhandled exception in MonitorAsync: \n" +
                                                e.ToString(),
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

        public async Task SetDeployedApplicationReplicaOrInstanceListAsync(Uri applicationNameFilter)
        {
            var deployedApps = await FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(NodeName, applicationNameFilter).ConfigureAwait(true);

            foreach (var deployedApp in deployedApps)
            {
                var replicasOrInstances = await GetDeployedPrimaryReplicaAsync(deployedApp.ApplicationName).ConfigureAwait(true);
                
                this.replicaOrInstanceList.AddRange(replicasOrInstances);
            }
        }

        public async Task<List<ReplicaMonitoringInfo>> GetDeployedPrimaryReplicaAsync(Uri appName)
        {
            var deployedReplicaList = await FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName).ConfigureAwait(true);
            var replicaMonitoringList = new List<ReplicaMonitoringInfo>();

            foreach (var deployedReplica in deployedReplicaList)
            {
                if (deployedReplica is DeployedStatefulServiceReplica statefuleReplica)
                {
                    if (statefuleReplica.ReplicaRole == ReplicaRole.Primary)
                    {
                        var replicaInfo = new ReplicaMonitoringInfo()
                        {
                            ApplicationName = appName,
                            ReplicaHostProcessId = statefuleReplica.HostProcessId,
                            ReplicaOrInstanceId = statefuleReplica.ReplicaId,
                            Partitionid = statefuleReplica.Partitionid
                        };

                        replicaMonitoringList.Add(replicaInfo);

                        continue;
                    }
                }

                if (deployedReplica is DeployedStatelessServiceInstance statelessReplica)
                {
                    var replicaInfo = new ReplicaMonitoringInfo()
                    {
                        ApplicationName = appName,
                        ReplicaHostProcessId = statelessReplica.HostProcessId,
                        ReplicaOrInstanceId = statelessReplica.InstanceId,
                        Partitionid = statelessReplica.Partitionid
                    };

                    replicaMonitoringList.Add(replicaInfo);

                    continue;
                }
            }

            return replicaMonitoringList;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                Token.ThrowIfCancellationRequested();
                var timeToLiveWarning = SetTimeToLiveWarning();

                // App-specific reporting...
                foreach (var app in this.targetList)
                {
                    Token.ThrowIfCancellationRequested();

                    // Log (csv) CPU/Mem/DiskIO per app...
                    if (CsvFileLogger.EnableCsvLogging || IsTelemetryEnabled)
                    {
                        LogAllAppResourceDataToCsv(app.Target);
                    }

                    // Process data for reporting...
                    foreach (var replicaOrInstance in this.replicaOrInstanceList.Where(x => x.ApplicationName.OriginalString == app.Target))
                    {
                        Token.ThrowIfCancellationRequested();

                        // CPU
                        ProcessResourceDataReportHealth(this.allAppCpuData.Where(x => x.Name == app.Target).FirstOrDefault(),
                                                        "CPU Time",
                                                        app.CpuErrorLimitPct,
                                                        app.CpuWarningLimitPct,
                                                        timeToLiveWarning,
                                                        HealthReportType.Application,
                                                        app.Target,
                                                        replicaOrInstance,
                                                        app.DumpProcessOnError);
                        // Memory
                        ProcessResourceDataReportHealth(this.allAppMemData.Where(x => x.Name == app.Target).FirstOrDefault(),
                                                        "Memory (Private working set)",
                                                        app.MemoryErrorLimitMB,
                                                        app.MemoryWarningLimitMB,                                                  
                                                        timeToLiveWarning,
                                                        HealthReportType.Application,
                                                        app.Target,
                                                        replicaOrInstance,
                                                        app.DumpProcessOnError);
                        // DiskIO
                        ProcessResourceDataReportHealth(this.allAppDiskReadsData.Where(x => x.Name == app.Target).FirstOrDefault(),
                                                        "IO Read Bytes/sec",
                                                        app.DiskIOErrorReadsPerSecMS,
                                                        app.DiskIOWarningReadsPerSecMS,
                                                        timeToLiveWarning,
                                                        HealthReportType.Application,
                                                        app.Target,
                                                        replicaOrInstance);

                        ProcessResourceDataReportHealth(this.allAppDiskWritesData.Where(x => x.Name == app.Target).FirstOrDefault(),
                                                        "IO Write Bytes/sec",
                                                        app.DiskIOErrorWritesPerSecMS,
                                                        app.DiskIOWarningWritesPerSecMS,
                                                        timeToLiveWarning,
                                                        HealthReportType.Application,
                                                        app.Target,
                                                        replicaOrInstance);

                        // Ports 
                        ProcessResourceDataReportHealth(this.allAppTotalActivePortsData.Where(x => x.Name == app.Target).FirstOrDefault(),
                                                        "Total Active Ports",
                                                        app.NetworkErrorActivePorts,
                                                        app.NetworkWarningActivePorts,
                                                        timeToLiveWarning,
                                                        HealthReportType.Application,
                                                        app.Target,
                                                        replicaOrInstance);

                        // Ports 
                        ProcessResourceDataReportHealth(this.allAppTotalActivePortsData.Where(x => x.Name == app.Target).FirstOrDefault(),
                                                        "Ephemeral Ports",
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
                WriteToLogWithLevel(ObserverName,
                                    "Unhandled exception in ReportAsync: \n" +
                                    e.ToString(),
                                    LogLevel.Error);
                throw;

            }
        }

        private void LogAllAppResourceDataToCsv(string appName)
        {
            if (!CsvFileLogger.EnableCsvLogging && !IsTelemetryEnabled)
            {
                return;
            }

            var fileName = appName.Replace("fabric:/", "") +
                           NodeName;

            // CPU Time
            CsvFileLogger.LogData(fileName,
                                  appName,
                                  "% CPU Time",
                                  "Average",
                                  Math.Round(this.allAppCpuData.Where(x => x.Name == appName)
                                                    .FirstOrDefault().AverageDataValue));
            CsvFileLogger.LogData(fileName,
                                  appName,
                                  "% CPU Time",
                                  "Peak",
                                  Math.Round(Convert.ToDouble(this.allAppCpuData.Where(x => x.Name == appName)
                                                                     .FirstOrDefault().MaxDataValue)));
            // Memory
            CsvFileLogger.LogData(fileName,
                                  appName,
                                  "Memory (Working set) MB",
                                  "Average",
                                  Math.Round(this.allAppMemData.Where(x => x.Name == appName)
                                                    .FirstOrDefault().AverageDataValue));
            CsvFileLogger.LogData(fileName,
                                  appName,
                                  "Memory (Working set) MB",
                                  "Peak",
                                  Math.Round(Convert.ToDouble(this.allAppMemData.Where(x => x.Name == appName)
                                                                     .FirstOrDefault().MaxDataValue)));
            // IO Read Bytes/s
            CsvFileLogger.LogData(fileName,
                                  appName,
                                  "IO Read Bytes/sec",
                                  "Average",
                                  Math.Round(this.allAppDiskReadsData.Where(x => x.Name == appName)
                                                    .FirstOrDefault().AverageDataValue));
            CsvFileLogger.LogData(fileName,
                                  appName,
                                  "IO Read Bytes/sec",
                                  "Peak",
                                  Math.Round(Convert.ToDouble(this.allAppDiskReadsData.Where(x => x.Name == appName)
                                                                     .FirstOrDefault().MaxDataValue)));
            // IO Write Bytes/s
            CsvFileLogger.LogData(fileName,
                                  appName,
                                  "IO Write Bytes/sec",
                                  "Average",
                                  Math.Round(this.allAppDiskWritesData.Where(x => x.Name == appName)
                                                    .FirstOrDefault().AverageDataValue));
            CsvFileLogger.LogData(fileName,
                                  appName,
                                  "IO Write Bytes/sec",
                                  "Peak",
                                  Math.Round(Convert.ToDouble(this.allAppDiskWritesData.Where(x => x.Name == appName)
                                                                    .FirstOrDefault().MaxDataValue)));

            // Network
            CsvFileLogger.LogData(fileName,
                                   appName,
                                   "Active Ports",
                                   "Total",
                                   Math.Round(Convert.ToDouble(this.allAppTotalActivePortsData.Where(x => x.Name == appName)
                                                                   .FirstOrDefault().MaxDataValue)));
            DataTableFileLogger.Flush();
        }
    }
}