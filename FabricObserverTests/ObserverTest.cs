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
using FabricObserver.Observers;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ClusterObserverManager = ClusterObserver.ClusterObserverManager;
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

        private static readonly bool isSFRuntimePresentOnTestMachine;
        private readonly CancellationToken token = new CancellationToken { };
        private readonly FabricClient fabricClient = new FabricClient(FabricClientRole.User);

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverTest"/> class.
        /// </summary>
        public ObserverTest()
        {
            // You must set ObserverBase's static IsTestRun to true to run these unit tests.
            ObserverBase.IsTestRun = true;
        }

        static ObserverTest()
        {
            isSFRuntimePresentOnTestMachine = IsLocalSFRuntimePresent();
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

            // Don't proceed if tests were run on a machine with no SF cluster running.
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }
        }

        [TestMethod]
        public void AppObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.AppObserverName);

            obs.Dispose(); 
        }

        [TestMethod]
        public void CertificateObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new CertificateObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.CertificateObserverName);

            obs.Dispose();
        }

        [TestMethod]
        public void DiskObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver(fabricClient, context);

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

            var obs = new FabricSystemObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.FabricSystemObserverName);

            obs.Dispose();
            
        }

        [TestMethod]
        public void NetworkObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;
            FabricObserver.Observers.ObserverBase.IsTestRun = true;

            var obs = new NetworkObserver(fabricClient, context);
            Assert.IsTrue(obs.ObserverLogger != null); 
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NetworkObserverName);

            obs.Dispose(); 
        }

        [TestMethod]
        public void NodeObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NodeObserverName);

            obs.Dispose();
            
        }

        [TestMethod]
        public void OSObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new OSObserver(fabricClient, context);

            Assert.IsTrue(obs.ObserverLogger != null);
            
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.OSObserverName);

            obs.Dispose();
            
        }

        [TestMethod]
        public void SFConfigurationObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new SFConfigurationObserver(fabricClient, context);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver(fabricClient, context)
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

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver(fabricClient, context)
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

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
        }

        [TestMethod]
        public async Task ClusterObserverMgr_ClusterObserver_Start_Run_Stop_Successful()
        {
            ClusterObserverManager.FabricClientInstance = fabricClient;
            ClusterObserverManager.FabricServiceContext = context;
            ClusterObserverManager.EtwEnabled = true;

            var obsMgr = new ClusterObserverManager();

            _ = Task.Run(async () =>
            {
                await obsMgr.StartAsync();
            });

            await WaitAsync(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            await obsMgr.StopAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);
            obsMgr.Dispose();
        }

        [TestMethod]
        public async Task ClusterObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            var startDateTime = DateTime.Now;
            ClusterObserverManager.FabricServiceContext = context;
            ClusterObserverManager.FabricClientInstance = fabricClient;
            
            var obs = new ClusterObserver.ClusterObserver();

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        // Stop observer tests. Ensure calling ObserverManager's StopObservers() works as expected.
        // NOTE: It is best to run these together as part of a single test run (so, not part of a Run All Tests run), otherwise, the results are flaky (false negatives).
        // In general, regardless, these tests are flaky (VS Test issue?). So re-run failed runs to ensure they pass (they will).
        [TestMethod]
        public async Task Successful_CertificateObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new CertificateObserver(fabricClient, context);

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Run(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            await WaitAsync(() => obsMgr.IsObserverRunning, 15);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            await obsMgr.StopObserversAsync().ConfigureAwait(false);
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            obsMgr.Dispose();
        }

        /* NOTE: Run the Run Cancellation tests below one by one, not as part of a Run All test or grouping. These can be flaky due to the Test infra. */

        [TestMethod]
        public async Task Successful_AppObserver_Run_Cancellation_Via_ObserverManager()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new AppObserver(fabricClient, context)
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

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Run(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            await WaitAsync(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            obsMgr.Dispose();
        }

        [TestMethod]
        public async Task Successful_DiskObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new DiskObserver(fabricClient, context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Run(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            await WaitAsync(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            obsMgr.Dispose();
        }

        [TestMethod]
        public async Task Successful_FabricSystemObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new FabricSystemObserver(fabricClient, context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Run(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            await WaitAsync(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);

            obs.Dispose();
            obsMgr.Dispose();
        }

        [TestMethod]
        public async Task Successful_NetworkObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NetworkObserver(fabricClient, context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Run(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            await WaitAsync(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);
            obs.Dispose();
            obsMgr.Dispose();
        }

        [TestMethod]
        public async Task Successful_NodeObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(fabricClient, context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
                CpuErrorUsageThresholdPct = 10,
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Run(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            await WaitAsync(() => obsMgr.IsObserverRunning, 15);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);
            obs.Dispose();
            obsMgr.Dispose();
        }

        [TestMethod]
        public async Task Successful_OSObserver_Run_Cancellation_Via_ObserverManager()
        {
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;


            var obs = new OSObserver(fabricClient, context)
            {
                IsEnabled = true,
                NodeName = "_Test_0",
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            _ = Task.Run(async () =>
            {
                await obsMgr.StartObserversAsync();
            });

            await WaitAsync(() => obsMgr.IsObserverRunning, 10);
            Assert.IsTrue(obsMgr.IsObserverRunning);
            _ = obsMgr.StopObserversAsync();
            Assert.IsFalse(obsMgr.IsObserverRunning);
            obs.Dispose();
            obsMgr.Dispose();
        }

        /* End Run Cancellation Tests */

        
        /****** NOTE: These tests below do NOT work without a running local SF cluster
                or in an Azure DevOps VSTest Pipeline ******/

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

            CertificateObserver obs = null;

            try
            {
                var startDateTime = DateTime.Now;
                ObserverManager.FabricServiceContext = context;
    
                ObserverManager.TelemetryEnabled = false;
                ObserverManager.EtwEnabled = false;

                obs = new CertificateObserver(fabricClient, context);

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

                await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new CertificateObserver(fabricClient, context);

            var obsMgr = new ObserverManager(obs, fabricClient)
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

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(fabricClient, context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = 10000,
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // Verify that a data container instance was created for the supplied metric.
            Assert.IsTrue(obs.CpuTimeData != null);

            // Verify that the type of data structure is the default type, IList<T>.
            Assert.IsTrue(obs.CpuTimeData.Data.GetType() == typeof(List<float>));

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions (so, it ignored meaningless percentage value).
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
        public async Task NodeObserver_Negative_Integer_CPU_Mem_Ports_Firewalls_Values_No_Exceptions_In_Intialize()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(fabricClient, context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsErrorThreshold = -42,
                FirewallRulesWarningThreshold = -1,
                ActivePortsWarningThreshold = -100,
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
        public async Task NodeObserver_Negative_Integer_Thresholds_CPU_Mem_Ports_Firewalls_All_Data_Containers_Are_Null()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(fabricClient, context)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsErrorThreshold = -42,
                FirewallRulesWarningThreshold = -1,
                ActivePortsWarningThreshold = -100,
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // Bad values don't crash Initialize.
            Assert.IsFalse(obs.IsUnhealthy);

            // Data containers are null.
            Assert.IsTrue(obs.CpuTimeData == null);
            Assert.IsTrue(obs.MemDataCommittedBytes == null);
            Assert.IsTrue(obs.MemDataPercentUsed == null);
            Assert.IsTrue(obs.ActivePortsData == null);
            Assert.IsTrue(obs.EphemeralPortsData == null);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new OSObserver(fabricClient, context)
            {
                TestManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"),
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver(fabricClient, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new DiskObserver(fabricClient, context)
            {
                // This should cause a Warning on most dev machines.
                DiskSpacePercentWarningThreshold = 10,
                MonitorDuration = TimeSpan.FromSeconds(5),
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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

            CleanupTestHealthReports();
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NetworkObserver(fabricClient, context);

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;

            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new NetworkObserver(fabricClient, context);

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.FabricClientInstance = fabricClient;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var obs = new NodeObserver(fabricClient, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(10),
                DataCapacity = 5,
                UseCircularBuffer = true,
                CpuWarningUsageThresholdPct = 10,
                MemWarningUsageThresholdMb = 1, // This will generate Warning for sure.
            };

            var obsMgr = new ObserverManager(obs, fabricClient);

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // Verify that the type of data structure is CircularBufferCollection.
            Assert.IsTrue(obs.CpuTimeData.Data.GetType() == typeof(CircularBufferCollection<float>));

            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
            await obsMgr.StopObserversAsync();
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
            obs.Dispose();

            CleanupTestHealthReports();
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task SFConfigurationObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            var obs = new SFConfigurationObserver(fabricClient, context)
            {
                IsEnabled = true,
                IsObserverTelemetryEnabled = false,
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await fabricClient.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = fabricClient;

            var obs = new FabricSystemObserver(fabricClient, context)
            {
                IsEnabled = true,
                DataCapacity = 5,
                MonitorDuration = TimeSpan.FromSeconds(1),
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await fabricClient.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = fabricClient;

            var obs = new FabricSystemObserver(fabricClient, context)
            {
                IsEnabled = true,
                MonitorDuration = TimeSpan.FromSeconds(1),
                MemWarnUsageThresholdMb = 5, // This will definitely cause Warning alerts.
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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

            CleanupTestHealthReports();
        }


        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_ActiveTcpPortsWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await fabricClient.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = fabricClient;

            var obs = new FabricSystemObserver(fabricClient, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveTcpPortCountWarning = 5, // This will definitely cause Warning.
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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

            CleanupTestHealthReports();
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_EphemeralPortsWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await fabricClient.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = fabricClient;

            var obs = new FabricSystemObserver(fabricClient, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveEphemeralPortCountWarning = 1, // This will definitely cause Warning.
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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

            CleanupTestHealthReports();
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_HandlesWarningsOrErrorsDetected()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await fabricClient.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = fabricClient;

            var obs = new FabricSystemObserver(fabricClient, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                AllocatedHandlesWarning = 100, // This will definitely cause Warning.
            };

            var obsMgr = new ObserverManager(obs, fabricClient)
            {
                ApplicationName = "fabric:/TestApp0",
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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

            CleanupTestHealthReports();
        }

        /// <summary>
        /// .
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        [TestMethod]
        public async Task FabricSystemObserver_Negative_Integer_CPU_Warn_Threshold_No_Unhandled_Exception()
        {
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await fabricClient.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = fabricClient;

            var obs = new FabricSystemObserver(fabricClient, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = -42,
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

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
            if (!isSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await fabricClient.QueryManager.GetNodeListAsync().ConfigureAwait(true);
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = context;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.FabricClientInstance = fabricClient;

            var obs = new FabricSystemObserver(fabricClient, context)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = 420,
            };

            await obs.ObserveAsync(token).ConfigureAwait(true);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            obs.Dispose();
            
        }

        /***** End Tests that require a currently running SF Cluster. *****/

        private static bool IsLocalSFRuntimePresent()
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

        private async Task WaitAsync(Func<bool> predicate, int timeoutInSeconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutInSeconds) && !predicate())
            {
                await Task.Delay(5).ConfigureAwait(false); // sleep 5 ms
            }
        }

        private void CleanupTestHealthReports()
        {
            // Clear any existing node or fabric:/System app Test Health Reports.
            try
            {
                FabricObserver.Observers.Utilities.HealthReport healthReport = new FabricObserver.Observers.Utilities.HealthReport
                {
                    Code = FOErrorWarningCodes.Ok,
                    HealthMessage = $"Clearing existing Error/Warning Test Health Reports.",
                    State = HealthState.Ok,
                    ReportType = HealthReportType.Application,
                    NodeName = "_Node_0",
                };

                var logger = new Logger("TestCleanUp");

                var fabricClient = new FabricClient(FabricClientRole.Admin);

                // System apps reports
                var appHealth = fabricClient.HealthManager.GetApplicationHealthAsync(new Uri("fabric:/System")).GetAwaiter().GetResult();

                foreach (var evt in appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains("FabricSystemObserver")))
                {
                    if (evt.HealthInformation.HealthState == HealthState.Ok)
                    {
                        continue;
                    }

                    healthReport.AppName = new Uri("fabric:/System");
                    healthReport.Property = evt.HealthInformation.Property;
                    healthReport.SourceId = evt.HealthInformation.SourceId;

                    var healthReporter = new ObserverHealthReporter(logger, fabricClient);
                    healthReporter.ReportHealthToServiceFabric(healthReport);

                    Thread.Sleep(250);
                   }
                

                // Node reports
                var nodeHealth = fabricClient.HealthManager.GetNodeHealthAsync("_Node_0").GetAwaiter().GetResult();
                healthReport.ReportType = HealthReportType.Node;

                 foreach (var evt in nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains("NodeObserver")
                                                                      || s.HealthInformation.SourceId.Contains("DiskObserver")))
                 {
                        if (evt.HealthInformation.HealthState == HealthState.Ok)
                        {
                            continue;
                        }

                        healthReport.Property = evt.HealthInformation.Property;
                        healthReport.SourceId = evt.HealthInformation.SourceId;

                        var healthReporter = new ObserverHealthReporter(logger, fabricClient);
                        healthReporter.ReportHealthToServiceFabric(healthReport);

                        Thread.Sleep(250);
                 }
            }
            catch (FabricException)
            {
            }
        }
    }
}