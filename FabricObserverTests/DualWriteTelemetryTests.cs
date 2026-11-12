// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FabricObserverTests
{
    [TestClass]
    public class DualWriteTelemetryTests
    {
        public enum FailureKind
        {
            /// <summary>The send method throws before returning a task.</summary>
            ThrowsImmediately,

            /// <summary>The send method returns a task that fails.</summary>
            FailedTask
        }

        private readonly CancellationTokenSource cancellation = new();

        /// <summary>
        /// Every send method on <see cref="ITelemetryProvider"/>, identified by signature so overloads are distinct test cases.
        /// </summary>
        public static IEnumerable<object[]> SendMethods =>
            SenderMethods().Select(method => new object[] { method.ToString() });

        public static IEnumerable<object[]> SendMethodFailures =>
            from method in SenderMethods()
            from legacyFails in new[] { true, false }
            from failure in Enum.GetValues<FailureKind>()
            select new object[] { method.ToString(), legacyFails, failure };

        [TestCleanup]
        public void Cleanup() => cancellation.Dispose();

        [TestMethod]
        [DynamicData(nameof(SendMethods), DynamicDataSourceType.Property)]
        public async Task EverySendMethod_ReachesBothSendersWithSameArguments(string signature)
        {
            FakeSender legacy = FakeSender.Create();
            FakeSender ingestion = FakeSender.Create();
            var dualWrite = new DualWriteTelemetry((ITelemetryProvider)legacy, (ITelemetryProvider)ingestion);
            (MethodInfo method, object[] args) = Prepare(signature);

            await (Task)method.Invoke(dualWrite, args);

            foreach ((string api, FakeSender sender) in new[] { ("old API", legacy), ("new API", ingestion) })
            {
                Assert.AreEqual(1, sender.Calls.Count, $"Expected exactly one call to the {api} sender.");
                Assert.AreEqual(method.Name, sender.Calls[0].Method);
                CollectionAssert.AreEqual(args, sender.Calls[0].Args, $"The {api} sender must receive the caller's arguments unchanged.");
            }
        }

        [TestMethod]
        [DynamicData(nameof(SendMethodFailures), DynamicDataSourceType.Property)]
        public async Task EverySendMethod_OneSenderFailing_OtherSenderStillReceivesRecord(string signature, bool legacyFails, FailureKind failure)
        {
            FakeSender legacy = FakeSender.Create(legacyFails ? failure : null);
            FakeSender ingestion = FakeSender.Create(legacyFails ? null : failure);
            FakeSender healthy = legacyFails ? ingestion : legacy;
            var dualWrite = new DualWriteTelemetry((ITelemetryProvider)legacy, (ITelemetryProvider)ingestion);
            (MethodInfo method, object[] args) = Prepare(signature);

            try
            {
                await (Task)method.Invoke(dualWrite, args);
            }
            catch (Exception e) when (e is InvalidOperationException or TargetInvocationException { InnerException: InvalidOperationException })
            {
                // Whether the wrapper surfaces a sender's failure is not decided here; this test covers delivery only.
            }

            Assert.AreEqual(1, healthy.Calls.Count, "A failure in one API must not stop the record reaching the other API.");
            CollectionAssert.AreEqual(args, healthy.Calls[0].Args);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task SlowSender_DoesNotDelayOtherSender(bool legacySlow)
        {
            FakeSender legacy = FakeSender.Create();
            FakeSender ingestion = FakeSender.Create();
            FakeSender slow = legacySlow ? legacy : ingestion;
            FakeSender other = legacySlow ? ingestion : legacy;
            slow.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dualWrite = new DualWriteTelemetry((ITelemetryProvider)legacy, (ITelemetryProvider)ingestion);

            Task pending = dualWrite.ReportHealthAsync(new ServiceTelemetryData(), cancellation.Token);

            Assert.AreEqual(1, other.Calls.Count, "A slow API must not delay sending through the other API.");
            Assert.IsFalse(pending.IsCompleted, "The dual-write task completes only after both APIs finish.");

            slow.Gate.SetResult(true);
            await pending;
        }

        [TestMethod]
        [DataRow(true, true, true)]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(false, false, false)]
        public async Task NamedMetric_ReturnsTrueOnlyWhenBothSendersReturnTrue(bool legacyResult, bool ingestionResult, bool expected)
        {
            FakeSender legacy = FakeSender.Create();
            FakeSender ingestion = FakeSender.Create();
            legacy.Result = legacyResult;
            ingestion.Result = ingestionResult;
            var dualWrite = new DualWriteTelemetry((ITelemetryProvider)legacy, (ITelemetryProvider)ingestion);

            bool result = await dualWrite.ReportMetricAsync("metric", 1.5, "Source", cancellation.Token);

            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public void Key_IsNotSupported()
        {
            var dualWrite = new DualWriteTelemetry((ITelemetryProvider)FakeSender.Create(), (ITelemetryProvider)FakeSender.Create());

            Assert.ThrowsException<NotSupportedException>(() => dualWrite.Key);
            Assert.ThrowsException<NotSupportedException>(() => dualWrite.Key = "key");
        }

        [TestMethod]
        public void Constructor_RequiresBothSenders()
        {
            var sender = (ITelemetryProvider)FakeSender.Create();

            Assert.AreEqual("legacySender", Assert.ThrowsException<ArgumentNullException>(() => new DualWriteTelemetry(null, sender)).ParamName);
            Assert.AreEqual("logsIngestionSender", Assert.ThrowsException<ArgumentNullException>(() => new DualWriteTelemetry(sender, null)).ParamName);
        }

        // ---- Helpers ----

        private static IEnumerable<MethodInfo> SenderMethods() =>
            typeof(ITelemetryProvider).GetMethods()
                .Where(method => !method.IsSpecialName && typeof(Task).IsAssignableFrom(method.ReturnType))
                .OrderBy(method => method.ToString(), StringComparer.Ordinal);

        /// <summary>
        /// Resolves a send method by signature and builds a distinct, non-default argument for each parameter,
        /// so the test can tell whether the wrapper passed the caller's arguments through unchanged.
        /// </summary>
        private (MethodInfo Method, object[] Args) Prepare(string signature)
        {
            MethodInfo method = SenderMethods().Single(candidate => candidate.ToString() == signature);

            if (method.IsGenericMethodDefinition)
            {
                method = method.MakeGenericMethod(typeof(double));
            }

            object[] args = method.GetParameters().Select(parameter => SampleArgument(parameter.ParameterType, parameter.Name)).ToArray();
            return (method, args);
        }

        private object SampleArgument(Type type, string name) => type switch
        {
            _ when type == typeof(string) => $"{name}-value",
            _ when type == typeof(Uri) => new Uri("fabric:/TestApp/TestService"),
            _ when type == typeof(Guid) => Guid.Parse("4f0c4ad5-2a8b-4d9a-9a4e-1b5f2f7c3e11"),
            _ when type == typeof(long) => 7L,
            _ when type == typeof(int) => 3,
            _ when type == typeof(double) => 1.5,
            _ when type == typeof(bool) => true,
            _ when type == typeof(DateTimeOffset) => new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero),
            _ when type == typeof(TimeSpan) => TimeSpan.FromSeconds(2),
            _ when type == typeof(HealthState) => HealthState.Warning,
            _ when type == typeof(CancellationToken) => cancellation.Token,
            _ when type == typeof(IDictionary<string, string>) => new Dictionary<string, string> { ["key"] = "value" },
            _ when !type.IsValueType => RuntimeHelpers.GetUninitializedObject(type),
            _ => throw new NotSupportedException($"Add a sample value for parameter '{name}' of type {type}.")
        };

        /// <summary>
        /// Records every call. Optionally fails every send (see <see cref="FailureKind"/>), returns <see cref="Result"/>
        /// from the named-metric method, or stays pending until <see cref="Gate"/> completes.
        /// </summary>
        public class FakeSender : DispatchProxy
        {
            private FailureKind? failure;

            public List<(string Method, object[] Args)> Calls { get; } = [];

            public bool Result { get; set; } = true;

            public TaskCompletionSource<bool> Gate { get; set; }

            public static FakeSender Create(FailureKind? failure = null)
            {
                var sender = (FakeSender)(object)Create<ITelemetryProvider, FakeSender>();
                sender.failure = failure;
                return sender;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (!typeof(Task).IsAssignableFrom(targetMethod.ReturnType))
                {
                    return null;
                }

                if (failure == FailureKind.ThrowsImmediately)
                {
                    throw new InvalidOperationException("Simulated sender failure.");
                }

                lock (Calls)
                {
                    Calls.Add((targetMethod.Name, args));
                }

                bool returnsBool = targetMethod.ReturnType == typeof(Task<bool>);

                if (failure == FailureKind.FailedTask)
                {
                    var error = new InvalidOperationException("Simulated sender failure.");
                    return returnsBool ? Task.FromException<bool>(error) : Task.FromException(error);
                }

                if (Gate != null)
                {
                    return Gate.Task;
                }

                return returnsBool ? Task.FromResult(Result) : Task.CompletedTask;
            }
        }
    }
}
