// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using ClusterObserver;
using FabricObserver.Observers;
using FabricObserver.Observers.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

/***PLEASE RUN ALL OF THESE TESTS ON YOUR LOCAL DEV MACHINE WITH A RUNNING SF CLUSTER BEFORE SUBMITTING A PULL REQUEST***/

namespace FabricObserverTests
{
    [TestClass]
    public class ObserverTest
    {
        private const string NodeName = "_Node_0";
        private static readonly Uri ServiceName = new Uri("fabric:/app/service");
        private static readonly bool isSFRuntimePresentOnTestMachine = IsLocalSFRuntimePresent();
        private static readonly CancellationToken token = new CancellationToken();
        private static readonly FabricClient fabricClient = new FabricClient();
        private static readonly ICodePackageActivationContext CodePackageContext
                                   = new MockCodePackageActivationContext(
                                               ServiceName.AbsoluteUri,
                                               "applicationType",
                                               "Code",
                                               "1.0.0.0",
                                               Guid.NewGuid().ToString(),
                                               @"C:\Log",
                                               @"C:\Temp",
                                               @"C:\Work",
                                               "ServiceManifest",
                                               "1.0.0.0");

        private static readonly StatelessServiceContext context
                                    = new StatelessServiceContext(
                                                new NodeContext(NodeName, new NodeId(0, 1), 0, "NodeType0", "TEST.MACHINE"),
                                                CodePackageContext,
                                                "FabricObserver.FabricObserverType",
                                                ServiceName,
                                                null,
                                                Guid.NewGuid(),
                                                long.MaxValue);
        /* Helpers */

        private static bool IsLocalSFRuntimePresent()
        {
            try
            {
                var ps = Process.GetProcessesByName("Fabric");
                return ps.Length != 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static async Task CleanupTestHealthReportsAsync(ObserverBase obs = null)
        {
            // Clear any existing user app, node or fabric:/System app Test Health Reports.
            try
            {
                var healthReport = new HealthReport
                {
                    Code = FOErrorWarningCodes.Ok,
                    HealthMessage = "Clearing existing Error/Warning Test Health Reports.",
                    State = HealthState.Ok,
                    ReportType = HealthReportType.Application,
                    NodeName = "_Node_0"
                };

                var logger = new Logger("TestCleanUp");
                var client = new FabricClient();

                // App reports
                if (obs is { HasActiveFabricErrorOrWarning: true } && obs.ObserverName != ObserverConstants.NetworkObserverName)
                {
                    if (obs.AppNames.Count > 0 && obs.AppNames.All(a => !string.IsNullOrWhiteSpace(a) && a.Contains("fabric:/")))
                    {
                        foreach (var app in obs.AppNames)
                        {
                            try
                            {
                                var appName = new Uri(app);
                                var appHealth = await client.HealthManager.GetApplicationHealthAsync(appName).ConfigureAwait(false);
                                var unhealthyEvents = appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName)
                                                            && (s.HealthInformation.HealthState == HealthState.Error || s.HealthInformation.HealthState == HealthState.Warning));

                                if (unhealthyEvents == null)
                                {
                                    continue;
                                }

                                foreach (HealthEvent evt in unhealthyEvents)
                                {
                                    healthReport.AppName = appName;
                                    healthReport.Property = evt.HealthInformation.Property;
                                    healthReport.SourceId = evt.HealthInformation.SourceId;

                                    var healthReporter = new ObserverHealthReporter(logger, client);
                                    healthReporter.ReportHealthToServiceFabric(healthReport);

                                    Thread.Sleep(50);
                                }
                            }
                            catch (FabricException)
                            {

                            }
                        }
                    }
                }

                // System reports
                var sysAppHealth = await client.HealthManager.GetApplicationHealthAsync(new Uri(ObserverConstants.SystemAppName)).ConfigureAwait(false);

                if (sysAppHealth != null)
                {
                    foreach (var evt in sysAppHealth.HealthEvents?.Where(
                                            s => s.HealthInformation.SourceId.Contains("FabricSystemObserver")
                                                && (s.HealthInformation.HealthState == HealthState.Error
                                                    || s.HealthInformation.HealthState == HealthState.Warning)))
                    {
                        healthReport.AppName = new Uri(ObserverConstants.SystemAppName);
                        healthReport.Property = evt.HealthInformation.Property;
                        healthReport.SourceId = evt.HealthInformation.SourceId;

                        var healthReporter = new ObserverHealthReporter(logger, client);
                        healthReporter.ReportHealthToServiceFabric(healthReport);

                        Thread.Sleep(50);
                    }
                }

                // Node reports
                var nodeHealth = await client.HealthManager.GetNodeHealthAsync(context.NodeContext.NodeName).ConfigureAwait(false);

                var unhealthyFONodeEvents = nodeHealth.HealthEvents?.Where(
                                                                s => s.HealthInformation.SourceId.Contains("NodeObserver")
                                                                    || s.HealthInformation.SourceId.Contains("DiskObserver")
                                                                    && (s.HealthInformation.HealthState == HealthState.Error
                                                                        || s.HealthInformation.HealthState == HealthState.Warning));

                healthReport.ReportType = HealthReportType.Node;

                if (unhealthyFONodeEvents != null)
                {
                    foreach (HealthEvent evt in unhealthyFONodeEvents)
                    {
                        healthReport.Property = evt.HealthInformation.Property;
                        healthReport.SourceId = evt.HealthInformation.SourceId;

                        var healthReporter = new ObserverHealthReporter(logger, client);
                        healthReporter.ReportHealthToServiceFabric(healthReport);

                        Thread.Sleep(50);
                    }
                }
            }
            catch (FabricException)
            {

            }
        }

        private static bool InstallCerts()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // We cannot install certs into local machine store on Linux
                return false;
            }

            var validCert = new X509Certificate2("MyValidCert.p12");
            var expiredCert = new X509Certificate2("MyExpiredCert.p12");

            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            try
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(validCert);
                store.Add(expiredCert);
                return true;
            }
            catch (CryptographicException ex) when (ex.HResult == 5) // access denied
            {
                return false;
            }
        }

        private static void UnInstallCerts()
        {
            var validCert = new X509Certificate2("MyValidCert.p12");
            var expiredCert = new X509Certificate2("MyExpiredCert.p12");

            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(validCert);
            store.Remove(expiredCert);
        }

        [ClassCleanup]
        public static async Task TestClassCleanupAsync()
        {
            // Remove any files generated.
            try
            {
                var outputFolder = Path.Combine(Environment.CurrentDirectory, "observer_logs");

                if (Directory.Exists(outputFolder))
                {
                    Directory.Delete(outputFolder, true);
                }
            }
            catch (IOException)
            {

            }

            await CleanupTestHealthReportsAsync();
        }

        /* End Helpers */

        [TestMethod]
        public void AppObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.AppObserverName);
        }

        [TestMethod]
        public void CertificateObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new CertificateObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.CertificateObserverName);
        }

        [TestMethod]
        public void ContainerObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new ContainerObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.ContainerObserverName);
        }

        [TestMethod]
        public void DiskObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new DiskObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.DiskObserverName);
        }

        [TestMethod]
        public void FabricSystemObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new FabricSystemObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.FabricSystemObserverName);
        }

        [TestMethod]
        public void NetworkObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new NetworkObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null); 
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NetworkObserverName);
        }

        [TestMethod]
        public void NodeObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NodeObserverName);
        }

        [TestMethod]
        public void OSObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new OSObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.OSObserverName);
        }

        [TestMethod]
        public void SFConfigurationObserver_Constructor_Test()
        {
            using var client = new FabricClient();
            
            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new SFConfigurationObserver(client, context);

            // These are set in derived ObserverBase.
            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.SFConfigurationObserverName);
        }


        /****** NOTE: These tests below do NOT work without a running local SF cluster
                or in an Azure DevOps VSTest Pipeline ******/

        [TestMethod]
        public async Task AppObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
            
            await CleanupTestHealthReportsAsync(obs).ConfigureAwait(true);
        }

        [TestMethod]
        public async Task AppObserver_ObserveAsync_OldConfigStyle_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(client, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.oldstyle.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            await CleanupTestHealthReportsAsync(obs).ConfigureAwait(true);
        }

        [TestMethod]
        public async Task ContainerObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new ContainerObserver(client, context)
            {
                ConfigurationFilePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "ContainerObserver.config.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            await CleanupTestHealthReportsAsync(obs).ConfigureAwait(true);
        }

        [TestMethod]
        public async Task ClusterObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            var client = new FabricClient();

            ClusterObserverManager.FabricServiceContext = context;
            ClusterObserverManager.FabricClientInstance = client;
            ClusterObserverManager.EtwEnabled = true;
            ClusterObserverManager.TelemetryEnabled = true;

            // On a one-node cluster like your dev machine, pass true for ignoreDefaultQueryTimeout otherwise each FabricClient query will take 2 minutes 
            // to timeout in ClusterObserver.
            var obs = new ClusterObserver.ClusterObserver(null, ignoreDefaultQueryTimeout: true)
            {
                ConfigSettings = new ClusterObserver.Utilities.ConfigSettings(null, null)
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task CertificateObserver_validCerts()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!InstallCerts())
            {
                Assert.Inconclusive("This test can only be run on Windows as an admin.");
            }

            try
            {
                var startDateTime = DateTime.Now;
                using var client = new FabricClient();

                ObserverManager.FabricServiceContext = context;
                ObserverManager.FabricClientInstance = client;
                ObserverManager.TelemetryEnabled = false;
                ObserverManager.EtwEnabled = false;

                using var obs = new CertificateObserver(client, context);

                var commonNamesToObserve = new List<string>
                {
                    "MyValidCert" // Common name of valid cert
                };

                var thumbprintsToObserve = new List<string>
                {
                    "1fda27a2923505e47de37db48ff685b049642c25" // thumbprint of valid cert
                };

                obs.DaysUntilAppExpireWarningThreshold = 14;
                obs.DaysUntilClusterExpireWarningThreshold = 14;
                obs.AppCertificateCommonNamesToObserve = commonNamesToObserve;
                obs.AppCertificateThumbprintsToObserve = thumbprintsToObserve;
                obs.SecurityConfiguration = new SecurityConfiguration
                {
                    SecurityType = SecurityType.None,
                    ClusterCertThumbprintOrCommonName = string.Empty,
                    ClusterCertSecondaryThumbprint = string.Empty
                };

                await obs.ObserveAsync(token);

                // observer ran to completion with no errors.
                Assert.IsTrue(obs.LastRunDateTime > startDateTime);

                // observer detected no error conditions.
                Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

                // observer did not have any internal errors during run.
                Assert.IsFalse(obs.IsUnhealthy);
            }
            finally
            {
                UnInstallCerts();
            }
        }

        [TestMethod]
        public async Task CertificateObserver_expiredAndexpiringCerts()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new CertificateObserver(client, context);

            using var obsMgr = new ObserverManager(obs, client)
            {
                ApplicationName = "fabric:/TestApp0"
            };

            var commonNamesToObserve = new List<string>
            {
                "MyExpiredCert" // common name of expired cert
            };

            var thumbprintsToObserve = new List<string>
            {
                "1fda27a2923505e47de37db48ff685b049642c25" // thumbprint of valid cert, but warning threshold causes expiring
            };

            obs.DaysUntilAppExpireWarningThreshold = int.MaxValue;
            obs.DaysUntilClusterExpireWarningThreshold = 14;
            obs.AppCertificateCommonNamesToObserve = commonNamesToObserve;
            obs.AppCertificateThumbprintsToObserve = thumbprintsToObserve;
            obs.SecurityConfiguration = new SecurityConfiguration
            {
                SecurityType = SecurityType.None,
                ClusterCertThumbprintOrCommonName = string.Empty,
                ClusterCertSecondaryThumbprint = string.Empty
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected error conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            // stop clears health warning
            await obsMgr.StopObserversAsync().ConfigureAwait(true);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task NodeObserver_Integer_Greater_Than_100_CPU_Warn_Threshold_No_Fail()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(client, context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = 10000
            };

            await obs.ObserveAsync(token);

            // observer ran to completion.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions (so, it ignored meaningless percentage value).
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task NodeObserver_Negative_Integer_CPU_Mem_Ports_Firewalls_Values_No_Exceptions_In_Intialize()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(client, context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsErrorThreshold = -42,
                FirewallRulesWarningThreshold = -1,
                ActivePortsWarningThreshold = -100
            };

            await obs.ObserveAsync(token);

            // Bad values don't crash Initialize.
            Assert.IsFalse(obs.IsUnhealthy);

            // It ran (crashing in Initialize would not set LastRunDate, which is MinValue until set.)
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task NodeObserver_Negative_Integer_Thresholds_CPU_Mem_Ports_Firewalls_All_Data_Containers_Are_Null()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(client, context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsErrorThreshold = -42,
                FirewallRulesWarningThreshold = -1,
                ActivePortsWarningThreshold = -100
            };

            await obs.ObserveAsync(token);

            // Bad values don't crash Initialize.
            Assert.IsFalse(obs.IsUnhealthy);

            // Data containers are null.
            Assert.IsTrue(obs.CpuTimeData == null);
            Assert.IsTrue(obs.MemDataInUse == null);
            Assert.IsTrue(obs.MemDataPercent == null);
            Assert.IsTrue(obs.ActivePortsData == null);
            Assert.IsTrue(obs.EphemeralPortsData == null);

            // It ran (crashing in Initialize would not set LastRunDate, which is MinValue until set.)
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task OSObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new OSObserver(client, context)
            {
                ClusterManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"),
                IsObserverWebApiAppDeployed = true
            };

            // This is required since output files are only created if fo api app is also deployed to cluster..

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "SysInfo.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task DiskObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var warningDictionary = new Dictionary<string, double>
            {
                { @"C:\SFDevCluster\Log\Traces", 50000 }
            };

            using var obs = new DiskObserver(client, context)
            {
                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true,
                MonitorDuration = TimeSpan.FromSeconds(1),
                FolderSizeMonitoringEnabled = true,
                FolderSizeConfigDataWarning = warningDictionary
            };


            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "disks.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task DiskObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            var warningDictionary = new Dictionary<string, double>
            {
                /* Windows paths.. */

                { @"%USERPROFILE%\AppData\Local\Temp", 50 },
                
                // This should be rather large.
                { "%USERPROFILE%", 50 }
            };

            using var obs = new DiskObserver(client, context)
            {
                // This should cause a Warning on most dev machines.
                DiskSpacePercentWarningThreshold = 10,
                FolderSizeMonitoringEnabled = true,

                // Folder size monitoring. This will most likely generate a warning.
                FolderSizeConfigDataWarning = warningDictionary,

                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true,
                MonitorDuration = TimeSpan.FromSeconds(5)
            };

            using var obsMgr = new ObserverManager(obs, client)
            {
                ApplicationName = "fabric:/TestApp0"
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected issues with disk/folder size.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // Disk consumption and folder size warnings were generated.
            Assert.IsTrue(obs.CurrentWarningCount == 3);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "disks.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);

            // Stop clears health warning
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NetworkObserver(client, context);
            await obs.ObserveAsync(token);

            // Observer ran to completion with no errors.
            // The supplied config does not include deployed app network configs, so
            // ObserveAsync will return in milliseconds.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_Observer_WritesLocalFile_ObsWebDeployed()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NetworkObserver(client, context)
            {
                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true
            };

            await obs.ObserveAsync(token);

            // Observer ran to completion with no errors.
            // The supplied config does not include deployed app network configs, so
            // ObserveAsync will return in milliseconds.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "NetInfo.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);
        }

        [TestMethod]
        public async Task NodeObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(client, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                DataCapacity = 5,
                UseCircularBuffer = false,
                CpuWarningUsageThresholdPct = 1, // This will generate Warning for sure.
                MemWarningUsageThresholdMb = 1, // This will generate Warning for sure.
                ActivePortsWarningThreshold = 100 // This will generate Warning for sure.
            };

            using var obsMgr = new ObserverManager(obs, client);
            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            // Stop clears health warning
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task SFConfigurationObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new SFConfigurationObserver(client, context)
            {
                IsEnabled = true,

                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true,
                ClusterManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml")
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs", "SFInfraInfo.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath).ConfigureAwait(false)).Length > 0);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(true);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, context)
            {
                IsEnabled = true,
                DataCapacity = 5,
                MonitorDuration = TimeSpan.FromSeconds(1)
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Adjust defaults in FabricObserver project's Observers/FabricSystemObserver.cs
            // file to experiment with err/warn detection/reporting behavior.
            // observer did not detect any errors or warnings for supplied thresholds.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_MemoryWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
            
            using var obs = new FabricSystemObserver(client, context)
            {
                IsEnabled = true,
                MonitorDuration = TimeSpan.FromSeconds(1),
                MemWarnUsageThresholdMb = 5
            };

            await obs.ObserveAsync(token);

            // observer ran to completion.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_ActiveTcpPortsWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(true);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveTcpPortCountWarning = 5
            };

            await obs.ObserveAsync(token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_EphemeralPortsWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(true);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveEphemeralPortCountWarning = 1
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_HandlesWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(true);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                AllocatedHandlesWarning = 100
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_Negative_Integer_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            
            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
            
            using var obs = new FabricSystemObserver(client, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = -42
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task FabricSystemObserver_Integer_Greater_Than_100_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync().ConfigureAwait(true);

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = client;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = client;
           
            using var obs = new FabricSystemObserver(client, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = 420
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }
    }
}