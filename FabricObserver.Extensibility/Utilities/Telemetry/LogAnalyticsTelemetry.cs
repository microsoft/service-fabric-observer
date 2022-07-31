// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
        private readonly Logger logger;

        private string WorkspaceId
        {
            get;
        }

        public string Key
        {
            get; set;
        }

        private string ApiVersion
        {
            get;
        }

        private string LogType
        {
            get;
        }

        public LogAnalyticsTelemetry(
                string workspaceId,
                string sharedKey,
                string logType,
                string apiVersion = "2016-04-01")
        {
            WorkspaceId = workspaceId;
            Key = sharedKey;
            LogType = logType;
            ApiVersion = apiVersion;
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
                    osPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux"
                });

            await SendTelemetryAsync(jsonPayload, cancellationToken);
        }

        public async Task ReportHealthAsync(TelemetryData telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string jsonPayload = JsonConvert.SerializeObject(telemetryData);
            await SendTelemetryAsync(jsonPayload, cancellationToken);
        }

        public async Task ReportMetricAsync(TelemetryData telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string jsonPayload = JsonConvert.SerializeObject(telemetryData);
            await SendTelemetryAsync(jsonPayload, cancellationToken);
        }

        public async Task ReportMetricAsync(List<ChildProcessTelemetryData> telemetryData, CancellationToken cancellationToken)
        {
            if (telemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string jsonPayload = JsonConvert.SerializeObject(telemetryData);
            await SendTelemetryAsync(jsonPayload, cancellationToken);
        }

        public async Task ReportMetricAsync(MachineTelemetryData machineTelemetryData, CancellationToken cancellationToken)
        {
            if (machineTelemetryData == null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string jsonPayload = JsonConvert.SerializeObject(machineTelemetryData);
            await SendTelemetryAsync(jsonPayload, cancellationToken);
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

            Uri requestUri = new Uri($"https://{WorkspaceId}.ods.opinsights.azure.com/api/logs?api-version={ApiVersion}");
            string date = DateTime.UtcNow.ToString("r");
            string signature = GetSignature("POST", payload.Length, "application/json", date, "/api/logs");
            
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUri);
            request.ContentType = "application/json";
            request.Method = "POST";
            request.Headers["Log-Type"] = LogType;
            request.Headers["x-ms-date"] = date;
            request.Headers["Authorization"] = signature;
            byte[] content = Encoding.UTF8.GetBytes(payload);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                using (var requestStreamAsync = await request.GetRequestStreamAsync())
                {
                    await requestStreamAsync.WriteAsync(content, 0, content.Length, cancellationToken);

                    using (var responseAsync = await request.GetResponseAsync() as HttpWebResponse)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        if (responseAsync != null && (responseAsync.StatusCode == HttpStatusCode.OK || responseAsync.StatusCode == HttpStatusCode.Accepted))
                        {
                            return;
                        }

                        if (responseAsync != null)
                        {
                            logger.LogWarning(
                                $"Unexpected response from server in LogAnalyticsTelemetry.SendTelemetryAsync:{Environment.NewLine}{responseAsync.StatusCode}: {responseAsync.StatusDescription}");
                        }
                    }
                }
            }
            catch (Exception e) when (e is SocketException || e is WebException)
            {
                logger.LogInfo($"Exception sending telemetry to LogAnalytics service:{Environment.NewLine}{e}");
            }
            catch (Exception e)
            {
                // Do not take down FO with a telemetry fault. Log it. Warning level will always log.
                // This means there is either a bug in this code or something else that needs your attention..
                logger.LogWarning($"Unhandled exception sending telemetry to LogAnalytics service:{Environment.NewLine}{e}");
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

            using (var encryptor = new HMACSHA256(Convert.FromBase64String(Key)))
            {
                return $"SharedKey {WorkspaceId}:{Convert.ToBase64String(encryptor.ComputeHash(bytes))}";
            }
        }
    }
}
