// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using FabricObserver.TelemetryLib;
using Newtonsoft.Json;
using System.Fabric;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    /// <summary>
    /// Abstracts the ApplicationInsights telemetry API calls allowing
    /// other telemetry providers to be plugged in.
    /// </summary>
    public class AppInsightsTelemetry : ITelemetryProvider
    {
        /// <summary>
        /// ApplicationInsights telemetry client.
        /// </summary>
        private readonly TelemetryClient telemetryClient;
        private readonly Logger logger;

        public AppInsightsTelemetry(string connString)
        {
            if (string.IsNullOrWhiteSpace(connString))
            {
                throw new ArgumentException("Argument is empty", nameof(connString));
            }

            logger = new Logger("TelemetryLog");
            TelemetryConfiguration configuration = TelemetryConfiguration.CreateDefault();
            configuration.ConnectionString = connString;
            telemetryClient = new TelemetryClient(configuration);
#if DEBUG
            // Expedites the flow of data through the pipeline.
            configuration.TelemetryChannel.DeveloperMode = true;
#endif
        }

        /// <summary>
        /// Gets a value indicating whether telemetry is enabled or not.
        /// </summary>
        private bool IsEnabled => telemetryClient.IsEnabled();

        /// <summary>
        /// Calls AI to track the availability.
        /// </summary>
        /// <param name="serviceName">Service name.</param>
        /// <param name="instance">Instance identifier.</param>
        /// <param name="testName">Availability test name.</param>
        /// <param name="captured">The time when the availability was captured.</param>
        /// <param name="duration">The time taken for the availability test to run.</param>
        /// <param name="location">Name of the location the availability test was run from.</param>
        /// <param name="success">True if the availability test ran successfully.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <param name="message">Error message on availability test run failure.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ReportAvailabilityAsync(
                        Uri serviceName,
                        string instance,
                        string testName,
                        DateTimeOffset captured,
                        TimeSpan duration,
                        string location,
                        bool success,
                        CancellationToken cancellationToken,
                        string message = null)
        {
            if (!IsEnabled || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(1);
            }

            var at = new AvailabilityTelemetry(testName, captured, duration, location, success, message);

            at.Properties.Add("Service", serviceName?.OriginalString);
            at.Properties.Add("Instance", instance);

            telemetryClient.TrackAvailability(at);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Calls AI to report health.
        /// </summary>
        /// <param name="scope">Scope of health evaluation (Cluster, Node, etc.).</param>
        /// <param name="propertyName">Value of the property.</param>
        /// <param name="state">Health state.</param>
        /// <param name="unhealthyEvaluations">Unhealthy evaluations aggregated description.</param>
        /// <param name="source">Source of emission.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <param name="serviceName">Optional: TraceTelemetry context cloud service name.</param>
        /// <param name="instanceName">Optional: TraceTelemetry context cloud instance name.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ReportHealthAsync(
                        string propertyName,
                        HealthState state,
                        string unhealthyEvaluations,
                        string source,
                        CancellationToken cancellationToken,
                        string serviceName = null,
                        string instanceName = null)
        {
            if (!IsEnabled || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(1);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sev = (state == HealthState.Error) ? SeverityLevel.Error
                                    : (state == HealthState.Warning) ? SeverityLevel.Warning : SeverityLevel.Information;

                string healthInfo = string.Empty;

                if (!string.IsNullOrEmpty(unhealthyEvaluations))
                {
                    healthInfo += $"{Environment.NewLine}{unhealthyEvaluations}";
                }

                var tt = new TraceTelemetry(
                    $"{Enum.GetName(typeof(HealthState), state)} from {source}:{Environment.NewLine}" +
                    $"{propertyName}{Environment.NewLine}" +
                    $"{healthInfo}", sev);

                tt.Context.Cloud.RoleName = serviceName;
                tt.Context.Cloud.RoleInstance = instanceName;

                telemetryClient.TrackTrace(tt);
            }
            catch (Exception e)
            {
                logger.LogWarning($"Unhandled exception in TelemetryClient.ReportHealthAsync:{Environment.NewLine}{e}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Calls telemetry provider to report health.
        /// </summary>
        /// <param name="telemetryData">TelemetryData instance.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>a Task.</returns>
        public Task ReportHealthAsync(TelemetryData telemetryData, CancellationToken cancellationToken)
        {
            if (!IsEnabled || cancellationToken.IsCancellationRequested || telemetryData == null)
            {
                return Task.CompletedTask;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var properties = new Dictionary<string, string>
                {
                    { "ClusterId", telemetryData.ClusterId ?? ClusterInformation.ClusterInfoTuple.ClusterId },
                    { "HealthState", Enum.GetName(typeof(HealthState), telemetryData.HealthState) },
                    { "ApplicationName", telemetryData.ApplicationName ?? string.Empty },
                    { "ApplicationTypeName", telemetryData.ApplicationType ?? string.Empty },
                    { "ServiceName", telemetryData.ServiceName ?? string.Empty },
                    { "ReplicaRole", telemetryData.ReplicaRole ?? string.Empty },
                    { "ServiceKind", telemetryData.ServiceKind ?? string.Empty },
                    { "ServicePackageActivationMode", telemetryData.ServicePackageActivationMode ?? string.Empty },
                    { "ProcessId", telemetryData.ProcessId == 0 ? string.Empty : telemetryData.ProcessId.ToString() },
                    { "ProcessName", telemetryData.ProcessName },
                    { "ProcessStartTime", telemetryData.ProcessStartTime },
                    { "ErrorCode", telemetryData.Code ?? string.Empty },
                    { "Description", telemetryData.Description ?? string.Empty },
                    { "Metric", telemetryData.Metric ?? string.Empty },
                    { "Value", telemetryData.Value.ToString() },
                    { "PartitionId", telemetryData.PartitionId != null ? telemetryData.PartitionId.ToString() : string.Empty },
                    { "ReplicaId", telemetryData.ReplicaId.ToString() },
                    { "RGEnabled", telemetryData.RGMemoryEnabled.ToString() },
                    { "RGMemoryLimitMb", telemetryData.RGAppliedMemoryLimitMb.ToString() },
                    { "ObserverName", telemetryData.ObserverName },
                    { "NodeName", telemetryData.NodeName ?? string.Empty },
                    { "OS", telemetryData.OS ?? string.Empty }
                };

                telemetryClient.TrackEvent(ObserverConstants.FabricObserverETWEventName, properties);
            }
            catch (Exception e)
            {
                logger.LogWarning($"Unhandled exception in TelemetryClient.ReportHealthAsync:{Environment.NewLine}{e}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends a metric to AppInsights telemetry service with supplied value of type T.
        /// </summary>
        /// <param name="metric">name of metric.</param>
        /// <param name="value">value of metric.</param>
        /// <param name="source">source of event.</param>
        /// <param name="cancellationToken">cancellation token.</param>
        /// <returns>A Task of bool.</returns>
        public Task<bool> ReportMetricAsync<T>(
                            string metric,
                            T value,
                            string source,
                            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrEmpty(metric) || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            telemetryClient?.TrackEvent(
                                string.IsNullOrEmpty(source) ? ObserverConstants.FabricObserverETWEventName : source,
                                new Dictionary<string, string> { { metric, value?.ToString() } });

            return Task.FromResult(true);
        }

        /// <summary>
        /// Reports a metric to a telemetry service.
        /// </summary>
        /// <param name="telemetryData">TelemetryData instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public Task ReportMetricAsync(TelemetryData telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null)
            {
                return Task.CompletedTask;
            }

            try
            {
                var properties = new Dictionary<string, string>
                {
                    { "ClusterId", telemetryData.ClusterId ?? ClusterInformation.ClusterInfoTuple.ClusterId },
                    { "HealthState", Enum.GetName(typeof(HealthState), telemetryData.HealthState) },
                    { "ApplicationName", telemetryData.ApplicationName ?? string.Empty },
                    { "ApplicationTypeName", telemetryData.ApplicationType ?? string.Empty },
                    { "ServiceName", telemetryData.ServiceName ?? string.Empty },
                    { "ReplicaRole", telemetryData.ReplicaRole ?? string.Empty },
                    { "ServiceKind", telemetryData.ServiceKind ?? string.Empty },
                    { "ServicePackageActivationMode", telemetryData.ServicePackageActivationMode?.ToString() ?? string.Empty },
                    { "ProcessId", telemetryData.ProcessId == 0 ? string.Empty : telemetryData.ProcessId.ToString() },
                    { "ProcessName", telemetryData.ProcessName },
                    { "ProcessStartTime", telemetryData.ProcessStartTime },
                    { "ErrorCode", telemetryData.Code ?? string.Empty },
                    { "Description", telemetryData.Description ?? string.Empty },
                    { "Metric", telemetryData.Metric ?? string.Empty },
                    { "Value", telemetryData.Value.ToString() },
                    { "PartitionId", telemetryData.PartitionId != null ? telemetryData.PartitionId.ToString() : string.Empty },
                    { "ReplicaId", telemetryData.ReplicaId.ToString() },
                    { "RGEnabled", telemetryData.RGMemoryEnabled.ToString() },
                    { "RGMemoryLimitMb", telemetryData.RGAppliedMemoryLimitMb.ToString() },
                    { "ObserverName", telemetryData.ObserverName },
                    { "NodeName", telemetryData.NodeName ?? string.Empty },
                    { "OS", telemetryData.OS ?? string.Empty },
                };

                var metric = new Dictionary<string, double>
                {
                    { telemetryData.Metric, telemetryData.Value }
                };

                telemetryClient.TrackEvent(ObserverConstants.FabricObserverETWEventName, properties, metric);
            }
            catch (Exception e)
            {
                logger.LogWarning($"Unhandled exception in TelemetryClient.ReportMetricAsync:{Environment.NewLine}{e}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Reports metric for a collection (List) of ChildProcessTelemetryData instances.
        /// </summary>
        /// <param name="telemetryDataList">List of ChildProcessTelemetryData.</param>
        /// <param name="cancellationToken">Cancellation Token</param>
        /// <returns></returns>
        public Task ReportMetricAsync(List<ChildProcessTelemetryData> telemetryDataList, CancellationToken cancellationToken)
        {
            if (telemetryDataList == null || cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            string OS = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";

            foreach (var telemData in telemetryDataList)
            {
                try
                {
                    var properties = new Dictionary<string, string>
                    {
                        { "ClusterId", ClusterInformation.ClusterInfoTuple.ClusterId },
                        { "ApplicationName", telemData.ApplicationName ?? string.Empty },
                        { "ServiceName", telemData.ServiceName ?? string.Empty },
                        { "ProcessId", telemData.ProcessId.ToString() },
                        { "ProcessName", telemData.ProcessName },
                        { "ChildProcessInfo", JsonConvert.SerializeObject(telemData.ChildProcessInfo) },
                        { "PartitionId", telemData.PartitionId },
                        { "ReplicaId", telemData.ReplicaId.ToString() },
                        { "Source", ObserverConstants.AppObserverName },
                        { "NodeName", telemData.NodeName },
                        { "OS", OS }
                    };

                    var metrics = new Dictionary<string, double>
                    {
                        { "ChildProcessCount", telemData.ChildProcessCount },
                        { $"{telemData.Metric} (Parent + Descendants)", telemData.Value }
                    };

                    telemetryClient.TrackEvent(ObserverConstants.FabricObserverETWEventName, properties, metrics);
                }
                catch (Exception e)
                {
                    logger.LogWarning($"Unhandled exception in TelemetryClient.ReportMetricAsync:{Environment.NewLine}{e}");
                }
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reports a metric to a telemetry service.
        /// </summary>
        /// <param name="machineTelemetryData">MachineTelemetryData instance.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public Task ReportMetricAsync(MachineTelemetryData machineTelemetryData, CancellationToken cancellationToken)
        {
            if (machineTelemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            try
            {
                string virtMem = "AvailableVirtualMemoryGB";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    virtMem = "SwapFreeGB";
                }

                var properties = new Dictionary<string, string>
                {
                    { "ActiveEphemeralTcpPorts", machineTelemetryData.ActiveEphemeralTcpPorts.ToString() },
                    { "ActiveFirewallRules", machineTelemetryData.ActiveFirewallRules.ToString() },
                    { "ActiveTcpPorts", machineTelemetryData.ActiveTcpPorts.ToString() },
                    { "DriveInfo", machineTelemetryData.DriveInfo },
                    { "FabricApplicationTcpPortRange", machineTelemetryData.FabricApplicationTcpPortRange },
                    { "AvailablePhysicalMemoryGB", machineTelemetryData.AvailablePhysicalMemoryGB.ToString(CultureInfo.InvariantCulture) },
                    { $"{virtMem}", machineTelemetryData.FreeVirtualMemoryGB.ToString(CultureInfo.InvariantCulture) },
                    { "HotFixes", machineTelemetryData.HotFixes ?? string.Empty },
                    { "LastBootUpTime", machineTelemetryData.LastBootUpTime },
                    { "Level", machineTelemetryData.HealthState },
                    { "LogicalDriveCount", machineTelemetryData.LogicalDriveCount.ToString() },
                    { "LogicalProcessorCount", machineTelemetryData.LogicalProcessorCount.ToString() },
                    { "NodeName", machineTelemetryData.NodeName },
                    { "NumberOfRunningProcesses", machineTelemetryData.NumberOfRunningProcesses.ToString() },
                    { "ObserverName", machineTelemetryData.ObserverName },
                    { "OSName", machineTelemetryData.OSName },
                    { "OSInstallDate", machineTelemetryData.OSInstallDate },
                    { "OSVersion", machineTelemetryData.OSVersion },
                    { "TotalMemorySizeGB", machineTelemetryData.TotalMemorySizeGB.ToString() },
                    { "EphemeralTcpPortRange", machineTelemetryData.EphemeralTcpPortRange },
                    { "WindowsUpdateAutoDownloadEnabled", machineTelemetryData.WindowsUpdateAutoDownloadEnabled.ToString() }
                };

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    _ = properties.Remove("ActiveFirewallRules");
                    _ = properties.Remove("WindowsUpdateAutoDownloadEnabled");
                    _ = properties.Remove("HotFixes");
                }

                telemetryClient.TrackEvent(ObserverConstants.FabricObserverETWEventName, properties);
            }
            catch (Exception e)
            {
                logger.LogWarning($"Unhandled exception in TelemetryClient.ReportMetricAsync:{Environment.NewLine}{e}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value of the property.</param>
        /// <param name="properties">IDictionary&lt;string&gt;,&lt;string&gt; containing name/value pairs of additional properties.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ReportMetricAsync(
                        string name,
                        long value,
                        IDictionary<string, string> properties,
                        CancellationToken cancellationToken)
        {
            if (!IsEnabled || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(1);
            }

            _ = telemetryClient.GetMetric(name).TrackValue(value, string.Join(";", properties));

            return Task.CompletedTask;
        }

        public Task ReportClusterUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token)
        {
            if (!IsEnabled || eventData?.FabricUpgradeProgress == null || token.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            try
            {
                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", "ClusterUpgradeEvent" },
                    { "TaskName", eventData.TaskName },
                    { "ClusterId", eventData.ClusterId ?? ClusterInformation.ClusterInfoTuple.ClusterId },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", eventData.OS },
                    { "UpgradeTargetCodeVersion", eventData.FabricUpgradeProgress.UpgradeDescription?.TargetCodeVersion },
                    { "UpgradeTargetConfigVersion", eventData.FabricUpgradeProgress.UpgradeDescription?.TargetConfigVersion },
                    { "UpgradeState", Enum.GetName(typeof(FabricUpgradeState), eventData.FabricUpgradeProgress.UpgradeState) },
                    { "CurrentUpgradeDomain", eventData.FabricUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName },
                    { "NextUpgradeDomain", eventData.FabricUpgradeProgress?.NextUpgradeDomain },
                    { "UpgradeDuration", eventData.FabricUpgradeProgress?.CurrentUpgradeDomainDuration.ToString() },
                    { "FailureReason", eventData.FabricUpgradeProgress.FailureReason.HasValue ? Enum.GetName(typeof(UpgradeFailureReason), eventData.FabricUpgradeProgress.FailureReason.Value) : null }
                };

                telemetryClient.TrackEvent($"{eventData.TaskName}.ClusterUpgradeEvent", eventProperties);
                telemetryClient.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                eventProperties.Clear();
                eventProperties = null;

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                logger.LogWarning($"Failure in ReportClusterUpgradeStatus:{Environment.NewLine}{e}");
            }

            return Task.CompletedTask;
        }

        public Task ReportApplicationUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token)
        {
            if (!IsEnabled || eventData?.ApplicationUpgradeProgress == null || token.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            try
            {
                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", "ApplicationUpgradeEvent" },
                    { "TaskName", eventData.TaskName },
                    { "ClusterId", eventData.ClusterId ?? ClusterInformation.ClusterInfoTuple.ClusterId },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", eventData.OS },
                    { "ApplicationName", eventData.ApplicationUpgradeProgress.ApplicationName?.OriginalString },
                    { "UpgradeTargetTypeVersion", eventData.ApplicationUpgradeProgress.UpgradeDescription?.TargetApplicationTypeVersion },
                    { "UpgradeState", Enum.GetName(typeof(ApplicationUpgradeState), eventData.ApplicationUpgradeProgress.UpgradeState) },
                    { "CurrentUpgradeDomain", eventData.ApplicationUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName },
                    { "NextUpgradeDomain", eventData.ApplicationUpgradeProgress?.NextUpgradeDomain },
                    { "UpgradeDuration", eventData.ApplicationUpgradeProgress.CurrentUpgradeDomainDuration.ToString() },
                    { "FailureReason", eventData.ApplicationUpgradeProgress.FailureReason.HasValue ? Enum.GetName(typeof(UpgradeFailureReason), eventData.ApplicationUpgradeProgress.FailureReason.Value) : null }
                };

                telemetryClient.TrackEvent($"{eventData.TaskName}.ApplicationUpgradeEvent", eventProperties);
                telemetryClient.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                eventProperties.Clear();
                eventProperties = null;

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                logger.LogWarning($"Failure in ReportApplicationUpgradeStatus:{Environment.NewLine}{e}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="role">Name of the service.</param>
        /// <param name="partition">Guid of the partition.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task ReportMetricAsync(
                        string role,
                        Guid partition,
                        string name,
                        long value,
                        CancellationToken cancellationToken)
        {
            return ReportMetricAsync(role, partition.ToString(), name, value, 1, value, value, value, 0.0, null, cancellationToken);
        }

        /// <summary>
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="role">Name of the service.</param>
        /// <param name="id">Replica or Instance identifier.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task ReportMetricAsync(
                            string role,
                            long id,
                            string name,
                            long value,
                            CancellationToken cancellationToken)
        {
            await ReportMetricAsync(role, id.ToString(), name, value, 1, value, value, value, 0.0, null, cancellationToken);
        }

        /// <summary>
        /// Calls AI to report a metric.
        /// </summary>
        /// <param name="roleName">Name of the role. Usually the service name.</param>
        /// <param name="instance">Instance identifier.</param>
        /// <param name="name">Name of the metric.</param>
        /// <param name="value">Value if the metric.</param>
        /// <param name="count">Number of samples for this metric.</param>
        /// <param name="min">Minimum value of the samples.</param>
        /// <param name="max">Maximum value of the samples.</param>
        /// <param name="sum">Sum of all of the samples.</param>
        /// <param name="deviation">Standard deviation of the sample set.</param>
        /// <param name="properties">IDictionary&lt;string&gt;,&lt;string&gt; containing name/value pairs of additional properties.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
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
                        CancellationToken cancellationToken)
        {
            if (!IsEnabled || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            var mt = new MetricTelemetry(name, value)
            {
                Count = count,
                Min = min,
                Max = max,
                StandardDeviation = deviation
            };

            mt.Context.Cloud.RoleName = roleName;
            mt.Context.Cloud.RoleInstance = instance;

            // Set the properties.
            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    mt.Properties.Add(prop);
                }
            }

            // Track the telemetry.
            telemetryClient.TrackMetric(mt);
            return Task.CompletedTask;
        }
    }
}