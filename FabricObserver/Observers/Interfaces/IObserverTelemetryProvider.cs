// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Interfaces
{
    /// <summary>
    /// IObserverTelemetry interface.
    /// </summary>
    public interface IObserverTelemetryProvider
    {
        /// <summary>
        /// Gets or sets the telemetry API key.
        /// </summary>
        string Key { get; set; }

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
        /// <returns>a completed task...</returns>
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
        /// <returns>a Task...</returns>
        Task ReportHealthAsync(
            string applicationName,
            string serviceName,
            string instance,
            string source,
            string propertyName,
            HealthState state,
            CancellationToken cancellationToken);

        /// <summary>
        /// Calls telemetry provider to report a metric.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value of the property.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task of bool...</returns>
        Task<bool> ReportMetricAsync<T>(string name, T value, CancellationToken cancellationToken);

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
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="service">Name of the service.</param>
        /// <param name="partition">Partition id.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task.</returns>
        Task ReportMetricAsync(
            string service,
            Guid partition,
            string name,
            long value,
            CancellationToken cancellationToken);

        /// <summary>
        /// Calls AI to report a metric.
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
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="roleName">Name of the role.</param>
        /// <param name="instance">Instance idenfitier.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
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
    }
}