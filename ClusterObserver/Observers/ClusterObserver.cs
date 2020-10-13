// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Utilities;
using FabricClusterObserver.Utilities.Telemetry;
using Newtonsoft.Json;

namespace FabricClusterObserver.Observers
{
    public class ClusterObserver : ObserverBase
    {
        private TimeSpan maxTimeNodeStatusNotOk;
        private readonly bool etwEnabled;

        private EventSource EtwLogger
        {
            get; set;
        }

        private readonly Uri repairManagerServiceUri = new Uri("fabric:/System/RepairManagerService");
        private readonly Uri fabricSystemAppUri = new Uri("fabric:/System");

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserver"/> class.
        /// This observer is a singleton (one partition) stateless service that runs on one node in an SF cluster.
        /// ClusterObserver and FabricObserver are completely independent service processes.
        /// </summary>
        public ClusterObserver()
            : base(ObserverConstants.ClusterObserverName)
        {
            if (ObserverManager.EtwEnabled
                && !string.IsNullOrEmpty(ObserverManager.EtwProviderName))
            {
                EtwLogger = new EventSource(ObserverManager.EtwProviderName);
                etwEnabled = true;
            }
        }

        private HealthState ClusterHealthState { get; set; } = HealthState.Unknown;

        /// <summary>
        /// Dictionary that holds node name (key) and tuple of node status, first detected time, last detected time.
        /// </summary>
        private Dictionary<string, (NodeStatus NodeStatus, DateTime FirstDetectedTime, DateTime LastDetectedTime)> NodeStatusDictionary
        {
            get;
        } =
                new Dictionary<string, (NodeStatus NodeStatus, DateTime FirstDetectedTime, DateTime LastDetectedTime)>();

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml
            if (RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
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

        public override async Task ReportAsync(CancellationToken token)
        {
            await ProbeClusterHealthAsync(token).ConfigureAwait(true);
        }

        private async Task ProbeClusterHealthAsync(CancellationToken token)
        {
            // The point of this service is to emit SF Health telemetry to your external log analytics service, so
            // if telemetry is not enabled or you don't provide an AppInsights instrumentation key, for example, 
            // then querying HM for health info isn't useful.
            if (!this.etwEnabled && (!IsTelemetryEnabled || ObserverTelemetryClient == null))
            {
                return;
            }

            token.ThrowIfCancellationRequested();

            // Get ClusterObserver settings (specified in PackageRoot/Config/Settings.xml).
            _ = bool.TryParse(
                GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.EmitHealthWarningEvaluationConfigurationSetting,
                    "false"), out bool emitWarningDetails);

            _ = bool.TryParse(
                GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.EmitOkHealthStateSetting,
                    "false"), out bool emitOkHealthState);

            _ = TimeSpan.TryParse(
                GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.MaxTimeNodeStatusNotOkSetting,
                    "04:00:00"), out this.maxTimeNodeStatusNotOk);

            try
            {
                string telemetryDescription = string.Empty;

                // Start monitoring node status.
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
                                                AsyncClusterOperationTimeoutSeconds,
                                                token).ConfigureAwait(true);

                // Previous run generated unhealthy evaluation report. It's now Ok.
                if (emitOkHealthState && clusterHealth.AggregatedHealthState == HealthState.Ok
                    && (ClusterHealthState == HealthState.Error
                    || (emitWarningDetails && ClusterHealthState == HealthState.Warning)))
                {
                    ClusterHealthState = HealthState.Ok;

                    // Telemetry.
                    if (IsTelemetryEnabled)
                    {
                        var telemetry = new TelemetryData(FabricClientInstance, Token)
                        {
                            HealthScope = "Cluster",
                            HealthState = "Ok",
                            HealthEventDescription = "Cluster has recovered from previous Error/Warning state.",
                            Metric = "AggregatedClusterHealth",
                            Source = ObserverName,
                        };

                        await ObserverTelemetryClient?.ReportHealthAsync(telemetry, Token);
                    }

                    // ETW.
                    if (this.etwEnabled)
                    {
                        EtwLogger?.Write(
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
                    if (!emitWarningDetails && clusterHealth.AggregatedHealthState == HealthState.Warning)
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

                        // Warn when System applications are in warning, but no need to dig in deeper.
                        if (evaluation.Kind == HealthEvaluationKind.SystemApplication)
                        {
                            if (IsTelemetryEnabled)
                            {
                                var telemetryData = new TelemetryData(FabricClientInstance, Token)
                                {
                                    HealthScope = "SystemApplication",
                                    HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                    HealthEventDescription = $"{evaluation.AggregatedHealthState}: {evaluation.Description}",
                                    Metric = "AggregatedClusterHealth",
                                    Source = ObserverName,
                                };

                                // Telemetry.
                                await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, Token);
                            }

                            if (this.etwEnabled)
                            {
                                EtwLogger?.Write(
                                    ObserverConstants.ClusterObserverETWEventName,
                                    new
                                    {
                                        HealthScope = "SystemApplication",
                                        HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                        HealthEventDescription = $"{evaluation.AggregatedHealthState}: {evaluation.Description}",
                                        Metric = "AggregatedClusterHealth",
                                        Source = ObserverName,
                                    });
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
                                        || (!emitWarningDetails
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
                                        AsyncClusterOperationTimeoutSeconds,
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
                                        string value = null;
                                        Guid partitionId = Guid.Empty;
                                        long replicaId = 0;

                                        if (foStats != null)
                                        {
                                            telemetryDescription += foStats.HealthEventDescription;
                                            sourceObserver = foStats.ObserverName;
                                            partitionId = foStats.PartitionId;
                                            replicaId = foStats.ReplicaId;
                                            metric = FoErrorWarningCodes.GetErrorWarningNameFromFOCode(
                                                 appHealthEvent.HealthInformation.SourceId);

                                            value = foStats.Value.ToString();
                                        }
                                        else if (!string.IsNullOrEmpty(appHealthEvent.HealthInformation.Description))
                                        {
                                            // This wil be whatever is provided in the health event description set by the emitter, which
                                            // was not FO.
                                            telemetryDescription += $"{appHealthEvent.HealthInformation.Description}{Environment.NewLine}";
                                        }

                                        // Telemetry.
                                        if (IsTelemetryEnabled)
                                        {
                                            var telemetryData = new TelemetryData(FabricClientInstance, Token)
                                            {
                                                ApplicationName = appHealth.ApplicationName.OriginalString,
                                                HealthScope = "Application",
                                                HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                                HealthEventDescription = telemetryDescription,
                                                Metric = metric ?? "AggregatedClusterHealth",
                                                ObserverName = sourceObserver ?? string.Empty,
                                                Source = ObserverName,
                                                PartitionId = partitionId,
                                                ReplicaId = replicaId,
                                                Value = double.TryParse(value, out double val) != false ? val : 0,
                                            };

                                            await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, Token);
                                        }

                                        // ETW.
                                        if (this.etwEnabled)
                                        {
                                            EtwLogger?.Write(
                                                ObserverConstants.ClusterObserverETWEventName,
                                                new
                                                {
                                                    ApplicationName = appHealth.ApplicationName.OriginalString,
                                                    HealthScope = "Application",
                                                    HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                                    HealthEventDescription = telemetryDescription,
                                                    Metric = metric ?? "AggregatedClusterHealth",
                                                    ObserverName = sourceObserver ?? string.Empty,
                                                    Source = ObserverName,
                                                    PartitionId = partitionId,
                                                    ReplicaId = replicaId,
                                                    Value = double.TryParse(value, out double val) != false ? val : 0,
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
                                        || (!emitWarningDetails && node.AggregatedHealthState == HealthState.Warning))
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
                                        AsyncClusterOperationTimeoutSeconds,
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
                                        string value = null;

                                        if (foStats != null)
                                        {
                                            telemetryDescription += foStats.HealthEventDescription;
                                            sourceObserver = foStats.ObserverName;
                                            metric = FoErrorWarningCodes.GetErrorWarningNameFromFOCode(
                                                 nodeHealthEvent.HealthInformation.SourceId);
                                            value = foStats.Value.ToString();
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
                                                AsyncClusterOperationTimeoutSeconds,
                                                token).ConfigureAwait(false);

                                        Node targetNode = null;

                                        if (targetNodeList?.Count > 0)
                                        {
                                            targetNode = targetNodeList[0];
                                        }

                                        if (IsTelemetryEnabled)
                                        {
                                            var telemetryData = new TelemetryData(FabricClientInstance, Token)
                                            {
                                                NodeName = node.NodeName,
                                                NodeStatus = targetNode != null ? Enum.GetName(typeof(NodeStatus), targetNode.NodeStatus) : string.Empty,
                                                HealthScope = "Node",
                                                HealthState = Enum.GetName(typeof(HealthState), clusterHealth.AggregatedHealthState),
                                                HealthEventDescription = telemetryDescription,
                                                Metric = metric ?? "AggregatedClusterHealth",
                                                ObserverName = sourceObserver ?? string.Empty,
                                                Source = ObserverName,
                                                Value = double.TryParse(value, out double val) != false ? val : 0,
                                            };

                                            // Telemetry.
                                            await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, Token);
                                        }

                                        // ETW.
                                        if (this.etwEnabled)
                                        {
                                            EtwLogger?.Write(
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
                                                    Value = double.TryParse(value, out double val) != false ? val : 0,
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
                    ClusterHealthState = clusterHealth.AggregatedHealthState;
                }
            }
            catch (Exception e) when
                  (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                ObserverLogger.LogWarning(
                   $"ProbeClusterHealthAsync threw {e.Message}{Environment.NewLine}" +
                    "Probing will continue.");
#if DEBUG
                if (IsTelemetryEnabled)
                {
                    var telemetryData = new TelemetryData(FabricClientInstance, Token)
                    {
                        HealthScope = "Cluster",
                        HealthState = "Warning",
                        HealthEventDescription = $"ProbeClusterHealthAsync threw {e.Message}{Environment.NewLine}" +
                                                  "Probing will continue.",
                        Metric = "AggregatedClusterHealth",
                        Source = ObserverName,
                    };

                    await ObserverTelemetryClient?.ReportHealthAsync(telemetryData, Token);        
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
                    AsyncClusterOperationTimeoutSeconds,
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
                    if (IsTelemetryEnabled)
                    {
                        var telemetry = new TelemetryData(FabricClientInstance, Token)
                        {
                            HealthScope = "Node",
                            HealthState = "Ok",
                            HealthEventDescription = $"{nodeDictItem.Key} is now Up.",
                            Metric = "NodeStatus",
                            NodeName = nodeDictItem.Key,
                            NodeStatus = "Up",
                            Source = ObserverName,
                        };

                        await ObserverTelemetryClient?.ReportHealthAsync(telemetry, Token);
                    }

                    // ETW.
                    if (this.etwEnabled)
                    {
                        EtwLogger?.Write(
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
                                >= this.maxTimeNodeStatusNotOk))
                    {
                        var kvp = NodeStatusDictionary.FirstOrDefault(
                                       dict => dict.Key == node.NodeName
                                        && dict.Value.LastDetectedTime.Subtract(dict.Value.FirstDetectedTime)
                                        >= this.maxTimeNodeStatusNotOk);

                        var message =
                            $"Node {kvp.Key} has been {kvp.Value.NodeStatus} " +
                            $"for {Math.Round(kvp.Value.LastDetectedTime.Subtract(kvp.Value.FirstDetectedTime).TotalHours, 2)} hours.{Environment.NewLine}";

                        // Telemetry.
                        if (IsTelemetryEnabled)
                        {
                            var telemetry = new TelemetryData(FabricClientInstance, Token)
                            {
                                HealthScope = "Node",
                                HealthState = "Warning",
                                HealthEventDescription = message,
                                Metric = "NodeStatus",
                                NodeName = kvp.Key,
                                NodeStatus = $"{kvp.Value.NodeStatus}",
                                Source = ObserverName,
                            };

                            await ObserverTelemetryClient?.ReportHealthAsync(telemetry, Token);
                        }

                        // ETW.
                        if (this.etwEnabled)
                        {
                            EtwLogger?.Write(
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
                                      this.fabricSystemAppUri,
                                      this.repairManagerServiceUri,
                                      AsyncClusterOperationTimeoutSeconds,
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
        internal async Task<RepairTaskList> GetRepairTasksCurrentlyProcessingAsync(
            CancellationToken cancellationToken)
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
                    AsyncClusterOperationTimeoutSeconds,
                    cancellationToken);

                return repairTasks;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
            }

            return null;
        }
    }
}
