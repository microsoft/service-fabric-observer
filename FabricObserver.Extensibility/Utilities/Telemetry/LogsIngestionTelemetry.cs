// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    /// <summary>
    /// Sends telemetry through the Azure Monitor Logs Ingestion API using a user-assigned managed identity.
    /// </summary>
    /// <remarks>
    /// Work in progress: this type currently carries the validated settings only. Uploads are implemented in a later change,
    /// so every report method is a no-op until then.
    /// </remarks>
    internal sealed class LogsIngestionTelemetry : ITelemetryProvider
    {
        public LogsIngestionTelemetry(Uri ingestionEndpoint, string dcrImmutableId, string streamName, string managedIdentityClientId)
        {
            IngestionEndpoint = ingestionEndpoint ?? throw new ArgumentNullException(nameof(ingestionEndpoint));
            DcrImmutableId = !string.IsNullOrWhiteSpace(dcrImmutableId) ? dcrImmutableId : throw new ArgumentException("Value is required.", nameof(dcrImmutableId));
            StreamName = !string.IsNullOrWhiteSpace(streamName) ? streamName : throw new ArgumentException("Value is required.", nameof(streamName));
            ManagedIdentityClientId = !string.IsNullOrWhiteSpace(managedIdentityClientId)
                ? managedIdentityClientId
                : throw new ArgumentException("Value is required.", nameof(managedIdentityClientId));
        }

        public Uri IngestionEndpoint { get; }

        public string DcrImmutableId { get; }

        public string StreamName { get; }

        public string ManagedIdentityClientId { get; }

        /// <summary>
        /// Not used. The Logs Ingestion API authenticates with a managed identity, not a key.
        /// </summary>
        public string Key { get; set; }

        public Task ReportAvailabilityAsync(
            Uri serviceUri,
            string instance,
            string testName,
            DateTimeOffset captured,
            TimeSpan duration,
            string location,
            bool success,
            CancellationToken cancellationToken,
            string message = null) => Task.CompletedTask;

        public Task ReportHealthAsync(
            string propertyName,
            HealthState state,
            string unhealthyEvaluations,
            string source,
            CancellationToken cancellationToken,
            string serviceName = null,
            string instanceName = null) => Task.CompletedTask;

        public Task ReportHealthAsync(TelemetryDataBase telemetryData, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ReportMetricAsync<T>(string name, T value, string source, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task ReportMetricAsync(TelemetryDataBase telemetryData, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportMetricAsync(List<ChildProcessTelemetryData> telemetryData, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportMetricAsync(MachineTelemetryData telemetryData, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportMetricAsync(string name, long value, IDictionary<string, string> properties, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportMetricAsync(string service, Guid partition, string name, long value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportMetricAsync(string role, long id, string name, long value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportMetricAsync(
            string roleName,
            string instance,
            string name,
            long value,
            int count,
            long min,
            long max,
            long sum,
            double deviation,
            IDictionary<string, string> properties,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportClusterUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token) => Task.CompletedTask;

        public Task ReportApplicationUpgradeStatusAsync(ServiceFabricUpgradeEventData appUpgradeInfo, CancellationToken token) => Task.CompletedTask;

        public Task ReportNodeSnapshotAsync(NodeSnapshotTelemetryData nodeSnapshotTelem, CancellationToken token) => Task.CompletedTask;
    }
}
