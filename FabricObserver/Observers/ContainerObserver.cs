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
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.MachineInfoModel;
using System.Fabric.Description;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Fabric.Health;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers
{
    public sealed class ContainerObserver : ObserverBase
    {
        private const int MaxProcessExitWaitTimeMS = 60000;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> allCpuDataPercentage;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> allMemDataMB;

        // userTargetList is the list of ApplicationInfo objects representing apps supplied in configuration.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        private ConcurrentQueue<ApplicationInfo> deployedTargetList;
        private ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList;
        private readonly object lockObj = new object();
        private Stopwatch runDurationTimer;
        public string ConfigurationFilePath = string.Empty;

        public bool EnableConcurrentMonitoring
        {
            get; set;
        }

        public ParallelOptions ParallelOptions
        {
            get; private set;
        }

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public ContainerObserver(StatelessServiceContext context) : base(null, context)
        {
            
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

            runDurationTimer = Stopwatch.StartNew();

            if (!await InitializeAsync(token))
            {
                return;
            }

            Token = token;

            if (MonitorContainers())
            {
                await ReportAsync(token);
            }

            runDurationTimer.Stop();
            RunDuration = runDurationTimer.Elapsed;

            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration {(ParallelOptions.MaxDegreeOfParallelism == -1 ? "with" : "without")} " +
                                       $"Parallel (Processors: {Environment.ProcessorCount}):{RunDuration}");
            }

            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            if (deployedTargetList.IsEmpty)
            {
                return Task.CompletedTask;
            }

            TimeSpan timeToLive = GetHealthReportTimeToLive();

            _ = Parallel.ForEach(ReplicaOrInstanceList, ParallelOptions, (repOrInst, state) =>
            {
                token.ThrowIfCancellationRequested();

                ApplicationInfo app = deployedTargetList.First(
                                            a => (a.TargetApp != null && a.TargetApp == repOrInst.ApplicationName.OriginalString) ||
                                                    (a.TargetAppType != null && a.TargetAppType == repOrInst.ApplicationTypeName));

                string serviceName = repOrInst.ServiceName.OriginalString.Replace(app.TargetApp, "").Replace("/", "");
                string cpuId = $"{serviceName}_cpu";
                string memId = $"{serviceName}_mem";
                var cpuFrudInst = allCpuDataPercentage[cpuId];
                var memFrudInst = allMemDataMB[memId];

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

                    // Log resource usage data to local CSV file(s). locks are required here.
                    // CPU Time
                    lock (lockObj)
                    {
                        CsvFileLogger.LogData(
                            csvFileName,
                            id,
                            ErrorWarningProperty.CpuTime,
                            "Total",
                            Math.Round(cpuFrudInst.AverageDataValue));
                    }

                    // Memory - MB
                    lock (lockObj)
                    {
                        CsvFileLogger.LogData(
                            csvFileName,
                            id,
                            ErrorWarningProperty.MemoryConsumptionMb,
                            "Total",
                            Math.Round(memFrudInst.AverageDataValue));
                    }
                }

                // Report -> Send Telemetry/Write ETW/Create SF Health Warnings (if threshold breach)

                ProcessResourceDataReportHealth(
                    cpuFrudInst,
                    app.CpuErrorLimitPercent,
                    app.CpuWarningLimitPercent,
                    timeToLive,
                    EntityType.Service,
                    null,
                    repOrInst);

                ProcessResourceDataReportHealth(
                    memFrudInst,
                    app.MemoryErrorLimitMb,
                    app.MemoryWarningLimitMb,
                    timeToLive,
                    EntityType.Service,
                    null,
                    repOrInst);

            });

            return Task.CompletedTask;
        }

        // Runs each time ObserveAsync is run to ensure that any new app targets and config changes will
        // be up to date across observer loop iterations.
        private async Task<bool> InitializeAsync(CancellationToken token)
        {
            if (!SetConfigurationFilePath())
            {
                ObserverLogger.LogWarning($"Will not observe container resource consumption as no configuration file has been supplied.");
                return false;
            }

            // Concurrency/Parallelism support.
            // Default to using [1/4 of available logical processors ~* 2] threads if MaxConcurrentTasks setting is not supplied.
            // So, this means around 10 - 11 threads (or less) could be used if processor count = 20. This is only being done to limit the impact
            // FabricObserver has on the resources it monitors and alerts on...
            // Concurrency/Parallelism support. The minimum requirement is 4 logical processors, regardless of user setting.
            if (Environment.ProcessorCount >= 4 && bool.TryParse(
                    GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableConcurrentMonitoringParameter), out bool enableConcurrency))
            {
                EnableConcurrentMonitoring = enableConcurrency;
            }

            // Effectively, sequential.
            int maxDegreeOfParallelism = 1;

            if (EnableConcurrentMonitoring)
            {
                // Default to using [1/4 of available logical processors ~* 2] threads if MaxConcurrentTasks setting is not supplied.
                // So, this means around 10 - 11 threads (or less) could be used if processor count = 20. This is only being done to limit the impact
                // FabricObserver has on the resources it monitors and alerts on...
                maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));

                // If user configures MaxConcurrentTasks setting, then use that value instead.
                if (int.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxConcurrentTasksParameter), out int maxTasks))
                {
                    maxDegreeOfParallelism = maxTasks;
                }
            }

            ParallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new ConcurrentQueue<ApplicationInfo>();
            ReplicaOrInstanceList = new ConcurrentQueue<ReplicaOrInstanceMonitoringInfo>();

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
                    token.ThrowIfCancellationRequested();

                    deployedAppQueryDesc.ContinuationToken = appList.ContinuationToken;
                    appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                deployedAppQueryDesc,
                                                ConfigurationSettings.AsyncTimeout,
                                                Token),
                                        Token);
                    apps.AddRange(appList.ToList());
                    await Task.Delay(250, Token);
                }

                foreach (var app in apps)
                {
                    token.ThrowIfCancellationRequested();

                    if (app.ApplicationName.OriginalString == ObserverConstants.SystemAppName)
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

            // This doesn't add any real value for parallelization unless there are hundreds of apps on the node.
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
                string[] filteredServiceList = null;

                if (!string.IsNullOrWhiteSpace(application.ServiceExcludeList))
                {
                    filteredServiceList = application.ServiceExcludeList.Replace(" ", string.Empty).Split(',').ToArray();
                    filterType = ServiceFilterType.Exclude;
                }
                else if (!string.IsNullOrWhiteSpace(application.ServiceIncludeList))
                {
                    filteredServiceList = application.ServiceIncludeList.Replace(" ", string.Empty).Split(',').ToArray();
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

                    deployedTargetList.Enqueue(application);
                    await SetInstanceOrReplicaMonitoringList(new Uri(application.TargetApp), filteredServiceList, filterType, null);
                }
                catch (Exception e) when (e is FabricException || e is TimeoutException)
                {
                    ObserverLogger.LogInfo($"Handled Exception in function InitializeAsync:{e.GetType().Name}.");
                }
            }

            MonitoredAppCount = deployedTargetList.Count;
            MonitoredServiceProcessCount = ReplicaOrInstanceList.Count;

            foreach (var rep in ReplicaOrInstanceList)
            {
                token.ThrowIfCancellationRequested();

                ObserverLogger.LogInfo($"Will observe container instance resource consumption by {rep.ServiceName.OriginalString} on Node {NodeName}.");
            }

            return true;
        }

        private bool MonitorContainers()
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

            allCpuDataPercentage ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            allMemDataMB ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();

            try
            {
                string args = "/c docker stats --no-stream --format \"table {{.Container}}\t{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\"";
                string filename = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe";
                string error = string.Empty;

                if (!IsWindows)
                {
                    args = string.Empty;

                    // We need the full path to the currently deployed FO CodePackage, which is where our 
                    // linux Capabilities-laced proxy binary lives, which is used for elevated_docker_stats call.

                    filename = $"{CodePackage.Path}/elevated_docker_stats";
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
                using Process process = new Process();
                process.ErrorDataReceived += (sender, e) => { error += e.Data; };
                process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { output.Add(e.Data); } };
                process.StartInfo = ps;

                if (!process.Start())
                {
                    return false;
                }

                // Start async reads.
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                // It should not take 60 seconds for the process that calls docker stats to exit.
                // If so, then end execution of the outer loop: stop monitoring for this run of ContainerObserver.
                if (!process.WaitForExit(MaxProcessExitWaitTimeMS))
                {
                    try
                    {
                        process?.Kill(true);
                    }
                    catch (Exception e) when (e is AggregateException || e is InvalidOperationException || e is NotSupportedException || e is Win32Exception)
                    {

                    }

                    ObserverLogger.LogWarning($"docker process has run too long ({MaxProcessExitWaitTimeMS} ms). Aborting.");
                    return false;
                }

                int exitStatus = process.ExitCode;

                // Was there an error running docker stats?
                if (exitStatus != 0)
                {
                    string msg = $"docker stats exited with {exitStatus} on node {NodeName}: {error}{Environment.NewLine}";

                    if (IsWindows)
                    {
                        msg += "NOTE: docker must be running and you must run FabricObserver as System user or Admin user on Windows " +
                               "in order for ContainerObserver to function correctly on Windows.";
                    }
                    else
                    {
                        if (error.ToLower().Contains("permission denied"))
                        {
                            msg += "elevated_docker_stats Capabilities may have been removed. " +
                                   "This is most likely due to an SF runtime upgrade, which unsets Linux caps because SF re-acls (so, touches) all binaries in application Code folders as part of the upgrade process. " +
                                   $"An unhandled LinuxPermissionException will be generated by FabricObserver that will take down the FabricObserver process running on node {NodeName} " +
                                   "to mitigate the issue. Capabilities will be set on the elevated_docker_stats binary when FabricObserver is re-activated by the SF runtime.";
                        }
                    }

                    ObserverLogger.LogWarning(msg);

                    var healthReport = new Utilities.HealthReport
                    {
                        AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                        EmitLogEvent = EnableVerboseLogging,
                        HealthMessage = msg,
                        HealthReportTimeToLive = GetHealthReportTimeToLive(),
                        Property = $"docker_stats_failure({NodeName})",
                        EntityType = EntityType.Service,
                        ServiceName = FabricServiceContext.ServiceName,
                        State = HealthState.Warning,
                        NodeName = NodeName,
                        Observer = ObserverName
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

                    // Linux: Try and work around the unsetting of caps issues when SF runs a cluster upgrade.
                    if (!IsWindows && error.ToLower().Contains("permission denied"))
                    {
                        // Throwing LinuxPermissionException here will eventually take down FO (by design). The failure will be logged and telemetry will be emitted, then
                        // the exception will be re-thrown by ObserverManager and the FO process will fail fast exit. Then, SF will create a new instance of FO on the offending node which
                        // will run the setup bash script that ensures the elevated_docker_stats binary has the correct caps in place.
                        throw new LinuxPermissionException($"Capabilities have been removed from elevated_docker_stats{Environment.NewLine}{error}");
                    }

                    return false;
                }

                _ = Parallel.ForEach(ReplicaOrInstanceList, ParallelOptions, (repOrInst, state) =>
                {
                    string serviceName = repOrInst.ServiceName.OriginalString.Replace(repOrInst.ApplicationName.OriginalString, "").Replace("/", "");
                    string cpuId = $"{serviceName}_cpu";
                    string memId = $"{serviceName}_mem";
                    string containerId = string.Empty;

                    if (!allCpuDataPercentage.ContainsKey(cpuId))
                    {
                        _ = allCpuDataPercentage.TryAdd(cpuId, new FabricResourceUsageData<double>(ErrorWarningProperty.CpuTime, cpuId, 1, EnableConcurrentMonitoring));
                    }

                    if (!allMemDataMB.ContainsKey(memId))
                    {
                        _ = allMemDataMB.TryAdd(memId, new FabricResourceUsageData<double>(ErrorWarningProperty.MemoryConsumptionMb, memId, 1, EnableConcurrentMonitoring));
                    }

                    foreach (string line in output)
                    {
                        Token.ThrowIfCancellationRequested();

                        try
                        {
                            if (line.Contains("CPU"))
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

                            if (string.IsNullOrWhiteSpace(repOrInst?.ServicePackageActivationId) || !stats[1].Contains(repOrInst.ServicePackageActivationId))
                            {
                                continue;
                            }

                            containerId = stats[0];
                            repOrInst.ContainerId = containerId;
#if DEBUG
                            ObserverLogger.LogInfo($"cpu: {stats[2]}");
                            ObserverLogger.LogInfo($"mem: {stats[3]}");
#endif
                            // CPU (%)
                            double cpu_percent = double.TryParse(stats[2].Replace("%", ""), out double cpuPerc) ? cpuPerc : 0;
                            allCpuDataPercentage[cpuId].AddData(cpu_percent);

                            // Memory (MiB)
                            double mem_working_set_mb = double.TryParse(stats[3].Replace("MiB", ""), out double memMib) ? memMib : 0;
                            allMemDataMB[memId].AddData(mem_working_set_mb);

                            break;
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }
                    }
                });

                output.Clear();
                output = null;
            }
            catch (AggregateException ae)
            {
                foreach (Exception e in ae.Flatten().InnerExceptions)
                {
                    if (e is OperationCanceledException || e is TaskCanceledException)
                    {
                        // Time to die. Do not run ReportAsync.
                        return false;
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException || e is SystemException)
            {
                // SystemException would come from WaitForExit.
                return false;
            }
            catch (Exception e)
            {
                ObserverLogger.LogWarning($"Unhandled Exception in MonitorContainers:{Environment.NewLine}{e}");

                // no-op. Bye.
                throw;
            }

            return true;
        }

        private bool SetConfigurationFilePath()
        {
            // Already set.
            if (File.Exists(ConfigurationFilePath))
            {
                return true;
            }

            string configFilename = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.ConfigurationFileNameParameter);

            if (string.IsNullOrWhiteSpace(configFilename))
            {
                return false;
            }

            string path = Path.Combine(ConfigPackage.Path, configFilename);

            if (File.Exists(path))
            {
                ConfigurationFilePath = path;
                return true;
            }

            return false;
        }

        private async Task SetInstanceOrReplicaMonitoringList(
                            Uri appName,
                            string[] filterList,
                            ServiceFilterType filterType,
                            string appTypeName)
        {

            var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () => FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, appName),
                                               Token);
            // DEBUG
            //var stopwatch = Stopwatch.StartNew();
            _ = Parallel.For(0, deployedReplicaList.Count, ParallelOptions, (i, state) =>
            {
                Token.ThrowIfCancellationRequested();

                var deployedReplica = deployedReplicaList[i];
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica statefulReplica when statefulReplica.ReplicaRole == ReplicaRole.Primary || statefulReplica.ReplicaRole == ReplicaRole.ActiveSecondary:
                        {
                            if (filterList != null && filterType != ServiceFilterType.None)
                            {
                                bool isInFilterList = filterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                                switch (filterType)
                                {
                                    case ServiceFilterType.Include when !isInFilterList:
                                    case ServiceFilterType.Exclude when isInFilterList:
                                        return;
                                }
                            }

                            replicaInfo = new ReplicaOrInstanceMonitoringInfo
                            {
                                ApplicationName = appName,
                                ApplicationTypeName = appTypeName,
                                HostProcessId = statefulReplica.HostProcessId,
                                ReplicaOrInstanceId = statefulReplica.ReplicaId,
                                PartitionId = statefulReplica.Partitionid,
                                ReplicaRole = statefulReplica.ReplicaRole,
                                ServiceKind = statefulReplica.ServiceKind,
                                ServiceName = statefulReplica.ServiceName,
                                ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                                ServicePackageActivationMode = string.IsNullOrWhiteSpace(statefulReplica.ServicePackageActivationId) ?
                                    ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                                ReplicaStatus = statefulReplica.ReplicaStatus
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
                                        return;
                                }
                            }

                            replicaInfo = new ReplicaOrInstanceMonitoringInfo
                            {
                                ApplicationName = appName,
                                ApplicationTypeName = appTypeName,
                                HostProcessId = statelessInstance.HostProcessId,
                                ReplicaOrInstanceId = statelessInstance.InstanceId,
                                PartitionId = statelessInstance.Partitionid,
                                ReplicaRole = ReplicaRole.None,
                                ServiceKind = statelessInstance.ServiceKind,
                                ServiceName = statelessInstance.ServiceName,
                                ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                                ServicePackageActivationMode = string.IsNullOrWhiteSpace(statelessInstance.ServicePackageActivationId) ?
                                    ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                                ReplicaStatus = statelessInstance.ReplicaStatus
                            };
                            break;
                        }
                }

                if (replicaInfo?.HostProcessId > 0 && !ReplicaOrInstanceList.Any(r => r.ServiceName == replicaInfo.ServiceName))
                {
                    ReplicaOrInstanceList.Enqueue(replicaInfo);
                }
            });
        }
    }
}