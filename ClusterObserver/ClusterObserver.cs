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

namespace ClusterObserver
{
    public sealed class ClusterObserver : ObserverBase
    {
        private readonly Uri repairManagerServiceUri = new($"{ClusterObserverConstants.SystemAppName}/RepairManagerService");
        private readonly Uri fabricSystemAppUri = new(ClusterObserverConstants.SystemAppName);
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

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserver"/> class.
        /// This observer is a singleton (one partition) stateless service that runs on one node in an SF cluster.
        /// </summary>
        public ClusterObserver(StatelessServiceContext serviceContext, bool ignoreDefaultQueryTimeout = false) : base (null, serviceContext)
        {
            NodeStatusDictionary = new Dictionary<string, (NodeStatus NodeStatus, DateTime FirstDetectedTime, DateTime LastDetectedTime)>();
            ApplicationUpgradesCompletedStatus = new Dictionary<string, bool>();

            this.ignoreDefaultQueryTimeout = ignoreDefaultQueryTimeout;
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

        /// <summary>
        /// Set properties with Application Parameter settings supplied by user.
        /// </summary>
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

                // Previous run generated unhealthy evaluation report. It's now Ok.
                if (clusterHealth.AggregatedHealthState == HealthState.Ok && (LastKnownClusterHealthState == HealthState.Error
                    || (EmitWarningDetails && LastKnownClusterHealthState == HealthState.Warning)))
                {
                    LastKnownClusterHealthState = HealthState.Ok;

                    var telemetry = new ClusterTelemetryData()
                    {
                        ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                        EntityType = EntityType.Cluster,
                        HealthState = HealthState.Ok,
                        Description = "Cluster has recovered from previous Error/Warning state.",
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

                if (clusterHealth.NodeHealthStates?.Count == 0 && clusterHealth.ApplicationHealthStates?.Count == 0) 
                {
                    await ProcessGenericEntityHealthAsync(clusterHealth.UnhealthyEvaluations, Token);
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
                    foTelemetryData.Description += telemetryDescription;

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

                    // Reset 
                    telemetryDescription = string.Empty;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(healthEvent.HealthInformation.Description))
                    {
                        telemetryDescription += healthEvent.HealthInformation.Description;
                    }
                    else
                    {
                        telemetryDescription += string.Join($"{Environment.NewLine}", appHealth.UnhealthyEvaluations);
                    }

                    var telemetryData = new ServiceTelemetryData()
                    {
                        ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                        ApplicationName = appName.OriginalString,
                        EntityType = EntityType.Application,
                        HealthState = appHealth.AggregatedHealthState,
                        Description = telemetryDescription,
                        Source = ObserverName
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

                    // Reset 
                    telemetryDescription = string.Empty;
                }
            }
        }

        private async Task ProcessServiceHealthAsync(ServiceHealthState serviceHealthState, CancellationToken Token)
        {
            Uri appName;
            Uri serviceName = serviceHealthState.ServiceName;
            string telemetryDescription = string.Empty;
            ServiceHealth serviceHealth = await FabricClientInstance.HealthManager.GetServiceHealthAsync(serviceName, ConfigurationSettings.AsyncTimeout, Token);
            ApplicationNameResult name = await FabricClientInstance.QueryManager.GetApplicationNameAsync(serviceName, ConfigurationSettings.AsyncTimeout, Token);
            appName = name.ApplicationName;
            var healthEvents = serviceHealth.HealthEvents;

            if (!healthEvents.Any(h => h.HealthInformation.HealthState == HealthState.Error || h.HealthInformation.HealthState == HealthState.Warning))
            {
                var partitionHealthStates = serviceHealth.PartitionHealthStates.Where(p => p.AggregatedHealthState == HealthState.Warning || p.AggregatedHealthState == HealthState.Error);

                foreach (var partitionHealthState in partitionHealthStates)
                {
                    var partitionHealth = await FabricClientInstance.HealthManager.GetPartitionHealthAsync(partitionHealthState.PartitionId, ConfigurationSettings.AsyncTimeout, Token);
                    var replicaHealthStates = partitionHealth.ReplicaHealthStates.Where(p => p.AggregatedHealthState == HealthState.Warning || p.AggregatedHealthState == HealthState.Error).ToList();

                    if (replicaHealthStates != null && replicaHealthStates.Count > 0)
                    {
                        foreach (var replica in replicaHealthStates)
                        {
                            var replicaHealth =
                                await FabricClientInstance.HealthManager.GetReplicaHealthAsync(partitionHealthState.PartitionId, replica.Id, ConfigurationSettings.AsyncTimeout, Token);

                            if (replicaHealth != null)
                            {
                                healthEvents = 
                                    replicaHealth.HealthEvents.Where(h => h.HealthInformation.HealthState == HealthState.Warning 
                                    || h.HealthInformation.HealthState == HealthState.Error).ToList();

                                break;
                            }
                        }
                        break;
                    }
                    else
                    {
                        await ProcessGenericEntityHealthAsync(partitionHealth.UnhealthyEvaluations, Token);
                    }
                }
            }

            foreach (HealthEvent healthEvent in healthEvents.OrderByDescending(f => f.SourceUtcTimestamp))
            {
                if (healthEvent.HealthInformation.HealthState != HealthState.Error && healthEvent.HealthInformation.HealthState != HealthState.Warning)
                {
                    continue;
                }

                // Description == serialized instance of ITelemetryData type?
                if (TryGetTelemetryData(healthEvent, out TelemetryDataBase foTelemetryData))
                {
                    foTelemetryData.Description += telemetryDescription;

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

                    // Reset 
                    telemetryDescription = string.Empty;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(healthEvent.HealthInformation.Description))
                    {
                        telemetryDescription += healthEvent.HealthInformation.Description;
                    }
                    else
                    {
                        telemetryDescription += string.Join($"{Environment.NewLine}", serviceHealth.UnhealthyEvaluations);
                    }

                    var telemetryData = new ServiceTelemetryData()
                    {
                        ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                        ApplicationName = appName.OriginalString,
                        EntityType = EntityType.Service,
                        ServiceName = serviceName.OriginalString,
                        HealthState = serviceHealth.AggregatedHealthState,
                        Description = telemetryDescription,
                        Source = ObserverName
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

                    // Reset 
                    telemetryDescription = string.Empty;
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

        private async Task ProcessGenericEntityHealthAsync(IList<HealthEvaluation> evaluations, CancellationToken Token)
        {
            foreach (var evaluation in evaluations)
            {
                Token.ThrowIfCancellationRequested();

                string telemetryDescription = evaluation.Description;

                var telemtryData = new ClusterTelemetryData
                {
                    ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                    EntityType = EntityType.Cluster,
                    Metric = "SF Entity Health",
                    Description = telemetryDescription,
                    HealthState = evaluation.AggregatedHealthState,
                    Source = ObserverName
                };

                // Telemetry.
                if (IsTelemetryEnabled)
                {
                    if (TelemetryClient != null)
                    {
                        await TelemetryClient.ReportHealthAsync(telemtryData, Token);
                    }
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(ClusterObserverConstants.ClusterObserverETWEventName, telemtryData);
                }
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

        /// <summary>
        /// This function determines if a HealthEvent.HealthInformation.Description is a JSON-serialized instance of TelemetryData type.
        /// If so, then it will deserialize the JSON string to an instance of TelemetryData and set telemetryData (out) as the instance.
        /// </summary>
        /// <param name="healthEvent">A Fabric Health event.</param>
        /// <param name="telemetryData">Will be an instance of TelemetryData if successful. Otherwise, null.</param>
        /// <returns>true if deserialization to TelemetryData type succeeds. Otherwise, false.</returns>
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

        /// <summary>
        /// Checks if the RepairManager System app service is deployed in the cluster.
        /// </summary>
        /// <param name="cancellationToken">cancellation Token to stop the async operation</param>
        /// <returns>true if RepairManager service is present in cluster, otherwise false</returns>
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

        /// <summary>
        /// This function returns a list of active Fabric repair tasks (RM) in the cluster.
        /// If a VM is being updated by VMSS, for example, then there will be a Fabric Repair task in play and
        /// this will cause changes in Fabric node status like Disabling, Disabled, Down, Enabling, etc.
        /// These are expected, so you should make action decisions based on this information to ensure you don't
        /// try and heal where no healing is needed.
        /// These could be custom repair tasks, Azure Tenant repair tasks (like Azure platform updates), etc.
        /// </summary>
        /// <returns>List of repair tasks in Active, Approved, or Executing State.</returns>
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
