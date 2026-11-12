// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace FabricObserverTests
{
    /// <summary>
    /// Tests the Logs Ingestion API sender against a fake HTTP handler and clock. No Azure resources are used.
    /// </summary>
    [TestClass]
    public class LogsIngestionTelemetryTests
    {
        // Fake values. Requests never leave the process: the sender sends through FakeHandler, not the network.
        // ".invalid" is a reserved domain that never resolves, so a broken handler wiring fails instead of reaching a real host.
        private const string Endpoint = "https://fake-ingestion-endpoint.invalid";
        private const string DcrImmutableId = "dcr-fake00000000000000000000000000";
        private const string StreamName = "Custom-FakeStream";
        private const string ClientId = "00000000-0000-0000-0000-00000000fa4e";
        private const string FakeWorkspaceId = "00000000-0000-0000-0000-000000000000";

        // The real IMDS address. The sender always calls it, so the fake handler uses it to tell token requests from uploads.
        private const string ImdsHost = "169.254.169.254";

        // Old-sender fields that hold a new random ID or the current time on every call.
        private static readonly string[] VolatileFields = ["id", "datetime", "Timestamp"];
        private static readonly string LegacyKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("legacy-test-key"));

        private readonly List<string> statusChanges = [];
        private TestTimeProvider clock;

        [TestInitialize]
        public void Initialize()
        {
            clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        }

        // ---- Request format ----

        [TestMethod]
        [DataRow(Endpoint)]
        [DataRow(Endpoint + "/")]
        public async Task Upload_PostsWrappedRecordToDcrStreamWithManagedIdentityToken(string endpoint)
        {
            var handler = new FakeHandler(this);
            ITelemetryProvider sender = CreateSender(handler, endpoint);

            await sender.ReportHealthAsync(ServiceRecord(), CancellationToken.None);

            RecordedRequest token = handler.TokenRequests.Single();
            Assert.AreEqual(HttpMethod.Get, token.Method);
            Assert.AreEqual("/metadata/identity/oauth2/token", token.Uri.AbsolutePath);
            Assert.AreEqual("true", token.Headers.GetValueOrDefault("Metadata"), "IMDS requires the Metadata: true header.");
            var query = HttpUtility.ParseQueryString(token.Uri.Query);
            Assert.AreEqual(ClientId, query["client_id"], "The token must be for the configured user-assigned identity.");
            StringAssert.StartsWith(query["resource"], "https://monitor.azure.com");
            Assert.IsFalse(string.IsNullOrEmpty(query["api-version"]));

            RecordedRequest upload = handler.Uploads.Single();
            Assert.AreEqual(HttpMethod.Post, upload.Method);
            Assert.AreEqual($"{Endpoint}/dataCollectionRules/{DcrImmutableId}/streams/{StreamName}?api-version=2023-01-01", upload.Uri.AbsoluteUri);
            Assert.AreEqual("Bearer token-1", upload.Headers.GetValueOrDefault("Authorization"));
            StringAssert.StartsWith(upload.Headers.GetValueOrDefault("Content-Type"), "application/json");
            JArray body = JArray.Parse(upload.Body);
            Assert.AreEqual(1, body.Count);
            CollectionAssert.AreEqual(new[] { "Payload" }, ((JObject)body[0]).Properties().Select(p => p.Name).ToArray());
            Assert.AreEqual(0, statusChanges.Count, "A successful first upload is not a status change.");
        }

        [TestMethod]
        [DynamicData(nameof(RecordCases))]
        public async Task RecordContent_MatchesDataCollectorSender(string recordCase)
        {
            var legacyHandler = new FakeHandler(this) { Ingestion = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)) };
            var handler = new FakeHandler(this);

            await Records[recordCase](CreateLegacySender(legacyHandler));
            await Records[recordCase](CreateSender(handler));

            JToken legacy = JToken.Parse(legacyHandler.Uploads.Single().Body);
            JToken[] expected = legacy is JArray records ? [.. records] : [legacy];
            JArray body = JArray.Parse(handler.Uploads.Single().Body);
            Assert.AreEqual(expected.Length, body.Count, "Each old-API record must become one item in the request.");

            for (int i = 0; i < expected.Length; i++)
            {
                var item = (JObject)body[i];
                CollectionAssert.AreEqual(new[] { "Payload" }, item.Properties().Select(p => p.Name).ToArray());
                JToken expectedRecord = WithoutVolatileFields(expected[i]);
                JToken actualRecord = WithoutVolatileFields(item["Payload"]);
                Assert.IsTrue(
                    JToken.DeepEquals(expectedRecord, actualRecord),
                    $"Record {i} differs from the old API.{Environment.NewLine}Expected: {expectedRecord}{Environment.NewLine}Actual: {actualRecord}");
            }
        }

        [TestMethod]
        public async Task ReportMethodsTheOldSenderIgnores_SendNothing()
        {
            var handler = new FakeHandler(this);
            ITelemetryProvider sender = CreateSender(handler);
            var properties = new Dictionary<string, string> { ["key"] = "value" };

            await sender.ReportAvailabilityAsync(new Uri("fabric:/App/Svc"), "instance", "test", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), "location", true, CancellationToken.None);
            await sender.ReportMetricAsync("metric", 1L, properties, CancellationToken.None);
            await sender.ReportMetricAsync("fabric:/App/Svc", Guid.NewGuid(), "metric", 1L, CancellationToken.None);
            await sender.ReportMetricAsync("role", 1L, "metric", 1L, CancellationToken.None);
            await sender.ReportMetricAsync("role", "instance", "metric", 1L, 1, 1L, 1L, 1L, 0.0, properties, CancellationToken.None);

            Assert.AreEqual(0, handler.Requests.Count, "The old sender sends nothing for these methods, so neither does the new one.");
        }

        // ---- Token handling ----

        [TestMethod]
        public async Task Token_IsReusedAndRefreshedBeforeExpiry()
        {
            var handler = new FakeHandler(this);
            ITelemetryProvider sender = CreateSender(handler);

            await SendRecordAsync(sender);
            clock.Advance(TimeSpan.FromMinutes(30));
            await SendRecordAsync(sender);
            Assert.AreEqual(1, handler.TokenRequests.Length, "A valid token must be reused, not requested per upload.");

            // The token expires 60 minutes after it was issued. Refresh must happen before then.
            clock.Advance(TimeSpan.FromMinutes(29));
            await SendRecordAsync(sender);

            Assert.AreEqual(2, handler.TokenRequests.Length, "A token about to expire must be refreshed.");
            Assert.AreEqual("Bearer token-2", handler.Uploads.Last().Headers.GetValueOrDefault("Authorization"));
            Assert.AreEqual(3, handler.Uploads.Length);
        }

        [TestMethod]
        public async Task ConcurrentUploads_RequestOneToken()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeHandler(this);
            handler.Imds = async (_, number) =>
            {
                await release.Task;
                return TokenResponse($"token-{number}");
            };
            ITelemetryProvider sender = CreateSender(handler);

            Task[] uploads = Enumerable.Range(0, 5).Select(_ => SendRecordAsync(sender)).ToArray();

            // Let every upload reach the token step before the first token arrives.
            await Task.Delay(200);
            release.SetResult();
            await Task.WhenAll(uploads).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(1, handler.TokenRequests.Length, "Concurrent uploads must share one token request.");
            Assert.AreEqual(5, handler.Uploads.Length);
        }

        [TestMethod]
        public async Task TokenFailure_ReportsProblemSkipsUploadAndRetriesAfterDelay()
        {
            var handler = new FakeHandler(this);
            handler.Imds = (_, number) => Task.FromResult(
                number == 1
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":\"invalid_request\",\"error_description\":\"Identity not found\"}")
                    }
                    : TokenResponse($"token-{number}"));
            ITelemetryProvider sender = CreateSender(handler);

            await SendRecordAsync(sender);

            Assert.AreEqual(0, handler.Uploads.Length, "Nothing can be uploaded without a token.");
            Assert.AreEqual(1, statusChanges.Count);
            Assert.IsNotNull(statusChanges[0]);
            StringAssert.Contains(statusChanges[0], ObserverConstants.LogAnalyticsManagedIdentityClientIdParameter, "Point the operator to the identity setting.");

            await SendRecordAsync(sender);

            Assert.AreEqual(1, handler.TokenRequests.Length, "IMDS must not be asked again right after a failure.");
            Assert.AreEqual(0, handler.Uploads.Length);
            Assert.AreEqual(1, statusChanges.Count, "The unchanged problem must not be reported again.");

            clock.Advance(TimeSpan.FromSeconds(31));
            await SendRecordAsync(sender);

            Assert.AreEqual(2, handler.TokenRequests.Length, "A failed token request must be retried after the delay.");
            Assert.AreEqual(1, handler.Uploads.Length);
            Assert.AreEqual(2, statusChanges.Count);
            Assert.IsNull(statusChanges[1], "A successful upload must clear the problem.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task TokenFailure_QueuedUploadsFailWithoutRequestingAgain(bool timeout)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeHandler(this);
            handler.Imds = async (_, _) =>
            {
                await release.Task;

                // A timeout surfaces as a cancellation the caller did not request.
                return timeout
                    ? throw new TaskCanceledException("IMDS did not respond.")
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            };
            ITelemetryProvider sender = CreateSender(handler);

            Task[] uploads = Enumerable.Range(0, 5).Select(_ => SendRecordAsync(sender)).ToArray();

            // Let every upload queue behind the first token request before it fails.
            await Task.Delay(200);
            release.SetResult();
            await Task.WhenAll(uploads).WaitAsync(TimeSpan.FromSeconds(10));

            // Each queued upload asking IMDS again would make a slow IMDS stall every sender one timeout at a time.
            Assert.AreEqual(1, handler.TokenRequests.Length, "Uploads queued behind a failed token request must not request again.");
            Assert.AreEqual(0, handler.Uploads.Length);
            Assert.AreEqual(1, statusChanges.Count);
            Assert.IsNotNull(statusChanges[0]);
        }

        // ---- Upload failures and status reporting ----

        [TestMethod]
        [DataRow(403)]
        [DataRow(404)]
        [DataRow(500)]
        public async Task UploadRejected_ReportsProblemWithStatusCode(int statusCode)
        {
            var handler = new FakeHandler(this)
            {
                Ingestion = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"Failure\",\"message\":\"Rejected\"}}")
                })
            };
            ITelemetryProvider sender = CreateSender(handler);

            await SendRecordAsync(sender);

            Assert.AreEqual(1, handler.Uploads.Length, "A failed upload must not be retried.");
            Assert.AreEqual(1, statusChanges.Count);
            Assert.IsNotNull(statusChanges[0]);
            StringAssert.Contains(statusChanges[0], statusCode.ToString());
            Assert.IsFalse(statusChanges[0].Contains("token-1"), "The access token must never be reported.");
        }

        [TestMethod]
        public async Task NetworkError_ReportsProblemWithoutThrowing()
        {
            var handler = new FakeHandler(this) { Ingestion = _ => throw new HttpRequestException("Connection refused") };
            ITelemetryProvider sender = CreateSender(handler);

            await SendRecordAsync(sender);

            Assert.AreEqual(1, handler.Uploads.Length, "A failed upload must not be retried.");
            Assert.AreEqual(1, statusChanges.Count);
            Assert.IsNotNull(statusChanges[0]);
        }

        [TestMethod]
        public async Task Status_IsReportedAgainWhenTheProblemChanges()
        {
            HttpStatusCode[] outcomes = [HttpStatusCode.ServiceUnavailable, HttpStatusCode.Forbidden];
            int call = 0;
            var handler = new FakeHandler(this) { Ingestion = _ => Task.FromResult(new HttpResponseMessage(outcomes[call++])) };
            ITelemetryProvider sender = CreateSender(handler);

            await SendRecordAsync(sender);
            await SendRecordAsync(sender);

            // A new cause needs a new report, or the health warning keeps showing the old one.
            Assert.AreEqual(2, statusChanges.Count);
            StringAssert.Contains(statusChanges[0], "503");
            StringAssert.Contains(statusChanges[1], "403");
        }

        [TestMethod]
        public async Task Status_IsReportedOnlyWhenItChanges()
        {
            HttpStatusCode[] outcomes = [HttpStatusCode.NoContent, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.NoContent, HttpStatusCode.NoContent];
            int call = 0;
            var handler = new FakeHandler(this) { Ingestion = _ => Task.FromResult(new HttpResponseMessage(outcomes[call++])) };
            ITelemetryProvider sender = CreateSender(handler);

            foreach (HttpStatusCode _ in outcomes)
            {
                await SendRecordAsync(sender);
            }

            // One report per record would flood the cluster health store, so report only changes.
            Assert.AreEqual(2, statusChanges.Count, $"Expected [problem, recovered] but got {statusChanges.Count} reports.");
            Assert.IsNotNull(statusChanges[0]);
            Assert.IsNull(statusChanges[1]);
        }

        [TestMethod]
        public async Task CancelledToken_SendsNothing()
        {
            var handler = new FakeHandler(this);
            ITelemetryProvider sender = CreateSender(handler);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await sender.ReportHealthAsync(ServiceRecord(), cancellation.Token);

            Assert.AreEqual(0, handler.Requests.Count);
            Assert.AreEqual(0, statusChanges.Count);
        }

        // ---- Records shared with the old-API comparison ----

        public static IEnumerable<object[]> RecordCases => Records.Keys.Select(name => new object[] { name });

        private static readonly Dictionary<string, Func<ITelemetryProvider, Task>> Records = new()
        {
            ["HealthEvent"] = sender => sender.ReportHealthAsync("Property", HealthState.Warning, "evaluations", "Source", CancellationToken.None, "fabric:/App/Svc", "instance"),
            ["ServiceHealth"] = sender => sender.ReportHealthAsync(ServiceRecord(), CancellationToken.None),
            ["NodeHealth"] = sender => sender.ReportHealthAsync(Fill(new NodeTelemetryData()), CancellationToken.None),
            ["DiskHealth"] = sender => sender.ReportHealthAsync(Fill(new DiskTelemetryData { DriveName = "C:", FolderName = "logs" }), CancellationToken.None),
            ["ClusterHealth"] = sender => sender.ReportHealthAsync(Fill(new ClusterTelemetryData()), CancellationToken.None),
            ["SystemServiceHealth"] = sender => sender.ReportHealthAsync(Fill(new SystemServiceTelemetryData()), CancellationToken.None),
            ["NetworkHealth"] = sender => sender.ReportHealthAsync(Fill(new NetworkTelemetryData()), CancellationToken.None),
            ["ContainerHealth"] = sender => sender.ReportHealthAsync(Fill(new ContainerTelemetryData { ContainerId = "container-1" }), CancellationToken.None),
            ["GenericHealth"] = sender => sender.ReportHealthAsync(Fill(new TelemetryData { ApplicationName = "fabric:/App", ProcessId = 42 }), CancellationToken.None),
            ["ServiceMetric"] = sender => sender.ReportMetricAsync(ServiceRecord(), CancellationToken.None),
            ["ChildProcessMetrics"] = sender => sender.ReportMetricAsync(ChildProcessRecords(), CancellationToken.None),
            ["MachineMetric"] = sender => sender.ReportMetricAsync(
                new MachineTelemetryData { NodeName = "_Node_0", ObserverName = "OSObserver", OSName = "Windows", HealthState = "Ok", TotalMemorySizeGB = 64, LogicalProcessorCount = 8 },
                CancellationToken.None),
            ["NamedMetric"] = sender => sender.ReportMetricAsync("metric", 1.5, "Source", CancellationToken.None),
            ["NodeSnapshot"] = sender => sender.ReportNodeSnapshotAsync(
                new NodeSnapshotTelemetryData { SnapshotId = "snapshot-1", NodeName = "_Node_0", NodeStatus = "Up", HealthState = "Ok", CodeVersion = "11.0" },
                CancellationToken.None)
        };

        private static ServiceTelemetryData ServiceRecord() => Fill(new ServiceTelemetryData
        {
            ApplicationName = "fabric:/App",
            ServiceName = "fabric:/App/Svc",
            PartitionId = "5d1e9a3c-63f2-4b6e-9f1a-0c4b2d7e8f90",
            ProcessId = 1234,
            ProcessName = "App.exe",

            // Largest integer a double holds exactly; catches records re-encoded through floating point.
            ReplicaId = 9007199254740991
        });

        private static T Fill<T>(T record) where T : TelemetryDataBase
        {
            record.Code = "FO001";
            record.Description = "Synthetic description";
            record.HealthState = HealthState.Warning;
            record.Metric = "CPU %";
            record.NodeName = "_Node_0";
            record.NodeType = "NodeType0";
            record.ObserverName = "AppObserver";
            record.Property = "CpuPercent";
            record.Source = "AppObserver";
            record.Value = 12.5;
            return record;
        }

        private static List<ChildProcessTelemetryData> ChildProcessRecords() =>
        [
            new ChildProcessTelemetryData
            {
                ApplicationName = "fabric:/App",
                ServiceName = "fabric:/App/Svc",
                Metric = "CPU %",
                Value = 3,
                ProcessId = 10,
                ProcessName = "parent",
                ReplicaId = 1,
                NodeName = "_Node_0",
                ChildProcessCount = 1,
                ChildProcessInfo = [new ChildProcessInfo { ProcessId = 11, ProcessName = "child", Value = 1.5 }]
            },
            new ChildProcessTelemetryData
            {
                ApplicationName = "fabric:/App",
                ServiceName = "fabric:/App/Svc2",
                Metric = "CPU %",
                Value = 4,
                ProcessId = 20,
                ProcessName = "parent2",
                ReplicaId = 2,
                NodeName = "_Node_0",
                ChildProcessCount = 0,
                ChildProcessInfo = []
            }
        ];

        // ---- Helpers ----

        private Task SendRecordAsync(ITelemetryProvider sender) => sender.ReportHealthAsync(ServiceRecord(), CancellationToken.None);

        /// <summary>
        /// Test seam: the sender takes its HTTP handler, clock, and a status callback (problem text, or null when sending recovers).
        /// Resolved by reflection so the test project still compiles before the seam exists.
        /// </summary>
        private ITelemetryProvider CreateSender(FakeHandler handler, string endpoint = Endpoint)
        {
            ConstructorInfo constructor = typeof(LogsIngestionTelemetry).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [typeof(Uri), typeof(string), typeof(string), typeof(string), typeof(HttpMessageHandler), typeof(TimeProvider), typeof(Action<string>)],
                null);
            Assert.IsNotNull(
                constructor,
                "Missing test seam: LogsIngestionTelemetry(Uri, string, string, string, HttpMessageHandler, TimeProvider, Action<string> statusChanged).");

            return (ITelemetryProvider)constructor.Invoke([new Uri(endpoint), DcrImmutableId, StreamName, ClientId, handler, clock, (Action<string>)RecordStatus]);
        }

        /// <summary>
        /// Test seam on the old sender, used only to compare record content.
        /// </summary>
        private static ITelemetryProvider CreateLegacySender(FakeHandler handler)
        {
            ConstructorInfo constructor = typeof(LogAnalyticsTelemetry).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [typeof(string), typeof(string), typeof(string), typeof(HttpMessageHandler)],
                null);
            Assert.IsNotNull(constructor, "Missing test seam: LogAnalyticsTelemetry(string, string, string, HttpMessageHandler).");

            return (ITelemetryProvider)constructor.Invoke([FakeWorkspaceId, LegacyKey, "ObserverTest", handler]);
        }

        private void RecordStatus(string problem)
        {
            lock (statusChanges)
            {
                statusChanges.Add(problem);
            }
        }

        private HttpResponseMessage TokenResponse(string accessToken, int expiresInSeconds = 3600)
        {
            // Same shape as an IMDS response. expires_in is a string in real responses.
            var token = new JObject
            {
                ["access_token"] = accessToken,
                ["client_id"] = ClientId,
                ["expires_in"] = expiresInSeconds.ToString(),
                ["expires_on"] = (clock.GetUtcNow().ToUnixTimeSeconds() + expiresInSeconds).ToString(),
                ["resource"] = "https://monitor.azure.com/",
                ["token_type"] = "Bearer"
            };

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(token.ToString(), Encoding.UTF8, "application/json") };
        }

        private static JToken WithoutVolatileFields(JToken record)
        {
            JToken copy = record.DeepClone();

            if (copy is JObject fields)
            {
                foreach (string name in VolatileFields)
                {
                    fields.Remove(name);
                }
            }

            return copy;
        }

        private sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);

        /// <summary>
        /// Stands in for the network. The sender builds its HttpClient on this handler, so every request it sends
        /// arrives in SendAsync below instead of going out. The handler records the request and returns a canned response.
        /// </summary>
        private sealed class FakeHandler : HttpMessageHandler
        {
            private int tokenRequestCount;

            public FakeHandler(LogsIngestionTelemetryTests test)
            {
                Imds = (_, number) => Task.FromResult(test.TokenResponse($"token-{number}"));
            }

            public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

            /// <summary>Receives the request and its 1-based token request number.</summary>
            public Func<HttpRequestMessage, int, Task<HttpResponseMessage>> Imds { get; set; }

            public Func<HttpRequestMessage, Task<HttpResponseMessage>> Ingestion { get; set; } =
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

            public RecordedRequest[] TokenRequests => Requests.Where(r => r.Uri.Host == ImdsHost).ToArray();

            public RecordedRequest[] Uploads => Requests.Where(r => r.Uri.Host != ImdsHost).ToArray();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var headers = request.Headers
                    .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                    .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => string.Join(",", group.SelectMany(header => header.Value)), StringComparer.OrdinalIgnoreCase);
                Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri, headers, body));

                return request.RequestUri.Host == ImdsHost
                    ? await Imds(request, Interlocked.Increment(ref tokenRequestCount))
                    : await Ingestion(request);
            }
        }

        private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
        {
            private DateTimeOffset now = start;

            public override DateTimeOffset GetUtcNow() => now;

            public void Advance(TimeSpan by) => now += by;
        }
    }
}
