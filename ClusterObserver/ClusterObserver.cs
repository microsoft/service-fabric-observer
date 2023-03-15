// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClusterObserver.Utilities;
using FabricObserver.Observers;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.TelemetryLib;
using System.Fabric.Description;
using System.IO;

namespace ClusterObserver
{
    public sealed class ClusterObserver : ObserverBase
    {
        private readonly Uri repairManagerServiceUri = new($"{ObserverConstants.SystemAppName}/RepairManagerService");
        private readonly Uri fabricSystemAppUri = new(ObserverConstants.SystemAppName);
        private readonly bool ignoreDefaultQueryTimeout;

        private HealthState LastKnownClusterHealthState
        {
            get; set;
        } = HealthState.Unknown;

        /// <summary>
        /// Dictionary that holds node name (key) and tuple of node status, first detected time, last detected time.
        /// </summary>
        private Dictionary<string, (NodeStatus NodeStatus, DateTime FirstDetectedTime, DateTime LastDetectedTime)> NodeStatusDictionary
        {
            get;
        }

        private Dictionary<string, bool> ApplicationUpgradesCompletedStatus
        {
            get;
        }

        public TimeSpan MaxTimeNodeStatusNotOk
        {
            get; set;
        } = TimeSpan.FromHours(2.0);

        public bool MonitorRepairJobStatus
        {
            get; set;
        }

        public bool MonitorUpgradeStatus
        {
            get; set;
        }

        public bool HasClusterUpgradeCompleted 
        {
            get; set; 
        }

        public bool EmitWarningDetails 
        { 
            get; set; 
        }

        public ClusterObserver(StatelessServiceContext serviceContext, bool ignoreDefaultQueryTimeout = false) 
            : base (null, serviceContext)
        {
            NodeStatusDictionary = new Dictionary<string, (NodeStatus NodeStatus, DateTime FirstDetectedTime, DateTime LastDetectedTime)>();
            ApplicationUpgradesCompletedStatus = new Dictionary<string, bool>();
            
            this.ignoreDefaultQueryTimeout = ignoreDefaultQueryTimeout;

            // Observer Logger setup. This will override the default setup in FabricObserver.Extensibility.dll.
            string logFolderBasePath;
            string observerLogPath = GetSettingParameterValue(
                     ClusterObserverConstants.ObserverManagerConfigurationSectionName,
                     ClusterObserverConstants.ObserverLogPathParameter);

            if (!string.IsNullOrWhiteSpace(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "cluster_observer_logs");
                logFolderBasePath = logFolderBase;
            }

            ObserverLogger = new Logger(GetType().Name, logFolderBasePath, 7)
            {
                EnableETWLogging = IsEtwProviderEnabled
            };
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            if (!IsEnabled || (!IsTelemetryEnabled && !IsEtwEnabled)
                || (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            // This is the RunAsync SF runtime cancellation token.
            Token = token;
            SetPropertiesFromApplicationSettings();
            await ReportAsync(Token);
            LastRunDateTime = DateTime.Now;
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            await ReportClusterHealthAsync();
        }

        private void SetPropertiesFromApplicationSettings()
        {
            if (TimeSpan.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ClusterObserverConstants.MaxTimeNodeStatusNotOkSettingParameter), out TimeSpan maxTime))
            {
                MaxTimeNodeStatusNotOk = maxTime;
            }

            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ClusterObserverConstants.EmitHealthWarningEvaluationConfigurationSetting), out bool emitWarnings))
            {
                EmitWarningDetails = emitWarnings;
            }

            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ClusterObserverConstants.MonitorRepairJobsConfigurationSetting), out bool monitorRepairJobs))
            {
                MonitorRepairJobStatus = monitorRepairJobs;
            }

            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ClusterObserverConstants.MonitorUpgradesConfigurationSetting), out bool monitorUpgrades))
            {
                MonitorUpgradeStatus = monitorUpgrades;
            }
        }

        private async Task ReportClusterHealthAsync()
        {
            try
            {
                // Monitor node status.
                await MonitorNodeStatusAsync(Token, ignoreDefaultQueryTimeout);

                // Check for active repairs in the cluster.
                if (MonitorRepairJobStatus)
                {
                    var repairsInProgress = await GetRepairTasksCurrentlyProcessingAsync(Token);
                    string repairState = string.Empty;

                    if (repairsInProgress?.Count > 0)
                    {
                        string ids = string.Empty;

                        foreach (var repair in repairsInProgress)
                        {
                            Token.ThrowIfCancellationRequested();
                            ids += $"TaskId: {repair.TaskId}{Environment.NewLine}State: {repair.State}{Environment.NewLine}";
                        }

                        repairState += $"There are currently one or more Repair Jobs processing in the cluster.{Environment.NewLine}{ids}";

                        var telemetry = new ClusterTelemetryData()
                        {
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = EntityType.Cluster,
                            HealthState = HealthState.Ok,
                            Description = repairState,
                            Metric = "RepairJobs",
                            Source = ObserverName
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetry, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetry);
                        }
                    }
                }

                // Check cluster upgrade status.
                if (MonitorUpgradeStatus)
                {
                    await ReportClusterUpgradeStatusAsync(Token);
                }

                var clusterQueryDesc = new ClusterHealthQueryDescription
                {
                    EventsFilter = new HealthEventsFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    ApplicationsFilter = new ApplicationHealthStatesFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    NodesFilter = new NodeHealthStatesFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    HealthPolicy = new ClusterHealthPolicy(),
                    HealthStatisticsFilter = new ClusterHealthStatisticsFilter
                    {
                        ExcludeHealthStatistics = false,
                        IncludeSystemApplicationHealthStatistics = true
                    }
                };

                ClusterHealth clusterHealth =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () =>
                                FabricClientInstance.HealthManager.GetClusterHealthAsync(
                                    clusterQueryDesc,
                                    ConfigurationSettings.AsyncTimeout,
                                    Token),
                            Token);

                // Previous aggregated cluster health state was Error or Warning. It's now Ok.
                if (clusterHealth.AggregatedHealthState == HealthState.Ok && (LastKnownClusterHealthState == HealthState.Error
                    || (EmitWarningDetails && LastKnownClusterHealthState == HealthState.Warning)))
                {
                    LastKnownClusterHealthState = HealthState.Ok;

                    var telemetry = new ClusterTelemetryData()
                    {
                        ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                        EntityType = EntityType.Cluster,
                        HealthState = HealthState.Ok,
                        Description = $"Cluster has recovered from previous {LastKnownClusterHealthState} state.",
                        Metric = "AggregatedClusterHealth",
                        Source = ObserverName
                    };

                    // Telemetry.
                    if (IsTelemetryEnabled)
                    {
                        if (TelemetryClient != null)
                        {
                            await TelemetryClient.ReportHealthAsync(telemetry, Token);
                        }
                    }

                    // ETW.
                    if (IsEtwEnabled)
                    {
                        ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetry);
                    }

                    return;
                }

                // Cluster is healthy. Nothing to do here.
                if (clusterHealth.AggregatedHealthState == HealthState.Ok)
                {
                    return;
                }

                // If in Warning and you are not sending Warning state reports, then end here.
                if (!EmitWarningDetails && clusterHealth.AggregatedHealthState == HealthState.Warning)
                {
                    return;
                }

                // Process node health.
                if (clusterHealth.NodeHealthStates != null && clusterHealth.NodeHealthStates.Count > 0)
                {
                    try
                    {
                        await ProcessNodeHealthAsync(clusterHealth.NodeHealthStates, Token);
                    }
                    catch (Exception e) when (e is FabricException || e is TimeoutException)
                    {
#if DEBUG
                        ObserverLogger.LogInfo($"Handled Exception in ReportClusterHealthAsync::Node:{Environment.NewLine}{e.Message}");
#endif
                    }
                }

                // Process Application/Service health.
                if (clusterHealth.ApplicationHealthStates != null && clusterHealth.ApplicationHealthStates.Count > 0)
                {
                    foreach (var app in clusterHealth.ApplicationHealthStates)
                    {
                        Token.ThrowIfCancellationRequested();

                        try
                        {
                            if (app.ApplicationName.OriginalString == ObserverConstants.SystemAppName)
                            {
                                await ProcessApplicationHealthAsync(app, Token);
                            }

                            var appHealth =
                                await FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                        app.ApplicationName,
                                        ConfigurationSettings.AsyncTimeout,
                                        Token);

                            if (appHealth.ServiceHealthStates != null && appHealth.ServiceHealthStates.Count > 0)
                            {
                                foreach (var service in appHealth.ServiceHealthStates)
                                {
                                    if (service.AggregatedHealthState == HealthState.Ok)
                                    {
                                        continue;
                                    }

                                    await ProcessServiceHealthAsync(service, Token);
                                }
                            }
                            else
                            {
                                await ProcessApplicationHealthAsync(app, Token);
                            }
                        }
                        catch (Exception e) when (e is FabricException || e is TimeoutException)
                        {
#if DEBUG
                            ObserverLogger.LogInfo($"Handled Exception in ReportClusterHealthAsync::Application:{Environment.NewLine}{e.Message}");
#endif
                        }
                    }
                }

                // Track current aggregated health state for use in next run.
                LastKnownClusterHealthState = clusterHealth.AggregatedHealthState;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
                string msg = $"Handled transient exception in ReportClusterHealthAsync:{Environment.NewLine}{e}";

                // Log it locally.
                ObserverLogger.LogWarning(msg);
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                string msg = $"Unhandled exception in ReportClusterHealthAsync:{Environment.NewLine}{e}";

                // Log it locally.
                ObserverLogger.LogWarning(msg);

                var telemetryData = new ClusterTelemetryData()
                {
                    ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                    EntityType = EntityType.Cluster,
                    HealthState = HealthState.Warning,
                    Description = msg,
                    Source = ObserverName
                };

                // Send Telemetry.
                if (IsTelemetryEnabled)
                {
                    if (TelemetryClient != null)
                    {
                        await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                    }
                }

                // Emit ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                }

                // Fix the bug.
                throw;
            }
        }

        private async Task ReportApplicationUpgradeStatus(Uri appName, CancellationToken Token)
        {
            ServiceFabricUpgradeEventData appUpgradeInfo =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => UpgradeChecker.GetApplicationUpgradeDetailsAsync(FabricClientInstance, Token, appName),
                            Token);

            if (appUpgradeInfo?.ApplicationUpgradeProgress == null || Token.IsCancellationRequested)
            {
                return;
            }

            if (appUpgradeInfo.ApplicationUpgradeProgress.UpgradeState == ApplicationUpgradeState.Invalid
                || appUpgradeInfo.ApplicationUpgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName == "-1")
            {
                return;
            }

            if (appUpgradeInfo.ApplicationUpgradeProgress.UpgradeState == ApplicationUpgradeState.RollingForwardCompleted
                || appUpgradeInfo.ApplicationUpgradeProgress.UpgradeState == ApplicationUpgradeState.RollingBackCompleted)
            {
                if (ApplicationUpgradesCompletedStatus.ContainsKey(appName.OriginalString))
                {
                    if (ApplicationUpgradesCompletedStatus[appName.OriginalString])
                    {
                        return;
                    }

                    ApplicationUpgradesCompletedStatus[appName.OriginalString] = true;
                }
                else
                {
                    ApplicationUpgradesCompletedStatus.Add(appName.OriginalString, true);
                }
            }
            else
            {
                if (ApplicationUpgradesCompletedStatus.ContainsKey(appName.OriginalString))
                {
                    ApplicationUpgradesCompletedStatus[appName.OriginalString] = false;
                }
                else
                {
                    ApplicationUpgradesCompletedStatus.Add(appName.OriginalString, false);
                }
            }

            // Telemetry.
            if (IsTelemetryEnabled)
            {
                if (TelemetryClient != null)
                {
                    await TelemetryClient.ReportApplicationUpgradeStatusAsync(appUpgradeInfo, Token);
                }
            }

            // ETW.
            if (IsEtwEnabled)
            {
                ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, appUpgradeInfo);
            }
        }

        private async Task ReportClusterUpgradeStatusAsync(CancellationToken Token)
        {
            var eventData =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                             () => UpgradeChecker.GetClusterUpgradeDetailsAsync(FabricClientInstance, Token), Token);

            if (eventData?.FabricUpgradeProgress == null || Token.IsCancellationRequested)
            {
                return;
            }

            if (eventData.FabricUpgradeProgress.UpgradeState == FabricUpgradeState.Invalid
                || eventData.FabricUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName == "-1")
            {
                return;
            }

            if (eventData.FabricUpgradeProgress.UpgradeState == FabricUpgradeState.RollingForwardCompleted
                || eventData.FabricUpgradeProgress.UpgradeState == FabricUpgradeState.RollingBackCompleted)
            {
                if (HasClusterUpgradeCompleted)
                {
                    return;
                }

                HasClusterUpgradeCompleted = true;
            }
            else
            {
                HasClusterUpgradeCompleted = false;
            }

            // Telemetry.
            if (IsTelemetryEnabled)
            {
                if (TelemetryClient != null)
                {
                    await TelemetryClient.ReportClusterUpgradeStatusAsync(eventData, Token);
                }
            }

            // ETW.
            if (IsEtwEnabled)
            {
                ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, eventData);
            }
        }

        private async Task ProcessApplicationHealthAsync(ApplicationHealthState appHealthState, CancellationToken Token)
        {
            string telemetryDescription = string.Empty;
            var appHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                            appHealthState.ApplicationName,
                                            ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigurationSettings.AsyncTimeout,
                                            Token),
                                    Token);
            
            if (appHealth == null)
            {
                return;
            }

            Uri appName = appHealthState.ApplicationName;

            // Check upgrade status of unhealthy application. Note, this doesn't apply to System applications as they update as part of a platform update.
            if (MonitorUpgradeStatus && !appName.Equals(fabricSystemAppUri))
            {
                await ReportApplicationUpgradeStatus(appName, Token);
            }

            var appHealthEvents =
                appHealth.HealthEvents.Where(
                    e => e.HealthInformation.HealthState == HealthState.Error || e.HealthInformation.HealthState == HealthState.Warning).ToList();

            if (!appHealthEvents.Any())
            {
                return;
            }

            foreach (HealthEvent healthEvent in appHealthEvents.OrderByDescending(f => f.SourceUtcTimestamp))
            {
                if (healthEvent.HealthInformation.HealthState != HealthState.Error && healthEvent.HealthInformation.HealthState != HealthState.Warning)
                {
                    continue;
                }

                // TelemetryData?
                if (TryGetTelemetryData(healthEvent, out TelemetryDataBase foTelemetryData))
                { 
                    // Telemetry.
                    if (IsTelemetryEnabled)
                    {
                        if (TelemetryClient != null)
                        {
                            await TelemetryClient.ReportHealthAsync(foTelemetryData, Token);
                        }
                    }

                    // ETW.
                    if (IsEtwEnabled)
                    {
                        ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, foTelemetryData);
                    }
                }
                else // Not from FO/FHProxy.
                {
                    var applicationHealth =
                        await FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                appName,
                                ConfigurationSettings.AsyncTimeout,
                                Token);

                    await ProcessEntityHealthAsync(applicationHealth, Token);
                    
                }
            }
        }

        private async Task ProcessServiceHealthAsync(ServiceHealthState serviceHealthState, CancellationToken Token)
        {
            Uri appName;
            Uri serviceName = serviceHealthState.ServiceName;
            ServiceHealth serviceHealth =
                await FabricClientInstance.HealthManager.GetServiceHealthAsync(serviceName, ConfigurationSettings.AsyncTimeout, Token);

            ApplicationNameResult name =
                await FabricClientInstance.QueryManager.GetApplicationNameAsync(serviceName, ConfigurationSettings.AsyncTimeout, Token);

            appName = name.ApplicationName;
            IList<HealthEvent> healthEvents = serviceHealth.HealthEvents;

            if (serviceHealth.PartitionHealthStates.Any(
                    p => p.AggregatedHealthState == HealthState.Error || p.AggregatedHealthState == HealthState.Warning))
            {
                var partitionHealthStates =
                    serviceHealth.PartitionHealthStates.Where(
                        p => p.AggregatedHealthState == HealthState.Warning || p.AggregatedHealthState == HealthState.Error);

                foreach (var partitionHealthState in partitionHealthStates)
                {
                    var partitionHealth =
                        await FabricClientInstance.HealthManager.GetPartitionHealthAsync(
                                partitionHealthState.PartitionId,
                                ConfigurationSettings.AsyncTimeout,
                                Token);

                    await ProcessEntityHealthAsync(partitionHealth, Token);

                    var replicaHealthStates =
                        partitionHealth.ReplicaHealthStates.Where(
                            p => p.AggregatedHealthState == HealthState.Warning || p.AggregatedHealthState == HealthState.Error);

                    if (replicaHealthStates != null && replicaHealthStates.Any())
                    {
                        foreach (var replica in replicaHealthStates)
                        {
                            var replicaHealth =
                                await FabricClientInstance.HealthManager.GetReplicaHealthAsync(
                                        partitionHealthState.PartitionId,
                                        replica.Id,
                                        ConfigurationSettings.AsyncTimeout,
                                        Token);

                            if (replicaHealth != null)
                            {
                                var replicaEvents =
                                    replicaHealth.HealthEvents.Where(
                                        h => h.HealthInformation.HealthState == HealthState.Warning
                                          || h.HealthInformation.HealthState == HealthState.Error).ToList();

                                if (!replicaEvents.Any(h => JsonHelper.TryDeserializeObject<TelemetryDataBase>(h.HealthInformation.Description, out _)))
                                {
                                    await ProcessEntityHealthAsync(replicaHealth, Token);
                                }
                            }
                        }
                    }
                }
            }

            // From FO/FHProxy or some other service/component created an SF health event.
            foreach (HealthEvent healthEvent in healthEvents.OrderByDescending(f => f.SourceUtcTimestamp))
            {
                if (healthEvent.HealthInformation.HealthState != HealthState.Error && healthEvent.HealthInformation.HealthState != HealthState.Warning)
                {
                    continue;
                }

                // HealthInformation.Description == serialized instance of TelemetryDataBase type?
                if (TryGetTelemetryData(healthEvent, out TelemetryDataBase foTelemetryData))
                {
                    // Telemetry.
                    if (IsTelemetryEnabled)
                    {
                        if (TelemetryClient != null)
                        {
                            await TelemetryClient.ReportHealthAsync(foTelemetryData, Token);
                        }
                    }

                    // ETW.
                    if (IsEtwEnabled)
                    {
                        ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, foTelemetryData);
                    }
                }
                else // Not from FO/FHProxy.
                {
                    var serviceEntityHealth = 
                        await FabricClientInstance.HealthManager.GetServiceHealthAsync(serviceName, ConfigurationSettings.AsyncTimeout, Token);

                    await ProcessEntityHealthAsync(serviceEntityHealth, Token);
                }
            }
        }

        private async Task ProcessNodeHealthAsync(IEnumerable<NodeHealthState> nodeHealthStates, CancellationToken Token)
        {
            // Check cluster upgrade status. This will be used to help determine if a node in Error is in that state because of a Fabric runtime upgrade.
            var clusterUpgradeInfo =
                await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                         () => 
                            UpgradeChecker.GetClusterUpgradeDetailsAsync(FabricClientInstance, Token),
                         Token);

            var supportedNodeHealthStates = nodeHealthStates.Where( a => a.AggregatedHealthState == HealthState.Warning || a.AggregatedHealthState == HealthState.Error);

            foreach (var node in supportedNodeHealthStates)
            {
                Token.ThrowIfCancellationRequested();

                if (node.AggregatedHealthState == HealthState.Ok || (!EmitWarningDetails && node.AggregatedHealthState == HealthState.Warning))
                {
                    continue;
                }

                string telemetryDescription = string.Empty;

                if (node.AggregatedHealthState == HealthState.Error
                    && clusterUpgradeInfo != null
                    && clusterUpgradeInfo.FabricUpgradeProgress.CurrentUpgradeDomainProgress.NodeProgressList.Any(n => n.NodeName == node.NodeName))
                {
                    telemetryDescription +=
                        $"Note: Cluster is currently upgrading in UD {clusterUpgradeInfo.FabricUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName}. " +
                        $"Node {node.NodeName} Error State could be due to this upgrade, which will temporarily take down a node as a " +
                        $"normal part of the upgrade process.{Environment.NewLine}";
                }

                var nodeHealth =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => 
                                FabricClientInstance.HealthManager.GetNodeHealthAsync(
                                    node.NodeName, ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigurationSettings.AsyncTimeout, Token),
                            Token);

                foreach (var nodeHealthEvent in nodeHealth.HealthEvents.Where(ev => ev.HealthInformation.HealthState != HealthState.Ok))
                {
                    Token.ThrowIfCancellationRequested();

                    var targetNodeList =
                        await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                () =>
                                    FabricClientInstance.QueryManager.GetNodeListAsync(
                                        node.NodeName,
                                        ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigurationSettings.AsyncTimeout,
                                        Token),
                                Token);

                    if (targetNodeList?.Count > 0)
                    {
                        Node targetNode = targetNodeList[0];

                        if (TryGetTelemetryData(nodeHealthEvent, out TelemetryDataBase telemetryData))
                        {
                            telemetryData.Description += 
                                $"{Environment.NewLine}Node Status: {(targetNode != null ? targetNode.NodeStatus.ToString() : string.Empty)}";

                            // Telemetry (AppInsights/LogAnalytics..).
                            if (IsTelemetryEnabled)
                            {
                                if (TelemetryClient != null)
                                {
                                    await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                                }
                            }

                            // ETW.
                            if (IsEtwEnabled)
                            {
                                ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(nodeHealthEvent.HealthInformation.Description))
                        {
                            telemetryDescription += 
                                $"{nodeHealthEvent.HealthInformation.Description}{Environment.NewLine}" +
                                $"Node Status: {(targetNode != null ? targetNode.NodeStatus.ToString() : string.Empty)}";

                            var telemData = new NodeTelemetryData()
                            {
                                ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                                EntityType = EntityType.Node,
                                NodeName = targetNode?.NodeName ?? node.NodeName,
                                NodeType = targetNode?.NodeType,
                                Source = ObserverName,
                                HealthState = targetNode?.HealthState ?? node.AggregatedHealthState,
                                Description = telemetryDescription
                            };

                            // Telemetry.
                            if (IsTelemetryEnabled)
                            {
                                if (TelemetryClient != null)
                                {
                                    await TelemetryClient.ReportHealthAsync(telemData, Token);
                                }
                            }

                            // ETW.
                            if (IsEtwEnabled)
                            {
                                ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemData);
                            }
                        }
                    }

                    // Reset 
                    telemetryDescription = string.Empty;
                }
            }
        }

        private async Task ProcessEntityHealthAsync<EntityHealth>(EntityHealth entityHealth, CancellationToken Token)
        {
            try
            {
                if (entityHealth is ApplicationHealth appHealth)
                {
                    if (appHealth.HealthEvents == null || appHealth.HealthEvents.Count == 0)
                    {
                        return;
                    }

                    foreach (var healthEvent in appHealth.HealthEvents.Where(
                                e => e.HealthInformation.HealthState == HealthState.Error || e.HealthInformation.HealthState == HealthState.Warning))
                    {
                        var telemetryData = new ServiceTelemetryData
                        {
                            ApplicationName = appHealth.ApplicationName.OriginalString,
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = EntityType.Application,
                            Metric = "AppHealth",
                            Property = healthEvent.HealthInformation.Property,
                            Description = healthEvent.HealthInformation.Description,
                            HealthState = healthEvent.HealthInformation.HealthState,
                            Source = healthEvent.HealthInformation.SourceId
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                        }
                    }
                }
                else if (entityHealth is DeployedApplicationHealth deployedAppHealth)
                {
                    if (deployedAppHealth.HealthEvents != null || deployedAppHealth.HealthEvents.Count == 0)
                    {
                        return;
                    }

                    foreach (var healthEvent in deployedAppHealth.HealthEvents.Where(
                                e => e.HealthInformation.HealthState == HealthState.Error || e.HealthInformation.HealthState == HealthState.Warning))
                    {
                        var telemetryData = new ServiceTelemetryData
                        {
                            ApplicationName = deployedAppHealth.ApplicationName.OriginalString,
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = EntityType.Application,
                            Metric = "AppHealth",
                            NodeName = deployedAppHealth.NodeName,
                            Property = healthEvent.HealthInformation.Property,
                            Description = healthEvent.HealthInformation.Description,
                            HealthState = healthEvent.HealthInformation.HealthState,
                            Source = healthEvent.HealthInformation.SourceId
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                        }
                    }
                }
                else if (entityHealth is DeployedServicePackageHealth depServicePackageHealth)
                {
                    if (depServicePackageHealth.HealthEvents == null || depServicePackageHealth.HealthEvents.Count == 0)
                    {
                        return;
                    }

                    var deployedServicePackages =
                            await FabricClientInstance.QueryManager.GetDeployedServicePackageListAsync(
                                    depServicePackageHealth.NodeName,
                                    depServicePackageHealth.ApplicationName,
                                    depServicePackageHealth.ServiceManifestName,
                                    ConfigurationSettings.AsyncTimeout,
                                    Token);

                    if (deployedServicePackages?.Count == 0)
                    {
                        return;
                    }

                    foreach (var healthEvent in depServicePackageHealth.HealthEvents.Where(
                                e => e.HealthInformation.HealthState == HealthState.Error || e.HealthInformation.HealthState == HealthState.Warning))
                    {
                        var telemetryData = new ServiceTelemetryData
                        {
                            ApplicationName = depServicePackageHealth.ApplicationName.OriginalString,
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = EntityType.Service,
                            Metric = "DeployedServicePkgHealth",
                            NodeName = depServicePackageHealth.NodeName,
                            Property = healthEvent.HealthInformation.Property,
                            Description = healthEvent.HealthInformation.Description,
                            HealthState = healthEvent.HealthInformation.HealthState,
                            Source = healthEvent.HealthInformation.SourceId
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                        }
                    }
                }
                else if (entityHealth is NodeHealth nodeHealth)
                {
                    if (nodeHealth.HealthEvents == null || nodeHealth.HealthEvents.Count == 0)
                    {
                        return;
                    }

                    foreach (var healthEvent in nodeHealth.HealthEvents.Where(
                                e => e.HealthInformation.HealthState == HealthState.Error || e.HealthInformation.HealthState == HealthState.Warning))
                    {
                        var telemetryData = new NodeTelemetryData
                        {
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = EntityType.Node,
                            Metric = "NodeHealth",
                            NodeName = nodeHealth.NodeName,
                            Property = healthEvent.HealthInformation.Property,
                            Description = healthEvent.HealthInformation.Description,
                            HealthState = healthEvent.HealthInformation.HealthState,
                            Source = healthEvent.HealthInformation.SourceId
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                        }
                    }
                }
                else if (entityHealth is PartitionHealth partitionHealth)
                {
                    if (partitionHealth.HealthEvents == null || partitionHealth.HealthEvents.Count == 0)
                    {
                        return;
                    }

                    foreach (var healthEvent in partitionHealth.HealthEvents.Where(
                                e => e.HealthInformation.HealthState == HealthState.Error || e.HealthInformation.HealthState == HealthState.Warning))
                    {
                        var telemetryData = new ServiceTelemetryData
                        {
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = EntityType.Partition,
                            Metric = "PartitionHealth",
                            Property = healthEvent.HealthInformation.Property,
                            PartitionId = partitionHealth.PartitionId.ToString(),
                            Description = healthEvent.HealthInformation.Description,
                            HealthState = healthEvent.HealthInformation.HealthState,
                            Source = healthEvent.HealthInformation.SourceId
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                        }
                    }
                }
                else if (entityHealth is ReplicaHealth replicaHealth)
                {
                    if (replicaHealth.HealthEvents == null || replicaHealth.HealthEvents.Count == 0)
                    {
                        return;
                    }

                    var replicaList =
                            await FabricClientInstance.QueryManager.GetReplicaListAsync(
                                    replicaHealth.PartitionId,
                                    replicaHealth.Id,
                                    ConfigurationSettings.AsyncTimeout,
                                    Token);

                    if (replicaList?.Count == 0)
                    {
                        return;
                    }

                    string serviceKind = replicaList[0].ServiceKind.ToString();
                    string nodeName = replicaList[0].NodeName;

                    foreach (var healthEvent in replicaHealth.HealthEvents.Where(
                                e => e.HealthInformation.HealthState == HealthState.Error || e.HealthInformation.HealthState == HealthState.Warning))
                    {
                        var telemetryData = new ServiceTelemetryData
                        {
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = EntityType.Replica,
                            Metric = "ReplicaHealth",
                            NodeName = nodeName,
                            Property = healthEvent.HealthInformation.Property,
                            PartitionId = replicaHealth.PartitionId.ToString(),
                            ReplicaId = replicaHealth.Id,
                            Description = healthEvent.HealthInformation.Description,
                            HealthState = healthEvent.HealthInformation.HealthState,
                            ServiceKind = serviceKind,
                            Source = healthEvent.HealthInformation.SourceId
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                        }
                    }
                }
                else if (entityHealth is ServiceHealth serviceHealth)
                {
                    ApplicationNameResult appNameResult =
                        await FabricClientInstance.QueryManager.GetApplicationNameAsync(serviceHealth.ServiceName, ConfigurationSettings.AsyncTimeout, Token);

                    if (appNameResult == null)
                    {
                        return;
                    }

                    ServiceList serviceList =
                        await FabricClientInstance.QueryManager.GetServiceListAsync(appNameResult.ApplicationName, serviceHealth.ServiceName, ConfigurationSettings.AsyncTimeout, Token);

                    if (serviceList == null || serviceList.Count == 0)
                    {
                        return;
                    }

                    if (serviceHealth.HealthEvents == null || serviceHealth.HealthEvents.Count == 0)
                    {
                        return;
                    }

                    foreach (var healthEvent in serviceHealth.HealthEvents.Where(
                                e => e.HealthInformation.HealthState == HealthState.Error || e.HealthInformation.HealthState == HealthState.Warning))
                    {

                        var telemetryData = new ServiceTelemetryData
                        {
                            ApplicationName = appNameResult?.ApplicationName.OriginalString,
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = EntityType.Service,
                            Metric = "ServiceHealth",
                            Property = healthEvent.HealthInformation.Property,
                            ServiceName = serviceHealth.ServiceName.OriginalString,
                            ServiceKind = serviceList[0].ServiceKind.ToString(),
                            ServiceTypeName = serviceList[0].ServiceTypeName,
                            ServiceTypeVersion = serviceList[0].ServiceManifestVersion,
                            Description = healthEvent.HealthInformation.Description,
                            HealthState = healthEvent.HealthInformation.HealthState,
                            Source = healthEvent.HealthInformation.SourceId
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetryData);
                        }
                    }
                }
            }
            catch (FabricException fe)
            {
                ObserverLogger.LogWarning($"Exception in ProcessGenericHealthEntityAsync: {fe.Message}.");
            }
        }

        private async Task MonitorNodeStatusAsync(CancellationToken Token, bool isTest = false)
        {
            // If a node's NodeStatus is Disabling, Disabled, or Down 
            // for at or above the specified maximum time (in ApplicationManifest.xml, see MaxTimeNodeStatusNotOk),
            // then CO will emit a Warning signal.
            var nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () =>
                                        FabricClientInstance.QueryManager.GetNodeListAsync(
                                                null,
                                                isTest ? TimeSpan.FromSeconds(1) : ConfigurationSettings.AsyncTimeout,
                                                Token),
                                     Token);

            // Are any of the nodes that were previously in non-Up status, now Up?
            if (NodeStatusDictionary.Count > 0)
            {
                foreach (var nodeDictItem in NodeStatusDictionary)
                {
                    if (!nodeList.Any(n => n.NodeName == nodeDictItem.Key && n.NodeStatus == NodeStatus.Up))
                    {
                        continue;
                    }

                    var telemetry = new NodeTelemetryData()
                    {
                        ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                        HealthState = HealthState.Ok,
                        Description = $"{nodeDictItem.Key} is now Up.",
                        Metric = "NodeStatus",
                        NodeName = nodeDictItem.Key,
                        NodeType = nodeList.Any(n => n.NodeName == nodeDictItem.Key) ? nodeList.First(n => n.NodeName == nodeDictItem.Key).NodeType : null,
                        Source = ObserverName,
                        Value = 0
                    };

                    // Telemetry.
                    if (IsTelemetryEnabled)
                    {
                        if (TelemetryClient != null)
                        {
                            await TelemetryClient.ReportHealthAsync(telemetry, Token);
                        }
                    }

                    // ETW.
                    if (IsEtwEnabled)
                    {
                        ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetry);
                    }

                    // Clear dictionary entry.
                    NodeStatusDictionary.Remove(nodeDictItem.Key);
                }
            }

            if (nodeList.Any(n => n.NodeStatus != NodeStatus.Up))
            {
                var filteredList = nodeList.Where(
                         node => node.NodeStatus == NodeStatus.Disabled
                              || node.NodeStatus == NodeStatus.Disabling
                              || node.NodeStatus == NodeStatus.Down);

                foreach (var node in filteredList)
                {
                    if (!NodeStatusDictionary.ContainsKey(node.NodeName))
                    {
                        NodeStatusDictionary.Add(node.NodeName, (node.NodeStatus, DateTime.Now, DateTime.Now));
                    }
                    else
                    {
                        if (NodeStatusDictionary.TryGetValue(node.NodeName, out var tuple))
                        {
                            NodeStatusDictionary[node.NodeName] = (node.NodeStatus, tuple.FirstDetectedTime, DateTime.Now);
                        }
                    }

                    // Nodes stuck in Disabled/Disabling/Down?
                    if (NodeStatusDictionary.Any(
                             dict => dict.Key == node.NodeName
                                && dict.Value.LastDetectedTime.Subtract(dict.Value.FirstDetectedTime)
                                >= MaxTimeNodeStatusNotOk))
                    {
                        var kvp = NodeStatusDictionary.FirstOrDefault(
                                       dict => dict.Key == node.NodeName
                                        && dict.Value.LastDetectedTime.Subtract(dict.Value.FirstDetectedTime)
                                        >= MaxTimeNodeStatusNotOk);

                        var message =
                            $"Node {kvp.Key} has been {kvp.Value.NodeStatus} " +
                            $"for {Math.Round(kvp.Value.LastDetectedTime.Subtract(kvp.Value.FirstDetectedTime).TotalHours, 2)} hours.{Environment.NewLine}";

                        var telemetry = new NodeTelemetryData()
                        {
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            HealthState = HealthState.Warning,
                            Description = message,
                            Metric = "NodeStatus",
                            NodeName = kvp.Key,
                            NodeType = nodeList.Any(n => n.NodeName == kvp.Key) ? nodeList.First(n => n.NodeName == kvp.Key).NodeType : null,
                            Source = ObserverName,
                            Value = 1,
                        };

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            if (TelemetryClient != null)
                            {
                                await TelemetryClient.ReportHealthAsync(telemetry, Token);
                            }
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemetry);
                        }
                    }
                }
            }
        }

        private static bool TryGetTelemetryData(HealthEvent healthEvent, out TelemetryDataBase telemetryData)
        {
            if (JsonHelper.TryDeserializeObject(healthEvent.HealthInformation.Description, out TelemetryData telemData))
            {
                switch (telemData.ObserverName)
                {
                    case ObserverConstants.AppObserverName:
                    case ObserverConstants.ContainerObserverName:
                    case ObserverConstants.FabricSystemObserverName:

                        if (JsonHelper.TryDeserializeObject(healthEvent.HealthInformation.Description, out ServiceTelemetryData serviceTelemetryData))
                        {
                            telemetryData = serviceTelemetryData;
                            return true;
                        }
                        break;

                    case ObserverConstants.DiskObserverName:

                        // enforce strict type member handling in Json deserialization as this type has specific properties that are unique to it.
                        if (JsonHelper.TryDeserializeObject(healthEvent.HealthInformation.Description, out DiskTelemetryData diskTelemetryData, treatMissingMembersAsError: true))
                        {
                            telemetryData = diskTelemetryData;
                            return true;
                        }
                        break;

                    case ObserverConstants.NodeObserverName:

                        if (JsonHelper.TryDeserializeObject(healthEvent.HealthInformation.Description, out NodeTelemetryData nodeTelemetryData))
                        {
                            telemetryData = nodeTelemetryData;
                            return true;
                        }
                        break;

                    default:

                        telemetryData = telemData;
                        return true;
                }
            }

            telemetryData = null;
            return false;
        }

        private async Task<bool> IsRepairManagerDeployedAsync(CancellationToken cancellationToken)
        {
            try
            {
                var serviceList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                            () => FabricClientInstance.QueryManager.GetServiceListAsync(
                                                    fabricSystemAppUri,
                                                    repairManagerServiceUri,
                                                    ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigurationSettings.AsyncTimeout,
                                                    cancellationToken),
                                            cancellationToken);  

                return serviceList?.Count > 0;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
                return false;
            }
        }

        private async Task<RepairTaskList> GetRepairTasksCurrentlyProcessingAsync(CancellationToken cancellationToken)
        {
            if (!await IsRepairManagerDeployedAsync(cancellationToken))
            {
                return null;
            }

            try
            {
                var repairTasks = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () => FabricClientInstance.RepairManager.GetRepairTaskListAsync(
                                                        null,
                                                        RepairTaskStateFilter.Active |
                                                        RepairTaskStateFilter.Approved |
                                                        RepairTaskStateFilter.Executing,
                                                        null,
                                                        ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigurationSettings.AsyncTimeout,
                                                        cancellationToken),
                                               cancellationToken);  

                return repairTasks;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {

            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogWarning(e.ToString());
            }

            return null;
        }
    }
}
