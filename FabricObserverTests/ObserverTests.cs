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
using ServiceFabric.Mocks;
using static ServiceFabric.Mocks.MockConfigurationPackage;
using System.Fabric.Description;
using System.Xml;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.Utilities.ServiceFabric;

/***PLEASE RUN ALL OF THESE TESTS ON YOUR LOCAL DEV MACHINE WITH A RUNNING SF CLUSTER BEFORE SUBMITTING A PULL REQUEST***/

namespace FabricObserverTests
{
    [TestClass]
    public class ObserverTests
    {
        private const string NodeName = "_Node_0";
        private static readonly Uri TestServiceName = new Uri("fabric:/app/service");
        private static readonly bool IsSFRuntimePresentOnTestMachine = IsLocalSFRuntimePresent();
        private static readonly CancellationToken Token = new CancellationToken();
        private static readonly ICodePackageActivationContext CodePackageContext = null;
        private static readonly StatelessServiceContext TestServiceContext = null;
        
        private static FabricClient FabricClient => FabricClientUtilities.FabricClientSingleton;

        static ObserverTests()
        {
            /* SF runtime mocking care of ServiceFabric.Mocks by loekd.
               https://github.com/loekd/ServiceFabric.Mocks */

            string configPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "Settings.xml");
            ConfigurationPackage configPackage = BuildConfigurationPackageFromSettingsFile(configPath);

            CodePackageContext =
                new MockCodePackageActivationContext(
                        TestServiceName.AbsoluteUri,
                        "applicationType",
                        "Code",
                        "1.0.0.0",
                        Guid.NewGuid().ToString(),
                        @"C:\Log",
                        @"C:\Temp",
                        @"C:\Work",
                        "ServiceManifest",
                        "1.0.0.0")
                {
                    ConfigurationPackage = configPackage
                };

            TestServiceContext =
                new StatelessServiceContext(
                        new NodeContext(NodeName, new NodeId(0, 1), 0, "NodeType0", "TEST.MACHINE"),
                        CodePackageContext,
                        "FabricObserver.FabricObserverType",
                        TestServiceName,
                        null,
                        Guid.NewGuid(),
                        long.MaxValue); 
        }

        /* Helpers */

        private static ConfigurationPackage BuildConfigurationPackageFromSettingsFile(string configPath)
        {
            StringReader sreader = null;
            XmlReader xreader = null;

            try
            {
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    return null;
                }

                string configXml = File.ReadAllText(configPath);

                // Safe XML pattern - *Do not use LoadXml*.
                XmlDocument xdoc = new XmlDocument { XmlResolver = null };
                sreader = new StringReader(configXml);
                xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null });
                xdoc.Load(xreader);

                var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");
                var sectionNodes = xdoc.SelectNodes("//sf:Section", nsmgr);
                var configSections = new ConfigurationSectionCollection();

                if (sectionNodes != null)
                {
                    foreach (XmlNode node in sectionNodes)
                    {
                        ConfigurationSection configSection = CreateConfigurationSection(node?.Attributes?.Item(0).Value);
                        var sectionParams = xdoc.SelectNodes($"//sf:Section[@Name='{configSection.Name}']//sf:Parameter", nsmgr);

                        if (sectionParams != null)
                        {
                            foreach (XmlNode node2 in sectionParams)
                            {
                                ConfigurationProperty parameter = CreateConfigurationSectionParameters(node2?.Attributes?.Item(0).Value, node2?.Attributes?.Item(1).Value);
                                configSection.Parameters.Add(parameter);
                            }
                        }

                        configSections.Add(configSection);
                    }

                    var configSettings = CreateConfigurationSettings(configSections);
                    ConfigurationPackage configPackage = CreateConfigurationPackage(configSettings, configPath.Replace("\\Settings.xml", ""));
                    return configPackage;
                }
            }
            finally
            {
                sreader.Dispose();
                xreader.Dispose();
            }

            return null;
        }

        private static async Task DeployHealthMetricsAppAsync()
        {
            string appName = "fabric:/HealthMetrics";
            string appType = "HealthMetricsType";
            string appVersion = "1.0.0.0";
            string serviceName1 = "fabric:/HealthMetrics/BandActorService";
            string serviceName2 = "fabric:/HealthMetrics/DoctorActorService";

            // Change this to suit your configuration (so, if you are on Windows and you installed SF on a different drive, for example).
            string imageStoreConnectionString = @"file:C:\SfDevCluster\Data\ImageStoreShare";
            string packagePathInImageStore = "HealthMetrics";
            string packagePathZip = Path.Combine(Environment.CurrentDirectory, "HealthMetrics.zip");
            string serviceType1 = "BandActorServiceType";
            string serviceType2 = "DoctorActorServiceType";
            string packagePath = Path.Combine(Environment.CurrentDirectory, "HealthMetricsApp", "pkg", "Debug");

            // If fabric:/HealthMetrics is already installed, exit.
            var deployedTestApp =
                    await FabricClient.QueryManager.GetDeployedApplicationListAsync(
                            NodeName,
                            new Uri(appName),
                            TimeSpan.FromSeconds(30),
                            Token);

            if (deployedTestApp?.Count > 0)
            {
                return;
            }

            // Unzip the compressed HealthMetrics app package.
            System.IO.Compression.ZipFile.ExtractToDirectory(packagePathZip, "HealthMetricsApp", true);

            // Copy the HealthMetrics app package to a location in the image store.
            FabricClient.ApplicationManager.CopyApplicationPackage(imageStoreConnectionString, packagePath, packagePathInImageStore);

            // Provision the HealthMetrics application.          
            await FabricClient.ApplicationManager.ProvisionApplicationAsync(packagePathInImageStore);

            // Create HealthMetrics app instance.
            ApplicationDescription appDesc = new ApplicationDescription(new Uri(appName), appType, appVersion);
            await FabricClient.ApplicationManager.CreateApplicationAsync(appDesc);

            // Create the HealthMetrics service descriptions.
            StatefulServiceDescription serviceDescription1 = new StatefulServiceDescription
            {
                ApplicationName = new Uri(appName),
                MinReplicaSetSize = 1,
                PartitionSchemeDescription = new SingletonPartitionSchemeDescription(),
                ServiceName = new Uri(serviceName1),
                ServiceTypeName = serviceType1
            };

            StatefulServiceDescription serviceDescription2 = new StatefulServiceDescription
            {
                ApplicationName = new Uri(appName),
                MinReplicaSetSize = 1,
                PartitionSchemeDescription = new SingletonPartitionSchemeDescription(),
                ServiceName = new Uri(serviceName2),
                ServiceTypeName = serviceType2
            };

            // Create the HealthMetrics app services. If any of the services are declared as a default service in the ApplicationManifest.xml,
            // then the service instance is already running and this call will fail..
            await FabricClient.ServiceManager.CreateServiceAsync(serviceDescription1);
            await FabricClient.ServiceManager.CreateServiceAsync(serviceDescription2);

            // This is a hack. Withouth this timeout, the deployed test services may not have populated the FC cache?
            // You may need to increase this value depending upon your dev machine? You'll find out..
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

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

        private static async Task CleanupTestHealthReportsAsync()
        {
            Logger logger = new Logger("TestLogger");
            var fabricClient = new FabricClient();
            var apps = await fabricClient.QueryManager.GetApplicationListAsync();

            foreach (var app in apps)
            {
                var replicas = await fabricClient.QueryManager.GetDeployedReplicaListAsync(NodeName, app.ApplicationName);

                foreach (var replica in replicas)
                {
                    var serviceHealth = await fabricClient.HealthManager.GetServiceHealthAsync(replica.ServiceName);
                    var fabricObserverServiceHealthEvents =
                        serviceHealth.HealthEvents?.Where(
                           s => s.HealthInformation.HealthState == HealthState.Error || s.HealthInformation.HealthState == HealthState.Warning);

                    foreach (var evt in fabricObserverServiceHealthEvents)
                    {
                        var healthReport = new HealthReport
                        {
                            Code = FOErrorWarningCodes.Ok,
                            EntityType = EntityType.Service,
                            HealthMessage = $"Clearing existing AppObserver Test health reports.",
                            State = HealthState.Ok,
                            NodeName = NodeName,
                            EmitLogEvent = false,
                            ServiceName = replica.ServiceName,
                            Property = evt.HealthInformation.Property,
                            SourceId = evt.HealthInformation.SourceId
                        };

                        var healthReporter = new ObserverHealthReporter(logger);
                        healthReporter.ReportHealthToServiceFabric(healthReport);
                        await Task.Delay(250);
                    }
                }
            }

            // System app reports.
            var sysAppHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(new Uri(ObserverConstants.SystemAppName));

            if (sysAppHealth != null)
            {
                foreach (var evt in sysAppHealth.HealthEvents.Where(
                              s => s.HealthInformation.SourceId.Contains(ObserverConstants.FabricSystemObserverName)
                                && s.HealthInformation.HealthState == HealthState.Error
                                || s.HealthInformation.HealthState == HealthState.Warning))
                {
                    var healthReport = new HealthReport
                    {
                        Code = FOErrorWarningCodes.Ok,
                        EntityType = EntityType.Application,
                        HealthMessage = $"Clearing existing FSO Test health reports.",
                        State = HealthState.Ok,
                        NodeName = NodeName,
                        EmitLogEvent = false,
                        AppName = new Uri(ObserverConstants.SystemAppName),
                        Property = evt.HealthInformation.Property,
                        SourceId = evt.HealthInformation.SourceId
                    };

                    var healthReporter = new ObserverHealthReporter(logger);
                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    await Task.Delay(250);
                }
            }

            // Node reports.
            var nodeHealth = await fabricClient.HealthManager.GetNodeHealthAsync(NodeName);

            if (nodeHealth != null)
            {
                var fabricObserverNodeHealthEvents = nodeHealth.HealthEvents?.Where(
                        s => (s.HealthInformation.SourceId.Contains(ObserverConstants.NodeObserverName)
                                || s.HealthInformation.SourceId.Contains(ObserverConstants.DiskObserverName))
                                && s.HealthInformation.HealthState == HealthState.Error || s.HealthInformation.HealthState == HealthState.Warning);

                foreach (var evt in fabricObserverNodeHealthEvents)
                {
                    var healthReport = new HealthReport
                    {
                        Code = FOErrorWarningCodes.Ok,
                        EntityType = EntityType.Machine,
                        HealthMessage = $"Clearing existing FSO Test health reports.",
                        State = HealthState.Ok,
                        NodeName = NodeName,
                        EmitLogEvent = false,
                        Property = evt.HealthInformation.Property,
                        SourceId = evt.HealthInformation.SourceId
                    };

                    var healthReporter = new ObserverHealthReporter(logger);
                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    await Task.Delay(250);
                }
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
            await RemoveTestApplicationAsync();
        }

        private static async Task RemoveTestApplicationAsync()
        {
            string appName = "fabric:/HealthMetrics";
            string appType = "HealthMetricsType";
            string appVersion = "1.0.0.0";
            string serviceName1 = "fabric:/HealthMetrics/BandActorService";
            string serviceName2 = "fabric:/HealthMetrics/DoctorActorService";
            string imageStoreConnectionString = @"file:C:\SfDevCluster\Data\ImageStoreShare";
            string packagePathInImageStore = "HealthMetrics";

            var fabricClient = new FabricClient();

            // Clean up the unzipped directory.
            fabricClient.ApplicationManager.RemoveApplicationPackage(imageStoreConnectionString, packagePathInImageStore);

            // Delete services.
            DeleteServiceDescription deleteServiceDescription1 = new DeleteServiceDescription(new Uri(serviceName1));
            DeleteServiceDescription deleteServiceDescription2 = new DeleteServiceDescription(new Uri(serviceName2));
            await fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription1);
            await fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription2);
           
            // Delete an application instance from the application type.
            DeleteApplicationDescription deleteApplicationDescription = new DeleteApplicationDescription(new Uri(appName));
            await fabricClient.ApplicationManager.DeleteApplicationAsync(deleteApplicationDescription);
           
            // Un-provision the application type.
            await fabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
        }

        private static async Task<bool> EnsureTestServicesExistAsync()
        {
            try
            {
                var services = await FabricClient.QueryManager.GetServiceListAsync(new Uri("fabric:/HealthMetrics"));
                return services?.Count == 2;
            }
            catch (FabricElementNotFoundException)
            {

            }

            return false;
        }

        /* End Helpers */

        /* Simple Tests */

        // It is unclear to me why TestInitialize does not work. A bug in the VS Test tool?. So, this hack.
        [TestMethod]
        public void AAAInitializeTestInfra()
        {
            Assert.IsTrue(IsLocalSFRuntimePresent());
            DeployHealthMetricsAppAsync().Wait(); 
        }

        [TestMethod]
        public void AppObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.AppObserverName);
        }

        [TestMethod]
        public void AzureStorageUploadObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AzureStorageUploadObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.AzureStorageUploadObserverName);
        }

        [TestMethod]
        public void CertificateObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new CertificateObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.CertificateObserverName);
        }

        [TestMethod]
        public void ContainerObserver_Constructor_test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new ContainerObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.ContainerObserverName);
        }

        [TestMethod]
        public void DiskObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new DiskObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.DiskObserverName);
        }

        [TestMethod]
        public void FabricSystemObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new FabricSystemObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.FabricSystemObserverName);
        }

        [TestMethod]
        public void NetworkObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new NetworkObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null); 
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NetworkObserverName);
        }

        [TestMethod]
        public void NodeObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.NodeObserverName);
        }

        [TestMethod]
        public void OSObserver_Constructor_Test()
        {
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new OSObserver(TestServiceContext);

            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.OSObserverName);
        }

        [TestMethod]
        public void SFConfigurationObserver_Constructor_Test()
        {
            using var client = new FabricClient();
            
            ObserverManager.FabricServiceContext = TestServiceContext;
            
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            ObserverManager.ObserverWebAppDeployed = true;

            using var obs = new SFConfigurationObserver(TestServiceContext);

            // These are set in derived ObserverBase.
            Assert.IsTrue(obs.ObserverLogger != null);
            Assert.IsTrue(obs.HealthReporter != null);
            Assert.IsTrue(obs.ObserverName == ObserverConstants.SFConfigurationObserverName);
        }

        /* End Simple Tests */

        /* AppObserver Initialization */

        [TestMethod]
        public async Task AppObserver_InitializeAsync_MalformedTargetAppValue_GeneratesWarning()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.targetAppMalformed.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();

            // observer had internal issues during run - in this case creating a Warning due to malformed targetApp value.
            Assert.IsTrue(obs.OperationalHealthEvents == 1);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_InvalidJson_GeneratesWarning()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.invalid.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();

            // observer had internal issues during run - in this case creating a Warning due to invalid json.
            Assert.IsTrue(obs.OperationalHealthEvents == 1);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_NoConfigFound_GeneratesWarning()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.empty.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();

            // observer had internal issues during run - in this case creating a Warning due to malformed targetApp value.
            Assert.IsTrue(obs.OperationalHealthEvents == 1);
        }

        /* Single serviceInclude/serviceExclude real tests. */

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_ServiceExcludeList_EnsureExcluded()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!await EnsureTestServicesExistAsync())
            {
                AAAInitializeTestInfra();
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.apptype.exclude.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());
            Assert.IsFalse(deployedTargets.Any(t => t.ServiceName.OriginalString.Contains("DoctorActorService")));
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_ServiceExcludeList_EnsureExcluded()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!await EnsureTestServicesExistAsync())
            {
                AAAInitializeTestInfra();
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.app.exclude.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());
            Assert.IsFalse(deployedTargets.Any(t => t.ServiceName.OriginalString.Contains("DoctorActorServiceType")));
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_ServiceIncludeList_EnsureIncluded()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!await EnsureTestServicesExistAsync())
            {
                AAAInitializeTestInfra();
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.apptype.include.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());
            Assert.IsTrue(deployedTargets.All(t => t.ServiceName.OriginalString.Contains("DoctorActorService")));
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_ServiceIncludeList_EnsureIncluded()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!await EnsureTestServicesExistAsync())
            {
                AAAInitializeTestInfra();
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.app.include.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());
            Assert.IsTrue(deployedTargets.All(t => t.ServiceName.OriginalString.Contains("DoctorActorService")));
        }

        /* Multiple exclude/include service settings for single targetApp/Type tests */

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_MultiServiceExcludeList_EnsureNotExcluded()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!await EnsureTestServicesExistAsync())
            {
                AAAInitializeTestInfra();
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.apptype.multi-exclude.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var serviceReplicas = obs.ReplicaOrInstanceList;
            Assert.IsTrue(serviceReplicas.Any());

            // You can't supply multiple Exclude lists for the same target app/type. None of the target services will be excluded..
            Assert.IsTrue(
                serviceReplicas.Count(
                    s => s.ServiceName.OriginalString == "fabric:/HealthMetrics/BandActorService"
                      || s.ServiceName.OriginalString == "fabric:/HealthMetrics/DoctorActorService") == 2);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_MultiServiceExcludeList_EnsureNotExcluded()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!await EnsureTestServicesExistAsync())
            {
                AAAInitializeTestInfra();
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.app.multi-exclude.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var serviceReplicas = obs.ReplicaOrInstanceList;
            Assert.IsTrue(serviceReplicas.Any());

            // You can't supply multiple Exclude lists for the same target app/type. None of the target services will be excluded..
            Assert.IsTrue(
                serviceReplicas.Count(
                    s => s.ServiceName.OriginalString == "fabric:/HealthMetrics/BandActorService"
                      || s.ServiceName.OriginalString == "fabric:/HealthMetrics/DoctorActorService") == 2);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_MultiServiceIncludeList_EnsureIncluded()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!await EnsureTestServicesExistAsync())
            {
                AAAInitializeTestInfra();
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.apptype.multi-include.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var serviceReplicas = obs.ReplicaOrInstanceList;
            Assert.IsTrue(serviceReplicas.Any());
            Assert.IsTrue(serviceReplicas.Count == 2);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_MultiServiceIncludeList_EnsureIncluded()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            if (!await EnsureTestServicesExistAsync())
            {
                AAAInitializeTestInfra();
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.app.multi-include.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.InitializeAsync();
            var deployedTargets = obs.ReplicaOrInstanceList;
            Assert.IsTrue(deployedTargets.Any());

            await obs.InitializeAsync();
            var serviceReplicas = obs.ReplicaOrInstanceList;
            Assert.IsTrue(serviceReplicas.Any());
            Assert.IsTrue(serviceReplicas.Count == 2);
        }

        /* End InitializeAsync tests. */

        /* ObserveAsync/ReportAsync */

        [TestMethod]
        public async Task AppObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task AppObserver_ObserveAsync_Successful_Observer_WarningsGenerated()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_warnings.config.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected warning conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task AppObserver_ObserveAsync_OldConfigStyle_Successful_Observer_WarningsGenerated()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.oldstyle_warnings.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task AppObserver_ObserveAsync_OldConfigStyle_Successful_Observer_NoWarningsGenerated()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.oldstyle_nowarnings.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task ContainerObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new ContainerObserver(TestServiceContext)
            {
                ConfigurationFilePath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "ContainerObserver.config.json"),
                EnableConcurrentMonitoring = true
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task ClusterObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ClusterObserverManager.FabricServiceContext = TestServiceContext;
            ClusterObserverManager.EtwEnabled = true;
            ClusterObserverManager.TelemetryEnabled = true;

            // On a one-node cluster like your dev machine, pass true for ignoreDefaultQueryTimeout otherwise each FabricClient query will take 2 minutes 
            // to timeout in ClusterObserver.
            var obs = new ClusterObserver.ClusterObserver(TestServiceContext, ignoreDefaultQueryTimeout: true)
            {
         
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task CertificateObserver_validCerts()
        {
            if (!IsSFRuntimePresentOnTestMachine)
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

                ObserverManager.FabricServiceContext = TestServiceContext;
                ObserverManager.TelemetryEnabled = false;
                ObserverManager.EtwEnabled = false;

                using var obs = new CertificateObserver(TestServiceContext);

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

                await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new CertificateObserver(TestServiceContext);

            using var obsMgr = new ObserverManager(obs)
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

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected error conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            // stop clears health warning
            await obsMgr.StopObserversAsync();
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task NodeObserver_Integer_Greater_Than_100_CPU_Warn_Threshold_No_Fail()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(TestServiceContext)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = 10000
            };

            await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(TestServiceContext)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsRawErrorThreshold = -42,
                FirewallRulesWarningThreshold = -1,
                ActivePortsWarningThreshold = -100
            };

            await obs.ObserveAsync(Token);

            // Bad values don't crash Initialize.
            Assert.IsFalse(obs.IsUnhealthy);

            // It ran (crashing in Initialize would not set LastRunDate, which is MinValue until set.)
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task NodeObserver_Negative_Integer_Thresholds_CPU_Mem_Ports_Firewalls_All_Data_Containers_Are_Null()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(TestServiceContext)
            {
                DataCapacity = 2,
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarningUsageThresholdPct = -1000,
                MemWarningUsageThresholdMb = -2500,
                EphemeralPortsRawErrorThreshold = -42,
                FirewallRulesWarningThreshold = -1,
                ActivePortsWarningThreshold = -100
            };

            await obs.ObserveAsync(Token);

            // Bad values don't crash Initialize.
            Assert.IsFalse(obs.IsUnhealthy);

            // Data containers are null.
            Assert.IsTrue(obs.CpuTimeData == null);
            Assert.IsTrue(obs.MemDataInUse == null);
            Assert.IsTrue(obs.MemDataPercent == null);
            Assert.IsTrue(obs.ActivePortsData == null);
            Assert.IsTrue(obs.EphemeralPortsDataRaw == null);

            // It ran (crashing in Initialize would not set LastRunDate, which is MinValue until set.)
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task OSObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new OSObserver(TestServiceContext)
            {
                ClusterManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"),
                IsObserverWebApiAppDeployed = true
            };

            // This is required since output files are only created if fo api app is also deployed to cluster..

            await obs.ObserveAsync(Token);

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
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath)).Length > 0);
        }

        [TestMethod]
        public async Task DiskObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            var warningDictionary = new Dictionary<string, double>
            {
                { @"C:\SFDevCluster\Log\Traces", 50000 }
            };

            using var obs = new DiskObserver(TestServiceContext)
            {
                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true,
                MonitorDuration = TimeSpan.FromSeconds(1),
                FolderSizeMonitoringEnabled = true,
                FolderSizeConfigDataWarning = warningDictionary
            };


            await obs.ObserveAsync(Token);

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
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath)).Length > 0);
        }

        [TestMethod]
        public async Task DiskObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrors()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            var warningDictionary = new Dictionary<string, double>
            {
                /* Windows paths.. */

                { @"%USERPROFILE%\AppData\Local\Temp", 50 },
                
                // This should be rather large.
                { "%USERPROFILE%", 50 }
            };

            using var obs = new DiskObserver(TestServiceContext)
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

            using var obsMgr = new ObserverManager(obs)
            {
                ApplicationName = "fabric:/TestApp0"
            };

            await obs.ObserveAsync(Token);

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
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath)).Length > 0);

            // Stop clears health warning
            await obsMgr.StopObserversAsync();
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NetworkObserver(TestServiceContext);
            await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NetworkObserver(TestServiceContext)
            {
                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true
            };

            await obs.ObserveAsync(Token);

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
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath)).Length > 0);
        }

        [TestMethod]
        public async Task NodeObserver_ObserveAsync_Successful_Observer_IsHealthy_WarningsOrErrorsDetected()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NodeObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                DataCapacity = 5,
                UseCircularBuffer = false,
                CpuWarningUsageThresholdPct = 0.01F, // This will generate Warning for sure.
                MemWarningUsageThresholdMb = 1, // This will generate Warning for sure.
                ActivePortsWarningThreshold = 100, // This will generate Warning for sure.
                EphemeralPortsPercentWarningThreshold = 0.01 // This will generate Warning for sure.
            };

            using var obsMgr = new ObserverManager(obs);
            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            // Stop clears health warning
            await obsMgr.StopObserversAsync();
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task SFConfigurationObserver_ObserveAsync_Successful_Observer_IsHealthy()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new SFConfigurationObserver(TestServiceContext)
            {
                IsEnabled = true,

                // This is required since output files are only created if fo api app is also deployed to cluster..
                IsObserverWebApiAppDeployed = true,
                ClusterManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml")
            };

            await obs.ObserveAsync(Token);

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
            Assert.IsTrue((await File.ReadAllLinesAsync(outputFilePath)).Length > 0);
        }

        [TestMethod]
        public async Task FabricSystemObserver_ObserveAsync_Successful_Observer_IsHealthy_NoWarningsOrErrors()
        {
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync();

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
                       
            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                IsEnabled = true,
                DataCapacity = 5,
                MonitorDuration = TimeSpan.FromSeconds(1)
            };

            await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
                        
            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                IsEnabled = true,
                MonitorDuration = TimeSpan.FromSeconds(1),
                MemWarnUsageThresholdMb = 5
            };

            await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await FabricClient.QueryManager.GetNodeListAsync();

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            
           
            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveTcpPortCountWarning = 5
            };

            await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await FabricClient.QueryManager.GetNodeListAsync();

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
           
            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                ActiveEphemeralPortCountWarning = 1
            };

            await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await FabricClient.QueryManager.GetNodeListAsync();

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
           
            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                AllocatedHandlesWarning = 100
            };

            await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            using var client = new FabricClient();
            var nodeList = await client.QueryManager.GetNodeListAsync();
            
            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            
            
            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = -42
            };

            await obs.ObserveAsync(Token);

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
            if (!IsSFRuntimePresentOnTestMachine)
            {
                return;
            }

            var nodeList = await FabricClient.QueryManager.GetNodeListAsync();

            // This is meant to be run on your dev machine's one node test cluster.
            if (nodeList?.Count > 1)
            {
                return;
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;
            
           
            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                CpuWarnUsageThresholdPct = 420
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no error conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }
    }
}