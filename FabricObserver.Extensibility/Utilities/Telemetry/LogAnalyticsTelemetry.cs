// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.TelemetryLib;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    // LogAnalyticsTelemetry class is partially (SendTelemetryAsync/GetSignature) based on public sample: https://dejanstojanovic.net/aspnet/2018/february/send-data-to-azure-log-analytics-from-c-code/
    public class LogAnalyticsTelemetry : ITelemetryProvider
    {
        private const string ApiVersion = "2016-04-01";
        private readonly Logger logger;

        private string WorkspaceId
        {
            get;
        }

        private string LogType
        {
            get;
        }

        private string TargetUri => $"https://{WorkspaceId}.ods.opinsights.azure.com/api/logs?api-version={ApiVersion}";

        public string Key
        {
            get; set;
        }

        /// <summary>
        /// Sends telemetry data to Azure LogAnalytics via REST.
        /// </summary>
        /// <param name="payload">Json string containing telemetry data.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A completed task or task containing exception info.</returns>
        private async Task SendTelemetryAsync(string payload, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(payload) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                string date = DateTime.UtcNow.ToString("r");
                string signature = GetSignature("POST", payload.Length, "application/json", date, "/api/logs");
                byte[] content = Encoding.UTF8.GetBytes(payload);
                using HttpClient httpClient = new();
                using HttpRequestMessage request = new(HttpMethod.Post, TargetUri);
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(signature);
                request.Content = new ByteArrayContent(content);
                request.Content.Headers.Add("Content-Type", "application/json");
                request.Content.Headers.Add("Log-Type", LogType);
                request.Content.Headers.Add("x-ms-date", date);
                using var response = await httpClient.SendAsync(request, cancellationToken);

                if (response != null && (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Accepted))
                {
                    return;
                }

                if (response != null)
                {
                    logger.LogWarning(
                        $"Unexpected response from server in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}{response.StatusCode}: {response.ReasonPhrase}");
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is InvalidOperationException)
            {
                logger.LogInfo($"Exception sending telemetry to LogAnalytics service:{Environment.NewLine}{e.Message}");
            }
            catch (Exception e)
            {
                // Do not take down FO with a telemetry fault. Log it. Warning level will always log.
                // This means there is either a bug in this code or something else that needs your attention.
#if DEBUG
                logger.LogWarning($"Exception sending telemetry to LogAnalytics service:{Environment.NewLine}{e}");
#else
                logger.LogWarning($"Exception sending telemetry to LogAnalytics service: {e.Message}");
#endif
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
            using HMACSHA256 encryptor = new(Convert.FromBase64String(Key));

            return $"SharedKey {WorkspaceId}:{Convert.ToBase64String(encryptor.ComputeHash(bytes))}";
        }

        public LogAnalyticsTelemetry(
                string workspaceId,
                string sharedKey,
                string logType)
        {
            WorkspaceId = workspaceId;
            Key = sharedKey;
            LogType = logType;
            logger = new Logger("TelemetryLogger");
        }

        public async Task ReportHealthAsync(
                            string propertyName,
                            HealthState state,
                            string unhealthyEvaluations,
                            string source,
                            CancellationToken cancellationToken,
                            string serviceName = null,
                            string instanceName = null)
        {
            string jsonPayload = JsonConvert.SerializeObject(
                    new
                    {
                        ClusterInformation.ClusterInfoTuple.ClusterId,
                        source,
                        property = propertyName,
                        healthState = state.ToString(),
                        healthEvaluation = unhealthyEvaluations,
                        serviceName = serviceName ?? string.Empty,
                        instanceName = instanceName ?? string.Empty,
                        osPlatform = OperatingSystem.IsWindows() ? "Windows" : "Linux"
                    });

            await SendTelemetryAsync(jsonPayload, cancellationToken);
        }

        public async Task ReportHealthAsync(TelemetryDataBase telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (telemetryData is ServiceTelemetryData serviceTelemData)
            {
                if (JsonHelper.TrySerializeObject(serviceTelemData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
            else if (telemetryData is NodeTelemetryData nodeTelemData)
            {
                if (JsonHelper.TrySerializeObject(nodeTelemData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
            else if (telemetryData is DiskTelemetryData diskTelemData)
            {
                if (JsonHelper.TrySerializeObject(diskTelemData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
            else if (telemetryData is ClusterTelemetryData clusterTelemData)
            {
                if (JsonHelper.TrySerializeObject(clusterTelemData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
            else
            {
                if (JsonHelper.TrySerializeObject(telemetryData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
        }

        public async Task ReportMetricAsync(TelemetryDataBase telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (telemetryData is ServiceTelemetryData serviceTelemData)
            {
                if (JsonHelper.TrySerializeObject(serviceTelemData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
            else if (telemetryData is NodeTelemetryData nodeTelemData)
            {
                if (JsonHelper.TrySerializeObject(nodeTelemData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
            else if (telemetryData is DiskTelemetryData diskTelemData)
            {
                if (JsonHelper.TrySerializeObject(diskTelemData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
            else if (telemetryData is ClusterTelemetryData clusterTelemData)
            {
                if (JsonHelper.TrySerializeObject(clusterTelemData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
            else
            {
                if (JsonHelper.TrySerializeObject(telemetryData, out string jsonPayload))
                {
                    await SendTelemetryAsync(jsonPayload, cancellationToken);
                }
            }
        }

        public async Task ReportMetricAsync(List<ChildProcessTelemetryData> telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (JsonHelper.TrySerializeObject(telemetryData, out string jsonPayload))
            {
                await SendTelemetryAsync(jsonPayload, cancellationToken);
            }
        }

        public async Task ReportMetricAsync(MachineTelemetryData machineTelemetryData, CancellationToken cancellationToken)
        {
            if (machineTelemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (JsonHelper.TrySerializeObject(machineTelemetryData, out string jsonPayload))
            {
                await SendTelemetryAsync(jsonPayload, cancellationToken);
            }
        }

        public async Task<bool> ReportMetricAsync<T>(
                                    string name,
                                    T value,
                                    string source,
                                    CancellationToken cancellationToken)
        {
            string jsonPayload = JsonConvert.SerializeObject(
                    new
                    {
                        id = $"FO_{Guid.NewGuid()}",
                        datetime = DateTime.UtcNow,
                        ClusterInformation.ClusterInfoTuple.ClusterId,
                        source,
                        property = name,
                        value
                    });

            await SendTelemetryAsync(jsonPayload, cancellationToken);

            return await Task.FromResult(true);
        }

        public async Task ReportClusterUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token)
        {
            if (eventData?.FabricUpgradeProgress == null || token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                string jsonPayload = JsonConvert.SerializeObject(
                        new
                        {
                            ClusterId = eventData.ClusterId ?? ClusterInformation.ClusterInfoTuple.ClusterId,
                            Timestamp = DateTime.UtcNow,
                            eventData.OS,
                            CurrentUpgradeDomain = eventData.FabricUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName,
                            eventData.FabricUpgradeProgress?.NextUpgradeDomain,
                            UpgradeTargetCodeVersion = eventData.FabricUpgradeProgress.UpgradeDescription?.TargetCodeVersion,
                            UpgradeTargetConfigVersion = eventData.FabricUpgradeProgress.UpgradeDescription?.TargetConfigVersion,
                            UpgradeState = Enum.GetName(typeof(FabricUpgradeState), eventData.FabricUpgradeProgress.UpgradeState),
                            UpgradeDuration = eventData.FabricUpgradeProgress.CurrentUpgradeDomainDuration,
                            FailureReason = eventData.FabricUpgradeProgress.FailureReason.HasValue ? Enum.GetName(typeof(UpgradeFailureReason), eventData.FabricUpgradeProgress.FailureReason.Value) : null,
                        });

                await SendTelemetryAsync(jsonPayload, token).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                logger.LogWarning($"Failure in ReportClusterUpgradeStatus:{Environment.NewLine}{e}");
            }
        }

        public async Task ReportApplicationUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token)
        {
            if (eventData?.ApplicationUpgradeProgress == null || token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                string jsonPayload = JsonConvert.SerializeObject(
                        new
                        {
                            ClusterId = eventData.ClusterId ?? ClusterInformation.ClusterInfoTuple.ClusterId,
                            Timestamp = DateTime.UtcNow,
                            eventData.OS,
                            ApplicationName = eventData.ApplicationUpgradeProgress.ApplicationName?.OriginalString,
                            CurrentUpgradeDomain = eventData.ApplicationUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName,
                            eventData.ApplicationUpgradeProgress?.NextUpgradeDomain,
                            UpgradeTargetAppTypeVersion = eventData.ApplicationUpgradeProgress.UpgradeDescription?.TargetApplicationTypeVersion,
                            UpgradeState = Enum.GetName(typeof(FabricUpgradeState), eventData.ApplicationUpgradeProgress.UpgradeState),
                            UpgradeDuration = eventData.ApplicationUpgradeProgress.CurrentUpgradeDomainDuration,
                            FailureReason = eventData.ApplicationUpgradeProgress.FailureReason.HasValue ? Enum.GetName(typeof(UpgradeFailureReason), eventData.ApplicationUpgradeProgress.FailureReason.Value) : null,
                        });

                await SendTelemetryAsync(jsonPayload, token).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                logger.LogWarning($"Failure in ReportClusterUpgradeStatus:{Environment.NewLine}{e}");
            }
        }

        public async Task ReportNodeSnapshotAsync(NodeSnapshotTelemetryData nodeSnapshotTelem, CancellationToken cancellationToken)
        {
            if (JsonHelper.TrySerializeObject(nodeSnapshotTelem, out string jsonPayload))
            {
                await SendTelemetryAsync(jsonPayload, cancellationToken);
            }
        }

        // Implement functions below as you need.
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
    }
}
