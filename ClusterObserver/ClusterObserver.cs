// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClusterObserver.Interfaces;
using ClusterObserver.Utilities;
using ClusterObserver.Utilities.Telemetry;
using Newtonsoft.Json;

namespace ClusterObserver
{
    public class ClusterObserver
    {
        private readonly Uri repairManagerServiceUri = new Uri($"{ObserverConstants.SystemAppName}/RepairManagerService");
        private readonly Uri fabricSystemAppUri = new Uri(ObserverConstants.SystemAppName);
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

        private bool TelemetryEnabled => ClusterObserverManager.TelemetryEnabled;

        private ITelemetryProvider ObserverTelemetryClient => ClusterObserverManager.TelemetryClient;

        private FabricClient FabricClientInstance => ClusterObserverManager.FabricClientInstance;

        private TimeSpan RunInterval => ConfigSettings?.RunInterval ?? TimeSpan.MinValue;

        private static bool EtwEnabled => ClusterObserverManager.EtwEnabled;

        public ConfigSettings ConfigSettings
        {
            get; set;
        }

        public string ObserverName
        {
            get;
        }

        private StatelessServiceContext FabricServiceContext
        {
            get;
        }

        private Logger ObserverLogger
        {
            get;
        }

        public DateTime LastRunDateTime
        {
            get;
            private set;
        }

        public bool IsEnabled => ConfigSettings == null || ConfigSettings.IsEnabled;

        public bool HasClusterUpgradeCompleted { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserver"/> class.
        /// This observer is a singleton (one partition) stateless service that runs on one node in an SF cluster.
        /// </summary>
        public ClusterObserver(ConfigurationSettings settings = null, bool ignoreDefaultQueryTimeout = false)
        {
            ObserverName = ObserverConstants.ClusterObserverName;
            FabricServiceContext = ClusterObserverManager.FabricServiceContext;
            NodeStatusDictionary = new Dictionary<string, (NodeStatus NodeStatus, DateTime FirstDetectedTime, DateTime LastDetectedTime)>();
            ApplicationUpgradesCompletedStatus = new Dictionary<string, bool>();
            settings ??= FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config")?.Settings;
            ConfigSettings = new ConfigSettings(settings, ObserverConstants.ClusterObserverConfigurationSectionName);
            ObserverLogger = new Logger(ObserverName, ClusterObserverManager.LogPath)
            {
                EnableVerboseLogging = ConfigSettings.EnableVerboseLogging
            };

            this.ignoreDefaultQueryTimeout = ignoreDefaultQueryTimeout;
        }

        public async Task ObserveAsync(CancellationToken token)
        {
            if (!IsEnabled || !ClusterObserverManager.TelemetryEnabled
                || (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            await ReportAsync(token).ConfigureAwait(false);

            LastRunDateTime = DateTime.Now;
        }

        private async Task ReportAsync(CancellationToken token)
        {
            await ReportClusterHealthAsync(token).ConfigureAwait(false);
        }

        private async Task ReportClusterHealthAsync(CancellationToken token)
        {
            try
            {
                // Monitor node status.
                await MonitorNodeStatusAsync(token, ignoreDefaultQueryTimeout).ConfigureAwait(false);

                // Check for active repairs in the cluster.
                if (ConfigSettings.MonitorRepairJobStatus)
                {
                    var repairsInProgress = await GetRepairTasksCurrentlyProcessingAsync(token).ConfigureAwait(false);
                    string repairState = string.Empty;

                    if (repairsInProgress?.Count > 0)
                    {
                        string ids = string.Empty;

                        foreach (var repair in repairsInProgress)
                        {
                            token.ThrowIfCancellationRequested();
                            ids +=  $"TaskId: {repair.TaskId}{Environment.NewLine}State: {repair.State}{Environment.NewLine}";
                        }

                        repairState += $"There are currently one or more Repair Jobs processing in the cluster.{Environment.NewLine}{ids}";

                        // Telemetry.
                        if (TelemetryEnabled)
                        {
                            var telemetry = new TelemetryData()
                            {
                                HealthState = HealthState.Ok,
                                Description = repairState,
                                Metric = "AggregatedClusterHealth",
                                Source = ObserverName
                            };

                            await ObserverTelemetryClient?.ReportHealthAsync(telemetry, token);
                        }

                        // ETW.
                        if (EtwEnabled)
                        {
                            ObserverLogger.LogEtw(
                                            ObserverConstants.ClusterObserverETWEventName,
                                            new
                                            {
                                                HealthState = "Ok",
                                                Description = repairState,
                                                Metric = "AggregatedClusterHealth",
                                                Source = ObserverName
                                            });
                        }
                    }
                }

                // Check cluster upgrade status.
                if (ConfigSettings.MonitorUpgradeStatus)
                {
                    await ReportClusterUpgradeStatusAsync(token);
                }

                /* Cluster Health State Monitoring - App/Node/... */

                ClusterHealth clusterHealth =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                            () => 
                                FabricClientInstance.HealthManager.GetClusterHealthAsync(ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigSettings.AsyncTimeout, token), 
                            token);

                // Previous run generated unhealthy evaluation report. It's now Ok.
                if (clusterHealth.AggregatedHealthState == HealthState.Ok && (LastKnownClusterHealthState == HealthState.Error
                    || (ConfigSettings.EmitWarningDetails && LastKnownClusterHealthState == HealthState.Warning)))
                {
                    LastKnownClusterHealthState = HealthState.Ok;

                    // Telemetry.
                    if (TelemetryEnabled)
                    {
                        var telemetry = new TelemetryData()
                        {
                            HealthState = HealthState.Ok,
                            Description = "Cluster has recovered from previous Error/Warning state.",
                            Metric = "AggregatedClusterHealth",
                            Source = ObserverName
                        };

                        await ObserverTelemetryClient?.ReportHealthAsync(telemetry, token);
                    }

                    // ETW.
                    if (EtwEnabled)
                    {
                        ObserverLogger.LogEtw(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            HealthState = "Ok",
                                            Description = "Cluster has recovered from previous Error/Warning state.",
                                            Metric = "AggregatedClusterHealth",
                                            Source = ObserverName
                                        });
                    }
                }
                else
                {
                    // Cluster is healthy. Don't do anything.
                    if (clusterHealth.AggregatedHealthState == HealthState.Ok)
                    {
                        return;
                    }

                    // If in Warning and you are not sending Warning state reports, then end here.
                    if (!ConfigSettings.EmitWarningDetails && clusterHealth.AggregatedHealthState == HealthState.Warning)
                    {
                        return;
                    }

                    var unhealthyEvaluations = clusterHealth.UnhealthyEvaluations;

                    // No Unhealthy Evaluations means nothing to see here. 
                    if (unhealthyEvaluations.Count == 0)
                    {
                        return;
                    }

                    foreach (var evaluation in unhealthyEvaluations)
                    {
                        token.ThrowIfCancellationRequested();

                        switch (evaluation.Kind)
                        {
                            case HealthEvaluationKind.Node:
                            case HealthEvaluationKind.Nodes:
                                try
                                {
                                    await ProcessNodeHealthAsync(clusterHealth.NodeHealthStates, token).ConfigureAwait(false);
                                }
                                catch (Exception e) when (e is FabricException || e is TimeoutException)
                                {

                                }
                                break;

                            case HealthEvaluationKind.Application:
                            case HealthEvaluationKind.Applications:
                            case HealthEvaluationKind.SystemApplication:

                                try
                                {
                                    foreach (var app in clusterHealth.ApplicationHealthStates)
                                    {
                                        token.ThrowIfCancellationRequested();

                                        try
                                        {
                                            var entityHealth =
                                                await FabricClientInstance.HealthManager.GetApplicationHealthAsync(app.ApplicationName, ConfigSettings.AsyncTimeout, token);

                                            if (app.AggregatedHealthState == HealthState.Ok)
                                            {
                                                continue;
                                            }

                                            if (entityHealth.ServiceHealthStates != null && entityHealth.ServiceHealthStates.Any(
                                                    s => s.AggregatedHealthState == HealthState.Error || s.AggregatedHealthState == HealthState.Warning))
                                            {
                                                foreach (var service in entityHealth.ServiceHealthStates.Where(
                                                                s => s.AggregatedHealthState == HealthState.Error || s.AggregatedHealthState == HealthState.Warning))
                                                {
                                                    await ProcessServiceHealthAsync(service, token);
                                                }
                                            }
                                            else
                                            {
                                                await ProcessApplicationHealthAsync(app, token);
                                            }
                                        }
                                        catch (Exception e) when (e is FabricException || e is TimeoutException)
                                        {
                                           
                                        }
                                    }
                                }
                                catch (Exception e) when (e is FabricException || e is TimeoutException)
                                {

                                }
                                break;

                            default:

                                try
                                {
                                    await ProcessGenericEntityHealthAsync(evaluation, token).ConfigureAwait(false);
                                }
                                catch (Exception e) when (e is FabricException || e is TimeoutException)
                                {

                                }
                                break;
                        }
                    }

                    // Track current aggregated health state for use in next run.
                    LastKnownClusterHealthState = clusterHealth.AggregatedHealthState;
                }
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
                
                // Send Telemetry.
                if (TelemetryEnabled)
                {
                    var telemetryData = new TelemetryData()
                    {
                        HealthState = HealthState.Warning,
                        Description = msg
                    };

                    await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, token);
                }

                // Emit ETW.
                if (EtwEnabled)
                {
                    ObserverLogger.LogEtw(
                                    ObserverConstants.ClusterObserverETWEventName,
                                    new
                                    {
                                        HealthState = "Warning",
                                        Description = msg
                                    });
                }

                // Fix the bug.
                throw;
            }
        }

        private async Task ReportApplicationUpgradeStatus(Uri appName, CancellationToken token)
        {
            ServiceFabricUpgradeEventData appUpgradeInfo =
                            await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => UpgradeChecker.GetApplicationUpgradeDetailsAsync(FabricClientInstance, token, appName),
                                    token);

            if (appUpgradeInfo?.ApplicationUpgradeProgress == null || token.IsCancellationRequested)
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
            if (TelemetryEnabled)
            {
                _ = await ObserverTelemetryClient?.ReportApplicationUpgradeStatusAsync(appUpgradeInfo, token);
            }

            // ETW.
            if (EtwEnabled)
            {
                ObserverLogger.LogEtw(
                    ObserverConstants.ClusterObserverETWEventName,
                    new
                    {
                        appUpgradeInfo.ClusterId,
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        appUpgradeInfo.OS,
                        ApplicationName = appUpgradeInfo.ApplicationUpgradeProgress?.UpgradeDescription?.ApplicationName?.OriginalString,
                        UpgradeTargetAppTypeVersion = appUpgradeInfo.ApplicationUpgradeProgress?.UpgradeDescription?.TargetApplicationTypeVersion,
                        UpgradeState = Enum.GetName(typeof(FabricUpgradeState), appUpgradeInfo.ApplicationUpgradeProgress.UpgradeState),
                        UpgradeDomain = appUpgradeInfo.ApplicationUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName,
                        UpgradeDuration = appUpgradeInfo.ApplicationUpgradeProgress.CurrentUpgradeDomainDuration,
                        FailureReason = appUpgradeInfo.ApplicationUpgradeProgress.FailureReason.HasValue ? Enum.GetName(typeof(UpgradeFailureReason), appUpgradeInfo.ApplicationUpgradeProgress.FailureReason.Value) : null,
                    });
            }
        }

        private async Task ReportClusterUpgradeStatusAsync(CancellationToken token)
        {
            var eventData =
                    await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                             () => UpgradeChecker.GetClusterUpgradeDetailsAsync(FabricClientInstance, token), token);

            if (eventData?.FabricUpgradeProgress == null || token.IsCancellationRequested)
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
            if (TelemetryEnabled)
            {
                _ = await ObserverTelemetryClient?.ReportClusterUpgradeStatusAsync(eventData, token);
            }

            // ETW.
            if (EtwEnabled)
            {
                ObserverLogger.LogEtw(
                    ObserverConstants.ClusterObserverETWEventName,
                    new
                    {
                        eventData.ClusterId,
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        eventData.OS,
                        UpgradeTargetCodeVersion = eventData.FabricUpgradeProgress?.UpgradeDescription?.TargetCodeVersion,
                        UpgradeTargetConfigVersion = eventData.FabricUpgradeProgress?.UpgradeDescription?.TargetConfigVersion,
                        UpgradeState = Enum.GetName(typeof(FabricUpgradeState), eventData.FabricUpgradeProgress.UpgradeState),
                        UpgradeDomain = eventData.FabricUpgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName,
                        UpgradeDuration = eventData.FabricUpgradeProgress.CurrentUpgradeDomainDuration.ToString(),
                        FailureReason = eventData.FabricUpgradeProgress.FailureReason.HasValue ? Enum.GetName(typeof(UpgradeFailureReason), eventData.FabricUpgradeProgress.FailureReason.Value) : null,
                    });
            }
        }

        private async Task ProcessApplicationHealthAsync(ApplicationHealthState appHealthState, CancellationToken token)
        {
            string telemetryDescription = string.Empty;
            var appHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                                appHealthState.ApplicationName,
                                                ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigSettings.AsyncTimeout,
                                                token),
                                    token);
            
            if (appHealth == null)
            {
                return;
            }

            Uri appName = appHealthState.ApplicationName;

            // Check upgrade status of unhealthy application. Note, this doesn't apply to System applications as they update as part of a platform update.
            if (ConfigSettings.MonitorUpgradeStatus && !appName.Equals(fabricSystemAppUri))
            {
                await ReportApplicationUpgradeStatus(appName, token);
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
                if (TryGetTelemetryData(healthEvent, out TelemetryData foTelemetryData))
                { 
                    foTelemetryData.Description += telemetryDescription;

                    // Telemetry.
                    if (TelemetryEnabled)
                    {
                        await ObserverTelemetryClient?.ReportHealthAsync(foTelemetryData, token);
                    }

                    // ETW.
                    if (EtwEnabled)
                    {
                        ObserverLogger.LogEtw(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            foTelemetryData.ApplicationName,
                                            foTelemetryData.ServiceName,
                                            foTelemetryData.HealthState,
                                            foTelemetryData.Description,
                                            foTelemetryData.Metric,
                                            foTelemetryData.ObserverName,
                                            foTelemetryData.NodeName,
                                            foTelemetryData.Source,
                                            foTelemetryData.PartitionId,
                                            foTelemetryData.ProcessId,
                                            foTelemetryData.ReplicaId,
                                            foTelemetryData.SystemServiceProcessName,
                                            foTelemetryData.Value
                                        });
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

                    // Telemetry.
                    if (TelemetryEnabled)
                    {
                        var telemetryData = new TelemetryData()
                        {
                            ApplicationName = appName.OriginalString,
                            HealthState = appHealth.AggregatedHealthState,
                            Description = telemetryDescription,
                            Source = ObserverName
                        };

                        await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, token);
                    }

                    // ETW.
                    if (EtwEnabled)
                    {
                        ObserverLogger.LogEtw(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            ApplicationName = appName.OriginalString,
                                            HealthState = Enum.GetName(typeof(HealthState), appHealth.AggregatedHealthState),
                                            Description = telemetryDescription,
                                            Source = ObserverName
                                        });
                    }

                    // Reset 
                    telemetryDescription = string.Empty;
                }
            }
        }

        private async Task ProcessServiceHealthAsync(ServiceHealthState serviceHealthState, CancellationToken token)
        {
            Uri appName;
            Uri serviceName = serviceHealthState.ServiceName;
            string telemetryDescription = string.Empty;
            ServiceHealth serviceHealth = await FabricClientInstance.HealthManager.GetServiceHealthAsync(serviceName, ConfigSettings.AsyncTimeout, token);
            ApplicationNameResult name = await FabricClientInstance.QueryManager.GetApplicationNameAsync(serviceName, ConfigSettings.AsyncTimeout, token);
            appName = name.ApplicationName;
            var healthEvents = serviceHealth.HealthEvents;

            if (!healthEvents.Any(h => h.HealthInformation.HealthState == HealthState.Error || h.HealthInformation.HealthState == HealthState.Warning))
            {
                var partitionHealthStates = serviceHealth.PartitionHealthStates.Where(p => p.AggregatedHealthState == HealthState.Warning || p.AggregatedHealthState == HealthState.Error);

                foreach (var partitionHealthState in partitionHealthStates)
                {
                    var partitionHealth = await FabricClientInstance.HealthManager.GetPartitionHealthAsync(partitionHealthState.PartitionId, ConfigSettings.AsyncTimeout, token);
                    var replicaHealthStates = partitionHealth.ReplicaHealthStates.Where(p => p.AggregatedHealthState == HealthState.Warning).ToList();

                    if (replicaHealthStates != null && replicaHealthStates.Count > 0)
                    {
                        foreach (var replica in replicaHealthStates)
                        {
                            var replicaHealth =
                                await FabricClientInstance.HealthManager.GetReplicaHealthAsync(partitionHealthState.PartitionId, replica.Id, ConfigSettings.AsyncTimeout, token);

                            if (replicaHealth != null)
                            {
                                healthEvents = replicaHealth.HealthEvents.Where(h => h.HealthInformation.HealthState == HealthState.Warning).ToList();
                                break;
                            }
                        }
                        break;
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
                if (TryGetTelemetryData(healthEvent, out TelemetryData foTelemetryData))
                {
                    foTelemetryData.Description += telemetryDescription;

                    // Telemetry.
                    if (TelemetryEnabled)
                    {
                        await ObserverTelemetryClient?.ReportHealthAsync(foTelemetryData, token);
                    }

                    // ETW.
                    if (EtwEnabled)
                    {
                        ObserverLogger.LogEtw(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            foTelemetryData.ApplicationName,
                                            foTelemetryData.ServiceName,
                                            foTelemetryData.HealthState,
                                            foTelemetryData.Description,
                                            foTelemetryData.Metric,
                                            foTelemetryData.ObserverName,
                                            foTelemetryData.NodeName,
                                            foTelemetryData.Source,
                                            foTelemetryData.PartitionId,
                                            foTelemetryData.ProcessId,
                                            foTelemetryData.ReplicaId,
                                            foTelemetryData.SystemServiceProcessName,
                                            foTelemetryData.Value
                                        });
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

                    // Telemetry.
                    if (TelemetryEnabled)
                    {
                        var telemetryData = new TelemetryData()
                        {
                            ApplicationName = appName.OriginalString,
                            EntityType = EntityType.Service,
                            ServiceName = serviceName.OriginalString,
                            HealthState = serviceHealth.AggregatedHealthState,
                            Description = telemetryDescription,
                            Source = ObserverName
                        };

                        await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, token);
                    }

                    // ETW.
                    if (EtwEnabled)
                    {
                        ObserverLogger.LogEtw(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            ApplicationName = appName.OriginalString,
                                            EntityType = EntityType.Service,
                                            ServiceName = serviceName.OriginalString,
                                            HealthState = Enum.GetName(typeof(HealthState), serviceHealth.AggregatedHealthState),
                                            Description = telemetryDescription,
                                            Source = ObserverName
                                        });
                    }

                    // Reset 
                    telemetryDescription = string.Empty;
                }
            }
        }

        private async Task ProcessNodeHealthAsync(IEnumerable<NodeHealthState> nodeHealthStates, CancellationToken token)
        {
            // Check cluster upgrade status. This will be used to help determine if a node in Error is in that state because of a Fabric runtime upgrade.
            var clusterUpgradeInfo =
                await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                         () => 
                            UpgradeChecker.GetClusterUpgradeDetailsAsync(FabricClientInstance, token),
                         token);

            var supportedNodeHealthStates = nodeHealthStates.Where( a => a.AggregatedHealthState == HealthState.Warning || a.AggregatedHealthState == HealthState.Error);

            foreach (var node in supportedNodeHealthStates)
            {
                token.ThrowIfCancellationRequested();

                if (node.AggregatedHealthState == HealthState.Ok || (!ConfigSettings.EmitWarningDetails && node.AggregatedHealthState == HealthState.Warning))
                {
                    continue;
                }

                string telemetryDescription = $"Node in Error or Warning: {node.NodeName}{Environment.NewLine}";

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
                                    node.NodeName, ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigSettings.AsyncTimeout, token),
                            token);

                foreach (var nodeHealthEvent in nodeHealth.HealthEvents.Where(ev => ev.HealthInformation.HealthState != HealthState.Ok))
                {
                    token.ThrowIfCancellationRequested();

                    var targetNodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                () =>
                                                    FabricClientInstance.QueryManager.GetNodeListAsync(
                                                        node.NodeName,
                                                        ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigSettings.AsyncTimeout,
                                                        token),
                                                token);

                    if (targetNodeList?.Count > 0)
                    {
                        Node targetNode = targetNodeList[0];

                        if (TryGetTelemetryData(nodeHealthEvent, out TelemetryData telemetryData))
                        {
                            telemetryData.Description += $"{Environment.NewLine}Node Status: { (targetNode != null ? Enum.GetName(typeof(NodeStatus), targetNode.NodeStatus) : string.Empty)}";
                            telemetryDescription += telemetryData.Description;
                            await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, token);

                            // ETW.
                            if (EtwEnabled)
                            {
                                ObserverLogger.LogEtw(
                                    ObserverConstants.ClusterObserverETWEventName,
                                    new
                                    {
                                        node.NodeName,
                                        EntityType = EntityType.Node,
                                        NodeStatus = targetNode != null ? Enum.GetName(typeof(NodeStatus), targetNode.NodeStatus) : string.Empty,
                                        HealthState = Enum.GetName(typeof(HealthState), node.AggregatedHealthState),
                                        Description = telemetryDescription
                                    });
                            }
                        }
                        else if (!string.IsNullOrEmpty(nodeHealthEvent.HealthInformation.Description))
                        {
                            telemetryDescription += $"{nodeHealthEvent.HealthInformation.Description}{Environment.NewLine}";

                            var telemData = new TelemetryData()
                            {
                                EntityType = EntityType.Node,
                                NodeName = node.NodeName,
                                HealthState = node.AggregatedHealthState,
                                Description = telemetryDescription
                            };

                            // Telemetry.
                            await ObserverTelemetryClient?.ReportHealthAsync(telemData, token);

                            // ETW.
                            if (EtwEnabled)
                            {
                                ObserverLogger.LogEtw(
                                    ObserverConstants.ClusterObserverETWEventName,
                                    new
                                    {
                                        node.NodeName,
                                        EntityType = EntityType.Node,
                                        NodeStatus = targetNode != null ? Enum.GetName(typeof(NodeStatus), targetNode.NodeStatus) : string.Empty,
                                        HealthState = Enum.GetName(typeof(HealthState), node.AggregatedHealthState),
                                        Description = telemetryDescription
                                    });
                            }
                        }
                    }

                    // Reset 
                    telemetryDescription = string.Empty;
                }
            }
        }

        private async Task ProcessGenericEntityHealthAsync(HealthEvaluation evaluation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            string telemetryDescription = evaluation.Description;

            var telemetryData = new TelemetryData()
            {
                Description = telemetryDescription,
                HealthState = evaluation.AggregatedHealthState,
                Source = ObserverName
            };

            // Telemetry.
            if (TelemetryEnabled)
            {
                await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, token);
            }

            // ETW.
            if (EtwEnabled)
            {
                ObserverLogger.LogEtw(
                                ObserverConstants.ClusterObserverETWEventName,
                                new
                                {
                                    Description = telemetryDescription,
                                    HealthState = Enum.GetName(typeof(HealthState), evaluation.AggregatedHealthState),
                                    Source = ObserverName
                                });
            }
        }

        private async Task MonitorNodeStatusAsync(CancellationToken token, bool isTest = false)
        {
            // If a node's NodeStatus is Disabling, Disabled, or Down 
            // for at or above the specified maximum time (in Settings.xml),
            // then CO will emit a Warning signal.
            var nodeList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () =>
                                                    FabricClientInstance.QueryManager.GetNodeListAsync(
                                                            null,
                                                            isTest ? TimeSpan.FromSeconds(1) : ConfigSettings.AsyncTimeout,
                                                            token),
                                               token).ConfigureAwait(false);

            // Are any of the nodes that were previously in non-Up status, now Up?
            if (NodeStatusDictionary.Count > 0)
            {
                foreach (var nodeDictItem in NodeStatusDictionary)
                {
                    if (!nodeList.Any(n => n.NodeName == nodeDictItem.Key && n.NodeStatus == NodeStatus.Up))
                    {
                        continue;
                    }

                    // Telemetry.
                    if (TelemetryEnabled)
                    {
                        var telemetry = new TelemetryData()
                        {
                            HealthState = HealthState.Ok,
                            Description = $"{nodeDictItem.Key} is now Up.",
                            Metric = "NodeStatus",
                            NodeName = nodeDictItem.Key,
                            Source = ObserverName,
                            Value = 0
                        };

                        await ObserverTelemetryClient?.ReportHealthAsync(telemetry, token);
                    }

                    // ETW.
                    if (EtwEnabled)
                    {
                        ObserverLogger.LogEtw(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            HealthState = "Ok",
                                            Description = $"{nodeDictItem.Key} is now Up.",
                                            Metric = "NodeStatus",
                                            NodeName = nodeDictItem.Key,
                                            Source = ObserverName,
                                            Value = 0
                                        });
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
                                >= ConfigSettings.MaxTimeNodeStatusNotOk))
                    {
                        var kvp = NodeStatusDictionary.FirstOrDefault(
                                       dict => dict.Key == node.NodeName
                                        && dict.Value.LastDetectedTime.Subtract(dict.Value.FirstDetectedTime)
                                        >= ConfigSettings.MaxTimeNodeStatusNotOk);

                        var message =
                            $"Node {kvp.Key} has been {kvp.Value.NodeStatus} " +
                            $"for {Math.Round(kvp.Value.LastDetectedTime.Subtract(kvp.Value.FirstDetectedTime).TotalHours, 2)} hours.{Environment.NewLine}";

                        // Telemetry.
                        if (TelemetryEnabled)
                        {
                            var telemetry = new TelemetryData()
                            {
                                HealthState = HealthState.Warning,
                                Description = message,
                                Metric = "NodeStatus",
                                NodeName = kvp.Key,
                                Source = ObserverName,
                                Value = 1,
                            };

                            await ObserverTelemetryClient?.ReportHealthAsync(telemetry, token);
                        }

                        // ETW.
                        if (EtwEnabled)
                        {
                            ObserverLogger.LogEtw(
                                            ObserverConstants.ClusterObserverETWEventName,
                                            new
                                            {
                                                HealthState = "Warning",
                                                Description = message,
                                                Metric = "NodeStatus",
                                                NodeName = kvp.Key,
                                                Source = ObserverName,
                                                Value = 1,
                                            });
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
        private static bool TryGetTelemetryData(HealthEvent healthEvent, out TelemetryData telemetryData)
        {
            if (!JsonHelper.IsJson<TelemetryData>(healthEvent.HealthInformation.Description))
            {
                telemetryData = null;
                return false;
            }

            telemetryData = JsonConvert.DeserializeObject<TelemetryData>(healthEvent.HealthInformation.Description);
            return true;
        }

        /// <summary>
        /// Checks if the RepairManager System app service is deployed in the cluster.
        /// </summary>
        /// <param name="cancellationToken">cancellation token to stop the async operation</param>
        /// <returns>true if RepairManager service is present in cluster, otherwise false</returns>
        private async Task<bool> IsRepairManagerDeployedAsync(CancellationToken cancellationToken)
        {
            try
            {
                var serviceList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () => FabricClientInstance.QueryManager.GetServiceListAsync(
                                                                              fabricSystemAppUri,
                                                                              repairManagerServiceUri,
                                                                              ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigSettings.AsyncTimeout,
                                                                              cancellationToken),
                                               cancellationToken).ConfigureAwait(false);  

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
                                                                ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : ConfigSettings.AsyncTimeout,
                                                                cancellationToken),
                                               cancellationToken).ConfigureAwait(false);  

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
