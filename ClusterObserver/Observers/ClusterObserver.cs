// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Utilities.Telemetry;
using FabricClusterObserver.Utilities;
using System.Fabric;
using System.Linq;

namespace FabricClusterObserver
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
                this.Token.ThrowIfCancellationRequested();

                return;
            }

            await ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            await this.ProbeClusterHealthAsync(token).ConfigureAwait(true);
        }

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
                    ObserverConstants.IgnoreSystemAppWarnings,
                    "false"), out bool ignoreSystemAppWarnings);

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
                string clusterHealthStatistics = string.Empty;

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

                    foreach (var evaluation in unhealthyEvaluations)
                    {
                        token.ThrowIfCancellationRequested();

                        telemetryDescription += $"{Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind)} - {evaluation.AggregatedHealthState}: {evaluation.Description}{Environment.NewLine}";

                        // Application in Warning or Error?
                        // Note: SF System app Warnings can be noisy, ephemeral (not Errors - you should generally not ignore Error states),
                        // so check for them and ignore if specified in your config's IgnoreFabricSystemAppWarnings setting.
                        foreach (var app in clusterHealth.ApplicationHealthStates)
                        {
                            if (app.AggregatedHealthState == HealthState.Ok
                                || (emitWarningDetails
                                   && (app.AggregatedHealthState != HealthState.Warning
                                      || (evaluation.Kind == HealthEvaluationKind.SystemApplication
                                      && ignoreSystemAppWarnings))))
                            {
                                continue;
                            }

                            telemetryDescription += $"Application in Error or Warning: {app.ApplicationName}{Environment.NewLine}";
                        }

                        // Custom Cluster Health Events: ClusterHealthReports you create (report to HM) become Cluster HealthEvents.
                        foreach (var healthEvent in clusterHealth.HealthEvents)
                        {
                            if (healthEvent.HealthInformation.HealthState == HealthState.Ok
                                || string.IsNullOrEmpty(healthEvent.HealthInformation.Description)
                                || emitWarningDetails && healthEvent.HealthInformation.HealthState != HealthState.Warning)
                            {
                                continue;
                            }

                            telemetryDescription += $"Cluster HealthEvent Details: {healthEvent.HealthInformation.Description}{Environment.NewLine}";
                        }
                    }

                    // HealthStatistics as a string.
                    if (emitHealthStatistics)
                    {
                        telemetryDescription += $"{clusterHealth.HealthStatistics.ToString()}";
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
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException)
            {
                this.ObserverLogger.LogError(
                    $"Unable to determine cluster health:{Environment.NewLine}{e.ToString()}");

                // Telemetry.
                await this.ObserverTelemetryClient.ReportHealthAsync(
                        HealthScope.Cluster,
                        "AggregatedClusterHealth",
                        HealthState.Unknown,
                        $"ProbeClusterHealthAsync threw {e.Message}{Environment.NewLine}" +
                        $"Unable to determine Cluster Health. Probing will continue.",
                        this.ObserverName,
                        this.Token);
            }
        }
    }
}
