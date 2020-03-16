// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Interfaces;
using Newtonsoft.Json;

namespace FabricClusterObserver.Utilities.Telemetry
{
    // LogAnalyticsTelemetry class is partially based on public (non-license-protected) sample https://dejanstojanovic.net/aspnet/2018/february/send-data-to-azure-log-analytics-from-c-code/
    public class LogAnalyticsTelemetry : ITelemetryProvider
    {
        private readonly FabricClient fabricClient;
        private readonly CancellationToken token;
        private readonly Logger logger;

        public string WorkspaceId { get; set; }

        public string Key { get; set; }

        public string ApiVersion { get; set; }

        public string LogType { get; set; }

        public LogAnalyticsTelemetry(
            string workspaceId,
            string sharedKey,
            string logType,
            FabricClient fabricClient,
            CancellationToken token,
            string apiVersion = "2016-04-01")
        {
            this.WorkspaceId = workspaceId;
            this.Key = sharedKey;
            this.LogType = logType;
            this.fabricClient = fabricClient;
            this.token = token;
            this.ApiVersion = apiVersion;
            logger = new Logger("TelemetryLogger");
        }

        /// <summary>
        /// Sends telemetry data to Azure LogAnalytics via REST.
        /// </summary>
        /// <param name="payload">Json string containing telemetry data.</param>
        /// <returns>A completed task or a task containing exception info.</returns>
        private Task SendTelemetryAsync(string payload)
        {
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

            using (var requestStreamAsync = request.GetRequestStream())
            {
                requestStreamAsync.Write(content, 0, content.Length);
            }

            using (var responseAsync = (HttpWebResponse)request.GetResponse())
            {
                if (responseAsync.StatusCode == HttpStatusCode.OK ||
                    responseAsync.StatusCode == HttpStatusCode.Accepted)
                {
                    return Task.CompletedTask;
                }

                var responseStream = responseAsync.GetResponseStream();

                if (responseStream == null)
                {
                    return Task.CompletedTask;
                }

                using (var streamReader = new StreamReader(responseStream))
                {
                    string err = $"Exception sending LogAnalytics Telemetry:{Environment.NewLine}{streamReader.ReadToEnd()}";
                    logger.LogWarning(err);

                    return Task.FromException(new Exception(err));
                }
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

        // This is the only function impl that really makes sense for ClusterObserver 
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
            var (clusterId, tenantId, clusterType) =
                await ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, token).ConfigureAwait(true);

            string jsonPayload = JsonConvert.SerializeObject(
                new
                {
                    id = $"CO_{Guid.NewGuid().ToString()}",
                    datetime = DateTime.UtcNow,
                    clusterId = clusterId ?? string.Empty,
                    source = ObserverConstants.ClusterObserverName,
                    property = propertyName,
                    healthScope = scope.ToString(),
                    healthState = state.ToString(),
                    healthEvaluation = unhealthyEvaluations,
                    serviceName = serviceName ?? string.Empty,
                    instanceName = instanceName ?? string.Empty
                });

            await SendTelemetryAsync(jsonPayload).ConfigureAwait(false);
        }

        public async Task ReportHealthAsync(
          TelemetryData telemetryData,
          CancellationToken cancellationToken)
        {
            if (telemetryData == null)
            {
                return;
            }

            string jsonPayload = JsonConvert.SerializeObject(telemetryData);

            await SendTelemetryAsync(jsonPayload).ConfigureAwait(false);

            return;
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
    }
}
