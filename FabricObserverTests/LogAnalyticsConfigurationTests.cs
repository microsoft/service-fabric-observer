// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClusterObserver;
using FabricObserver.Observers;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using ServiceFabric.Mocks;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;
using Logger = FabricObserver.Observers.Utilities.Logger;

namespace FabricObserverTests
{
    [TestClass]
    [DoNotParallelize]
    public class LogAnalyticsConfigurationTests
    {
        private const string WriteModeParameter = "LogAnalyticsWriteMode";
        private const string EndpointParameter = "LogAnalyticsIngestionEndpoint";
        private const string WorkspaceIdParameter = "LogAnalyticsWorkspaceId";
        private const string LegacySecret = "legacy-key-must-not-be-logged";
        private const string ValidEndpoint = "https://observer-test.westus3-1.ingest.monitor.azure.com";
        private const string ModeProperty = "LogAnalyticsConfiguration";
        private const string LegacyProperty = "LogAnalyticsLegacyConfiguration";
        private const string IngestionProperty = "LogAnalyticsIngestionConfiguration";
        private const string LegacySender = nameof(LogAnalyticsTelemetry);
        private const string NewSender = nameof(LogsIngestionTelemetry);
        private const string UploadProblem = "Log Analytics Logs Ingestion API upload failed: HTTP 403 (Forbidden).";
        private static readonly string[] HealthProperties = [ModeProperty, LegacyProperty, IngestionProperty];

        private readonly List<HealthReport> healthReports = [];
        private string testDirectory;
        private MemoryTarget logMessages;
        private LoggingConfiguration previousLogConfiguration;
        private StatelessServiceContext previousFabricContext;
        private StatelessServiceContext previousClusterContext;
        private bool previousFabricTelemetryEnabled;
        private bool previousClusterTelemetryEnabled;
        private object previousFabricSender;
        private object previousClusterSender;
        private StatelessServiceContext previousObserverContext;

        [TestInitialize]
        public void Initialize()
        {
            testDirectory = Path.Combine(Path.GetTempPath(), "ObserverConfigurationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);
            previousLogConfiguration = LogManager.Configuration;
            previousFabricContext = ObserverManager.FabricServiceContext;
            previousClusterContext = ClusterObserverManager.FabricServiceContext;
            previousFabricTelemetryEnabled = ObserverManager.TelemetryProviderEnabled;
            previousClusterTelemetryEnabled = ClusterObserverManager.TelemetryEnabled;
            previousFabricSender = GetSenderProperty(typeof(ObserverManager)).GetValue(null);
            previousClusterSender = GetSenderProperty(typeof(ClusterObserverManager)).GetValue(null);
            previousObserverContext = ObserverBase.FabricServiceContext;

            // Bad configuration intentionally leaves an existing sender in place, so start without one left by another test.
            GetSenderProperty(typeof(ObserverManager)).SetValue(null, null);
            GetSenderProperty(typeof(ClusterObserverManager)).SetValue(null, null);
            logMessages = new MemoryTarget("ConfigurationWarnings") { Layout = "${level}|${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, logMessages);
            LogManager.Configuration = logging;
        }

        [TestCleanup]
        public void Cleanup()
        {
            Logger.Flush();
            LogManager.Configuration = previousLogConfiguration;
            ObserverManager.FabricServiceContext = previousFabricContext;
            ClusterObserverManager.FabricServiceContext = previousClusterContext;
            ObserverManager.TelemetryProviderEnabled = previousFabricTelemetryEnabled;
            ClusterObserverManager.TelemetryEnabled = previousClusterTelemetryEnabled;
            GetSenderProperty(typeof(ObserverManager)).SetValue(null, previousFabricSender);
            GetSenderProperty(typeof(ClusterObserverManager)).SetValue(null, previousClusterSender);
            typeof(ObserverBase).GetProperty(nameof(ObserverBase.FabricServiceContext)).SetValue(null, previousObserverContext);
            Directory.Delete(testDirectory, recursive: true);
        }

        // ---- Write-mode selection ----

        [TestMethod]
        [DataRow(false, null)]
        [DataRow(true, null)]
        [DataRow(false, "")]
        [DataRow(true, "")]
        [DataRow(false, "LegacyOnly")]
        [DataRow(true, "LegacyOnly")]
        public async Task DefaultOrLegacyOnlyMode_UsesLegacySenderOnly(bool clusterObserver, string writeMode)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: writeMode);
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsTrue(IsManagerTelemetryEnabled(clusterObserver));
            AssertSenders(GetManagerSender(manager), LegacySender);
            AssertSenders(observer.Sender, LegacySender);
            Assert.IsTrue(observer.IsTelemetryEnabled);
            AssertNoActiveWarnings();
            Assert.IsFalse(WarningLogs().Any(), "A valid legacy configuration must not log warnings.");

            await RunObserverLoopAsync(manager, clusterObserver);
            Assert.AreEqual(1, observer.ObservationCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void LogsIngestionOnlyMode_UsesNewSenderAndIgnoresLegacySettings(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: "LogsIngestionOnly");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsTrue(IsManagerTelemetryEnabled(clusterObserver));
            AssertSenders(GetManagerSender(manager), NewSender);
            AssertSenders(observer.Sender, NewSender);
            AssertNoActiveWarnings();
        }

        [TestMethod]
        [DataRow(false, "DualWrite")]
        [DataRow(true, "DualWrite")]
        [DataRow(false, "dualwrite")]
        [DataRow(true, "dualwrite")]
        public void DualWriteMode_UsesBothSenders(bool clusterObserver, string writeMode)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: writeMode);
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsTrue(IsManagerTelemetryEnabled(clusterObserver));
            AssertSenders(GetManagerSender(manager), LegacySender, NewSender);
            AssertSenders(observer.Sender, LegacySender, NewSender);
            AssertNoActiveWarnings();
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UnknownWriteMode_WarnsWithoutSenderOrStoppingMonitoring(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: "BothApis");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsFalse(IsManagerTelemetryEnabled(clusterObserver));
            AssertSenders(GetManagerSender(manager));
            AssertSenders(observer.Sender);
            Assert.IsFalse(observer.IsTelemetryEnabled);
            AssertWarning(ModeProperty, context, WriteModeParameter);
            Assert.IsTrue(observer.IsEnabled, "Monitoring must remain enabled.");

            await RunObserverLoopAsync(manager, clusterObserver);
            Assert.AreEqual(1, observer.ObservationCount);
        }

        // ---- Per-API settings validation ----

        [TestMethod]
        [DataRow(false, "")]
        [DataRow(true, "")]
        [DataRow(false, null)]
        [DataRow(true, null)]
        [DataRow(false, "  ")]
        [DataRow(true, "  ")]
        public async Task LogsIngestionOnly_MissingEndpoint_WarnsWithoutSenderOrStoppingMonitoring(bool clusterObserver, string endpoint)
        {
            var context = CreateContext(clusterObserver, endpoint, writeMode: "LogsIngestionOnly");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsFalse(IsManagerTelemetryEnabled(clusterObserver), "Missing endpoint must disable the sender, even when legacy credentials exist.");
            AssertSenders(GetManagerSender(manager));
            AssertSenders(observer.Sender);
            Assert.IsFalse(observer.IsTelemetryEnabled);
            Assert.IsTrue(observer.IsEnabled, "Monitoring must remain enabled.");
            AssertWarning(IngestionProperty, context, EndpointParameter);
            Assert.AreNotEqual(HealthState.Warning, Latest(LegacyProperty)?.State, "Legacy settings are not selected and must not warn.");

            await RunObserverLoopAsync(manager, clusterObserver);
            Assert.AreEqual(1, observer.ObservationCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void LegacyOnly_MissingLegacySettings_WarnsWithoutSender(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: "LegacyOnly", legacyConfigured: false);
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsFalse(IsManagerTelemetryEnabled(clusterObserver));
            AssertSenders(GetManagerSender(manager));
            AssertSenders(observer.Sender);
            AssertWarning(LegacyProperty, context, WorkspaceIdParameter);
            Assert.AreNotEqual(HealthState.Warning, Latest(IngestionProperty)?.State, "New API settings are not selected and must not warn.");
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DualWrite_InvalidNewSettings_KeepsLegacySender(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, string.Empty, writeMode: "DualWrite");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsTrue(IsManagerTelemetryEnabled(clusterObserver));
            AssertSenders(GetManagerSender(manager), LegacySender);
            AssertSenders(observer.Sender, LegacySender);
            AssertWarning(IngestionProperty, context, EndpointParameter);
            Assert.AreNotEqual(HealthState.Warning, Latest(LegacyProperty)?.State);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DualWrite_InvalidLegacySettings_KeepsNewSender(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: "DualWrite", legacyConfigured: false);
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsTrue(IsManagerTelemetryEnabled(clusterObserver));
            AssertSenders(GetManagerSender(manager), NewSender);
            AssertSenders(observer.Sender, NewSender);
            AssertWarning(LegacyProperty, context, WorkspaceIdParameter);
            Assert.AreNotEqual(HealthState.Warning, Latest(IngestionProperty)?.State);
        }

        // ---- Startup and configuration updates ----

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DisabledTelemetry_DoesNotWarnAndMonitoringContinues(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, string.Empty, telemetryEnabled: false, writeMode: "LogsIngestionOnly");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsFalse(IsManagerTelemetryEnabled(clusterObserver));
            AssertSenders(GetManagerSender(manager));
            AssertSenders(observer.Sender);
            AssertAllWarningsCleared("Startup must clear any warning left by a previous instance.");
            Assert.AreEqual(0, logMessages.Logs.Count);
            await RunObserverLoopAsync(manager, clusterObserver);
            Assert.AreEqual(1, observer.ObservationCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ApplicationInsights_IgnoresLogAnalyticsSettings(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, string.Empty, provider: "AzureApplicationInsights", writeMode: "BothApis");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);

            Assert.IsTrue(IsManagerTelemetryEnabled(clusterObserver));
            Assert.IsInstanceOfType<AppInsightsTelemetry>(GetManagerSender(manager));
            Assert.IsInstanceOfType<AppInsightsTelemetry>(observer.Sender);
            AssertAllWarningsCleared("Startup must clear any warning left by a previous instance.");
            Assert.IsFalse(WarningLogs().Any(), "Log Analytics settings must not be validated when Application Insights is selected.");
            await RunObserverLoopAsync(manager, clusterObserver);
            Assert.AreEqual(1, observer.ObservationCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ConfigurationUpdateToMissingEndpoint_WarnsAndDisablesTelemetry(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, string.Empty, provider: "AzureApplicationInsights");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);
            Assert.IsNotNull(GetManagerSender(manager));
            Assert.IsNotNull(observer.Sender);

            var updatedContext = CreateContext(clusterObserver, string.Empty, writeMode: "LogsIngestionOnly");
            ApplyConfiguration(manager, observer, updatedContext);

            Assert.IsFalse(IsManagerTelemetryEnabled(clusterObserver));
            Assert.IsFalse(observer.IsTelemetryEnabled);
            AssertWarning(IngestionProperty, updatedContext, EndpointParameter);
            await RunObserverLoopAsync(manager, clusterObserver);
            Assert.AreEqual(1, observer.ObservationCount);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ConfigurationUpdate_ChangingWriteMode_RebuildsSenders(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: "LegacyOnly");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);
            AssertSenders(GetManagerSender(manager), LegacySender);

            ApplyConfiguration(manager, observer, CreateContext(clusterObserver, ValidEndpoint, writeMode: "DualWrite"));
            AssertSenders(GetManagerSender(manager), LegacySender, NewSender);
            AssertSenders(observer.Sender, LegacySender, NewSender);

            ApplyConfiguration(manager, observer, CreateContext(clusterObserver, ValidEndpoint, writeMode: "LogsIngestionOnly"));
            AssertSenders(GetManagerSender(manager), NewSender);
            AssertSenders(observer.Sender, NewSender);

            ApplyConfiguration(manager, observer, CreateContext(clusterObserver, ValidEndpoint, writeMode: "LegacyOnly"));
            AssertSenders(GetManagerSender(manager), LegacySender);
            AssertSenders(observer.Sender, LegacySender);
            AssertNoActiveWarnings();
        }

        [TestMethod]
        [DataRow(false, true)]
        [DataRow(true, true)]
        [DataRow(false, false)]
        [DataRow(true, false)]
        public void DualWrite_ConfigurationUpdateBreakingOneApi_KeepsOnlyValidSender(bool clusterObserver, bool breakNewApi)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: "DualWrite");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);
            AssertSenders(GetManagerSender(manager), LegacySender, NewSender);

            var updatedContext = breakNewApi
                ? CreateContext(clusterObserver, string.Empty, writeMode: "DualWrite")
                : CreateContext(clusterObserver, ValidEndpoint, writeMode: "DualWrite", legacyConfigured: false);
            ApplyConfiguration(manager, observer, updatedContext);

            // Telemetry stays on, so a reused dual-write sender would keep sending through the broken API with its old settings.
            string remaining = breakNewApi ? LegacySender : NewSender;
            Assert.IsTrue(IsManagerTelemetryEnabled(clusterObserver));
            Assert.IsTrue(observer.IsTelemetryEnabled);
            AssertSenders(GetManagerSender(manager), remaining);
            AssertSenders(observer.Sender, remaining);
            AssertWarning(breakNewApi ? IngestionProperty : LegacyProperty, updatedContext, breakNewApi ? EndpointParameter : WorkspaceIdParameter);
            Assert.AreNotEqual(HealthState.Warning, Latest(breakNewApi ? LegacyProperty : IngestionProperty)?.State);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SwitchingToLegacyOnly_ClearsIngestionWarning(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, string.Empty, writeMode: "LogsIngestionOnly");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);
            Assert.AreEqual(HealthState.Warning, Latest(IngestionProperty)?.State);

            ApplyConfiguration(manager, observer, CreateContext(clusterObserver, string.Empty, writeMode: "LegacyOnly"));

            Assert.AreEqual(HealthState.Ok, Latest(IngestionProperty)?.State, "A warning for an API that is no longer selected must be cleared.");
            AssertSenders(GetManagerSender(manager), LegacySender);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DisablingTelemetry_ClearsConfigurationWarning(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, string.Empty, writeMode: "LogsIngestionOnly");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);
            HealthReport warning = Latest(IngestionProperty);
            Assert.AreEqual(HealthState.Warning, warning?.State);

            ApplyConfiguration(manager, observer, CreateContext(clusterObserver, string.Empty, telemetryEnabled: false, writeMode: "LogsIngestionOnly"));

            HealthReport cleared = Latest(IngestionProperty);
            Assert.AreEqual(HealthState.Ok, cleared.State);
            Assert.AreEqual(warning.SourceId, cleared.SourceId);
            AssertSenders(GetManagerSender(manager));
            AssertSenders(observer.Sender);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RestartWithTelemetryDisabled_ClearsPreviousEndpointWarning(bool clusterObserver)
        {
            var context = CreateContext(clusterObserver, string.Empty, writeMode: "LogsIngestionOnly");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            CreateManager(clusterObserver, services, context);
            HealthReport warning = Latest(IngestionProperty);
            Assert.AreEqual(HealthState.Warning, warning?.State);

            var disabledContext = CreateContext(clusterObserver, string.Empty, telemetryEnabled: false, writeMode: "LogsIngestionOnly");
            using var restartedObserver = new ConfigurationProbeObserver(disabledContext);
            using var restartedServices = CreateServices(disabledContext, restartedObserver);
            CreateManager(clusterObserver, restartedServices, disabledContext);

            HealthReport cleared = Latest(IngestionProperty);
            Assert.AreEqual(HealthState.Ok, cleared.State, "A persistent configuration warning must clear even when disabling telemetry requires a process restart.");
            Assert.AreEqual(warning.SourceId, cleared.SourceId);
        }

        [TestMethod]
        [DataRow(false, "", true)]
        [DataRow(true, "", true)]
        [DataRow(false, "InvalidProvider", true)]
        [DataRow(true, "InvalidProvider", true)]
        [DataRow(false, "AzureApplicationInsights", true)]
        [DataRow(true, "AzureApplicationInsights", true)]
        [DataRow(false, "AzureApplicationInsights", false)]
        [DataRow(true, "AzureApplicationInsights", false)]
        public void SwitchingAwayFromLogAnalytics_ClearsEndpointWarning(bool clusterObserver, string provider, bool appInsightsConfigured)
        {
            var context = CreateContext(clusterObserver, string.Empty, writeMode: "LogsIngestionOnly");
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);
            HealthReport warning = Latest(IngestionProperty);
            Assert.AreEqual(HealthState.Warning, warning?.State);

            ApplyConfiguration(
                manager,
                observer,
                CreateContext(clusterObserver, string.Empty, provider: provider, writeMode: "LogsIngestionOnly", appInsightsConfigured: appInsightsConfigured));

            HealthReport cleared = Latest(IngestionProperty);
            Assert.AreEqual(HealthState.Ok, cleared.State, "A Log Analytics warning must not remain after that provider is deselected.");
            Assert.AreEqual(warning.SourceId, cleared.SourceId);
            Assert.AreEqual(provider == "AzureApplicationInsights" && appInsightsConfigured, IsManagerTelemetryEnabled(clusterObserver));
        }

        // ---- Upload status from the Logs Ingestion sender ----

        [TestMethod]
        [DataRow(false, "LogsIngestionOnly")]
        [DataRow(true, "LogsIngestionOnly")]
        [DataRow(false, "DualWrite")]
        [DataRow(true, "DualWrite")]
        public void CurrentSenderUploadFailure_ReportsThenClearsIngestionWarning(bool clusterObserver, string writeMode)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: writeMode);
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);
            Action<string> reportStatus = GetIngestionStatusCallback(GetManagerSender(manager));

            reportStatus(UploadProblem);

            AssertWarning(IngestionProperty, context, "403");

            reportStatus(null);

            Assert.AreEqual(HealthState.Ok, Latest(IngestionProperty)?.State, "A successful upload must clear the upload warning.");
        }

        [TestMethod]
        [DataRow(false, "DualWrite", "LegacyOnly")]
        [DataRow(true, "DualWrite", "LegacyOnly")]
        [DataRow(false, "LogsIngestionOnly", "LegacyOnly")]
        [DataRow(true, "LogsIngestionOnly", "LegacyOnly")]
        [DataRow(false, "LogsIngestionOnly", "LogsIngestionOnly")]
        [DataRow(true, "LogsIngestionOnly", "LogsIngestionOnly")]
        public void ReplacedSenderLateUploadFailure_IsNotReported(bool clusterObserver, string writeMode, string newWriteMode)
        {
            var context = CreateContext(clusterObserver, ValidEndpoint, writeMode: writeMode);
            using var observer = new ConfigurationProbeObserver(context);
            using var services = CreateServices(context, observer);
            object manager = CreateManager(clusterObserver, services, context);
            Action<string> replacedSenderStatus = GetIngestionStatusCallback(GetManagerSender(manager));

            ApplyConfiguration(manager, observer, CreateContext(clusterObserver, ValidEndpoint, writeMode: newWriteMode));
            Assert.AreEqual(HealthState.Ok, Latest(IngestionProperty)?.State);
            int reportCount = healthReports.Count;

            // An upload started before the settings change fails after it.
            replacedSenderStatus(UploadProblem);

            Assert.AreEqual(reportCount, healthReports.Count, "A replaced sender must not report health.");
            Assert.AreEqual(HealthState.Ok, Latest(IngestionProperty).State);
            Assert.IsFalse(WarningLogs().Any(message => message.Contains("403")), "A replaced sender must not log a warning.");
        }

        // ---- Application packaging ----

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void LogAnalyticsParameters_AreWiredThroughApplicationManifest(bool clusterObserver)
        {
            string application = clusterObserver ? "ClusterObserver" : "FabricObserver";
            string root = Path.Combine(AppContext.BaseDirectory, "ConfigurationContracts", application);
            XNamespace ns = "http://schemas.microsoft.com/2011/01/fabric";
            var manifest = XDocument.Load(Path.Combine(root, "ApplicationManifest.xml"));
            var settings = XDocument.Load(Path.Combine(root, "Settings.xml"));
            var expectedDefaults = new Dictionary<string, string>
            {
                [WriteModeParameter] = "LegacyOnly",
                [EndpointParameter] = string.Empty,
                ["LogAnalyticsDcrImmutableId"] = string.Empty,
                ["LogAnalyticsStreamName"] = string.Empty,
                ["LogAnalyticsManagedIdentityClientId"] = string.Empty
            };
            foreach (var (name, defaultValue) in expectedDefaults)
            {
                var parameter = manifest.Root.Element(ns + "Parameters").Elements(ns + "Parameter")
                    .SingleOrDefault(element => (string)element.Attribute("Name") == name);
                Assert.IsNotNull(parameter, $"Missing application parameter {name} in {application}.");
                Assert.AreEqual(defaultValue, (string)parameter.Attribute("DefaultValue"), $"Unexpected default for {name} in {application}.");
                var settingsParameter = settings.Descendants(ns + "Section")
                    .Single(element => (string)element.Attribute("Name") == "ObserverManagerConfiguration")
                    .Elements(ns + "Parameter").SingleOrDefault(element => (string)element.Attribute("Name") == name);
                Assert.IsNotNull(settingsParameter, $"Missing Settings.xml parameter {name} in {application}.");
                Assert.AreEqual(string.Empty, (string)settingsParameter.Attribute("Value"));
                Assert.AreEqual("true", (string)settingsParameter.Attribute("MustOverride"));
                var configOverride = manifest.Descendants(ns + "Section")
                    .Single(element => (string)element.Attribute("Name") == "ObserverManagerConfiguration")
                    .Elements(ns + "Parameter").SingleOrDefault(element => (string)element.Attribute("Name") == name);
                Assert.IsNotNull(configOverride, $"Missing ConfigOverride for {name} in {application}.");
                Assert.AreEqual($"[{name}]", (string)configOverride.Attribute("Value"));
            }
        }

        // ---- Helpers ----

        private StatelessServiceContext CreateContext(
            bool clusterObserver,
            string endpoint,
            bool telemetryEnabled = true,
            string provider = "AzureLogAnalytics",
            string writeMode = null,
            bool legacyConfigured = true,
            bool appInsightsConfigured = true)
        {
            var parameters = new Dictionary<string, string>
            {
                ["EnableTelemetryProvider"] = telemetryEnabled.ToString(),
                ["TelemetryProvider"] = provider,
                [WriteModeParameter] = writeMode,
                [EndpointParameter] = endpoint,
                ["LogAnalyticsDcrImmutableId"] = "dcr-0123456789abcdef0123456789abcdef",
                ["LogAnalyticsStreamName"] = "Custom-Observer",
                ["LogAnalyticsManagedIdentityClientId"] = "9bf519ae-ac98-4331-a229-396fd165f35c",
                [WorkspaceIdParameter] = legacyConfigured ? "8c0901c7-5d68-4822-b623-1340af7b31cc" : string.Empty,
                ["LogAnalyticsSharedKey"] = legacyConfigured ? LegacySecret : string.Empty,
                ["LogAnalyticsLogType"] = "ObserverConfigurationTest",
                ["ObserverLogPath"] = testDirectory,
                ["EnableVerboseLogging"] = "false",
                ["EnableETWProvider"] = "false",
                ["ObserverExecutionTimeout"] = "5",
                ["AppInsightsConnectionString"] = appInsightsConfigured ? "InstrumentationKey=5fb51623-b334-4e41-b7da-06b74e8bff5e" : string.Empty
            };
            var managerSection = MockConfigurationPackage.CreateConfigurationSection("ObserverManagerConfiguration");
            foreach (var parameter in parameters)
            {
                if (parameter.Value != null)
                {
                    managerSection.Parameters.Add(MockConfigurationPackage.CreateConfigurationSectionParameters(parameter.Key, parameter.Value));
                }
            }

            var observerSection = MockConfigurationPackage.CreateConfigurationSection("ConfigurationProbeObserverConfiguration");
            observerSection.Parameters.Add(MockConfigurationPackage.CreateConfigurationSectionParameters("IsEnabled", "true"));
            observerSection.Parameters.Add(MockConfigurationPackage.CreateConfigurationSectionParameters("EnableTelemetry", "true"));
            var settings = MockConfigurationPackage.CreateConfigurationSettings(
                new MockConfigurationPackage.ConfigurationSectionCollection { managerSection, observerSection });
            ConfigurationPackage package = MockConfigurationPackage.CreateConfigurationPackage(settings, testDirectory);
            string applicationName = clusterObserver ? "fabric:/ClusterObserver" : "fabric:/FabricObserver";
            var activation = new MockCodePackageActivationContext(
                applicationName, "ObserverType", "Code", "1.0.0", Guid.NewGuid().ToString(),
                testDirectory, testDirectory, testDirectory, "ServiceManifest", "1.0.0")
            {
                ConfigurationPackage = package
            };
            return new StatelessServiceContext(
                new NodeContext("TestNode", new NodeId(0, 1), 0, "TestNodeType", "localhost"),
                activation, "ObserverServiceType", new Uri(applicationName + "/ObserverService"),
                null, Guid.NewGuid(), 1);
        }

        private static ServiceProvider CreateServices(StatelessServiceContext context, ObserverBase observer)
        {
            return new ServiceCollection()
                .AddSingleton(context)
                .AddSingleton(observer)
                .BuildServiceProvider();
        }

        private object CreateManager(bool clusterObserver, ServiceProvider services, StatelessServiceContext context)
        {
            if (clusterObserver)
            {
                return new ClusterObserverManager(services, CancellationToken.None, healthReports.Add);
            }

            ObserverManager.FabricServiceContext = context;
            return new ObserverManager(services, CancellationToken.None, healthReports.Add);
        }

        private static bool IsManagerTelemetryEnabled(bool clusterObserver)
        {
            return clusterObserver ? ClusterObserverManager.TelemetryEnabled : ObserverManager.TelemetryProviderEnabled;
        }

        private static object GetManagerSender(object manager)
        {
            return GetSenderProperty(manager.GetType()).GetValue(null);
        }

        private static PropertyInfo GetSenderProperty(Type managerType) =>
            managerType.GetProperty("TelemetryClient", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>
        /// Lists the concrete sender types behind a telemetry client. A dual-write wrapper is identified by
        /// exposing its inner senders as <see cref="ITelemetryProvider"/> properties, so tests do not depend on its name.
        /// </summary>
        private static string[] SenderKinds(object sender)
        {
            if (sender == null)
            {
                return [];
            }

            object[] inner = sender.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(property => typeof(ITelemetryProvider).IsAssignableFrom(property.PropertyType) && property.GetIndexParameters().Length == 0)
                .Select(property => property.GetValue(sender))
                .Where(value => value != null)
                .ToArray();

            return inner.Length == 0
                ? [sender.GetType().Name]
                : inner.SelectMany(SenderKinds).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// Returns the status callback the manager gave its Logs Ingestion sender; invoking it simulates the sender reporting
        /// an upload failure (problem text) or a recovery (null).
        /// </summary>
        private static Action<string> GetIngestionStatusCallback(object sender)
        {
            LogsIngestionTelemetry ingestionSender = LogAnalyticsConfiguration.GetIngestionSender((ITelemetryProvider)sender);
            Assert.IsNotNull(ingestionSender, "Expected a Logs Ingestion sender.");
            var callback = (Action<string>)typeof(LogsIngestionTelemetry)
                .GetField("statusChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(ingestionSender);
            Assert.IsNotNull(callback, "The manager must give its Logs Ingestion sender a status callback.");
            return callback;
        }

        private static void AssertSenders(object sender, params string[] expected)
        {
            string[] expectedSorted = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            string[] actual = SenderKinds(sender);
            CollectionAssert.AreEqual(
                expectedSorted,
                actual,
                $"Expected senders [{string.Join(", ", expectedSorted)}] but found [{string.Join(", ", actual)}].");
        }

        private HealthReport Latest(string property) => healthReports.LastOrDefault(report => report.Property == property);

        private void AssertWarning(string property, StatelessServiceContext context, string mentionedParameter)
        {
            HealthReport warning = Latest(property);
            Assert.IsNotNull(warning, $"Expected a {property} health report.");
            Assert.AreEqual(HealthState.Warning, warning.State);
            Assert.AreEqual(context.ServiceName, warning.ServiceName);
            Assert.AreEqual(context.NodeContext.NodeName, warning.NodeName);
            Assert.AreEqual(EntityType.Service, warning.EntityType);
            Assert.AreEqual(TimeSpan.MaxValue, warning.HealthReportTimeToLive, "An unresolved configuration warning must not expire while it remains unresolved.");
            StringAssert.Contains(warning.SourceId, context.NodeContext.NodeName);
            StringAssert.Contains(warning.HealthMessage, mentionedParameter);
            Assert.IsTrue(WarningLogs().Any(message => message.Contains(mentionedParameter)), $"Expected a local warning log mentioning {mentionedParameter}.");
            Assert.IsFalse(logMessages.Logs.Any(message => message.Contains(LegacySecret)), "The shared key must never be logged.");
            Assert.IsFalse(warning.HealthMessage.Contains(LegacySecret), "The shared key must never be reported.");
        }

        private void AssertNoActiveWarnings()
        {
            foreach (string property in HealthProperties)
            {
                Assert.AreNotEqual(HealthState.Warning, Latest(property)?.State, $"Unexpected active {property} warning.");
            }
        }

        private void AssertAllWarningsCleared(string message)
        {
            foreach (string property in HealthProperties)
            {
                Assert.AreEqual(HealthState.Ok, Latest(property)?.State, $"{message} Property: {property}.");
            }
        }

        private IEnumerable<string> WarningLogs() => logMessages.Logs.Where(message => message.StartsWith("Warn|"));

        private static void ApplyConfiguration(object manager, ObserverBase observer, StatelessServiceContext context)
        {
            ConfigurationPackage package = context.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            manager.GetType().GetMethod("SetPropertiesFromConfigurationParameters", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, [package.Settings]);
            observer.ConfigPackage = package;
            observer.ConfigurationSettings = new ConfigSettings(package.Settings, "ConfigurationProbeObserverConfiguration");
            observer.InitializeObserverLoggingInfra(isConfigUpdate: true);
        }

        private static Task RunObserverLoopAsync(object manager, bool clusterObserver)
        {
            string method = clusterObserver ? "RunAsync" : "RunObserversAsync";
            return (Task)manager.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
        }

        private sealed class ConfigurationProbeObserver(StatelessServiceContext context) : ObserverBase(null, context)
        {
            public ITelemetryProvider Sender => TelemetryClient;

            public int ObservationCount { get; private set; }

            public override Task ObserveAsync(CancellationToken token)
            {
                ObservationCount++;
                return Task.CompletedTask;
            }

            public override Task ReportAsync(CancellationToken token) => Task.CompletedTask;
        }
    }
}
