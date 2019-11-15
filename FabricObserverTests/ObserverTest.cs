using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Fabric;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver;
using FabricObserver.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/*

 Many of these tests will work without the presence of a Fabric runtime (so, no running cluster)...
 Some of them can't because their is a need for things like an actual Fabric runtime instance...

 ***PLEASE RUN ALL OF THESE TESTS ON YOUR LOCAL DEV MACHINE WITH A RUNNING SF CLUSTER BEFORE SUBMITTING A PULL REQUEST***

 Make sure that your observers can run as Network Service (e.g., FabricClientRole.User).
 There is seldom a real need to run FabricObserver as an Admin or System user. Currently, the only potential reason
 would be due to mitigation/healing actions, which are not currently implemented. As a rule, do not run with system level privileges unless you provably have to...

*/

namespace FabricObserverTests
{
    [TestClass]

    // [DeploymentItem(@"MyValidCert.p12")]
    // [DeploymentItem(@"MyExpiredCert.p12")]
    public class ObserverTest
    {
        private static readonly Uri ServiceName = new Uri("fabric:/app/service");
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

        private readonly StatelessServiceContext context
                = new StatelessServiceContext(
                    new NodeContext("Node0", new NodeId(0, 1), 0, "NodeType1", "TEST.MACHINE"),
                    CodePackageContext,
                    "FabricObserver.FabricObserverType",
                    ServiceName,
                    null,
                    Guid.NewGuid(),
                    long.MaxValue);

        private readonly bool isSFRuntimePresentOnTestMachine = false;
        private CancellationToken token = new CancellationToken { };

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverTest"/> class.
        /// </summary>
        public ObserverTest()
        {
            this.isSFRuntimePresentOnTestMachine = this.IsLocalSFRuntimePresent();
        }

        [ClassInitialize]
        public static void InstallCerts(TestContext tc)
        {
            var validCert = new X509Certificate2("MyValidCert.p12");
            var expiredCert = new X509Certificate2("MyExpiredCert.p12");
            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);

            try
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(validCert);
                store.Add(expiredCert);
            }
            finally
            {
                store?.Dispose();
            }
        }

        [ClassCleanup]
        public static void UninstallCerts()
        {
            var validCert = new X509Certificate2("MyValidCert.p12");
            var expiredCert = new X509Certificate2("MyExpiredCert.p12");
            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);

            try
            {
                store.Open(OpenFlags.ReadWrite);
                store.Remove(validCert);
                store.Remove(expiredCert);
            }
            finally
            {
                store?.Dispose();
            }

            // Remove any files generated...
            try
            {
                var outputFolder = $@"{Environment.CurrentDirectory}\observer_logs\";

                if (Directory.Exists(outputFolder))
                {
                    Directory.Delete(outputFolder, true);
                }
            }
            catch
            {
            }
        }

        [TestMethod]
        public void AppObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver();

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.CsvFileLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.AppObserverName);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        [TestMethod]
        public void CertificateObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new CertificateObserver();

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.CsvFileLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.CertificateObserverName);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        [TestMethod]
        public void DiskObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver();

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.CsvFileLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.DiskObserverName);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        [TestMethod]
        public void FabricSystemObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver();

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.CsvFileLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.FabricSystemObserverName);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        [TestMethod]
        public void NetworkObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new NetworkObserver
            {
                IsTestRun = true,
            };

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.CsvFileLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NetworkObserverName);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        [TestMethod]
        public void NodeObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver();

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.CsvFileLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NodeObserverName);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        [TestMethod]
        public void OSObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new OSObserver();

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.CsvFileLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.OSObserverName);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        [TestMethod]
        public void SFConfigurationObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new SFConfigurationObserver();

            // These are set in derived ObserverBase...
            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.CsvFileLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.SFConfigurationObserverName);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task AppObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver
            {
                IsTestRun = true,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        // Stop observer tests. Ensure calling ObserverManager's StopObservers() works as expected.
        [TestMethod]
        [SuppressMessage("Code Quality", "IDE0067:Dispose objects before losing scope", Justification = "Noise...")]
        public void Successful_CertificateObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);

            var stopWatch = new Stopwatch();

            var obs = new CertificateObserver
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var objReady = new ManualResetEventSlim(false);

            stopWatch.Start();

            var t = Task.Factory.StartNew(() =>
            {
                objReady.Set();
                obsMgr.StartObservers();
            });

            objReady?.Wait();

            while (!obsMgr.IsObserverRunning && stopWatch.Elapsed.TotalSeconds < 10)
            {
                // wait...
            }

            stopWatch.Stop();

            // Observer is running. Stop it...
            obsMgr.StopObservers();
            Thread.Sleep(5);

            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            objReady?.Dispose();
        }

        [TestMethod]
        public void Successful_AppObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);

            var stopWatch = new Stopwatch();

            var obs = new AppObserver
            {
                IsEnabled = true,
                NodeName = "_Test_0",
                IsTestRun = true,
            };

            var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var objReady = new ManualResetEventSlim(false);

            stopWatch.Start();
            var t = Task.Factory.StartNew(() =>
            {
                objReady.Set();
                obsMgr.StartObservers();
            });

            objReady?.Wait();

            while (!obsMgr.IsObserverRunning && stopWatch.Elapsed.TotalSeconds < 10)
            {
                // wait...
            }

            stopWatch.Stop();

            // Observer is running. Stop it...
            obsMgr.StopObservers();
            Thread.Sleep(5);

            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            objReady?.Dispose();
        }

        [TestMethod]
        public void Successful_DiskObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);

            var stopWatch = new Stopwatch();

            var obs = new DiskObserver
            {
                IsEnabled = true,
                NodeName = "_Test_0",
                IsTestRun = true,
            };

            var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var objReady = new ManualResetEventSlim(false);

            stopWatch.Start();
            var t = Task.Factory.StartNew(() =>
            {
                objReady.Set();
                obsMgr.StartObservers();
            });

            objReady?.Wait();

            while (!obsMgr.IsObserverRunning && stopWatch.Elapsed.TotalSeconds < 10)
            {
                // wait...
            }

            stopWatch.Stop();

            obsMgr.StopObservers();

            Thread.Sleep(5);
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            objReady?.Dispose();
        }

        [TestMethod]
        public void Successful_FabricSystemObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);

            var stopWatch = new Stopwatch();

            var obs = new FabricSystemObserver
            {
                IsEnabled = true,
                NodeName = "_Test_0",
                IsTestRun = true,
            };

            var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var objReady = new ManualResetEventSlim(false);

            stopWatch.Start();
            var t = Task.Factory.StartNew(() =>
            {
                objReady.Set();
                obsMgr.StartObservers();
            });

            objReady?.Wait();

            while (!obsMgr.IsObserverRunning && stopWatch.Elapsed.TotalSeconds < 10)
            {
                // wait...
            }

            stopWatch.Stop();

            obsMgr.StopObservers();

            Thread.Sleep(5);
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            objReady?.Dispose();
        }

        [TestMethod]
        public void Successful_NetworkObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);

            var stopWatch = new Stopwatch();

            var obs = new NetworkObserver
            {
                IsEnabled = true,
                NodeName = "_Test_0",
                IsTestRun = true,
            };

            var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var objReady = new ManualResetEventSlim(false);

            stopWatch.Start();
            var t = Task.Factory.StartNew(() =>
            {
                objReady.Set();
                obsMgr.StartObservers();
            });

            objReady?.Wait();

            while (!obsMgr.IsObserverRunning && stopWatch.Elapsed.TotalSeconds < 10)
            {
                // wait...
            }

            stopWatch.Stop();

            obsMgr.StopObservers();

            Thread.Sleep(5);
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            objReady?.Dispose();
        }

        [TestMethod]
        public void Successful_NodeObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);

            var stopWatch = new Stopwatch();

            var obs = new NodeObserver
            {
                IsEnabled = true,
                NodeName = "_Test_0",
                IsTestRun = true,
            };

            var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var objReady = new ManualResetEventSlim(false);

            stopWatch.Start();
            var t = Task.Factory.StartNew(() =>
            {
                objReady.Set();
                obsMgr.StartObservers();
            });

            objReady?.Wait();

            while (!obsMgr.IsObserverRunning && stopWatch.Elapsed.TotalSeconds < 10)
            {
                // wait...
            }

            stopWatch.Stop();

            obsMgr.StopObservers();

            Thread.Sleep(5);
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            objReady?.Dispose();
        }

        [TestMethod]
        public void Successful_OSObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);

            var stopWatch = new Stopwatch();

            var obs = new OSObserver
            {
                IsEnabled = true,
                NodeName = "_Test_0",
                IsTestRun = true,
            };

            var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var objReady = new ManualResetEventSlim(false);

            stopWatch.Start();
            var t = Task.Factory.StartNew(() =>
            {
                objReady.Set();
                obsMgr.StartObservers();
            });

            objReady?.Wait();

            while (!obsMgr.IsObserverRunning && stopWatch.Elapsed.TotalSeconds < 10)
            {
                // wait...
            }

            stopWatch.Stop();

            obsMgr.StopObservers();

            Thread.Sleep(5);
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            objReady?.Dispose();
        }

        [TestMethod]
        public void Successful_SFConfigurationObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);

            var stopWatch = new Stopwatch();

            var obs = new SFConfigurationObserver
            {
                IsEnabled = true,
                NodeName = "_Test_0",
                IsTestRun = true,
            };

            var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var objReady = new ManualResetEventSlim(false);

            stopWatch.Start();
            var t = Task.Factory.StartNew(() =>
            {
                objReady.Set();
                obsMgr.StartObservers();
            });

            objReady?.Wait();

            while (!obsMgr.IsObserverRunning && stopWatch.Elapsed.TotalSeconds < 10)
            {
                // wait...
            }

            stopWatch.Stop();

            obsMgr.StopObservers();

            Thread.Sleep(5);
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            objReady?.Dispose();
        }

        /// <summary>
        /// Incorrect/meaningless config properties tests. Ensure that bad values do not
        /// crash observers OR they do, which is your design decision...
        /// They should handle the case when unexpected config values are provided...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task NodeObserver_Negative_Integer_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                Assert.IsTrue(1 == 1);
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver
            {
                IsTestRun = true,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /****** These tests do NOT work without a running local SF cluster
                or in an Azure DevOps VSTest Pipeline ******/

        [TestMethod]
        public async Task CertificateObserver_validCerts()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new CertificateObserver
            {
                IsTestRun = true,
            };

            var commonNamesToObserve = new System.Collections.Generic.List<string>
            {
                "MyValidCert", // Common name of valid cert
            };

            var thumbprintsToObserve = new System.Collections.Generic.List<string>
            {
                "1fda27a2923505e47de37db48ff685b049642c25", // thumbprint of valid cert
            };

            obs.DaysUntilAppExpireWarningThreshold = 14;
            obs.DaysUntilClusterExpireWarningThreshold = 14;
            obs.AppCertificateCommonNamesToObserve = commonNamesToObserve;
            obs.AppCertificateThumbprintsToObserve = thumbprintsToObserve;
            obs.SecurityConfiguration = new SecurityConfiguration
            {
                SecurityType = SecurityType.None,
                ClusterCertThumbprintOrCommonName = string.Empty,
                ClusterCertSecondaryThumbprint = string.Empty,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        [TestMethod]
        public async Task CertificateObserver_expiredAndexpiringCerts()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new CertificateObserver
            {
                IsTestRun = true,
            };

            var commonNamesToObserve = new System.Collections.Generic.List<string>
            {
                "MyExpiredCert", // common name of expired cert
            };

            var thumbprintsToObserve = new System.Collections.Generic.List<string>
            {
                "1fda27a2923505e47de37db48ff685b049642c25", // thumbprint of valid cert, but warning threshold causes expiring
            };

            obs.DaysUntilAppExpireWarningThreshold = int.MaxValue;
            obs.DaysUntilClusterExpireWarningThreshold = 14;
            obs.AppCertificateCommonNamesToObserve = commonNamesToObserve;
            obs.AppCertificateThumbprintsToObserve = thumbprintsToObserve;
            obs.SecurityConfiguration = new SecurityConfiguration
            {
                SecurityType = SecurityType.None,
                ClusterCertThumbprintOrCommonName = string.Empty,
                ClusterCertSecondaryThumbprint = string.Empty,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected error conditions...
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// NodeObserver_Integer_Greater_Than_100_CPU_Warn_Threshold_No_Fail.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task NodeObserver_Integer_Greater_Than_100_CPU_Warn_Threshold_No_Fail()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver
            {
                IsTestRun = true,
                CpuWarningUsageThresholdPct = 10000,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task NodeObserver_Negative_Integer_CPU_Mem_Ports_Firewalls_Values_No_Exceptions_Intialize()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver
            {
                IsTestRun = true,
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMB = -2500,
                EphemeralPortsErrorThreshold = -42,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // Bad values don't crash Initialize...
            Assert.IsFalse(obs.IsUnhealthy);

            // It ran (crashing in Initialize would not set LastRunDate, which is MinValue until set...)
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task OSObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new OSObserver()
            {
                IsTestRun = true,
                TestManifestPath = $@"{Environment.CurrentDirectory}\clusterManifest.xml",
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = $@"{Environment.CurrentDirectory}\observer_logs\SysInfo.txt";

            // Output log file was created successfully during test...
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty...
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task DiskObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver();
            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = $@"{Environment.CurrentDirectory}\observer_logs\disks.txt";

            // Output log file was created successfully during test...
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty...
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task DiskObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrors()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver
            {
                DiskSpacePercentWarningThreshold = 20, // This should cause a Warning on most dev machines...
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected error or warning disk health conditions...
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = $@"{Environment.CurrentDirectory}\observer_logs\disks.txt";

            // Output log file was created successfully during test...
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty...
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NetworkObserver
            {
                IsTestRun = true,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // Observer ran to completion with no errors...
            // The supplied config does not include deployed app network configs, so
            // ObserveAsync will return in milliseconds...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_Observer_WritesLocalFile_ObsWebDeployed()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new NetworkObserver
            {
                IsTestRun = true,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // Observer ran to completion with no errors...
            // The supplied config does not include deployed app network configs, so
            // ObserveAsync will return in milliseconds...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = $@"{Environment.CurrentDirectory}\observer_logs\NetInfo.txt";

            // Output log file was created successfully during test...
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty...
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task NodeObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrorsDetected()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver
            {
                IsTestRun = true,
                MemWarningUsageThresholdMB = 1, // This will generate Warning for sure...
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task SFConfigurationObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new SFConfigurationObserver
            {
                IsTestRun = true,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            var outputFilePath = $@"{Environment.CurrentDirectory}\observer_logs\SFInfraInfo.txt";

            // Output log file was created successfully during test...
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty...
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver
            {
                IsTestRun = true,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Adjust defaults in FabricObserver project's Observers/FabricSystemObserver.cs
            // file to experiment with err/warn detection/reporting behavior.
            // observer did not detect any errors or warnings for supplied thresholds...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrorsDetected()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver
            {
                IsTestRun = true,
                MemWarnUsageThresholdMB = 20, // This will definitely cause Warning alerts...
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s)...
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_Negative_Integer_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver
            {
                IsTestRun = true,
                CpuWarnUsageThresholdPct = -42,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /// <summary>
        /// ...
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_Integer_Greater_Than_100_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = new FabricClient(FabricClientRole.User);
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver
            {
                IsTestRun = true,
                CpuWarnUsageThresholdPct = 420,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors...
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions...
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run...
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            ObserverManager.FabricClientInstance.Dispose();
        }

        /***** End Tests that require a currently running SF Cluster... *****/

        private bool IsLocalSFRuntimePresent()
        {
            try
            {
                var ps = Process.GetProcessesByName("Fabric");
                if (ps?.Length == 0)
                {
                    return false;
                }

                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}