// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClusterObserver.Interfaces;
using Newtonsoft.Json;
using FabricObserver.TelemetryLib;

namespace ClusterObserver.Utilities.Telemetry
{
    // LogAnalyticsTelemetry class is partially based on public (non-license-protected) sample https://dejanstojanovic.net/aspnet/2018/february/send-data-to-azure-log-analytics-from-c-code/
    public class LogAnalyticsTelemetry : ITelemetryProvider
    {
        private const int MaxRetries = 5;
        private readonly FabricClient fabricClient;
        private readonly CancellationToken token;
        private readonly Logger logger;
        private int retries;

        public string WorkspaceId
        {
            get; set;
        }

        public string Key
        {
            get; set;
        }

        public string ApiVersion
        {
            get; set;
        }

        public string LogType
        {
            get; set;
        }

        public LogAnalyticsTelemetry(
                string workspaceId,
                string sharedKey,
                string logType,
                FabricClient fabricClient,
                CancellationToken token,
                string apiVersion = "2016-04-01")
        {
            WorkspaceId = workspaceId;
            Key = sharedKey;
            LogType = logType;
            this.fabricClient = fabricClient;
            this.token = token;
            ApiVersion = apiVersion;
            logger = new Logger("TelemetryLogger");
        }

        /// <summary>
        /// Sends telemetry data to Azure LogAnalytics via REST.
        /// </summary>
        /// <param name="payload">Json string containing telemetry data.</param>
        /// <returns>A completed task or task containing exception info.</returns>
        private async Task SendTelemetryAsync(string payload, CancellationToken token)
        {
            if (string.IsNullOrEmpty(WorkspaceId))
            {
                return;
            }

            var requestUri = new Uri($"https://{WorkspaceId}.ods.opinsights.azure.com/api/logs?api-version={ApiVersion}");
            string date = DateTime.UtcNow.ToString("r");
            string signature = GetSignature("POST", payload.Length, "application/json", date, "/api/logs");

            var request = (HttpWebRequest)WebRequest.Create(requestUri);
            request.ContentType = "application/json";
            request.Method = "POST";
            request.Headers["Log-Type"] = LogType;
            request.Headers["x-ms-date"] = date;
            request.Headers["Authorization"] = signature;
            byte[] content = Encoding.UTF8.GetBytes(payload);

            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                using (var requestStreamAsync = await request.GetRequestStreamAsync())
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    await requestStreamAsync.WriteAsync(content, 0, content.Length);
                }

                using var responseAsync = await request.GetResponseAsync() as HttpWebResponse;

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (responseAsync.StatusCode == HttpStatusCode.OK ||
                    responseAsync.StatusCode == HttpStatusCode.Accepted)
                {
                    retries = 0;
                    return;
                }

                logger.LogWarning($"Unexpected response from server in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}{responseAsync.StatusCode}: {responseAsync.StatusDescription}");
            }
            catch (Exception e)
            {
                // An Exception during telemetry data submission should never take down CO process. Log it. Don't throw it. Fix it.
                logger.LogWarning($"Handled Exception in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}{e}");
            }

            if (retries < MaxRetries)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                retries++;
                await Task.Delay(1000).ConfigureAwait(true);
                await SendTelemetryAsync(payload, token).ConfigureAwait(true);
            }
            else
            {
                // Exhausted retries. Reset counter.
                logger.LogWarning($"Exhausted request retries in LogAnalyticsTelemetry.SendTelemetryAsync: {MaxRetries}. See logs for error details.");
                retries = 0;
            }
        }

        private string GetSignature(
                        string method,
                        int contentLength,
                        string contentType,
                        string date,
                        string resource)
        {
            string message = $"{method}\n{contentLength}\n{contentType}\nx-ms-date:{date}\n{resource}";
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            using var encryptor = new HMACSHA256(Convert.FromBase64String(Key));
            return $"SharedKey {WorkspaceId}:{Convert.ToBase64String(encryptor.ComputeHash(bytes))}";
        }

        // These two overloads of ReportHealthAsync are the only function impls that really makes sense for ClusterObserver 
        // with respect to ITelemetryProvider as CO does not monitor resources and generate data. 
        // It just reports AggregatedClusterHealth and related details surfaced by other Fabric services
        // running in the cluster.
        public async Task ReportHealthAsync(
                            HealthScope scope,
                            string propertyName,
                            HealthState state,
                            string unhealthyEvaluations,
                            string source,
                            CancellationToken cancellationToken,
                            string serviceName = null,
                            string instanceName = null)
        {
            string clusterId = ClusterInformation.ClusterInfoTuple.ClusterId;
            string jsonPayload = JsonConvert.SerializeObject(
                                                new
                                                {
                                                    id = $"CO_{Guid.NewGuid()}",
                                                    datetime = DateTime.UtcNow,
                                                    clusterId = clusterId ?? string.Empty,
                                                    source = ObserverConstants.ClusterObserverName,
                                                    property = propertyName,
                                                    healthScope = scope.ToString(),
                                                    healthState = state.ToString(),
                                                    healthEvaluation = unhealthyEvaluations,
                                                    serviceName = serviceName ?? string.Empty,
                                                    instanceName = instanceName ?? string.Empty,
                                                    osPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux"
                                                });

            await SendTelemetryAsync(jsonPayload, cancellationToken).ConfigureAwait(true);
        }

        public async Task ReportHealthAsync(TelemetryData telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string jsonPayload = JsonConvert.SerializeObject(telemetryData);

            await SendTelemetryAsync(jsonPayload, cancellationToken).ConfigureAwait(true);
        }

        // TODO - Implement functions below as you need them.
        public Task ReportAvailabilityAsync(
                        Uri serviceUri,
                        string instance,
                        string testName,
                        DateTimeOffset captured,
                        TimeSpan duration,
                        string location,
                        bool success,
                        CancellationToken cancellationToken,
                        string message = null)
        {
            return Task.CompletedTask;
        }

        public Task<bool> ReportMetricAsync<T>(
                            string name,
                            T value,
                            CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task ReportMetricAsync(
                        string name,
                        long value,
                        IDictionary<string, string> properties,
                        CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReportMetricAsync(
                        string service,
                        Guid partition,
                        string name,
                        long value,
                        CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReportMetricAsync(
                        string role,
                        long id,
                        string name,
                        long value,
                        CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

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
            return Task.CompletedTask;
        }

        public async Task<bool> ReportClusterUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token)
        {
            if (eventData?.FabricUpgradeProgress == null || token.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                string jsonPayload = JsonConvert.SerializeObject(
                        new
                        {
                            eventData.ClusterId,
                            Timestamp = DateTime.UtcNow,
                            eventData.OS,
                            UpgradeTargetCodeVersion = eventData.FabricUpgradeProgress.UpgradeDescription?.TargetCodeVersion,
                            UpgradeTargetConfigVersion = eventData.FabricUpgradeProgress.UpgradeDescription?.TargetConfigVersion,
                            UpgradeState = Enum.GetName(typeof(FabricUpgradeState), eventData.FabricUpgradeProgress.UpgradeState),
                            eventData.FabricUpgradeProgress.CurrentUpgradeDomainProgress.UpgradeDomainName,
                            UpgradeDuration = eventData.FabricUpgradeProgress.CurrentUpgradeDomainDuration,
                            FailureReason = eventData.FabricUpgradeProgress.FailureReason.HasValue ? Enum.GetName(typeof(UpgradeFailureReason), eventData.FabricUpgradeProgress.FailureReason.Value) : null,
                        });

                await SendTelemetryAsync(jsonPayload, token).ConfigureAwait(true);
                return true;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                logger.LogWarning($"Failure in ReportClusterUpgradeStatus:{Environment.NewLine}{e}");
            }

            return false;
        }

        public async Task<bool> ReportApplicationUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token)
        {
            if (eventData?.ApplicationUpgradeProgress == null || token.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                string jsonPayload = JsonConvert.SerializeObject(
                        new
                        {
                            eventData.ClusterId,
                            Timestamp = DateTime.UtcNow,
                            eventData.OS,
                            ApplicationName = eventData.ApplicationUpgradeProgress.ApplicationName?.OriginalString,
                            UpgradeTargetAppTypeVersion = eventData.ApplicationUpgradeProgress.UpgradeDescription?.TargetApplicationTypeVersion,
                            UpgradeState = Enum.GetName(typeof(FabricUpgradeState), eventData.ApplicationUpgradeProgress.UpgradeState),
                            eventData.ApplicationUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName,
                            UpgradeDuration = eventData.ApplicationUpgradeProgress.CurrentUpgradeDomainDuration,
                            FailureReason = eventData.ApplicationUpgradeProgress.FailureReason.HasValue ? Enum.GetName(typeof(UpgradeFailureReason), eventData.ApplicationUpgradeProgress.FailureReason.Value) : null,
                        });

                await SendTelemetryAsync(jsonPayload, token).ConfigureAwait(true);
                return true;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                logger.LogWarning($"Failure in ReportClusterUpgradeStatus:{Environment.NewLine}{e}");
            }

            return false;
        }
    }
}
