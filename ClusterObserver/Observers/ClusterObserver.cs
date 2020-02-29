// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Utilities;
using FabricClusterObserver.Utilities.Telemetry;

namespace FabricClusterObserver.Observers
{
    public class ClusterObserver : ObserverBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserver"/> class.
        /// This observer is a singleton (one partition) stateless service that runs on one node in an SF cluster.
        /// ClusterObserver and FabricObserver are completely independent service processes.
        /// </summary>
        public ClusterObserver()
            : base(ObserverConstants.ClusterObserverName)
        {
        }

        private HealthState ClusterHealthState { get; set; } = HealthState.Unknown;

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            await ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            await ProbeClusterHealthAsync(token).ConfigureAwait(true);
        }

        // TODO: Check for active fabric repairs (RM) in cluster.
        private async Task ProbeClusterHealthAsync(CancellationToken token)
        {
            // The point of this service is to emit SF Health telemetry to your external log analytics service, so
            // if telemetry is not enabled or you don't provide an AppInsights instrumentation key, for example, 
            // then querying HM for health info isn't useful.
            if (!this.IsTelemetryEnabled || this.ObserverTelemetryClient == null)
            {
                return;
            }

            token.ThrowIfCancellationRequested();

            // Get ClusterObserver settings (specified in PackageRoot/Config/Settings.xml).
            _ = bool.TryParse(
                this.GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.EmitHealthWarningEvaluationConfigurationSetting,
                    "false"), out bool emitWarningDetails);

            _ = bool.TryParse(
                this.GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.EmitOkHealthState,
                    "false"), out bool emitOkHealthState);

            _ = bool.TryParse(
                this.GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.EmitHealthStatistics,
                    "false"), out bool emitHealthStatistics);

            try
            {
                
                var clusterHealth = await this.FabricClientInstance.HealthManager.GetClusterHealthAsync(
                                                this.AsyncClusterOperationTimeoutSeconds,
                                                token).ConfigureAwait(true);

                string telemetryDescription = string.Empty;

                // Previous run generated unhealthy evaluation report. Clear it (send Ok) .
                if (emitOkHealthState && clusterHealth.AggregatedHealthState == HealthState.Ok
                    && (this.ClusterHealthState == HealthState.Error
                    || (emitWarningDetails && this.ClusterHealthState == HealthState.Warning)))
                {
                    telemetryDescription += "Cluster has recovered from previous Error/Warning state.";
                }
                else // Construct unhealthy state information.
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

                        telemetryDescription += $"{Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind)} - {evaluation.AggregatedHealthState}: {evaluation.Description}{Environment.NewLine}{Environment.NewLine}";

                        switch (evaluation.Kind)
                        {
                            // Application in Warning or Error?
                            case HealthEvaluationKind.Application:
                            case HealthEvaluationKind.Applications:
                            {
                                foreach (var app in clusterHealth.ApplicationHealthStates)
                                {
                                    Token.ThrowIfCancellationRequested();
                                
                                    if (app.AggregatedHealthState == HealthState.Ok)
                                    {
                                        continue;
                                    }

                                    // Ignore any Warning state?
                                    if (!emitWarningDetails
                                        && app.AggregatedHealthState == HealthState.Warning)
                                    {
                                        continue;
                                    }

                                    telemetryDescription += $"Application in Error or Warning: {app.ApplicationName}{Environment.NewLine}";

                                    foreach (var application in clusterHealth.ApplicationHealthStates)
                                    {
                                        Token.ThrowIfCancellationRequested();

                                        if (application.AggregatedHealthState == HealthState.Ok
                                            || (!emitWarningDetails 
                                                && application.AggregatedHealthState == HealthState.Warning))
                                        {
                                            continue;
                                        }

                                        var appUpgradeStatus = await FabricClientInstance.ApplicationManager.GetApplicationUpgradeProgressAsync(application.ApplicationName);
                                    
                                        if (appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingBackInProgress
                                            || appUpgradeStatus.UpgradeState == ApplicationUpgradeState.RollingForwardInProgress)
                                        {
                                            var udInAppUpgrade = await UpgradeChecker.GetUdsWhereApplicationUpgradeInProgressAsync(
                                                FabricClientInstance,
                                                Token,
                                                appUpgradeStatus.ApplicationName);
                                        
                                            string udText = string.Empty;
                                        
                                            // -1 means no upgrade in progress for application
                                            // int.MaxValue means an exception was thrown during upgrade check and you should
                                            // check the logs for what went wrong, then fix the bug (if it's a bug you can fix).
                                            if (udInAppUpgrade.Any(ud => ud > -1 && ud < int.MaxValue))
                                            {
                                                udText = $" in UD {udInAppUpgrade.First(ud => ud > -1 && ud < int.MaxValue)}";
                                            }

                                            telemetryDescription += 
                                                $"Note: {app.ApplicationName} is currently upgrading{udText}, " +
                                                $"which may be why it's in a transient error or warning state.{Environment.NewLine}";
                                        }

                                        var appHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                            application.ApplicationName,
                                            this.AsyncClusterOperationTimeoutSeconds,
                                            token);

                                        var serviceHealthStates = appHealth.ServiceHealthStates;
                                        var appHealthEvents = appHealth.HealthEvents;

                                        // From FO?
                                        foreach (var appHealthEvent in appHealthEvents)
                                        {
                                            Token.ThrowIfCancellationRequested();

                                            if (!FoErrorWarningCodes.AppErrorCodesDictionary.ContainsKey(appHealthEvent.HealthInformation.SourceId))
                                            {
                                                continue;
                                            }

                                            string errorWarning = "Warning";

                                            if (FoErrorWarningCodes.AppErrorCodesDictionary[appHealthEvent.HealthInformation.SourceId].Contains("Error"))
                                            {
                                                errorWarning = "Error";
                                            }

                                            telemetryDescription +=
                                                $"  FabricObserver {errorWarning} Code: {appHealthEvent.HealthInformation.SourceId}{Environment.NewLine}" +
                                                $"  {errorWarning} Details: {appHealthEvent.HealthInformation.Description}{Environment.NewLine}";
                                        }

                                        // Service in error?
                                        foreach (var service in serviceHealthStates)
                                        {
                                            Token.ThrowIfCancellationRequested();

                                            if (service.AggregatedHealthState == HealthState.Ok
                                                || (!emitWarningDetails && service.AggregatedHealthState == HealthState.Warning))
                                            {
                                                continue;
                                            }

                                            telemetryDescription += $"Service in Error: {service.ServiceName}{Environment.NewLine}";
                                        }
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
                                        this.AsyncClusterOperationTimeoutSeconds,
                                        token);

                                    // From FO?
                                    foreach (var nodeHealthEvent in nodeHealth.HealthEvents)
                                    {
                                        Token.ThrowIfCancellationRequested();

                                        if (!FoErrorWarningCodes.NodeErrorCodesDictionary.ContainsKey(nodeHealthEvent.HealthInformation.SourceId))
                                        {
                                            continue;
                                        }

                                        string errorWarning = "Warning";

                                        if (FoErrorWarningCodes.NodeErrorCodesDictionary[nodeHealthEvent.HealthInformation.SourceId].Contains("Error"))
                                        {
                                            errorWarning = "Error";
                                        }

                                        telemetryDescription +=
                                            $"  FabricObserver {errorWarning} Code: {nodeHealthEvent.HealthInformation.SourceId}{Environment.NewLine}" +
                                            $"  {errorWarning} Details: {nodeHealthEvent.HealthInformation.Description}{Environment.NewLine}";
                                    }
                                }

                                break;
                            }
                            case HealthEvaluationKind.Invalid:
                                break;
                            case HealthEvaluationKind.Event:
                                break;
                            case HealthEvaluationKind.Replicas:
                                break;
                            case HealthEvaluationKind.Partitions:
                                break;
                            case HealthEvaluationKind.DeployedServicePackages:
                                break;
                            case HealthEvaluationKind.DeployedApplications:
                                break;
                            case HealthEvaluationKind.Services:
                                break;
                            case HealthEvaluationKind.SystemApplication:
                                break;
                            case HealthEvaluationKind.UpgradeDomainDeployedApplications:
                                break;
                            case HealthEvaluationKind.UpgradeDomainNodes:
                                break;
                            case HealthEvaluationKind.Replica:
                                break;
                            case HealthEvaluationKind.Partition:
                                break;
                            case HealthEvaluationKind.DeployedServicePackage:
                                break;
                            case HealthEvaluationKind.DeployedApplication:
                                break;
                            case HealthEvaluationKind.Service:
                                break;
                            case HealthEvaluationKind.DeltaNodesCheck:
                                break;
                            case HealthEvaluationKind.UpgradeDomainDeltaNodesCheck:
                                break;
                            case HealthEvaluationKind.ApplicationTypeApplications:
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }

                    // HealthStatistics as a string.
                    if (emitHealthStatistics)
                    {
                        telemetryDescription += $"{clusterHealth.HealthStatistics}";
                    }
                }

                // Track current health state for use in next run.
                this.ClusterHealthState = clusterHealth.AggregatedHealthState;

                // This means there is no cluster health state data to emit.
                if (string.IsNullOrEmpty(telemetryDescription))
                {
                    return;
                }

                // Telemetry.
                await this.ObserverTelemetryClient?.ReportHealthAsync(
                        HealthScope.Cluster,
                        "AggregatedClusterHealth",
                        clusterHealth.AggregatedHealthState,
                        telemetryDescription,
                        this.ObserverName,
                        this.Token);
            }
            catch (Exception e) when 
                  (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                this.ObserverLogger.LogError(
                    $"Unable to determine cluster health:{Environment.NewLine}{e}");

                // Telemetry.
                await this.ObserverTelemetryClient.ReportHealthAsync(
                        HealthScope.Cluster,
                        "AggregatedClusterHealth",
                        HealthState.Unknown,
                        $"ProbeClusterHealthAsync threw {e.Message}{Environment.NewLine}" +
                        "Unable to determine Cluster Health. Probing will continue.",
                        this.ObserverName,
                        this.Token);
            }
        }
    }
}
