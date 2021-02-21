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
using ClusterObserver.Utilities.Telemetry;
using ClusterObserver.Interfaces;
using Newtonsoft.Json;
using System.Fabric.Description;

namespace ClusterObserver
{
    public class ClusterObserver
    {
        private bool etwEnabled;
        private readonly Uri repairManagerServiceUri = new Uri("fabric:/System/RepairManagerService");
        private readonly Uri fabricSystemAppUri = new Uri("fabric:/System");

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
        } = new Dictionary<string, (NodeStatus NodeStatus, DateTime FirstDetectedTime, DateTime LastDetectedTime)>();

        protected bool TelemetryEnabled => ClusterObserverManager.TelemetryEnabled;

        protected ITelemetryProvider ObserverTelemetryClient => ClusterObserverManager.TelemetryClient;

        protected FabricClient FabricClientInstance => ClusterObserverManager.FabricClientInstance;

        public ConfigSettings ConfigSettings
        {
            get; set;
        }

        public bool IsTestRun
        {
            get; set;
        } = false;

        public string ObserverName
        {
            get; set;
        }

        public string NodeName
        {
            get; set;
        }

        public string NodeType
        {
            get; private set;
        }

        public StatelessServiceContext FabricServiceContext
        {
            get;
        }

        public DateTime LastRunDateTime
        {
            get; set;
        }

        public CancellationToken Token
        {
            get; set;
        }

        public bool IsUnhealthy
        {
            get; set;
        } = false;

        public Logger ObserverLogger
        {
            get; set;
        }

        public bool IsEnabled
        {
            get
            {
                if (ConfigSettings != null)
                {
                    return ConfigSettings.IsEnabled;
                }

                return true;
            }
        }

        public TimeSpan RunInterval
        {
            get
            {
                if (ConfigSettings != null)
                {
                    return ConfigSettings.RunInterval;
                }

                return TimeSpan.MinValue;
            }
        }

        public bool EtwEnabled
        {
            get => etwEnabled;

            set => etwEnabled = value;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserver"/> class.
        /// This observer is a singleton (one partition) stateless service that runs on one node in an SF cluster.
        /// </summary>
        public ClusterObserver(ConfigurationSettings settings = null)
        {
            ObserverName = ObserverConstants.ClusterObserverName;
            etwEnabled = ClusterObserverManager.EtwEnabled;
            FabricServiceContext = ClusterObserverManager.FabricServiceContext;
            NodeName = FabricServiceContext.NodeContext.NodeName;
            NodeType = FabricServiceContext.NodeContext.NodeType;

            if (settings == null)
            {
                settings = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config")?.Settings;
            }

            ConfigSettings = new ConfigSettings(settings, ObserverConstants.ClusterObserverConfigurationSectionName);

            // Observer Logger setup.
            ObserverLogger = new Logger(ObserverName, ClusterObserverManager.LogPath)
            {
                EnableVerboseLogging = ConfigSettings.EnableVerboseLogging,
            };
        }

        public async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml
            if (!IsEnabled || (RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            await ReportAsync(token).ConfigureAwait(true);
            LastRunDateTime = DateTime.Now;
        }

        public async Task ReportAsync(CancellationToken token)
        {
            await ReportClusterHealthAsync(token).ConfigureAwait(true);
        }

        private async Task ReportClusterHealthAsync(CancellationToken token)
        {
            // The point of this service is to emit SF Health telemetry to your external log analytics service, so
            // if telemetry is not enabled or you don't provide an AppInsights instrumentation key or LogAnalytics identity settings, for example, 
            // then querying HM for health info isn't useful.
            if (!EtwEnabled && (!TelemetryEnabled || ObserverTelemetryClient == null))
            {
                return;
            }

            token.ThrowIfCancellationRequested();

            try
            {
                string telemetryDescription = string.Empty;

                // Monitor node status.
                await MonitorNodeStatusAsync().ConfigureAwait(false);

                // Check for active repairs in the cluster.
                var repairsInProgress = await GetRepairTasksCurrentlyProcessingAsync(token).ConfigureAwait(false);

                if (repairsInProgress?.Count > 0)
                {
                    string ids = string.Empty;

                    foreach (var repair in repairsInProgress)
                    {
                        ids += $"TaskId: {repair.TaskId}{Environment.NewLine}" +
                               $"State: {repair.State}{Environment.NewLine}";
                    }

                    telemetryDescription +=
                    $"Note: There are currently one or more Repair Tasks processing in the cluster.{Environment.NewLine}" +
                    $"{ids}";
                }

                /* Cluster Health State Monitoring - App/Node */

                var clusterHealth = await FabricClientInstance.HealthManager.GetClusterHealthAsync(
                                                ConfigSettings.AsyncTimeout,
                                                token).ConfigureAwait(true);

                // Previous run generated unhealthy evaluation report. It's now Ok.
                if (clusterHealth.AggregatedHealthState == HealthState.Ok
                    && (LastKnownClusterHealthState == HealthState.Error
                    || (ConfigSettings.EmitWarningDetails && LastKnownClusterHealthState == HealthState.Warning)))
                {
                    LastKnownClusterHealthState = HealthState.Ok;

                    // Telemetry.
                    if (TelemetryEnabled && ObserverTelemetryClient != null)
                    {
                        var telemetry = new TelemetryData(FabricClientInstance, Token)
                        {
                            //HealthScope = "Cluster",
                            HealthState = "Ok",
                            HealthEventDescription = "Cluster has recovered from previous Error/Warning state.",
                            Metric = "AggregatedClusterHealth",
                            Source = ObserverName,
                        };

                        await ObserverTelemetryClient.ReportHealthAsync(telemetry, Token);
                    }

                    // ETW.
                    if (etwEnabled)
                    {
                        Logger.EtwLogger?.Write(
                            ObserverConstants.ClusterObserverETWEventName,
                            new
                            {
                                HealthScope = "Cluster",
                                HealthState = "Ok",
                                HealthEventDescription = "Cluster has recovered from previous Error/Warning state.",
                                Metric = "AggregatedClusterHealth",
                                Source = ObserverName,
                            });
                    }
                }
                else
                {
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

                    // Check cluster upgrade status.
                    int udInClusterUpgrade = await UpgradeChecker.GetUdsWhereFabricUpgradeInProgressAsync(
                                                    FabricClientInstance,
                                                    Token).ConfigureAwait(false);

                    foreach (var evaluation in unhealthyEvaluations)
                    {
                        token.ThrowIfCancellationRequested();

                        // System service health.
                        if (evaluation.Kind == HealthEvaluationKind.SystemApplication)
                        {
                            var appHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                       new Uri("fabric:/System"),
                                       ConfigSettings.AsyncTimeout,
                                       token);

                            var appHealthEvents = appHealth.HealthEvents;

                            // From FO?
                            foreach (var appHealthEvent in appHealthEvents.Where(
                                        ev => ev.HealthInformation.HealthState != HealthState.Ok))
                            {
                                Token.ThrowIfCancellationRequested();

                                // FabricObservers health event details need to be formatted correctly.
                                // If FO did not emit this event, then foStats will be null.
                                var foStats = TryGetFOHealthStateEventData(appHealthEvent, HealthScope.Application);
                                string sourceObserver = null;
                                string metric = null;
                                string systemServiceName = null;
                                string partitionId = null;
                                string replicaId = null;

                                if (foStats != null)
                                {
                                    telemetryDescription += foStats.HealthEventDescription;
                                    sourceObserver = foStats.ObserverName;
                                    partitionId = foStats.PartitionId;
                                    replicaId = foStats.ReplicaId;
                                    metric = foStats.Metric;

                                    if (!string.IsNullOrWhiteSpace(foStats.SystemServiceProcessName))
                                    {
                                        systemServiceName = foStats.SystemServiceProcessName;
                                    }
                                }
                                else if (!string.IsNullOrEmpty(appHealthEvent.HealthInformation.Description))
                                {
                                    // This wil be whatever is provided in the health event description set by the emitter, which
                                    // was not FO.
                                    telemetryDescription += $"{appHealthEvent.HealthInformation.Description}{Environment.NewLine}";
                                }

                                // Telemetry.
                                if (TelemetryEnabled && ObserverTelemetryClient != null)
                                {
                                    var telemetryData = new TelemetryData(FabricClientInstance, Token)
                                    {
                                        ApplicationName = appHealth.ApplicationName.OriginalString,
                                        HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                        HealthEventDescription = telemetryDescription,
                                        Metric = metric ?? "AggregatedClusterHealth",
                                        NodeName = foStats != null ? foStats.NodeName : string.Empty,
                                        ObserverName = sourceObserver ?? string.Empty,
                                        Source = ObserverName,
                                        PartitionId = partitionId,
                                        ReplicaId = replicaId,
                                        SystemServiceProcessName = systemServiceName ?? string.Empty,
                                        Value = foStats != null ? foStats.Value : string.Empty,
                                    };

                                    await ObserverTelemetryClient.ReportHealthAsync(telemetryData, Token);
                                }

                                // ETW.
                                if (etwEnabled)
                                {
                                    double value = 0;
                                    if (foStats != null)
                                    {
                                        value = double.TryParse(foStats.Value.ToString(), out double val) ? val : 0;
                                    }

                                    Logger.EtwLogger?.Write(
                                       ObserverConstants.ClusterObserverETWEventName,
                                       new
                                       {
                                           ApplicationName = appHealth.ApplicationName.OriginalString,
                                           HealthScope = "Application",
                                           HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                           HealthEventDescription = telemetryDescription,
                                           Metric = metric ?? "AggregatedClusterHealth",
                                           NodeName = foStats != null ? foStats.NodeName : string.Empty,
                                           ObserverName = sourceObserver ?? string.Empty,
                                           Source = ObserverName,
                                           PartitionId = partitionId,
                                           ReplicaId = replicaId,
                                           SystemServiceProcessName = systemServiceName ?? string.Empty,
                                           Value = value,
                                       });
                                }

                                // Reset 
                                telemetryDescription = string.Empty;
                            }

                            continue;
                        }

                        telemetryDescription += $"{Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind)} - {evaluation.AggregatedHealthState}: {evaluation.Description}{Environment.NewLine}{Environment.NewLine}";

                        switch (evaluation.Kind)
                        {
                            // Application in Warning or Error?
                            case HealthEvaluationKind.Application:
                            case HealthEvaluationKind.Applications:
                                {
                                    foreach (var application in clusterHealth.ApplicationHealthStates)
                                    {
                                        Token.ThrowIfCancellationRequested();

                                        // Ignore any Warning state?
                                        if (application.AggregatedHealthState == HealthState.Ok
                                            || (!ConfigSettings.EmitWarningDetails
                                                && application.AggregatedHealthState == HealthState.Warning))
                                        {
                                            continue;
                                        }

                                        telemetryDescription += $"Application in Error or Warning: " +
                                            $"{application.ApplicationName}{Environment.NewLine}";

                                        var appUpgradeStatus = await FabricClientInstance.ApplicationManager.GetApplicationUpgradeProgressAsync(application.ApplicationName);

                                        if (appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingBackInProgress
                                            || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardInProgress)
                                        {
                                            var udInAppUpgrade = await UpgradeChecker.GetUdsWhereApplicationUpgradeInProgressAsync(
                                                FabricClientInstance,
                                                Token,
                                                application.ApplicationName);

                                            string udText = string.Empty;

                                            // -1 means no upgrade in progress for application
                                            // int.MaxValue means an exception was thrown during upgrade check and you should
                                            // check the logs for what went wrong, then fix the bug (if it's a bug you can fix).
                                            if (udInAppUpgrade.Any(ud => ud > -1 && ud < int.MaxValue))
                                            {
                                                udText = $" in UD {udInAppUpgrade.First(ud => ud > -1 && ud < int.MaxValue)}";
                                            }

                                            telemetryDescription +=
                                                $"Note: {application.ApplicationName} is currently upgrading{udText}, " +
                                                $"which may be why it's in a transient error or warning state.{Environment.NewLine}";
                                        }

                                        var appHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                            application.ApplicationName,
                                            ConfigSettings.AsyncTimeout,
                                            token);

                                        var appHealthEvents = appHealth.HealthEvents;

                                        // From FO?
                                        foreach (var appHealthEvent in appHealthEvents.Where(
                                                    ev => ev.HealthInformation.HealthState != HealthState.Ok))
                                        {
                                            Token.ThrowIfCancellationRequested();

                                            // FabricObservers health event details need to be formatted correctly.
                                            // If FO did not emit this event, then foStats will be null.
                                            var foStats = TryGetFOHealthStateEventData(appHealthEvent, HealthScope.Application);
                                            string sourceObserver = null;
                                            string metric = null;
                                            string systemServiceName = null;
                                            string partitionId = null;
                                            string replicaId = null;

                                            if (foStats != null)
                                            {
                                                telemetryDescription += foStats.HealthEventDescription;
                                                sourceObserver = foStats.ObserverName;
                                                partitionId = foStats.PartitionId;
                                                replicaId = foStats.ReplicaId;
                                                metric = foStats.Metric;

                                                if (!string.IsNullOrWhiteSpace(foStats.SystemServiceProcessName))
                                                {
                                                    systemServiceName = foStats.SystemServiceProcessName;
                                                }
                                            }
                                            else if (!string.IsNullOrEmpty(appHealthEvent.HealthInformation.Description))
                                            {
                                                // This wil be whatever is provided in the health event description set by the emitter, which
                                                // was not FO.
                                                telemetryDescription += $"{appHealthEvent.HealthInformation.Description}{Environment.NewLine}";
                                            }

                                            // Telemetry.
                                            if (TelemetryEnabled && ObserverTelemetryClient != null)
                                            {
                                                var telemetryData = new TelemetryData(FabricClientInstance, Token)
                                                {
                                                    ApplicationName = appHealth.ApplicationName.OriginalString,
                                                    HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                                    HealthEventDescription = telemetryDescription,
                                                    Metric = metric ?? "AggregatedClusterHealth",
                                                    ObserverName = sourceObserver ?? string.Empty,
                                                    NodeName = foStats != null ? foStats.NodeName : string.Empty,
                                                    Source = ObserverName,
                                                    PartitionId = partitionId,
                                                    ReplicaId = replicaId,
                                                    SystemServiceProcessName = systemServiceName ?? string.Empty,
                                                    Value = foStats != null ? foStats.Value : string.Empty,
                                                };

                                                await ObserverTelemetryClient.ReportHealthAsync(telemetryData, Token);
                                            }

                                            // ETW.
                                            if (etwEnabled)
                                            {
                                                double value = 0;
                                                if (foStats != null)
                                                {
                                                    value = double.TryParse(foStats.Value.ToString(), out double val) ? val : 0;
                                                }

                                                Logger.EtwLogger?.Write(
                                                    ObserverConstants.ClusterObserverETWEventName,
                                                    new
                                                    {
                                                        ApplicationName = appHealth.ApplicationName.OriginalString,
                                                        HealthScope = "Application",
                                                        HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                                        HealthEventDescription = telemetryDescription,
                                                        Metric = metric ?? "AggregatedClusterHealth",
                                                        NodeName = foStats != null ? foStats.NodeName : string.Empty,
                                                        ObserverName = sourceObserver ?? string.Empty,
                                                        Source = ObserverName,
                                                        PartitionId = partitionId,
                                                        ReplicaId = replicaId,
                                                        SystemServiceProcessName = systemServiceName ?? string.Empty,
                                                        Value = value,
                                                    });
                                            }

                                            // Reset 
                                            telemetryDescription = string.Empty;
                                        }
                                    }

                                    break;
                                }
                            case HealthEvaluationKind.Node:
                            case HealthEvaluationKind.Nodes:
                                {
                                    // Node in Warning or Error?
                                    foreach (var node in clusterHealth.NodeHealthStates)
                                    {
                                        if (node.AggregatedHealthState == HealthState.Ok
                                            || (!ConfigSettings.EmitWarningDetails && node.AggregatedHealthState == HealthState.Warning))
                                        {
                                            continue;
                                        }

                                        telemetryDescription += $"Node in Error or Warning: {node.NodeName}{Environment.NewLine}";

                                        if (udInClusterUpgrade > -1)
                                        {
                                            telemetryDescription +=
                                                $"Note: Cluster is currently upgrading in UD {udInClusterUpgrade}. " +
                                                $"Node {node.NodeName} Error State could be due to this upgrade process, which will take down a node as a " +
                                                $"normal part of upgrade process. This is a temporary condition.{Environment.NewLine}.";
                                        }

                                        var nodeHealth = await FabricClientInstance.HealthManager.GetNodeHealthAsync(
                                            node.NodeName,
                                            ConfigSettings.AsyncTimeout,
                                            token);

                                        // From FO?
                                        foreach (var nodeHealthEvent in nodeHealth.HealthEvents.Where(
                                                 ev => ev.HealthInformation.HealthState != HealthState.Ok))
                                        {
                                            Token.ThrowIfCancellationRequested();

                                            // FabricObservers health event details need to be formatted correctly.
                                            // If FO did not emit this event, then foStats will be null.
                                            var foStats = TryGetFOHealthStateEventData(nodeHealthEvent, HealthScope.Node);
                                            string sourceObserver = null;
                                            string metric = null;

                                            if (foStats != null)
                                            {
                                                telemetryDescription += foStats.HealthEventDescription;
                                                sourceObserver = foStats.ObserverName;
                                                metric = foStats.Metric;
                                            }
                                            else if (!string.IsNullOrEmpty(nodeHealthEvent.HealthInformation.Description))
                                            {
                                                // This wil be whatever is provided in the health event description set by the emitter, which
                                                // was not FO.
                                                telemetryDescription += $"{nodeHealthEvent.HealthInformation.Description}{Environment.NewLine}";
                                            }

                                            var targetNodeList =
                                                await FabricClientInstance.QueryManager.GetNodeListAsync(
                                                    node.NodeName,
                                                    ConfigSettings.AsyncTimeout,
                                                    token).ConfigureAwait(false);

                                            Node targetNode = null;

                                            if (targetNodeList?.Count > 0)
                                            {
                                                targetNode = targetNodeList[0];
                                            }

                                            if (TelemetryEnabled && ObserverTelemetryClient != null)
                                            {
                                                var telemetryData = new TelemetryData(FabricClientInstance, Token)
                                                {
                                                    NodeName = node.NodeName,
                                                    HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                                    HealthEventDescription = $"{telemetryDescription}{Environment.NewLine}Node Status: {(targetNode != null ? Enum.GetName(typeof(NodeStatus), targetNode.NodeStatus) : string.Empty)}",
                                                    Metric = metric ?? "AggregatedClusterHealth",
                                                    ObserverName = sourceObserver ?? string.Empty,
                                                    Source = ObserverName,
                                                    Value = foStats != null ? foStats.Value : string.Empty,
                                                };

                                                // Telemetry.
                                                await ObserverTelemetryClient.ReportHealthAsync(telemetryData, Token);
                                            }

                                            // ETW.
                                            if (etwEnabled)
                                            {
                                                double value = 0;
                                                if (foStats != null)
                                                {
                                                    value = double.TryParse(foStats.Value.ToString(), out double val) ? val : 0;
                                                }

                                                Logger.EtwLogger?.Write(
                                                    ObserverConstants.ClusterObserverETWEventName,
                                                    new
                                                    {
                                                        node.NodeName,
                                                        NodeStatus = targetNode != null ? Enum.GetName(typeof(NodeStatus), targetNode.NodeStatus) : string.Empty,
                                                        HealthScope = "Node",
                                                        HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                                        HealthEventDescription = telemetryDescription,
                                                        Metric = metric ?? "AggregatedClusterHealth",
                                                        ObserverName = sourceObserver ?? string.Empty,
                                                        Source = ObserverName,
                                                        Value = value,
                                                    });
                                                ;
                                            }

                                            // Reset 
                                            telemetryDescription = string.Empty;
                                        }
                                    }

                                    break;
                                }
                            default:

                                continue;
                        }
                    }

                    // Track current aggregated health state for use in next run.
                    LastKnownClusterHealthState = clusterHealth.AggregatedHealthState;
                }
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                ObserverLogger.LogWarning(
                   $"ProbeClusterHealthAsync threw {e.Message}{Environment.NewLine}" +
                    "Probing will continue.");
#if DEBUG
                if (TelemetryEnabled && ObserverTelemetryClient != null)
                {
                    var telemetryData = new TelemetryData(FabricClientInstance, Token)
                    {
                        HealthState = "Warning",
                        HealthEventDescription = $"ProbeClusterHealthAsync threw {e.Message}{Environment.NewLine}" +
                                                  "Probing will continue.",
                        Metric = "AggregatedClusterHealth",
                        Source = ObserverName,
                    };

                    await ObserverTelemetryClient.ReportHealthAsync(telemetryData, Token);
                }
#endif  
            }
        }

        private async Task MonitorNodeStatusAsync()
        {
            // If a node's NodeStatus is Disabling, Disabled, or Down 
            // for at or above the specified maximum time (in Settings.xml),
            // then CO will emit a Warning signal.
            var nodeList =
            await FabricClientInstance.QueryManager.GetNodeListAsync(
                    null,
                    ConfigSettings.AsyncTimeout,
                    Token).ConfigureAwait(true);

            // Are any of the nodes that were previously in non-Up status, now Up?
            if (NodeStatusDictionary.Count > 0)
            {
                foreach (var nodeDictItem in NodeStatusDictionary)
                {
                    if (!nodeList.Any(n => n.NodeName == nodeDictItem.Key
                                        && n.NodeStatus == NodeStatus.Up))
                    {
                        continue;
                    }

                    // Telemetry.
                    if (TelemetryEnabled && ObserverTelemetryClient != null)
                    {
                        var telemetry = new TelemetryData(FabricClientInstance, Token)
                        {
                            HealthState = "Ok",
                            HealthEventDescription = $"{nodeDictItem.Key} is now Up.",
                            Metric = "NodeStatus",
                            NodeName = nodeDictItem.Key,
                            Value = "Up",
                            Source = ObserverName,
                        };

                        await ObserverTelemetryClient.ReportHealthAsync(telemetry, Token);
                    }

                    // ETW.
                    if (etwEnabled)
                    {
                        Logger.EtwLogger?.Write(
                            ObserverConstants.ClusterObserverETWEventName,
                            new
                            {
                                HealthScope = "Node",
                                HealthState = "Ok",
                                HealthEventDescription = $"{nodeDictItem.Key} is now Up.",
                                Metric = "NodeStatus",
                                NodeName = nodeDictItem.Key,
                                NodeStatus = "Up",
                                Source = ObserverName,
                            });
                    }

                    // Clear dictionary entry.
                    NodeStatusDictionary.Remove(nodeDictItem.Key);
                }
            }

            if (!nodeList.All(
                    n =>
                    n.NodeStatus == NodeStatus.Up))
            {
                var filteredList = nodeList.Where(
                         node => node.NodeStatus == NodeStatus.Disabled
                              || node.NodeStatus == NodeStatus.Disabling
                              || node.NodeStatus == NodeStatus.Down);

                foreach (var node in filteredList)
                {
                    if (!NodeStatusDictionary.ContainsKey(node.NodeName))
                    {
                        NodeStatusDictionary.Add(
                            node.NodeName,
                            (node.NodeStatus, DateTime.Now, DateTime.Now));
                    }
                    else
                    {
                        if (NodeStatusDictionary.TryGetValue(
                            node.NodeName, out var tuple))
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
                        if (TelemetryEnabled && ObserverTelemetryClient != null)
                        {
                            var telemetry = new TelemetryData(FabricClientInstance, Token)
                            {
                                HealthState = "Warning",
                                HealthEventDescription = message,
                                Metric = "NodeStatus",
                                NodeName = kvp.Key,
                                Value = $"{kvp.Value.NodeStatus}",
                                Source = ObserverName,
                            };

                            await ObserverTelemetryClient.ReportHealthAsync(telemetry, Token);
                        }

                        // ETW.
                        if (etwEnabled)
                        {
                            Logger.EtwLogger?.Write(
                                ObserverConstants.ClusterObserverETWEventName,
                                new
                                {
                                    HealthScope = "Node",
                                    HealthState = "Warning",
                                    HealthEventDescription = message,
                                    Metric = "NodeStatus",
                                    NodeName = kvp.Key,
                                    NodeStatus = $"{kvp.Value.NodeStatus}",
                                    Source = ObserverName,
                                });
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This function determines if a health event was created by FabricObserver.
        /// If so, then it constructs a string that CO will use as part of the telemetry post.
        /// </summary>
        /// <param name="healthEvent">A Fabric Health event.</param>
        /// <returns>A formatted string that contains the FabricObserver error/warning code 
        /// and description of the detected issue.</returns>
        private TelemetryData TryGetFOHealthStateEventData(HealthEvent healthEvent, HealthScope scope)
        {
            if (!JsonHelper.IsJson<TelemetryData>(healthEvent.HealthInformation.Description))
            {
                return null;
            }

            TelemetryData foHealthData = JsonConvert.DeserializeObject<TelemetryData>(
                healthEvent.HealthInformation.Description);

            // Supported Error code from FO?
            if (scope == HealthScope.Node && !FoErrorWarningCodes.NodeErrorCodesDictionary.ContainsKey(foHealthData.Code))
            {
                return null;
            }

            if (scope == HealthScope.Application && !FoErrorWarningCodes.AppErrorCodesDictionary.ContainsKey(foHealthData.Code))
            {
                return null;
            }

            return foHealthData;
        }

        /// <summary>
        /// Checks if the RepairManager System app service is deployed in the cluster.
        /// </summary>
        /// <param name="cancellationToken">cancellation token to stop the async operation</param>
        /// <returns>true if RepairManager service is present in cluster, otherwise false</returns>
        internal async Task<bool> IsRepairManagerDeployedAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var serviceList = await FabricClientInstance.QueryManager.GetServiceListAsync(
                                      fabricSystemAppUri,
                                      repairManagerServiceUri,
                                      ConfigSettings.AsyncTimeout,
                                      cancellationToken).ConfigureAwait(true);

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
        internal async Task<RepairTaskList> GetRepairTasksCurrentlyProcessingAsync(CancellationToken cancellationToken)
        {
            if (!await IsRepairManagerDeployedAsync(cancellationToken))
            {
                return null;
            }

            try
            {
                var repairTasks = await FabricClientInstance.RepairManager.GetRepairTaskListAsync(
                                            null,
                                            RepairTaskStateFilter.Active |
                                            RepairTaskStateFilter.Approved |
                                            RepairTaskStateFilter.Executing,
                                            null,
                                            ConfigSettings.AsyncTimeout,
                                            cancellationToken);

                return repairTasks;
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException || e is TimeoutException)
            {

            }

            return null;
        }
    }
}
