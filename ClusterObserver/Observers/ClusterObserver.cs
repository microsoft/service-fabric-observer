// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Utilities.Telemetry;
using FabricObserver.Utilities;

namespace FabricObserver
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
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example...
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
            if (this.FabricClientInstance == null)
            {
                throw new ArgumentException("fabricClient cannot be null...");
            }

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

                string unhealthEvaluationsDescription = string.Empty;

                if (clusterHealth.AggregatedHealthState == HealthState.Error
                    || (emitWarningDetails && clusterHealth.AggregatedHealthState == HealthState.Warning))
                {
                    var unhealthEvaluations = clusterHealth.UnhealthyEvaluations;

                    foreach (var evaluation in unhealthEvaluations)
                    {
                        token.ThrowIfCancellationRequested();

                        unhealthEvaluationsDescription += $"{evaluation.AggregatedHealthState}: {evaluation.Description}\n";
                    }
                }

                // Telemetry...
                if (this.IsTelemetryEnabled)
                {
                    await this.ObserverTelemetryClient?.ReportHealthAsync(
                        HealthScope.Cluster,
                        "AggregatedClusterHealth",
                        clusterHealth.AggregatedHealthState,
                        unhealthEvaluationsDescription,
                        this.ObserverName,
                        this.Token);
                }
            }
            catch (ArgumentException ae) { this.ObserverLogger.LogError("Handled Exception in IsClusterHealthyAsync(): \n {0}", ae.ToString()); }
            catch (FabricException fe) { this.ObserverLogger.LogError("Handled Exception in IsClusterHealthyAsync(): \n {0}", fe.ToString()); }
            catch (TimeoutException te) { this.ObserverLogger.LogError("Handled Exception in IsClusterHealthyAsync(): \n {0}", te.ToString()); }
            catch (Exception e)
            {
                this.ObserverLogger.LogError("Unhandled Exception in IsClusterHealthyAsync(): \n {0}", e.ToString());

                throw;
            }
        }
    }
}
