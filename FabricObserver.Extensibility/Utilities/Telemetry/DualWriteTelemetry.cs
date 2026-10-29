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
    /// Forwards every record to both the Data Collector API sender and the Logs Ingestion API sender
    /// (LogAnalyticsWriteMode=DualWrite). Remove this type when the Data Collector API is retired.
    /// </summary>
    internal sealed class DualWriteTelemetry(ITelemetryProvider legacySender, ITelemetryProvider logsIngestionSender) : ITelemetryProvider
    {
        public ITelemetryProvider LegacySender { get; } = legacySender ?? throw new ArgumentNullException(nameof(legacySender));

        public ITelemetryProvider LogsIngestionSender { get; } = logsIngestionSender ?? throw new ArgumentNullException(nameof(logsIngestionSender));

        public string Key
        {
            get => throw new NotSupportedException("Key is not supported for dual write. Each API sender owns its own credentials.");
            set => throw new NotSupportedException("Key is not supported for dual write. Each API sender owns its own credentials.");
        }

        public Task ReportAvailabilityAsync(
            Uri serviceUri,
            string instance,
            string testName,
            DateTimeOffset captured,
            TimeSpan duration,
            string location,
            bool success,
            CancellationToken cancellationToken,
            string message = null) =>
            ForwardAsync(sender => sender.ReportAvailabilityAsync(serviceUri, instance, testName, captured, duration, location, success, cancellationToken, message));

        public Task ReportHealthAsync(
            string propertyName,
            HealthState state,
            string unhealthyEvaluations,
            string source,
            CancellationToken cancellationToken,
            string serviceName = null,
            string instanceName = null) =>
            ForwardAsync(sender => sender.ReportHealthAsync(propertyName, state, unhealthyEvaluations, source, cancellationToken, serviceName, instanceName));

        public Task ReportHealthAsync(TelemetryDataBase telemetryData, CancellationToken cancellationToken) =>
            ForwardAsync(sender => sender.ReportHealthAsync(telemetryData, cancellationToken));

        public async Task<bool> ReportMetricAsync<T>(string name, T value, string source, CancellationToken cancellationToken)
        {
            bool[] results = await Task.WhenAll(
                LegacySender.ReportMetricAsync(name, value, source, cancellationToken),
                LogsIngestionSender.ReportMetricAsync(name, value, source, cancellationToken));

            return results[0] && results[1];
        }

        public Task ReportMetricAsync(TelemetryDataBase telemetryData, CancellationToken cancellationToken) =>
            ForwardAsync(sender => sender.ReportMetricAsync(telemetryData, cancellationToken));

        public Task ReportMetricAsync(List<ChildProcessTelemetryData> telemetryData, CancellationToken cancellationToken) =>
            ForwardAsync(sender => sender.ReportMetricAsync(telemetryData, cancellationToken));

        public Task ReportMetricAsync(MachineTelemetryData telemetryData, CancellationToken cancellationToken) =>
            ForwardAsync(sender => sender.ReportMetricAsync(telemetryData, cancellationToken));

        public Task ReportMetricAsync(string name, long value, IDictionary<string, string> properties, CancellationToken cancellationToken) =>
            ForwardAsync(sender => sender.ReportMetricAsync(name, value, properties, cancellationToken));

        public Task ReportMetricAsync(string service, Guid partition, string name, long value, CancellationToken cancellationToken) =>
            ForwardAsync(sender => sender.ReportMetricAsync(service, partition, name, value, cancellationToken));

        public Task ReportMetricAsync(string role, long id, string name, long value, CancellationToken cancellationToken) =>
            ForwardAsync(sender => sender.ReportMetricAsync(role, id, name, value, cancellationToken));

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
            CancellationToken cancellationToken) =>
            ForwardAsync(sender => sender.ReportMetricAsync(roleName, instance, name, value, count, min, max, sum, deviation, properties, cancellationToken));

        public Task ReportClusterUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token) =>
            ForwardAsync(sender => sender.ReportClusterUpgradeStatusAsync(eventData, token));

        public Task ReportApplicationUpgradeStatusAsync(ServiceFabricUpgradeEventData appUpgradeInfo, CancellationToken token) =>
            ForwardAsync(sender => sender.ReportApplicationUpgradeStatusAsync(appUpgradeInfo, token));

        public Task ReportNodeSnapshotAsync(NodeSnapshotTelemetryData nodeSnapshotTelem, CancellationToken token) =>
            ForwardAsync(sender => sender.ReportNodeSnapshotAsync(nodeSnapshotTelem, token));

        private Task ForwardAsync(Func<ITelemetryProvider, Task> send) => Task.WhenAll(send(LegacySender), send(LogsIngestionSender));
    }
}
