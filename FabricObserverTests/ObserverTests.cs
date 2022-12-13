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
using Microsoft.Extensions.DependencyInjection;

/***PLEASE RUN ALL OF THESE TESTS ON YOUR LOCAL DEV MACHINE WITH A RUNNING SF CLUSTER BEFORE SUBMITTING A PULL REQUEST***/

namespace FabricObserverTests
{
    [TestClass]
    public class ObserverTests
    {
        // Change this to suit your test env.
        private const string NodeName = "_Node_0";

        // Change this to suit your test env.
        private const string EtwTestsLogFolder = @"C:\temp\FOTests";

        private static readonly Uri TestServiceName = new("fabric:/app/service");
        private static readonly bool IsSFRuntimePresentOnTestMachine = IsLocalSFRuntimePresent();
        private static readonly CancellationToken Token = new();
        private static readonly ICodePackageActivationContext CodePackageContext = null;
        private static readonly StatelessServiceContext TestServiceContext = null;
        private static readonly Logger _logger = new("TestLogger", EtwTestsLogFolder, 1)
        {
            EnableETWLogging = true,
            EnableVerboseLogging = true,
        };

        private static FabricClient FabricClient => FabricClientUtilities.FabricClientSingleton;

        static ObserverTests()
        {
            /* SF runtime mocking care of ServiceFabric.Mocks by loekd.
               https://github.com/loekd/ServiceFabric.Mocks */

            // NOTE: Make changes in Settings.xml located in this project (FabricObserverTests) PackageRoot/Config directory to configure observer settings.
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
                XmlDocument xdoc = new() { XmlResolver = null };
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
            string packagePath = Path.Combine(Environment.CurrentDirectory, "HealthMetricsApp", "HealthMetrics", "pkg", "Debug");

            // Unzip the compressed HealthMetrics app package.
            System.IO.Compression.ZipFile.ExtractToDirectory(packagePathZip, "HealthMetricsApp", true);

            // Copy the HealthMetrics app package to a location in the image store.
            FabricClient.ApplicationManager.CopyApplicationPackage(imageStoreConnectionString, packagePath, packagePathInImageStore);

            // Provision the HealthMetrics application.          
            await FabricClient.ApplicationManager.ProvisionApplicationAsync(packagePathInImageStore);

            // Create HealthMetrics app instance.
            /* override app params..
            NameValueCollection nameValueCollection = new NameValueCollection();
            nameValueCollection.Add("foo", "bar");
            */
            ApplicationDescription appDesc = new(new Uri(appName), appType, appVersion/*, nameValueCollection */);
            await FabricClient.ApplicationManager.CreateApplicationAsync(appDesc);

            // Create the HealthMetrics service descriptions.
            StatefulServiceDescription serviceDescription1 = new()
            {
                ApplicationName = new Uri(appName),
                MinReplicaSetSize = 1,
                PartitionSchemeDescription = new SingletonPartitionSchemeDescription(),
                ServiceName = new Uri(serviceName1),
                ServiceTypeName = serviceType1
            };

            StatefulServiceDescription serviceDescription2 = new()
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

        private static async Task DeployTestApp42Async()
        {
            string appName = "fabric:/TestApp42";

            // If fabric:/TestApp42 is already installed, exit.
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

            string appType = "TestApp42Type";
            string appVersion = "1.0.0";

            // Change this to suit your configuration (so, if you are on Windows and you installed SF on a different drive, for example).
            string imageStoreConnectionString = @"file:C:\SfDevCluster\Data\ImageStoreShare";
            string packagePathInImageStore = "TestApp42";
            string packagePathZip = Path.Combine(Environment.CurrentDirectory, "TestApp42.zip");
            string packagePath = Path.Combine(Environment.CurrentDirectory, "TestApp42", "Release");

            // Unzip the compressed HealthMetrics app package.
            System.IO.Compression.ZipFile.ExtractToDirectory(packagePathZip, "TestApp42", true);

            // Copy the HealthMetrics app package to a location in the image store.
            FabricClient.ApplicationManager.CopyApplicationPackage(imageStoreConnectionString, packagePath, packagePathInImageStore);

            // Provision the HealthMetrics application.          
            await FabricClient.ApplicationManager.ProvisionApplicationAsync(packagePathInImageStore);

            // Create HealthMetrics app instance.
            ApplicationDescription appDesc = new(new Uri(appName), appType, appVersion);
            await FabricClient.ApplicationManager.CreateApplicationAsync(appDesc);

            // This is a hack. Withouth this timeout, the deployed test services may not have populated the FC cache?
            // You may need to increase this value depending upon your dev machine? You'll find out..
            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        private async Task DeployVotingAppAsync()
        {
            string appName = "fabric:/Voting";

            // If fabric:/Voting is already installed, exit.
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

            string appType = "VotingType";
            string appVersion = "1.0.0";

            // Change this to suit your configuration (so, if you are on Windows and you installed SF on a different drive, for example).
            string imageStoreConnectionString = @"file:C:\SfDevCluster\Data\ImageStoreShare";
            string packagePathInImageStore = "VotingApp";
            string packagePathZip = Path.Combine(Environment.CurrentDirectory, "VotingApp.zip");
            string packagePath = Path.Combine(Environment.CurrentDirectory, "VotingApp");

            // Unzip the compressed HealthMetrics app package.
            System.IO.Compression.ZipFile.ExtractToDirectory(packagePathZip, "VotingApp", true);

            // Copy the HealthMetrics app package to a location in the image store.
            FabricClient.ApplicationManager.CopyApplicationPackage(imageStoreConnectionString, packagePath, packagePathInImageStore);

            // Provision the HealthMetrics application.          
            await FabricClient.ApplicationManager.ProvisionApplicationAsync(packagePathInImageStore);

            // Create HealthMetrics app instance.
            ApplicationDescription appDesc = new(new Uri(appName), appType, appVersion);
            await FabricClient.ApplicationManager.CreateApplicationAsync(appDesc);

            // This is a hack. Withouth this timeout, the deployed test services may not have populated the FC cache?
            // You may need to increase this value depending upon your dev machine? You'll find out..
            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        private static bool IsLocalSFRuntimePresent()
        {
            try
            {
                int count = Process.GetProcessesByName("Fabric").Length;
                return count > 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static async Task CleanupTestHealthReportsAsync()
        {
            Logger logger = new("TestLogger");
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            // Remove any files generated.
            try
            {
                var outputFolder = Path.Combine(Environment.CurrentDirectory, "fabric_logs");

                if (Directory.Exists(outputFolder))
                {
                    Directory.Delete(outputFolder, true);
                }

                outputFolder = EtwTestsLogFolder;

                if (Directory.Exists(outputFolder))
                {
                    Directory.Delete(outputFolder, true);
                }
            }
            catch (IOException)
            {

            }

            await CleanupTestHealthReportsAsync();
            await RemoveTestApplicationsAsync();
        }

        private static async Task RemoveTestApplicationsAsync()
        {
            // HealthMetrics \\
            var fabricClient = new FabricClient();
            string imageStoreConnectionString = @"file:C:\SfDevCluster\Data\ImageStoreShare";

            if (await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                string appName = "fabric:/HealthMetrics";
                string appType = "HealthMetricsType";
                string appVersion = "1.0.0.0";
                string serviceName1 = "fabric:/HealthMetrics/BandActorService";
                string serviceName2 = "fabric:/HealthMetrics/DoctorActorService";
                string packagePathInImageStore = "HealthMetrics";

                // Clean up the unzipped directory.
                fabricClient.ApplicationManager.RemoveApplicationPackage(imageStoreConnectionString, packagePathInImageStore);

                // Delete services.
                DeleteServiceDescription deleteServiceDescription1 = new(new Uri(serviceName1));
                DeleteServiceDescription deleteServiceDescription2 = new(new Uri(serviceName2));
                await fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription1);
                await fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription2);

                // Delete an application instance from the application type.
                DeleteApplicationDescription deleteApplicationDescription = new(new Uri(appName));
                await fabricClient.ApplicationManager.DeleteApplicationAsync(deleteApplicationDescription);

                // Un-provision the application type.
                await fabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
            }

            // TestApp42 \\

            if (await EnsureTestServicesExistAsync("fabric:/TestApp42"))
            {
                string appName = "fabric:/TestApp42";
                string appType = "TestApp42Type";
                string appVersion = "1.0.0";
                string serviceName1 = "fabric:/TestApp42/ChildProcessCreator";
                string packagePathInImageStore = "TestApp42";

                // Clean up the unzipped directory.
                fabricClient.ApplicationManager.RemoveApplicationPackage(imageStoreConnectionString, packagePathInImageStore);

                // Delete services.
                var deleteServiceDescription1 = new DeleteServiceDescription(new Uri(serviceName1));
                await fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription1);

                // Delete an application instance from the application type.
                var deleteApplicationDescription = new DeleteApplicationDescription(new Uri(appName));
                await fabricClient.ApplicationManager.DeleteApplicationAsync(deleteApplicationDescription);

                // Un-provision the application type.
                await fabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
            }

            // Voting \\

            if (await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                string appName = "fabric:/Voting";
                string appType = "VotingType";
                string appVersion = "1.0.0";
                string serviceName1 = "fabric:/Voting/VotingData";
                string serviceName2 = "fabric:/Voting/VotingWeb";
                string packagePathInImageStore = "VotingApp";

                // Clean up the unzipped directory.
                fabricClient.ApplicationManager.RemoveApplicationPackage(imageStoreConnectionString, packagePathInImageStore);

                // Delete services.
                var deleteServiceDescription1 = new DeleteServiceDescription(new Uri(serviceName1));
                var deleteServiceDescription2 = new DeleteServiceDescription(new Uri(serviceName2));
                await fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription1);
                await fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription2);

                // Delete an application instance from the application type.
                var deleteApplicationDescription = new DeleteApplicationDescription(new Uri(appName));
                await fabricClient.ApplicationManager.DeleteApplicationAsync(deleteApplicationDescription);

                // Un-provision the application type.
                await fabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
            }
        }

        private static async Task<bool> EnsureTestServicesExistAsync(string appName)
        {
            try
            {
                var services = await FabricClient.QueryManager.GetServiceListAsync(new Uri(appName));
                return services?.Count > 0;
            }
            catch (FabricElementNotFoundException)
            {

            }

            return false;
        }

        /* End Helpers */

        /* Simple Tests */

        [TestMethod]
        public void AAAInitializeTestInfra()
        {
            Assert.IsTrue(IsLocalSFRuntimePresent());

            DeployHealthMetricsAppAsync().Wait();
            DeployTestApp42Async().Wait();
            DeployVotingAppAsync().Wait();
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.invalid.json"),
                EnableConcurrentMonitoring = true,
                EnableChildProcessMonitoring = true
            };

            await obs.InitializeAsync();

            // observer had internal issues during run - in this case creating a Warning due to invalid json.
            Assert.IsTrue(obs.OperationalHealthEvents == 1);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_NoConfigFound_GeneratesWarning()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.empty.json"),
                EnableConcurrentMonitoring = true,
                EnableChildProcessMonitoring = true
            };

            await obs.InitializeAsync();

            // observer had internal issues during run - in this case creating a Warning due to malformed targetApp value.
            Assert.IsTrue(obs.OperationalHealthEvents == 1);
        }

        /* Single serviceInclude/serviceExclude real tests. */

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_ServiceExcludeList_EnsureExcluded()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
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
        public async Task AppObserver_ObserveAsync_Successful_IsHealthy()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.json"),
                EnableConcurrentMonitoring = true,
                IsEtwProviderEnabled = true,
                EnableChildProcessMonitoring = true,
                MaxChildProcTelemetryDataCount = 25,
                MonitorResourceGovernanceLimits = true
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
        public async Task AppObserver_ObserveAsync_Successful_WarningsGenerated()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_warnings.config.json"),
                EnableConcurrentMonitoring = true,
                CheckPrivateWorkingSet = true,
                IsEtwProviderEnabled = true
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
        public async Task AppObserver_ObserveAsync_PrivateBytes_Successful_WarningsGenerated()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_PrivateBytes_warning.config.json"),
                IsEtwProviderEnabled = true
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected warning conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        // RG \\

        [TestMethod]
        public async Task AppObserver_ObserveAsync_Successful_RGLimitWarningGenerated()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);
            
            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_rg_warning.config.json"),
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
        public async Task AppObserver_ObserveAsync_Successful_RGLimit_Validate_Multiple_Memory_Specification()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1)
            };

            await obs.ObserveAsync(Token);

            Assert.IsTrue(obs.MonitorResourceGovernanceLimits == true);

            var clientUtilities = new FabricClientUtilities(NodeName);
            string appManifest =
                await FabricClient.ApplicationManager.GetApplicationManifestAsync(
                        "VotingType", "1.0.0",
                        TimeSpan.FromSeconds(60),
                        Token);

            // VotingWeb has both MemoryInMB and MemoryInMBLimit specified in a code package rg policy node. Ensure that only the value
            // of MemoryInMBLimit is used (this is known to be 2048, whereas MemoryInMB is known to be 1024, per the application's App manifest).
            var (RGEnabled, RGMemoryLimit) = clientUtilities.TupleGetMemoryResourceGovernanceInfo(appManifest, "VotingWebPkg", "Code");
            Assert.IsTrue(RGEnabled);
            Assert.IsTrue(RGMemoryLimit == 2048);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task AppObserver_ObserveAsync_OldConfigStyle_Successful_WarningsGenerated()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
        public async Task AppObserver_ObserveAsync_OldConfigStyle_Successful_NoWarningsGenerated()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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

        #region Dump Tests

        [TestMethod]
        public async Task AppObserver_DumpProcessOnWarning_SuccessfulDumpCreation()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_warnings_dmps.config.json"),
                DumpsPath = Path.Combine(EtwTestsLogFolder, "AppObserver", "MemoryDumps")
            };

            await obs.ObserveAsync(Token);

            Assert.IsTrue(Directory.Exists(obs.DumpsPath));

            var dmps = Directory.GetFiles(obs.DumpsPath, "*.dmp");

            Assert.IsTrue(dmps != null && dmps.Any());

            // VotingData service, and two helper codepackage binaries.
            Assert.IsTrue(dmps.All(d => d.Contains("VotingData") || d.Contains("ConsoleApp6") || d.Contains("ConsoleApp7")));

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            // Clean up.
            Directory.Delete(obs.DumpsPath, true);

            await CleanupTestHealthReportsAsync();
        }

        [TestMethod]
        public async Task AppObserver_DumpProcessOnError_SuccessfulDumpCreation()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_errors_dmps.config.json"),
                DumpsPath = Path.Combine(EtwTestsLogFolder, "AppObserver", "MemoryDumps")
            };

            await obs.ObserveAsync(Token);

            Assert.IsTrue(Directory.Exists(obs.DumpsPath));

            var dmps = Directory.GetFiles(obs.DumpsPath, "*.dmp");
            
            Assert.IsTrue(dmps != null && dmps.Any());

            // VotingData service, and two helper codepackage binaries.
            Assert.IsTrue(dmps.All(d => d.Contains("VotingData") || d.Contains("ConsoleApp6") || d.Contains("ConsoleApp7")));

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            // Clean up.
            Directory.Delete(obs.DumpsPath, true);

            await CleanupTestHealthReportsAsync();
        }
        #endregion

        [TestMethod]
        public async Task ContainerObserver_ObserveAsync_Successful_IsHealthy()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
        public async Task ClusterObserver_ObserveAsync_Successful_IsHealthy()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new CertificateObserver(TestServiceContext);
            IServiceCollection services = new ServiceCollection();
            services.AddScoped(typeof(ObserverBase), s => obs);
            await using ServiceProvider serviceProvider = services.BuildServiceProvider();

            using var obsMgr = new ObserverManager(serviceProvider, Token)
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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
        public async Task OSObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

            using var obs = new OSObserver(TestServiceContext)
            {
                ClusterManifestPath = Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"),
                IsObserverWebApiAppDeployed = true,
                IsEtwProviderEnabled = true
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
        public async Task DiskObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

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
                FolderSizeConfigDataWarning = warningDictionary,
                IsEtwProviderEnabled = true
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
        public async Task DiskObserver_ObserveAsync_Successful_IsHealthy_WarningsOrErrors()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

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
                MonitorDuration = TimeSpan.FromSeconds(5),
                IsEtwProviderEnabled = true,
            };

            IServiceCollection services = new ServiceCollection();
            services.AddScoped(typeof(ObserverBase), s => obs);
            await using ServiceProvider serviceProvider = services.BuildServiceProvider();

            using var obsMgr = new ObserverManager(serviceProvider, Token)
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
        public async Task NetworkObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
        public async Task NetworkObserver_ObserveAsync_Successful_WritesLocalFile_ObsWebDeployed()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
        public async Task NodeObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrorsDetected()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

            using var obs = new NodeObserver(TestServiceContext)
            {
                IsEnabled = true,
                MonitorDuration = TimeSpan.FromSeconds(5),
                CpuWarningUsageThresholdPct = 90, // This will generate Warning for sure.
                ActivePortsWarningThreshold = 10000,
                MemoryWarningLimitPercent = 90,
                EphemeralPortsPercentWarningThreshold = 30,
                FirewallRulesWarningThreshold = 3000,
                IsEtwProviderEnabled = true
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);

            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task NodeObserver_ObserveAsync_Successful_IsHealthy_WarningsOrErrorsDetected()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

            using var obs = new NodeObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                DataCapacity = 5,
                UseCircularBuffer = false,
                IsEtwProviderEnabled = true,
                CpuWarningUsageThresholdPct = 0.01F, // This will generate Warning for sure.
                MemWarningUsageThresholdMb = 1, // This will generate Warning for sure.
                ActivePortsWarningThreshold = 100, // This will generate Warning for sure.
                EphemeralPortsPercentWarningThreshold = 0.01 // This will generate Warning for sure.
            };

            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }

        [TestMethod]
        public async Task SFConfigurationObserver_ObserveAsync_Successful_IsHealthy()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
        public async Task FabricSystemObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
            ObserverManager.EtwEnabled = true;

            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                IsEnabled = true,
                DataCapacity = 5,
                MonitorDuration = TimeSpan.FromSeconds(1),
                IsEtwProviderEnabled = true,
                MemWarnUsageThresholdMb = 10000,
                CpuWarnUsageThresholdPct = 90,
                ActiveEphemeralPortCountWarning = 20000
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
        public async Task FabricSystemObserver_ObserveAsync_Successful_IsHealthy_MemoryWarningsOrErrorsDetected()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

            using var obs = new FabricSystemObserver(TestServiceContext)
            {
                IsEnabled = true,
                MonitorDuration = TimeSpan.FromSeconds(1),
                MemWarnUsageThresholdMb = 5,
                IsEtwProviderEnabled = true
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
        public async Task FabricSystemObserver_ObserveAsync_Successful_IsHealthy_ActiveTcpPortsWarningsOrErrorsDetected()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
                ActiveTcpPortCountWarning = 3
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
        public async Task FabricSystemObserver_ObserveAsync_Successful_IsHealthy_EphemeralPortsWarningsOrErrorsDetected()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
        public async Task FabricSystemObserver_ObserveAsync_Successful_IsHealthy_HandlesWarningsOrErrorsDetected()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

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

        [TestMethod]
        public void Ephemeral_Ports_Machine_Total_Greater_Than_Zero()
        {
            int ports = OSInfoProvider.Instance.GetActiveEphemeralPortCount();

            // 0 would mean something failed in the impl or that there are no active TCP connections on the machine (unlikely).
            Assert.IsTrue(ports > 0);
        }

        [TestMethod]
        public void Active_TCP_Ports_Machine_Total_Greater_Than_Zero()
        {
            int ports = OSInfoProvider.Instance.GetActiveTcpPortCount();

            // 0 would mean something failed in the impl or that there are no active TCP connections on the machine (highly unlikely).
            Assert.IsTrue(ports > 0);
        }

        [TestMethod]
        public void Validate_Dynamic_Port_Range_LowPort_HighPort_TotalNumberOfPorts()
        {
            var (LowPort, HighPort, NumberOfPorts) = OSInfoProvider.Instance.TupleGetDynamicPortRange();
            Assert.IsTrue(NumberOfPorts > 0);
            Assert.IsTrue(LowPort > 0);
            Assert.IsTrue(HighPort > LowPort);
            Assert.IsTrue(NumberOfPorts == HighPort - LowPort);
        }

        [TestMethod]
        public void Active_Ephemeral_Ports_Machine_Total_Greater_Than_Zero()
        {
            int ports = OSInfoProvider.Instance.GetActiveEphemeralPortCount();

            // 0 would mean something failed in the impl or that there are no active TCP connections in the dynamic range on the machine (highly unlikely).
            Assert.IsTrue(ports > 0);
        }

        [TestMethod]
        public void Active_TCP_Ports_Machine_Greater_Than_Active_Ephemeral_Ports_Machine()
        {
            int total_tcp_ports = OSInfoProvider.Instance.GetActiveTcpPortCount();
            int ephemeral_tcp_ports = OSInfoProvider.Instance.GetActiveEphemeralPortCount();
            
            Assert.IsTrue(total_tcp_ports > 0 && ephemeral_tcp_ports > 0);
            Assert.IsTrue(total_tcp_ports > ephemeral_tcp_ports);
        }

        #region ETW Tests

        // AppObserver: ChildProcessTelemetryData \\

        [TestMethod]
        public async Task AppObserver_ETW_EventData_IsChildProcessTelemetryData()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/TestApp42"))
            {
                await DeployTestApp42Async();

                // Ensure enough time for child process creation by the test service parent process.
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
            using var foEtwListener = new FabricObserverEtwListener(_logger);
            await AppObserver_ObserveAsync_Successful_IsHealthy();
            List<List<ChildProcessTelemetryData>> childProcessTelemetryData = foEtwListener.foEtwConverter.ChildProcessTelemetry;
            
            Assert.IsNotNull(childProcessTelemetryData);
            Assert.IsTrue(childProcessTelemetryData.Count > 0);

            foreach (var t in childProcessTelemetryData)
            {
                foreach (var x in t)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(x.ApplicationName));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(x.ServiceName));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(x.Metric));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(x.ProcessName));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(x.ProcessStartTime));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(x.PartitionId));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(x.NodeName));
                    Assert.IsFalse(x.ReplicaId == 0);
                    Assert.IsFalse(x.ChildProcessCount == 0);

                    Assert.IsTrue(x.ChildProcessInfo != null && x.ChildProcessInfo.Count == x.ChildProcessCount);
                    Assert.IsTrue(x.ChildProcessInfo.FindAll(p => p.ProcessId > 0).Distinct().Count() == x.ChildProcessCount);

                    foreach (var c in x.ChildProcessInfo)
                    {
                        Assert.IsFalse(string.IsNullOrWhiteSpace(c.ProcessName));
                    
                        Assert.IsTrue(
                            !string.IsNullOrWhiteSpace(c.ProcessStartTime) 
                            && DateTime.TryParse(c.ProcessStartTime, out DateTime startTime) && startTime > DateTime.MinValue);
                        Assert.IsTrue(c.Value > -1);
                        Assert.IsTrue(c.ProcessId > 0);
                    }
                }
            }
        }

        // AppObserver: TelemetryData \\

        [TestMethod]
        public async Task AppObserver_ETW_EventData_IsTelemetryData()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/TestApp42"))
            {
                await DeployTestApp42Async();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await AppObserver_ObserveAsync_Successful_IsHealthy();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            foreach (var t in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ApplicationType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ServiceName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));

                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(t.ProcessStartTime)
                    && DateTime.TryParse(t.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(t.EntityType == EntityType.Service || t.EntityType == EntityType.Process);
                Assert.IsTrue(t.ServicePackageActivationMode == "ExclusiveProcess"
                              || t.ServicePackageActivationMode == "SharedProcess");
                Assert.IsTrue(t.HealthState == HealthState.Invalid);
                Assert.IsTrue(t.ProcessId > 0);
                Assert.IsTrue(t.ObserverName == ObserverConstants.AppObserverName);
                Assert.IsTrue(t.Code == null);
                Assert.IsTrue(t.Description == null);
                Assert.IsTrue(t.Source == ObserverConstants.AppObserverName);
                Assert.IsTrue(t.Value >= 0.0);
            }
        }

        [TestMethod]
        public async Task AppObserver_ETW_EventData_IsTelemetryData_HealthWarnings()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/HealthMetrics"))
            {
                await DeployHealthMetricsAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await AppObserver_ObserveAsync_Successful_WarningsGenerated();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            var warningEvents = telemData.Where(t => t.HealthState == HealthState.Warning);
            Assert.IsTrue(warningEvents.Any());

            foreach (var t in warningEvents)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ApplicationType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Code));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Description));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Property));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ServiceName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));

                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(t.ProcessStartTime)
                    && DateTime.TryParse(t.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(t.EntityType == EntityType.Service || t.EntityType == EntityType.Process);
                Assert.IsTrue(t.ServicePackageActivationMode == "ExclusiveProcess"
                              || t.ServicePackageActivationMode == "SharedProcess");
                Assert.IsTrue(t.HealthState == HealthState.Warning);
                Assert.IsTrue(t.ProcessId > 0);
                Assert.IsTrue(t.Value > 0.0);
                Assert.IsTrue(t.ObserverName == ObserverConstants.AppObserverName);
                Assert.IsTrue(t.Source == $"{t.ObserverName}({t.Code})");
            }
        }

        // RG
        [TestMethod]
        public async Task AppObserver_ETW_EventData_RGEnabled_MemoryInMB_Or_MemoryInMBLimit_ValuesAreNonZero()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await AppObserver_ObserveAsync_Successful_IsHealthy();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;

            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(t => t.ApplicationName == "fabric:/Voting").ToList();
            Assert.IsTrue(telemData.Any());

            foreach (var t in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ApplicationType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ServiceName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(t.ProcessStartTime)
                    && DateTime.TryParse(t.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(t.EntityType == EntityType.Service || t.EntityType == EntityType.Process);
                Assert.IsTrue(t.ServicePackageActivationMode == "ExclusiveProcess"
                              || t.ServicePackageActivationMode == "SharedProcess");
                Assert.IsTrue(t.HealthState == HealthState.Invalid);
                Assert.IsTrue(t.ProcessId > 0);
                Assert.IsTrue(t.ObserverName == ObserverConstants.AppObserverName);
                Assert.IsTrue(t.Code == null);
                Assert.IsTrue(t.Description == null);
                Assert.IsTrue(t.Source == ObserverConstants.AppObserverName);
                
                // RG
                if (t.ProcessName == "VotingData" || t.ProcessName == "VotingWeb" || t.ProcessName == "ConsoleApp6" || t.ProcessName == "ConsoleApp7")
                {
                    Assert.IsTrue(t.RGMemoryEnabled && t.RGAppliedMemoryLimitMb > 0);     
                }

                Assert.IsTrue(t.Value >= 0.0);
            }
        }

        // Private Bytes
        [TestMethod]
        public async Task AppObserver_ETW_PrivateBytes_Multiple_CodePackages_ValuesAreNonZero_Warnings_MB_Percent()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            using var foEtwListener = new FabricObserverEtwListener(_logger);
            await AppObserver_ObserveAsync_PrivateBytes_Successful_WarningsGenerated();
            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;

            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(
                t => t.ApplicationName == "fabric:/Voting" && t.HealthState == HealthState.Warning).ToList();

            // 2 service code packages + 2 helper code packages (VotingData) * 2 metrics = 8 warnings...
            Assert.IsTrue(telemData.Any() && telemData.Count == 8);
        }

        // Private Bytes
        [TestMethod]
        public async Task AppObserver_ETW_PrivateBytes_Warning_ChildProcesses()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/TestApp42"))
            {
                await DeployTestApp42Async();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
            using var foEtwListener = new FabricObserverEtwListener(_logger);
            await AppObserver_ObserveAsync_PrivateBytes_Successful_WarningsGenerated();
            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            List<List<ChildProcessTelemetryData>> childProcessTelemetryData = foEtwListener.foEtwConverter.ChildProcessTelemetry;

            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);
            Assert.IsNotNull(childProcessTelemetryData);
            Assert.IsTrue(childProcessTelemetryData.Count > 0);

            // We only care about the launched test app, fabric:/TestApp42, and one metric, Private Bytes (MB).
            childProcessTelemetryData =
                childProcessTelemetryData.Where(
                    c => c.Find(cti => cti.ApplicationName == "fabric:/TestApp42").Metric == ErrorWarningProperty.PrivateBytesMb).ToList();
            
            // Ensure parent service is put into warning.
            telemData = telemData.Where(
                t => t.ApplicationName == "fabric:/TestApp42" && t.HealthState == HealthState.Warning).ToList();

            // TestApp42 service launches 3 child processes.
            Assert.IsTrue(childProcessTelemetryData[0][0].ChildProcessInfo.Count == 3);

            // 1 service code package (with 2 children) * 1 metric = 1 warning (parent).
            Assert.IsTrue(telemData.Count(t => t.ApplicationName == "fabric:/TestApp42" && t.Metric == ErrorWarningProperty.PrivateBytesMb) == 1);

            // All children should definitely have more than 0 bytes committed.
            Assert.IsTrue(childProcessTelemetryData.All(
                c => c.TrueForAll(ct => ct.ApplicationName == "fabric:/TestApp42" && ct.Value > 0)));
        }

        // RG - warningRGMemoryLimitPercent
        [TestMethod]
        public async Task AppObserver_ETW_RGMemoryLimitPercent_Warning()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            using var foEtwListener = new FabricObserverEtwListener(_logger);
            await AppObserver_ObserveAsync_Successful_RGLimitWarningGenerated();
            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;

            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(
                t => t.ApplicationName == "fabric:/Voting" && t.HealthState == HealthState.Warning).ToList();

            // 2 service code packages + 2 helper code packages (VotingData) * 1 metric = 4 warnings...
            Assert.IsTrue(telemData.All(t => t.Metric == ErrorWarningProperty.RGMemoryUsagePercent) && telemData.Count == 4);
        }

        // DiskObserver: TelemetryData \\

        [TestMethod]
        public async Task DiskObserver_ETW_EventData_IsTelemetryData()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await DiskObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            foreach (var t in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Property));

                Assert.IsTrue(t.EntityType == EntityType.Disk);
                Assert.IsTrue(t.HealthState == HealthState.Invalid);
                Assert.IsTrue(t.ObserverName == ObserverConstants.DiskObserverName);
                Assert.IsTrue(t.Code == null);
                Assert.IsTrue(t.Description == null);
                Assert.IsTrue(t.Value > 0.0);
            }
        }

        [TestMethod]
        public async Task DiskObserver_ETW_EventData_IsTelemetryData_Warnings()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await DiskObserver_ObserveAsync_Successful_IsHealthy_WarningsOrErrors();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(d => d.HealthState == HealthState.Warning).ToList();
            Assert.IsTrue(telemData.Any());

            foreach (var t in telemData)
            {
                Assert.IsTrue(t.EntityType == EntityType.Disk);
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Code));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Description));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Property));

                Assert.IsTrue(t.HealthState == HealthState.Warning);
                Assert.IsTrue(t.ObserverName == ObserverConstants.DiskObserverName);
                Assert.IsTrue(t.Value > 0.0);
                Assert.IsTrue(t.Source == $"{t.ObserverName}({t.Code})");
            }
        }

        // FabricSystemObserver: TelemetryData \\

        [TestMethod]
        public async Task FabricSystemObserver_ETW_EventData_IsTelemetryData()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await FabricSystemObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            foreach (var t in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));

                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(t.ProcessStartTime)
                    && DateTime.TryParse(t.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(t.EntityType == EntityType.Application);
                Assert.IsTrue(t.HealthState == HealthState.Invalid);
                Assert.IsTrue(t.ProcessId > 0);
                Assert.IsTrue(t.ObserverName == ObserverConstants.FabricSystemObserverName);
                Assert.IsTrue(t.Code == null);
                Assert.IsTrue(t.Description == null);
                Assert.IsTrue(t.Source == ObserverConstants.FabricSystemObserverName);
                Assert.IsTrue(t.Value >= 0.0);
            }
        }

        [TestMethod]
        public async Task FabricSystemObserver_ETW_EventData_IsTelemetryData_Warnings()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await FabricSystemObserver_ObserveAsync_Successful_IsHealthy_MemoryWarningsOrErrorsDetected();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(d => d.HealthState == HealthState.Warning).ToList();
            Assert.IsTrue(telemData.Any());

            foreach (var t in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));

                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(t.ProcessStartTime)
                    && DateTime.TryParse(t.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(t.EntityType == EntityType.Application);
                Assert.IsTrue(t.HealthState == HealthState.Warning);
                Assert.IsTrue(t.ProcessId > 0);
                Assert.IsTrue(t.ObserverName == ObserverConstants.FabricSystemObserverName);
                Assert.IsTrue(t.Code != null);
                Assert.IsTrue(t.Description != null);
                Assert.IsTrue(t.Property != null);
                Assert.IsTrue(t.Source == $"{t.ObserverName}({t.Code})");
                Assert.IsTrue(t.Value > 0.0);
            }
        }

        // NodeObserver: TelemetryData \\

        [TestMethod]
        public async Task NodeObserver_ETW_EventData_IsTelemetryData()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await NodeObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrorsDetected();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            foreach (var t in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));
                Assert.IsFalse(t.Property == null);

                Assert.IsTrue(t.EntityType == EntityType.Machine);
                Assert.IsTrue(t.HealthState == HealthState.Invalid);
                Assert.IsTrue(t.ObserverName == ObserverConstants.NodeObserverName);
                Assert.IsTrue(t.Code == null);
                Assert.IsTrue(t.Description == null);
                Assert.IsTrue(t.Source == ObserverConstants.NodeObserverName);
                Assert.IsTrue(t.Value >= 0.0);
            }
        }

        [TestMethod]
        public async Task NodeObserver_ETW_EventData_IsTelemetryData_Warnings()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await NodeObserver_ObserveAsync_Successful_IsHealthy_WarningsOrErrorsDetected();

            List<TelemetryData> telemData = foEtwListener.foEtwConverter.TelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(d => d.HealthState == HealthState.Warning).ToList();
            Assert.IsTrue(telemData.Any());

            foreach (var t in telemData)
            {
                Assert.IsTrue(t.ObserverName == ObserverConstants.NodeObserverName);
                Assert.IsTrue(t.EntityType == EntityType.Machine);

                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.OS));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Code));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Property));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t.Description));

                Assert.IsTrue(t.HealthState == HealthState.Warning);
                Assert.IsTrue(t.Source == $"{t.ObserverName}({t.Code})");
                Assert.IsTrue(t.Value > 0.0);
            }
        }

        // OSObserver: MachineTelemetryData \\

        [TestMethod]
        public async Task OSObserver_ETW_EventData_IsMachineTelemetryData()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            using var foEtwListener = new FabricObserverEtwListener(_logger);
            await OSObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors();
            MachineTelemetryData machineTelemetryData = foEtwListener.foEtwConverter.MachineTelemetryData;
            
            Assert.IsNotNull(machineTelemetryData);

            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.DriveInfo));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.EphemeralTcpPortRange));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.FabricApplicationTcpPortRange));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.HealthState));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.HotFixes));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.LastBootUpTime));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.NodeName));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.ObserverName));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.OSInstallDate));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.OSName));
            Assert.IsFalse(string.IsNullOrWhiteSpace(machineTelemetryData.OSVersion));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.IsTrue(machineTelemetryData.ActiveFirewallRules > 0);
            }

            Assert.IsTrue(machineTelemetryData.ActiveEphemeralTcpPorts > 0);
            Assert.IsTrue(machineTelemetryData.ActiveTcpPorts > 0);
            Assert.IsTrue(machineTelemetryData.AvailablePhysicalMemoryGB > 0);
            Assert.IsTrue(machineTelemetryData.FreeVirtualMemoryGB > 0);
            Assert.IsTrue(machineTelemetryData.LogicalDriveCount > 0);
            Assert.IsTrue(machineTelemetryData.LogicalProcessorCount > 0);
            Assert.IsTrue(machineTelemetryData.NumberOfRunningProcesses > 0);
            Assert.IsTrue(machineTelemetryData.TotalMemorySizeGB > 0);
        }
        #endregion

        #region CodePackage Tests

        // Tests for ensuring ServiceManifests that specify multiple code packages are correctly handled by AppObserver. \\

        [TestMethod]
        public async Task AppObserver_Detects_Monitors_Multiple_Helper_CodePackages()
        {
            Assert.IsTrue(IsSFRuntimePresentOnTestMachine);

            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver.config.json"),
                EnableConcurrentMonitoring = true,
                EnableChildProcessMonitoring = true
            };

            var startDateTime = DateTime.Now;
            
            await obs.InitializeAsync();

            // fabric:/Voting application has 2 default services (that create service types) and 2 extra CodePackages (specified in VotingData manifest)
            // that contain helper binaries, ConsoleApp6.exe and ConsoleApp7.exe. Therefore, Console6App7 and ConsoleApp7 processes should be added to ReplicaOrInstanceList
            // and therefore will be treated like any service that AppObserver monitors.
            Assert.IsTrue(obs.ReplicaOrInstanceList.Any(r => r.HostProcessName == "ConsoleApp6"));
            Assert.IsTrue(obs.ReplicaOrInstanceList.Any(r => r.HostProcessName == "ConsoleApp7"));
            
            await obs.ObserveAsync(Token);

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // observer detected no warning conditions.
            Assert.IsFalse(obs.HasActiveFabricErrorOrWarning);

            // observer did not have any internal errors during run.
            Assert.IsFalse(obs.IsUnhealthy);
        }
        #endregion
    }
}