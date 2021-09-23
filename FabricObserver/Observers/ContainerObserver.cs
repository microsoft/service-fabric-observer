// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Fabric.Query;
using System.Fabric;
using System.Runtime.InteropServices;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.MachineInfoModel;
using System.Fabric.Description;
using System.Fabric.Health;
using System.ComponentModel;

namespace FabricObserver.Observers
{
    public class ContainerObserver : ObserverBase
    {
        private const int MaxProcessExitWaitTimeMS = 60000;
        private List<FabricResourceUsageData<double>> allCpuDataPercentage;
        private List<FabricResourceUsageData<double>> allMemDataMB;

        // userTargetList is the list of ApplicationInfo objects representing apps supplied in configuration.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        private List<ApplicationInfo> deployedTargetList;
        private List<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList;
        private readonly string ConfigPackagePath;
        private string ConfigurationFilePath = string.Empty;

        public ContainerObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            var configSettings = new MachineInfoModel.ConfigSettings(context);
            ConfigPackagePath = configSettings.ConfigPackagePath;
        }

        // OsbserverManager passes in a special token to ObserveAsync and ReportAsync that enables it to stop this observer outside of
        // of the SF runtime, but this token will also cancel when the runtime cancels the main token.
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            var runDurationTimer = Stopwatch.StartNew();

            if (!await InitializeAsync(token).ConfigureAwait(false))
            {
                return;
            }

            await MonitorContainersAsync().ConfigureAwait(true);
            await ReportAsync(token).ConfigureAwait(true);
            CleanUp();
            runDurationTimer.Stop();
            RunDuration = runDurationTimer.Elapsed;
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            TimeSpan timeToLive = GetHealthReportTimeToLive();

            foreach (var app in deployedTargetList)
            {
                if (!ReplicaOrInstanceList.Any(rep => rep.ApplicationName.OriginalString == app.TargetApp))
                {
                    continue;
                }

                foreach (var repOrInst in ReplicaOrInstanceList.Where(rep => rep.ApplicationName.OriginalString == app.TargetApp))
                {
                    string serviceName = repOrInst.ServiceName.OriginalString.Replace(app.TargetApp, "").Replace("/", "");
                    string cpuId = $"{serviceName}_cpu";
                    string memId = $"{serviceName}_mem";
                    var cpuFrudInst = allCpuDataPercentage.Find(cpu => cpu.Id == cpuId);
                    var memFrudInst = allMemDataMB.Find(mem => mem.Id == memId);

                    if (EnableCsvLogging)
                    {
                        var csvFileName = $"{serviceName}Data{(CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives ? "_" + DateTime.UtcNow.ToString("o") : string.Empty)}";
                        string appName = repOrInst.ApplicationName.OriginalString.Replace("fabric:/", "");
                        string id = $"{appName}:{serviceName}";

                        // BaseLogDataLogFolderPath is set in ObserverBase or a default one is created by CsvFileLogger.
                        // This means a new folder will be added to the base path.
                        if (CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives)
                        {
                            CsvFileLogger.DataLogFolder = serviceName;
                        }

                        // Log resource usage data to local CSV file(s).
                        // CPU Time
                        CsvFileLogger.LogData(
                                        csvFileName,
                                        id,
                                        ErrorWarningProperty.TotalCpuTime,
                                        "Average",
                                        Math.Round(cpuFrudInst.AverageDataValue));

                        CsvFileLogger.LogData(
                                        csvFileName,
                                        id,
                                        ErrorWarningProperty.TotalCpuTime,
                                        "Peak",
                                        Math.Round(cpuFrudInst.MaxDataValue));


                        // Memory - MB
                        CsvFileLogger.LogData(
                                        csvFileName,
                                        id,
                                        ErrorWarningProperty.TotalMemoryConsumptionMb,
                                        "Average",
                                        Math.Round(memFrudInst.AverageDataValue));

                        CsvFileLogger.LogData(
                                        csvFileName,
                                        id,
                                        ErrorWarningProperty.TotalMemoryConsumptionMb,
                                        "Peak",
                                        Math.Round(memFrudInst.MaxDataValue));

                    }

                    // Report -> Send Telemetry/Write ETW/Create SF Health Warnings (if threshold breach)
                    ProcessResourceDataReportHealth(
                                    cpuFrudInst,
                                    app.CpuErrorLimitPercent,
                                    app.CpuWarningLimitPercent,
                                    timeToLive,
                                    HealthReportType.Application,
                                    repOrInst);

                    ProcessResourceDataReportHealth(
                                    memFrudInst,
                                    app.MemoryErrorLimitMb,
                                    app.MemoryWarningLimitMb,
                                    timeToLive,
                                    HealthReportType.Application,
                                    repOrInst);
                }
            }

            return Task.CompletedTask;
        }

        // Runs each time ObserveAsync is run to ensure that any new app targets and config changes will
        // be up to date across observer loop iterations.
        private async Task<bool> InitializeAsync(CancellationToken token)
        {
            SetConfigurationFilePath();

            if (!File.Exists(ConfigurationFilePath))
            {
                ObserverLogger.LogWarning($"Will not observe container resource consumption as no configuration file has been supplied.");
                return false;
            }

            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new List<ApplicationInfo>();
            ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();

            using (Stream stream = new FileStream(ConfigurationFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 0 && JsonHelper.IsJson<List<ApplicationInfo>>(File.ReadAllText(ConfigurationFilePath)))
                {
                    userTargetList.AddRange(JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream));
                }
            }

            if (userTargetList.Count == 0)
            {
                ObserverLogger.LogWarning($"Will not observe container resource consumption as no app targets have been supplied.");
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
                    MaxResults = 50,
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
                    await Task.Delay(250, Token).ConfigureAwait(false);
                }

                foreach (var app in apps)
                {
                    Token.ThrowIfCancellationRequested();

                    if (app.ApplicationName.OriginalString == "fabric:/System")
                    {
                        continue;
                    }

                    // App filtering: AppExludeList, AppIncludeList. This is only useful when you are observing All/* applications for a range of thresholds.
                    if (!string.IsNullOrWhiteSpace(application.AppExcludeList) && application.AppExcludeList.Contains(app.ApplicationName.OriginalString))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(application.AppIncludeList) && !application.AppIncludeList.Contains(app.ApplicationName.OriginalString))
                    {
                        continue;
                    }

                    if (userTargetList.Any(a => a.TargetApp == app.ApplicationName.OriginalString))
                    {
                        var existingAppConfig = userTargetList.Find(a => a.TargetApp == app.ApplicationName.OriginalString);

                        if (existingAppConfig == null)
                        {
                            continue;
                        }

                        existingAppConfig.MemoryWarningLimitMb = existingAppConfig.MemoryWarningLimitMb == 0 && application.MemoryWarningLimitMb > 0 ? application.MemoryWarningLimitMb : existingAppConfig.MemoryWarningLimitMb;
                        existingAppConfig.MemoryErrorLimitMb = existingAppConfig.MemoryErrorLimitMb == 0 && application.MemoryErrorLimitMb > 0 ? application.MemoryErrorLimitMb : existingAppConfig.MemoryErrorLimitMb;
                        existingAppConfig.CpuErrorLimitPercent = existingAppConfig.CpuErrorLimitPercent == 0 && application.CpuErrorLimitPercent > 0 ? application.CpuErrorLimitPercent : existingAppConfig.CpuErrorLimitPercent;
                        existingAppConfig.CpuWarningLimitPercent = existingAppConfig.CpuWarningLimitPercent == 0 && application.CpuWarningLimitPercent > 0 ? application.CpuWarningLimitPercent : existingAppConfig.CpuWarningLimitPercent;
                    }
                    else
                    {
                        var appConfig = new ApplicationInfo
                        {
                            TargetApp = app.ApplicationName.OriginalString,
                            AppExcludeList = application.AppExcludeList,
                            AppIncludeList = application.AppIncludeList,
                            MemoryWarningLimitMb = application.MemoryWarningLimitMb,
                            MemoryErrorLimitMb = application.MemoryErrorLimitMb,
                            CpuErrorLimitPercent = application.CpuErrorLimitPercent,
                            CpuWarningLimitPercent = application.CpuWarningLimitPercent,
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
            MonitoredAppCount = userTargetList.Count;

            foreach (var application in userTargetList)
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(application.TargetApp))
                {
                    ObserverLogger.LogWarning($"InitializeAsync: Required setting, targetApp, is not set.");
                    settingsFail++;
                    continue;
                }

                // No required settings for supplied application(s).
                if (settingsFail == userTargetList.Count)
                {
                    return false;
                }

                ServiceFilterType filterType = ServiceFilterType.None;
                List<string> filteredServiceList = null;

                if (!string.IsNullOrWhiteSpace(application.ServiceExcludeList))
                {
                    filteredServiceList = application.ServiceExcludeList.Replace(" ", string.Empty).Split(',').ToList();
                    filterType = ServiceFilterType.Exclude;
                }
                else if (!string.IsNullOrWhiteSpace(application.ServiceIncludeList))
                {
                    filteredServiceList = application.ServiceIncludeList.Replace(" ", string.Empty).Split(',').ToList();
                    filterType = ServiceFilterType.Include;
                }

                try
                {
                    var codepackages = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () => FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(
                                                                                          NodeName,
                                                                                          new Uri(application.TargetApp),
                                                                                          null,
                                                                                          null,
                                                                                          ConfigurationSettings.AsyncTimeout,
                                                                                          token),
                                               Token);

                    if (codepackages.Count == 0)
                    {
                        continue;
                    }

                    int containerHostCount = codepackages.Count(c => c.HostType == HostType.ContainerHost);

                    if (containerHostCount == 0)
                    {
                        continue;
                    }

                    deployedTargetList.Add(application);
                    await SetInstanceOrReplicaMonitoringList(new Uri(application.TargetApp), filteredServiceList, filterType, null).ConfigureAwait(false);
                }
                catch (Exception e) when (e is FabricException || e is TimeoutException)
                {
                    ObserverLogger.LogInfo($"Handled Exception in function InitializeAsync:{e.GetType().Name}.");
                }
            }

            MonitoredServiceProcessCount = ReplicaOrInstanceList.Count;

            foreach (var rep in ReplicaOrInstanceList)
            {
                token.ThrowIfCancellationRequested();

                ObserverLogger.LogInfo($"Will observe container instance resource consumption by {rep.ServiceName.OriginalString} on Node {NodeName}.");
            }

            return true;
        }

        private async Task MonitorContainersAsync()
        {
            /* docker stats --no-stream --format "table {{.Container}}\t{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}"

               Windows:
                CONTAINER      NAME                                                                             CPU %     PRIV WORKING SET
                990e60f2e235   sf-9-df9dbf27-8dff-448f-ac07-d670464b1503_2f8733f2-81a7-440c-bd1f-d990e0d6d5eb   0.00%     291.2MiB
                08dd2259c5d6   sf-9-2cd3cf02-2ec3-4d5a-99fa-22d2f5c7680a_51cd197c-e159-49f1-a89f-842acc778648   0.01%     291.4MiB

               Linux:
                CONTAINER      NAME                                                                               CPU %     MEM USAGE / LIMIT
                9e380a42233c   sf-243-2d2f9fde-fb93-4e77-a5d2-df1600000000_3161e2ee-3d8f-2d45-b195-e88b31e079c0   0.05%     27.35MiB / 15.45GiB
                fed0da6f7bad   sf-243-723d5795-01c7-477f-950e-45a400000000_2cc293c0-929c-5c40-bc96-cd4e596b6b6a   0.05%     27.19MiB / 15.45GiB
             */

            int listcapacity = ReplicaOrInstanceList.Count;
            allCpuDataPercentage ??= new List<FabricResourceUsageData<double>>(listcapacity);
            allMemDataMB ??= new List<FabricResourceUsageData<double>>(listcapacity);
            string error = string.Empty;
            string args = "/c docker stats --no-stream --format \"table {{.Container}}\t{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\"";
            string filename = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                args = string.Empty;

                // We need the full path to the currently deployed FO CodePackage, which is where our 
                // linux Capabilities-laced proxy binary lives, which is used for elevated_docker_stats call.
                string path = FabricServiceContext.CodePackageActivationContext.GetCodePackageObject("Code").Path;
                filename = $"{path}/elevated_docker_stats";
            }

            var ps = new ProcessStartInfo
            {
                Arguments = args,
                FileName = filename,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var output = new List<string>();
            using Process p = new Process();
            p.ErrorDataReceived += (sender, e) => { error += e.Data; };
            p.OutputDataReceived += (sender, e) => { output.Add(e.Data); };
            p.StartInfo = ps;
            _ = p.Start();

            // Start async reads.
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();

            // It should not take 60 seconds for the process that calls docker stats to exit.
            // If so, then end execution of the outer loop: stop monitoring for this run of ContainerObserver.
            if (!p.WaitForExit(MaxProcessExitWaitTimeMS))
            {
                try
                {
                    p?.Kill(true);
                }
                catch (Exception e) when (e is AggregateException || e is InvalidOperationException || e is NotSupportedException || e is Win32Exception)
                {

                }

                ObserverLogger.LogWarning($"docker process has run too long ({MaxProcessExitWaitTimeMS} ms). Aborting.");
                return;
            }

            int exitStatus = p.ExitCode;

            // Was there an error running docker stats?
            if (exitStatus != 0)
            {
                string msg = $"docker stats --no-stream exited with {exitStatus}: {error}";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    msg += " NOTE: docker must be running and you must run FabricObserver as System user or Admin user on Windows " +
                            "in order for ContainerObserver to function correctly on Windows.";
                }

                ObserverLogger.LogWarning(msg);
                CurrentWarningCount++;

                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = EnableVerboseLogging,
                    HealthMessage = $"{msg}",
                    HealthReportTimeToLive = GetHealthReportTimeToLive(),
                    Property = "docker_stats_failure",
                    ReportType = HealthReportType.Application,
                    State = HealthState.Warning,
                    NodeName = NodeName,
                    Observer = ObserverName,
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                                                "docker_stats_failure",
                                                HealthState.Warning,
                                                msg,
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
                                        Property = "docker_stats_failure",
                                        Level = "Warning",
                                        Message = msg,
                                        ObserverName
                                    });
                }

                return;
            }

            try
            {
                foreach (ReplicaOrInstanceMonitoringInfo repOrInst in ReplicaOrInstanceList)
                {
                    Token.ThrowIfCancellationRequested();
                    string serviceName = repOrInst.ServiceName.OriginalString.Replace(repOrInst.ApplicationName.OriginalString, "").Replace("/", "");
                    string cpuId = $"{serviceName}_cpu";
                    string memId = $"{serviceName}_mem";
                    string containerId = string.Empty;

                    if (!allCpuDataPercentage.Any(frud => frud.Id == cpuId))
                    {
                        allCpuDataPercentage.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalCpuTime, cpuId, 1));
                    }

                    if (!allMemDataMB.Any(frud => frud.Id == memId))
                    {
                        allMemDataMB.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.TotalMemoryConsumptionMb, memId, 1));
                    }

                    foreach (string line in output)
                    {
                        Token.ThrowIfCancellationRequested();

                        if (line == null || line.Contains("CPU"))
                        {
                            continue;
                        }

                        string[] stats = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        // Something went wrong if the collection size is less than 4 given the supplied output table format:
                        // {{.Container}}\t{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}
                        if (stats.Length < 4)
                        {
                            ObserverLogger.LogWarning($"docker stats not returning expected information: stats.Count = {stats.Length}. Expected 4.");
                            return;
                        }

                        if (!stats[1].Contains(repOrInst.ServicePackageActivationId))
                        {
                            continue;
                        }

                        containerId = stats[0];
                        repOrInst.ContainerId = containerId;

                        ObserverLogger.LogInfo($"cpu: {stats[2]}");
                        ObserverLogger.LogInfo($"mem: {stats[3]}");

                        // CPU (%)
                        double cpu_percent = double.TryParse(stats[2].Replace("%", ""), out double cpuPerc) ? cpuPerc : 0;
                        allCpuDataPercentage?.FirstOrDefault(f => f.Id == cpuId)?.Data.Add(cpu_percent);

                        // Memory (MiB)
                        double mem_working_set_mb = double.TryParse(stats[3].Replace("MiB", ""), out double memMib) ? memMib : 0;
                        allMemDataMB?.FirstOrDefault(f => f.Id == memId)?.Data.Add(mem_working_set_mb);

                        await Task.Delay(150, Token);
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogWarning($"Failure in ObserveAsync:{Environment.NewLine}{e}");

                // fix the bug..
                throw;
            }
        }

        private void SetConfigurationFilePath()
        {
            string configDataFilename = GetSettingParameterValue(ConfigurationSectionName, "ConfigFileName");

            if (!string.IsNullOrEmpty(configDataFilename) && !ConfigurationFilePath.Contains(configDataFilename))
            {
                ConfigurationFilePath = Path.Combine(ConfigPackagePath, configDataFilename);
            }
        }

        private async Task SetInstanceOrReplicaMonitoringList(
                              Uri appName,
                              IReadOnlyCollection<string> serviceFilterList,
                              ServiceFilterType filterType,
                              string appTypeName)
        {
            var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () => FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName),
                                               Token);

            foreach (var deployedReplica in deployedReplicaList)
            {
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica statefulReplica when statefulReplica.ReplicaRole == ReplicaRole.Primary:

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo()
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statefulReplica.HostProcessId,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ServiceName = statefulReplica.ServiceName,
                            ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                        };

                        if (serviceFilterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = serviceFilterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    continue;
                            }
                        }
                        break;
                        
                    case DeployedStatelessServiceInstance statelessInstance:
                        
                        replicaInfo = new ReplicaOrInstanceMonitoringInfo()
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statelessInstance.HostProcessId,
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ServiceName = statelessInstance.ServiceName,
                            ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                        };

                        if (serviceFilterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = serviceFilterList.Any(s => statelessInstance.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    continue;
                            }
                        }
                        break;   
                }

                if (replicaInfo == null)
                {
                    continue;
                }

                ReplicaOrInstanceList.Add(replicaInfo);
            }
        }

        private void CleanUp()
        {
            deployedTargetList?.Clear();
            deployedTargetList = null;

            ReplicaOrInstanceList?.Clear();
            ReplicaOrInstanceList = null;

            userTargetList?.Clear();
            userTargetList = null;

            if (!HasActiveFabricErrorOrWarning)
            {
                allCpuDataPercentage?.Clear();
                allCpuDataPercentage = null;

                allMemDataMB?.Clear();
                allMemDataMB = null;
            }
        }
    }
}
