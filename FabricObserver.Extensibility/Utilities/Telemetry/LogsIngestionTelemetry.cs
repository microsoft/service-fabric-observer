// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    /// <summary>
    /// Sends telemetry through the Azure Monitor Logs Ingestion API using a user-assigned managed identity.
    /// </summary>
    internal sealed class LogsIngestionTelemetry : ITelemetryProvider
    {
        // These clients own their handlers for the lifetime of the process, not the configuration.
        private static readonly HttpClient UploadClient = new(new HttpClientHandler { AllowAutoRedirect = false });
        private static readonly HttpClient IdentityClient = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
        private readonly HttpMessageInvoker uploadClient;
        private readonly HttpMessageInvoker identityClient;
        private readonly TimeProvider timeProvider;
        private readonly Action<string> statusChanged;
        private readonly SemaphoreSlim tokenLock = new(1, 1);
        private readonly object statusLock = new();
        private readonly Uri tokenUri;
        private readonly Logger logger = new("TelemetryLogger");
        private string accessToken;
        private DateTimeOffset tokenExpiresOn;
        private string currentProblem;

        public LogsIngestionTelemetry(Uri ingestionEndpoint, string dcrImmutableId, string streamName, string managedIdentityClientId)
            : this(ingestionEndpoint, dcrImmutableId, streamName, managedIdentityClientId, null, TimeProvider.System, null)
        {
        }

        internal LogsIngestionTelemetry(
            Uri ingestionEndpoint,
            string dcrImmutableId,
            string streamName,
            string managedIdentityClientId,
            Action<string> statusChanged)
            : this(ingestionEndpoint, dcrImmutableId, streamName, managedIdentityClientId, null, TimeProvider.System, statusChanged)
        {
        }

        internal LogsIngestionTelemetry(
            Uri ingestionEndpoint,
            string dcrImmutableId,
            string streamName,
            string managedIdentityClientId,
            HttpMessageHandler messageHandler,
            TimeProvider timeProvider,
            Action<string> statusChanged)
        {
            IngestionEndpoint = ingestionEndpoint ?? throw new ArgumentNullException(nameof(ingestionEndpoint));
            if (!ingestionEndpoint.IsAbsoluteUri)
            {
                throw new ArgumentException("An absolute URL is required.", nameof(ingestionEndpoint));
            }

            DcrImmutableId = !string.IsNullOrWhiteSpace(dcrImmutableId) ? dcrImmutableId : throw new ArgumentException("Value is required.", nameof(dcrImmutableId));
            StreamName = !string.IsNullOrWhiteSpace(streamName) ? streamName : throw new ArgumentException("Value is required.", nameof(streamName));
            ManagedIdentityClientId = !string.IsNullOrWhiteSpace(managedIdentityClientId)
                ? managedIdentityClientId
                : throw new ArgumentException("Value is required.", nameof(managedIdentityClientId));
            this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            this.statusChanged = statusChanged;
            // An injected handler is caller-owned; the invoker itself owns no network resources.
            uploadClient = messageHandler == null ? UploadClient : new HttpMessageInvoker(messageHandler, disposeHandler: false);
            identityClient = messageHandler == null ? IdentityClient : uploadClient;
            tokenUri = new Uri(
                "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01" +
                $"&resource={Uri.EscapeDataString("https://monitor.azure.com/")}&client_id={Uri.EscapeDataString(ManagedIdentityClientId)}");
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
            string instanceName = null) =>
            SendRecordAsync(() => LogAnalyticsPayload.Health(propertyName, state, unhealthyEvaluations, source, serviceName, instanceName), cancellationToken);

        public Task ReportHealthAsync(TelemetryDataBase telemetryData, CancellationToken cancellationToken) =>
            SendObjectAsync(telemetryData, cancellationToken);

        public Task<bool> ReportMetricAsync<T>(string name, T value, string source, CancellationToken cancellationToken) =>
            SendRecordAsync(() => LogAnalyticsPayload.Metric(name, value, source), cancellationToken);

        public Task ReportMetricAsync(TelemetryDataBase telemetryData, CancellationToken cancellationToken) =>
            SendObjectAsync(telemetryData, cancellationToken);

        public Task ReportMetricAsync(List<ChildProcessTelemetryData> telemetryData, CancellationToken cancellationToken) =>
            telemetryData == null
                ? Task.CompletedTask
                : SendRecordAsync(
                    () => JsonConvert.SerializeObject(telemetryData.ConvertAll(record => new { Payload = record })),
                    cancellationToken,
                    wrapPayload: false);

        public Task ReportMetricAsync(MachineTelemetryData telemetryData, CancellationToken cancellationToken) =>
            SendObjectAsync(telemetryData, cancellationToken);

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

        public Task ReportClusterUpgradeStatusAsync(ServiceFabricUpgradeEventData eventData, CancellationToken token) =>
            eventData?.FabricUpgradeProgress == null
                ? Task.CompletedTask
                : SendRecordAsync(() => LogAnalyticsPayload.ClusterUpgrade(eventData), token);

        public Task ReportApplicationUpgradeStatusAsync(ServiceFabricUpgradeEventData appUpgradeInfo, CancellationToken token) =>
            appUpgradeInfo?.ApplicationUpgradeProgress == null
                ? Task.CompletedTask
                : SendRecordAsync(() => LogAnalyticsPayload.ApplicationUpgrade(appUpgradeInfo), token);

        public Task ReportNodeSnapshotAsync(NodeSnapshotTelemetryData nodeSnapshotTelem, CancellationToken token) =>
            SendObjectAsync(nodeSnapshotTelem, token);

        private Task SendObjectAsync<T>(T record, CancellationToken cancellationToken) =>
            record == null ? Task.CompletedTask : SendRecordAsync(() => JsonConvert.SerializeObject(record), cancellationToken);

        private async Task<bool> SendRecordAsync(Func<string> serialize, CancellationToken cancellationToken, bool wrapPayload = true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            bool requestingToken = false;
            try
            {
                string json = serialize();
                string payload = wrapPayload ? "[{\"Payload\":" + json + "}]" : json;
                requestingToken = true;
                string token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                if (token == null)
                {
                    return false;
                }

                requestingToken = false;
                var uploadUri = new UriBuilder(IngestionEndpoint)
                {
                    Path = IngestionEndpoint.AbsolutePath.TrimEnd('/') +
                        $"/dataCollectionRules/{Uri.EscapeDataString(DcrImmutableId)}/streams/{Uri.EscapeDataString(StreamName)}",
                    Query = "api-version=2023-01-01",
                    Fragment = string.Empty
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, uploadUri.Uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await uploadClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    ReportStatus($"Log Analytics Logs Ingestion API upload failed: HTTP {(int)response.StatusCode}.");
                    return false;
                }

                ReportStatus(null);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                ReportStatus(FailureMessage(requestingToken, "request timed out"));
            }
            catch (HttpRequestException e)
            {
                ReportStatus(FailureMessage(requestingToken, $"HTTP request error ({e.HttpRequestError})"));
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or NotSupportedException or IOException)
            {
                // Exception messages and response bodies may contain credentials or customer data.
                ReportStatus(FailureMessage(requestingToken, $"invalid request or response ({e.GetType().Name})"));
            }

            return false;
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (accessToken != null && tokenExpiresOn - timeProvider.GetUtcNow() > RefreshMargin)
                {
                    return accessToken;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, tokenUri);
                request.Headers.Add("Metadata", "true");
                using HttpResponseMessage response = await identityClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    ReportStatus(FailureMessage(true, $"IMDS returned HTTP {(int)response.StatusCode}"));
                    return null;
                }

                JObject result = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                string token = result["access_token"]?.Type == JTokenType.String ? (string)result["access_token"] : null;
                JToken expiresOn = result["expires_on"];
                if (string.IsNullOrWhiteSpace(token) ||
                    expiresOn?.Type is not (JTokenType.String or JTokenType.Integer) ||
                    !long.TryParse(expiresOn.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long expiry) ||
                    expiry < DateTimeOffset.MinValue.ToUnixTimeSeconds() ||
                    expiry > DateTimeOffset.MaxValue.ToUnixTimeSeconds() ||
                    DateTimeOffset.FromUnixTimeSeconds(expiry) <= timeProvider.GetUtcNow())
                {
                    ReportStatus(FailureMessage(true, "IMDS returned an invalid access_token or expires_on"));
                    return null;
                }

                accessToken = token;
                tokenExpiresOn = DateTimeOffset.FromUnixTimeSeconds(expiry);
                // Even inside the refresh margin, use a freshly fetched token for this upload.
                return token;
            }
            finally
            {
                tokenLock.Release();
            }
        }

        private static string FailureMessage(bool requestingToken, string cause) =>
            requestingToken
                ? $"Log Analytics Logs Ingestion API token request failed for {ObserverConstants.LogAnalyticsManagedIdentityClientIdParameter}: {cause}."
                : $"Log Analytics Logs Ingestion API upload failed: {cause}.";

        private void ReportStatus(string problem)
        {
            lock (statusLock)
            {
                if (string.Equals(currentProblem, problem, StringComparison.Ordinal))
                {
                    return;
                }

                currentProblem = problem;
                if (statusChanged != null)
                {
                    statusChanged(problem);
                }
                else if (problem != null)
                {
                    logger.LogWarning("{0}", problem);
                }
            }
        }
    }
}
