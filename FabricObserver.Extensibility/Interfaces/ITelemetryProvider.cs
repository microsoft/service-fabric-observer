// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers.Interfaces
{
    /// <summary>
    /// ITelemetry interface.
    /// </summary>
    public interface ITelemetryProvider
    {
        /// <summary>
        /// Calls telemetry provider to track the availability.
        /// </summary>
        /// <param name="serviceUri">Service name.</param>
        /// <param name="instance">Instance identifier.</param>
        /// <param name="testName">Availability test name.</param>
        /// <param name="captured">The time when the availability was captured.</param>
        /// <param name="duration">The time taken for the availability test to run.</param>
        /// <param name="location">Name of the location the availability test was run from.</param>
        /// <param name="success">True if the availability test ran successfully.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <param name="message">Error message on availability test run failure.</param>
        /// <returns>a completed task.</returns>
        Task ReportAvailabilityAsync(
            Uri serviceUri,
            string instance,
            string testName,
            DateTimeOffset captured,
            TimeSpan duration,
            string location,
            bool success,
            CancellationToken cancellationToken,
            string message = null);

        /// <summary>
        /// Calls telemetry provider to report health.
        /// </summary>
        /// <param name="scope">Scope of health evaluation (Cluster, Node, etc.).</param>
        /// <param name="propertyName">Value of the property.</param>
        /// <param name="state">Health state.</param>
        /// <param name="unhealthyEvaluations">Unhealthy evaluations aggregated description.</param>
        /// <param name="source">Source of emission.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <param name="serviceName">Optional: TraceTelemetry context cloud service name.</param>
        /// <param name="instanceName">Optional: TraceTelemetry context cloud instance name.</param>
        /// <returns>a Task.</returns>
        Task ReportHealthAsync(
            string propertyName,
            HealthState state,
            string unhealthyEvaluations,
            string source,
            CancellationToken cancellationToken,
            string serviceName = null,
            string instanceName = null);

        /// <summary>
        /// Calls telemetry provider to report Health.
        /// </summary>
        /// <param name="telemetryData">MachineTelemetryData instance.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        Task ReportHealthAsync(
            TelemetryData telemetryData,
            CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value of the property.</param>
        /// <param name="source">Name of the observer omitting the signal.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task of bool.</returns>
        Task<bool> ReportMetricAsync<T>(
            string name,
            T value,
            string source,
            CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="telemetryData">TelemetryData instance.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        Task ReportMetricAsync(
          TelemetryData telemetryData,
          CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="telemetryData">MachineTelemetryData instance.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        Task ReportMetricAsync(
          MachineTelemetryData telemetryData,
          CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="telemetryData">List of ChildProcessTelemetry.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        Task ReportMetricAsync(
          List<ChildProcessTelemetryData> telemetryData,
          CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value of the property.</param>
        /// <param name="properties">IDictionary&lt;string&gt;,&lt;string&gt; containing name/value pairs of additional properties.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task.</returns>
        Task ReportMetricAsync(
            string name,
            long value,
            IDictionary<string, string> properties,
            CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="service">Name of the service.</param>
        /// <param name="partition">Partition id.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value of the metric.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task.</returns>
        Task ReportMetricAsync(
            string service,
            Guid partition,
            string name,
            long value,
            CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="role">Name of the role.</param>
        /// <param name="id">Replica or instance identifier.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task.</returns>
        Task ReportMetricAsync(
            string role,
            long id,
            string name,
            long value,
            CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="roleName">Name of the role.</param>
        /// <param name="instance">Instance idenfitier.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value of the metric.</param>
        /// <param name="count">Number of samples for this metric.</param>
        /// <param name="min">Minimum value of the samples.</param>
        /// <param name="max">Maximum value of the samples.</param>
        /// <param name="sum">Sum of all of the samples.</param>
        /// <param name="deviation">Standard deviation of the sample set.</param>
        /// <param name="properties">IDictionary&lt;string&gt;,&lt;string&gt; containing name/value pairs of additional properties.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task.</returns>
        Task ReportMetricAsync(
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
            CancellationToken cancellationToken);

        Task ReportClusterUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token);

        Task ReportApplicationUpgradeStatusAsync(ServiceFabricUpgradeEventData appUpgradeInfo, CancellationToken token);
    }
}