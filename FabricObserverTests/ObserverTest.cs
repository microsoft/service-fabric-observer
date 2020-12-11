using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Observers;
using FabricObserver.Observers;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ClusterObserverManager = FabricClusterObserver.Observers.ObserverManager;
using ObserverManager = FabricObserver.Observers.ObserverManager;

/*

 Many of these tests will work without the presence of a Fabric runtime (so, no running cluster).
 Some of them can't because their is a need for things like an actual Fabric runtime instance.

 ***PLEASE RUN ALL OF THESE TESTS ON YOUR LOCAL DEV MACHINE WITH A RUNNING SF CLUSTER BEFORE SUBMITTING A PULL REQUEST***

 Make sure that your observers can run as Network Service (e.g., FabricClientRole.User).
 There is seldom a real need to run FabricObserver as an Admin or System user. Currently, the only potential reason
 would be due to mitigation/healing actions, which are not currently implemented. As a rule, do not run with system level privileges unless you provably have to.

*/

namespace FabricObserverTests
{
    [TestClass]
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
                    new NodeContext("_Node_0", new NodeId(0, 1), 0, "NodeType0", "TEST.MACHINE"),
                    CodePackageContext,
                    "FabricObserver.FabricObserverType",
                    ServiceName,
                    null,
                    Guid.NewGuid(),
                    long.MaxValue);

        private readonly bool isSFRuntimePresentOnTestMachine;
        private readonly CancellationToken token = new CancellationToken { };
        private readonly FabricClient fabricClient = new FabricClient(FabricClientRole.User);

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverTest"/> class.
        /// </summary>
        public ObserverTest()
        {
            this.isSFRuntimePresentOnTestMachine = IsLocalSFRuntimePresent();

            // You must set ObserverBase's static IsTestRun to true to run these unit tests.
            FabricObserver.Observers.ObserverBase.IsTestRun = true;
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
        public static void TestClassCleanup()
        {
            // Remove any files generated.
            try
            {
                string outputFolder = Path.Combine(Environment.CurrentDirectory, "observer_logs");

                if (Directory.Exists(outputFolder))
                {
                    Directory.Delete(outputFolder, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [TestMethod]
        public void AppObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver(this.fabricClient, this.context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.AppObserverName);

            obs.Dispose(); 
        }

        [TestMethod]
        public void CertificateObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new CertificateObserver(this.fabricClient, this.context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.CertificateObserverName);

            obs.Dispose();
        }

        [TestMethod]
        public void ClusterObserver_Constructor_Test()
        {
            ClusterObserverManager.FabricServiceContext = this.context;
            ClusterObserverManager.FabricClientInstance = this.fabricClient;
            ClusterObserverManager.TelemetryEnabled = false;

            var obs = new ClusterObserver();

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.ObserverName == FabricClusterObserver.Utilities.ObserverConstants.ClusterObserverName);

            obs.Dispose();
        }

        [TestMethod]
        public void DiskObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver(this.fabricClient, this.context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.DiskObserverName);

            obs.Dispose();
            
        }

        [TestMethod]
        public void FabricSystemObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver(this.fabricClient, this.context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.FabricSystemObserverName);

            obs.Dispose();
            
        }

        [TestMethod]
        public void NetworkObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;
            FabricObserver.Observers.ObserverBase.IsTestRun = true;

            var obs = new NetworkObserver(this.fabricClient, this.context);
            Assert.IsTrue(obs.ObserverLogger != null); 
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NetworkObserverName);

            obs.Dispose(); 
        }

        [TestMethod]
        public void NodeObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(this.fabricClient, this.context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NodeObserverName);

            obs.Dispose();
            
        }

        [TestMethod]
        public void OSObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new OSObserver(this.fabricClient, this.context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.OSObserverName);

            obs.Dispose();
            
        }

        [TestMethod]
        public void SFConfigurationObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new SFConfigurationObserver(this.fabricClient, this.context);

            // These are set in derived ObserverBase.
            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.SFConfigurationObserverName);

            obs.Dispose();
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task AppObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(5),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.json"),
                ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>(),
            };

            obs.ReplicaOrInstanceList.Add(new ReplicaOrInstanceMonitoringInfo
            {
                ApplicationName = new Uri("fabric:/TestApp"),
                PartitionId = Guid.NewGuid(),
                HostProcessId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 0 : 1,
                ReplicaOrInstanceId = default(long),
            });

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task AppObserver_ObserveAsync_TargetAppType_Successful_Observer_IsHealthy()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(5),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.json"),
                ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>(),
            };

            obs.ReplicaOrInstanceList.Add(new ReplicaOrInstanceMonitoringInfo
            {
                ApplicationName = new Uri("fabric:/TestApp"),
                ApplicationTypeName = "TestAppType",
                PartitionId = Guid.NewGuid(),
                HostProcessId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 0 : 1,
                ReplicaOrInstanceId = default(long),
            });

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task ClusterObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            var startDateTime = DateTime.Now;
            ClusterObserverManager.FabricServiceContext = this.context;
            ClusterObserverManager.FabricClientInstance = this.fabricClient;
            ClusterObserverManager.TelemetryEnabled = false;

            var obs = new ClusterObserver
            {
                IsTestRun = true,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
        }

        // Stop observer tests. Ensure calling ObserverManager's StopObservers() works as expected.
        [TestMethod]
        public async Task Successful_CertificateObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = this.fabricClient;

            var obs = new CertificateObserver(this.fabricClient, this.context);

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Factory.StartNew(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            Wait(() => obsMgr.IsObserverRunning, 15);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            obsMgr.Dispose();
        }

        [TestMethod]
        public void Successful_AppObserver_Run_Cancellation_Via_ObserverManager()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = this.fabricClient;

            var obs = new AppObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(15),
                ConfigPackagePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.json"),
                ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>(),
            };

            obs.ReplicaOrInstanceList.Add(new ReplicaOrInstanceMonitoringInfo
            {
                ApplicationName = new Uri("fabric:/TestApp"),
                PartitionId = Guid.NewGuid(),
                HostProcessId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 0 : 1,
                ReplicaOrInstanceId = default(long),
            });

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Factory.StartNew(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            Wait(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            obsMgr.Dispose();
        }

        [TestMethod]
        public void Successful_DiskObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = this.fabricClient;

            var obs = new DiskObserver(this.fabricClient, this.context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Factory.StartNew(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            Wait(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
        }

        [TestMethod]
        public void Successful_FabricSystemObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = this.fabricClient;

            var obs = new FabricSystemObserver(this.fabricClient, this.context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Factory.StartNew(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            Wait(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
        }

        [TestMethod]
        public void Successful_NetworkObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = this.fabricClient;

            var obs = new NetworkObserver(this.fabricClient, this.context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Factory.StartNew(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            Wait(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);
            obs.Dispose();
        }

        [TestMethod]
        public void Successful_NodeObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = this.fabricClient;

            var obs = new NodeObserver(this.fabricClient, this.context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Factory.StartNew(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            Wait(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
        }

        [TestMethod]
        public void Successful_OSObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = this.fabricClient;

            var obs = new OSObserver(this.fabricClient, this.context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Factory.StartNew(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            Wait(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
        }

        /****** These tests do NOT work without a running local SF cluster
                or in an Azure DevOps VSTest Pipeline ******/

        /// <summary>
        /// Incorrect/meaningless config properties tests. Ensure that bad values do not
        /// crash observers OR they do, which is your design decision.
        /// They should handle the case when unexpected config values are provided.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task NodeObserver_Negative_Integer_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                UseCircularBuffer = true,
                DataCapacity = 5,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // Verify that the type of data structure is CircularBufferCollection.
            Assert.IsTrue(obs.AllCpuTimeData.Data.GetType() == typeof(CircularBufferCollection<float>));

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            
        }

        [TestMethod]
        public async Task CertificateObserver_validCerts()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!InstallCerts())
            {
                Assert.Inconclusive("This test can only be run on Windows as an admin.");
            }

            CertificateObserver obs = null;

            try
            {
                var startDateTime = DateTime.Now;
                ObserverManager.FabricServiceContext = this.context;
                ObserverManager.FabricClientInstance = this.fabricClient;
                ObserverManager.TelemetryEnabled = false;
                ObserverManager.EtwEnabled = false;

                obs = new CertificateObserver(this.fabricClient, this.context);

                var commonNamesToObserve = new List<string>
                {
                    "MyValidCert", // Common name of valid cert
                };

                var thumbprintsToObserve = new List<string>
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

                obs?.Dispose();
                ObserverManager.FabricClientInstance?.Dispose();
            }
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new CertificateObserver(this.fabricClient, this.context);

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            var commonNamesToObserve = new List<string>
            {
                "MyExpiredCert", // common name of expired cert
            };

            var thumbprintsToObserve = new List<string>
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

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected error conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
            await obsMgr.StopObserversAsync();
            await Task.Delay(1000).ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
            obs.Dispose();
            obsMgr.Dispose();
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(this.fabricClient, this.context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = 10000,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // Verify that the type of data structure is the default type, IList<T>.
            Assert.IsTrue(obs.AllCpuTimeData.Data.GetType() == typeof(List<float>));

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(this.fabricClient, this.context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsErrorThreshold = -42,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // Bad values don't crash Initialize.
            Assert.IsFalse(obs.IsUnhealthy);

            // It ran (crashing in Initialize would not set LastRunDate, which is MinValue until set.)
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new OSObserver(this.fabricClient, this.context)
            {
                TestManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"),
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            string outputFilePath = Path.Combine(Environment.CurrentDirectory, "observer_logs", "SysInfo.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            string outputFilePath = Path.Combine(Environment.CurrentDirectory, "observer_logs", "disks.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver(this.fabricClient, this.context)
            {
                // This should cause a Warning on most dev machines.
                DiskSpacePercentWarningThreshold = 10,
                MonitorDuration = TimeSpan.FromSeconds(5),
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected error or warning disk health conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            string outputFilePath = Path.Combine(Environment.CurrentDirectory, "observer_logs", "disks.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            await obsMgr.StopObserversAsync();
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
            obs.Dispose();
            obsMgr.Dispose();
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NetworkObserver(this.fabricClient, this.context);

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // Observer ran to completion with no errors.
            // The supplied config does not include deployed app network configs, so
            // ObserveAsync will return in milliseconds.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new NetworkObserver(this.fabricClient, this.context);

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // Observer ran to completion with no errors.
            // The supplied config does not include deployed app network configs, so
            // ObserveAsync will return in milliseconds.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            string outputFilePath = Path.Combine(Environment.CurrentDirectory, "observer_logs", "NetInfo.txt");

            Console.WriteLine($"outputFilePath: {outputFilePath}");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(10),
                DataCapacity = 5,
                UseCircularBuffer = true,
                MemWarningUsageThresholdMb = 1, // This will generate Warning for sure.
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Verify that the type of data structure is CircularBufferCollection.
            Assert.IsTrue(obs.AllCpuTimeData.Data.GetType() == typeof(CircularBufferCollection<float>));

            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
            await obsMgr.StopObserversAsync();
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
            obs.Dispose();
            obsMgr.Dispose();
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new SFConfigurationObserver(this.fabricClient, this.context);

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            string outputFilePath = Path.Combine(Environment.CurrentDirectory, "observer_logs", "SFInfraInfo.txt");

            // Output log file was created successfully during test.
            Assert.IsTrue(File.Exists(outputFilePath)
                          && File.GetLastWriteTime(outputFilePath) > startDateTime
                          && File.GetLastWriteTime(outputFilePath) < obs.LastRunDateTime);

            // Output file is not empty.
            Assert.IsTrue(File.ReadAllLines(outputFilePath).Length > 0);

            obs.Dispose();
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver(this.fabricClient, this.context)
            {
                DataCapacity = 5,
                MonitorDuration = TimeSpan.FromSeconds(1),
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Adjust defaults in FabricObserver project's Observers/FabricSystemObserver.cs
            // file to experiment with err/warn detection/reporting behavior.
            // observer did not detect any errors or warnings for supplied thresholds.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_MemoryWarningsOrErrorsDetected()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            
            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                MemWarnUsageThresholdMb = 5, // This will definitely cause Warning alerts.
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
            obs.Dispose();
            obsMgr.Dispose();
        }


        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_ActiveTcpPortsWarningsOrErrorsDetected()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);

            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveTcpPortCountWarning = 5, // This will definitely cause Warning.
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
            obs.Dispose();
            obsMgr.Dispose();
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_EphemeralPortsWarningsOrErrorsDetected()
        {
            if (!this.isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = this.context;
            ObserverManager.FabricClientInstance = this.fabricClient;
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);

            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveEphemeralPortCountWarning = 1, // This will definitely cause Warning.
            };

            var obsMgr = new ObserverManager(obs, this.fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Experiment with err/warn detection/reporting behavior.
            // observer detected errors or warnings for supplied threshold(s).
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
            await obsMgr.StopObserversAsync();
            await Task.Delay(1000).ConfigureAwait(false);
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
            obs.Dispose();
            obsMgr.Dispose();
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            
            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = -42,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            
        }

        /// <summary>
        /// .
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
            ObserverManager.FabricClientInstance = this.fabricClient;
            var nodeList = await ObserverManager.FabricClientInstance.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            
            if (nodeList?.Count > 1)
            {
                return;
            }

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver(this.fabricClient, this.context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = 420,
            };

            await obs.ObserveAsync(this.token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            
        }

        /***** End Tests that require a currently running SF Cluster. *****/

        private bool IsLocalSFRuntimePresent()
        {
            try
            {
                var ps = Process.GetProcessesByName("Fabric");
                return ps?.Length != 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void Wait(Func<bool> predicate, int timeoutInSeconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutInSeconds) && !predicate())
            {
                Thread.Sleep(5); // sleep 5 ms
            }
        }
    }
}