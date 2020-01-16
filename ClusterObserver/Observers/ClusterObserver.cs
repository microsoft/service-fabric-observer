// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Utilities.Telemetry;
using FabricClusterObserver.Utilities;

namespace FabricClusterObserver
{
    class ClusterObserver : ObserverBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserver"/> class.
        /// This observer runs on one node and as an independent service since FabricObserver 
        /// is a -1 singleton partition service (runs on every node). ClusterObserver and FabricObserver
        /// can run in the same cluster as they are independent processes...
        /// </summary>
        public ClusterObserver()
            : base(ObserverConstants.ClusterObserverName)
        {
        }

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
            token.ThrowIfCancellationRequested();

            _ = bool.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.ClusterObserverConfigurationSectionName,
                    ObserverConstants.EmitHealthWarningEvaluationConfigurationSetting), out bool emitWarningDetails);
            
            try
            {
                var clusterHealth = await this.FabricClientInstance.HealthManager.GetClusterHealthAsync(
                                                this.AsyncClusterOperationTimeoutSeconds,
                                                token).ConfigureAwait(true);

                if (clusterHealth.AggregatedHealthState == HealthState.Ok
                    || (emitWarningDetails && clusterHealth.AggregatedHealthState != HealthState.Warning))
                {
                    return;
                }

                string unhealthyEvaluationsDescription = string.Empty;
                var unhealthyEvaluations = clusterHealth.UnhealthyEvaluations;

                // Unhealthy evaluation descriptions...
                foreach (var evaluation in unhealthyEvaluations)
                {
                    token.ThrowIfCancellationRequested();

                    unhealthyEvaluationsDescription += $"{Enum.GetName(typeof(HealthEvaluationKind), evaluation.Kind)} - {evaluation.AggregatedHealthState}: {evaluation.Description}\n";

                    // Application in error/warning?...
                    foreach (var app in clusterHealth.ApplicationHealthStates)
                    {
                        if (app.AggregatedHealthState == HealthState.Ok
                            || (emitWarningDetails && app.AggregatedHealthState != HealthState.Warning))
                        {
                            continue;
                        }

                        unhealthyEvaluationsDescription += $"Application in Error or Warning: {app.ApplicationName}\n";
                    }
                }

                // Telemetry...
                if (this.IsTelemetryEnabled)
                {
                    await this.ObserverTelemetryClient?.ReportHealthAsync(
                        HealthScope.Cluster,
                        "AggregatedClusterHealth",
                        clusterHealth.AggregatedHealthState,
                        unhealthyEvaluationsDescription,
                        this.ObserverName,
                        this.Token);
                }
            }
            catch (ArgumentException ae) 
            { 
                this.ObserverLogger.LogError("Unable to determine cluster health:\n {0}", ae.ToString()); 
            }
            catch (FabricException fe) 
            { 
                this.ObserverLogger.LogError("Unable to determine cluster health:\n {0}", fe.ToString()); 
            }
            catch (TimeoutException te) 
            { 
                this.ObserverLogger.LogError("Unable to determine cluster health:\n {0}", te.ToString()); 
            }
            catch (Exception e)
            {
                this.ObserverLogger.LogError("Unable to determine cluster health:\n {0}", e.ToString());

                throw;
            }
        }
    }
}
