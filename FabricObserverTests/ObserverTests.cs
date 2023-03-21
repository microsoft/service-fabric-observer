// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using ClusterObserver;
using FabricObserver.Interfaces;
using FabricObserver.Observers;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.Utilities.ServiceFabric;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ServiceFabric.Mocks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static ServiceFabric.Mocks.MockConfigurationPackage;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

/***PLEASE RUN ALL OF THESE TESTS ON YOUR LOCAL DEV MACHINE WITH A RUNNING SF CLUSTER BEFORE SUBMITTING A PULL REQUEST***/

namespace FabricObserverTests
{
    [TestClass]
    public class ObserverTests
    {
        // Change this to suit your test env.
        private const string NodeName = "_Node_0";
        private static readonly Uri TestServiceName = new("fabric:/app/service");
        private static readonly CancellationToken Token = new();
        private static ICodePackageActivationContext CodePackageContext = null;
        private static StatelessServiceContext TestServiceContext = null;
        private static readonly Logger _logger = new("TestLogger", Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs"), 1)
        {
            EnableETWLogging = true,
            EnableVerboseLogging = true,
        };

        private static FabricClient FabricClient => FabricClientUtilities.FabricClientSingleton;

        [ClassInitialize]
        public static async Task TestClassStartUp(TestContext testContext)
        {
            if (!IsLocalSFRuntimePresent())
            {
                throw new Exception("Can't run these tests without a local dev cluster");
            }

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

            // Install required SF test applications.
            await DeployHealthMetricsAppAsync();
            await DeployTestApp42Async();
            await DeployVotingAppAsync();
        }

        [ClassCleanup]
        public static async Task TestClassCleanupAsync()
        {
            Assert.IsTrue(IsLocalSFRuntimePresent());

            // Remove any files generated.
            try
            {
                var outputFolder = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs");

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

            try
            {
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
            catch (FabricException fe)
            {
                if (fe.ErrorCode == FabricErrorCode.ApplicationAlreadyExists)
                {
                    await FabricClient.ApplicationManager.DeleteApplicationAsync(new DeleteApplicationDescription(new Uri(appName)) { ForceDelete = true});
                    await DeployHealthMetricsAppAsync();
                }
                else if (fe.ErrorCode == FabricErrorCode.ApplicationTypeAlreadyExists)
                {
                    var appList = await FabricClient.QueryManager.GetApplicationListAsync(new Uri(appName));
                    if (appList.Count > 0)
                    {
                        await FabricClient.ApplicationManager.DeleteApplicationAsync(new DeleteApplicationDescription(new Uri(appName)) { ForceDelete = true });
                    }
                    await FabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
                    await DeployHealthMetricsAppAsync();
                }
            }
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

            try
            {
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
            catch (FabricException fe)
            {
                if (fe.ErrorCode == FabricErrorCode.ApplicationAlreadyExists)
                {
                    await FabricClient.ApplicationManager.DeleteApplicationAsync(new DeleteApplicationDescription(new Uri(appName)) { ForceDelete = true });
                    await DeployTestApp42Async();
                }
                else if (fe.ErrorCode == FabricErrorCode.ApplicationTypeAlreadyExists)
                {
                    var appList = await FabricClient.QueryManager.GetApplicationListAsync(new Uri(appName));
                    if (appList.Count > 0)
                    {
                        await FabricClient.ApplicationManager.DeleteApplicationAsync(new DeleteApplicationDescription(new Uri(appName)) { ForceDelete = true });
                    }
                    await FabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
                    await DeployTestApp42Async();
                }
            }
        }

        private static async Task DeployVotingAppAsync()
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

            try
            {
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
            catch (FabricException fe)
            {
                if (fe.ErrorCode == FabricErrorCode.ApplicationAlreadyExists)
                {
                    await FabricClient.ApplicationManager.DeleteApplicationAsync(new DeleteApplicationDescription(new Uri(appName)) { ForceDelete = true });
                    await DeployVotingAppAsync();
                }
                else if (fe.ErrorCode == FabricErrorCode.ApplicationTypeAlreadyExists)
                {
                    var appList = await FabricClient.QueryManager.GetApplicationListAsync(new Uri(appName));
                    if (appList.Count > 0)
                    {
                        await FabricClient.ApplicationManager.DeleteApplicationAsync(new DeleteApplicationDescription(new Uri(appName)) { ForceDelete = true });
                    }
                    await FabricClient.ApplicationManager.UnprovisionApplicationAsync(appType, appVersion);
                    await DeployVotingAppAsync();
                }
            }
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
                           s => s.HealthInformation.HealthState is HealthState.Error or HealthState.Warning);

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

                        var healthReporter = new ObserverHealthReporter(_logger);
                        healthReporter.ReportHealthToServiceFabric(healthReport);
                        await Task.Delay(250);
                    }
                }
            }

            // NetworkObserver App reports.
            foreach (var app in apps)
            {
                var appHealth = await fabricClient.HealthManager.GetApplicationHealthAsync(app.ApplicationName);

                foreach (var evt in appHealth.HealthEvents.Where(
                              s => s.HealthInformation.SourceId.Contains(ObserverConstants.NetworkObserverName)
                                && s.HealthInformation.HealthState == HealthState.Error
                                || s.HealthInformation.HealthState == HealthState.Warning))
                {
                    var healthReport = new HealthReport
                    {
                        Code = FOErrorWarningCodes.Ok,
                        EntityType = EntityType.Application,
                        HealthMessage = $"Clearing existing NetworkObserver Test health reports.",
                        State = HealthState.Ok,
                        NodeName = NodeName,
                        EmitLogEvent = false,
                        AppName = app.ApplicationName,
                        Property = evt.HealthInformation.Property,
                        SourceId = evt.HealthInformation.SourceId
                    };

                    var healthReporter = new ObserverHealthReporter(_logger);
                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    await Task.Delay(250);
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

                    var healthReporter = new ObserverHealthReporter(_logger);
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

                    var healthReporter = new ObserverHealthReporter(_logger);
                    healthReporter.ReportHealthToServiceFabric(healthReport);
                    await Task.Delay(250);
                }
            }
        }

        private static bool InstallCerts()
        {
            if (OperatingSystem.IsLinux())
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
                    s => s.ServiceName.OriginalString is "fabric:/HealthMetrics/BandActorService"
                      or "fabric:/HealthMetrics/DoctorActorService") == 2);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetApp_MultiServiceExcludeList_EnsureNotExcluded()
        {
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
                    s => s.ServiceName.OriginalString is "fabric:/HealthMetrics/BandActorService"
                      or "fabric:/HealthMetrics/DoctorActorService") == 2);
        }

        [TestMethod]
        public async Task AppObserver_InitializeAsync_TargetAppType_MultiServiceIncludeList_EnsureIncluded()
        {
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

        #region Concurrency Tests

        [TestMethod]
        public async Task Ensure_ConcurrentQueue_Collection_Has_Data_CPU_Win32Impl()
        {
            FabricClientUtilities fabricClientUtilities = new(NodeName);
            var services = await fabricClientUtilities.GetAllDeployedReplicasOrInstancesAsync(true, Token);

            Assert.IsTrue(services.Any());

            ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData = new();
            ConcurrentQueue<int> serviceProcs = new();

            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = -1,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            _ = Parallel.For(0, services.Count, parallelOptions, (i, state) =>
            {
                var service = services[i];
                string procName = NativeMethods.GetProcessNameFromId((int)service.HostProcessId);

                _ = AllAppCpuData.TryAdd($"{procName}:{service.HostProcessId}", new FabricResourceUsageData<double>(
                        property: ErrorWarningProperty.CpuTime,
                        id: $"{procName}:{service.HostProcessId}",
                        dataCapacity: 8,
                        useCircularBuffer: false,
                        isParallel: true));

                serviceProcs.Enqueue((int)service.HostProcessId);
            });

            Assert.IsTrue(AllAppCpuData.Any() && serviceProcs.Any());
            Assert.IsTrue(serviceProcs.Count == AllAppCpuData.Count);

            TimeSpan duration = TimeSpan.FromSeconds(3);

            _ = Parallel.For(0, serviceProcs.Count, parallelOptions, (i, state) =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                int procId = serviceProcs.ElementAt(i);
                string procName = NativeMethods.GetProcessNameFromId(procId);
                CpuUsageWin32 cpuUsage = new();

                while (sw.Elapsed <= duration)
                {
                    double cpu = cpuUsage.GetCurrentCpuUsagePercentage(procId, procName);

                    // procId is no longer mapped to process. see CpuUsageProcess/CpuUsageWin32 impls.
                    if (cpu < 0)
                    {
                        continue;
                    }

                    AllAppCpuData[$"{procName}:{procId}"].AddData(cpu);
                    Thread.Sleep(150);
                }
            });

            Assert.IsTrue(AllAppCpuData.All(d => d.Value.Data.Any()));
        }

        [TestMethod]
        public async Task Ensure_ConcurrentQueue_Collection_Has_Data_CPU_NET6ProcessImpl()
        {
            FabricClientUtilities fabricClientUtilities = new(NodeName);
            var services = await fabricClientUtilities.GetAllDeployedReplicasOrInstancesAsync(true, Token);

            Assert.IsTrue(services.Any());

            ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData = new();
            ConcurrentQueue<int> serviceProcs = new();

            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = -1,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            _ = Parallel.For(0, services.Count, parallelOptions, (i, state) =>
            {
                var service = services[i];
                string procName = NativeMethods.GetProcessNameFromId((int)service.HostProcessId);

                _ = AllAppCpuData.TryAdd($"{procName}:{service.HostProcessId}", new FabricResourceUsageData<double>(
                        property: ErrorWarningProperty.CpuTime,
                        id: $"{procName}:{service.HostProcessId}",
                        dataCapacity: 8,
                        useCircularBuffer: false,
                        isParallel: true));

                serviceProcs.Enqueue((int)service.HostProcessId);
            });

            Assert.IsTrue(AllAppCpuData.Any() && serviceProcs.Any());
            Assert.IsTrue(serviceProcs.Count == AllAppCpuData.Count);

            TimeSpan duration = TimeSpan.FromSeconds(3);

            _ = Parallel.For(0, serviceProcs.Count, parallelOptions, (i, state) =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                int procId = serviceProcs.ElementAt(i);
                string procName = NativeMethods.GetProcessNameFromId(procId);
                CpuUsageProcess cpuUsage = new();

                while (sw.Elapsed <= duration)
                {
                    double cpu = cpuUsage.GetCurrentCpuUsagePercentage(procId, procName);

                    // procId is no longer mapped to process. see CpuUsageProcess/CpuUsageWin32 impls.
                    if (cpu < 0)
                    {
                        continue;
                    }

                    AllAppCpuData[$"{procName}:{procId}"].AddData(cpu);
                    Thread.Sleep(150);
                }
            });

            Assert.IsTrue(AllAppCpuData.All(d => d.Value.Data.Any()));
        }

        [TestMethod]
        public async Task Ensure_CircularBuffer_Collection_Has_Data_CPU_Win32Impl()
        {
            FabricClientUtilities fabricClientUtilities = new(NodeName);
            var services = await fabricClientUtilities.GetAllDeployedReplicasOrInstancesAsync(true, Token);

            Assert.IsTrue(services.Any());

            ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData = new();
            ConcurrentQueue<int> serviceProcs = new();

            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = -1,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            _ = Parallel.For(0, services.Count, parallelOptions, (i, state) =>
            {
                var service = services[i];
                string procName = NativeMethods.GetProcessNameFromId((int)service.HostProcessId);

                _ = AllAppCpuData.TryAdd($"{procName}:{service.HostProcessId}", new FabricResourceUsageData<double>(
                        property: ErrorWarningProperty.CpuTime,
                        id: $"{procName}:{service.HostProcessId}",
                        dataCapacity: 10,
                        useCircularBuffer: true,
                        isParallel: true));

                serviceProcs.Enqueue((int)service.HostProcessId);
            });

            Assert.IsTrue(AllAppCpuData.Any() && serviceProcs.Any());
            Assert.IsTrue(serviceProcs.Count == AllAppCpuData.Count);

            TimeSpan duration = TimeSpan.FromSeconds(3);

            _ = Parallel.For(0, serviceProcs.Count, parallelOptions, (i, state) =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                int procId = serviceProcs.ElementAt(i);
                string procName = NativeMethods.GetProcessNameFromId(procId);
                CpuUsageWin32 cpuUsage = new();

                while (sw.Elapsed <= duration)
                {
                    double cpu = cpuUsage.GetCurrentCpuUsagePercentage(procId, procName);

                    // procId is no longer mapped to process. see CpuUsageProcess/CpuUsageWin32 impls.
                    if (cpu < 0)
                    {
                        continue;
                    }

                    AllAppCpuData[$"{procName}:{procId}"].AddData(cpu);
                    Thread.Sleep(150);
                }
            });

            Assert.IsTrue(AllAppCpuData.All(d => d.Value.Data.Any()));
        }

        [TestMethod]
        public async Task Ensure_CircularBuffer_Collection_Has_Data_CPU_NET6ProcessImpl()
        {
            FabricClientUtilities fabricClientUtilities = new(NodeName);
            var services = await fabricClientUtilities.GetAllDeployedReplicasOrInstancesAsync(true, Token);

            Assert.IsTrue(services.Any());

            ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData = new();
            ConcurrentQueue<int> serviceProcs = new();

            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = -1,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };

            _ = Parallel.For(0, services.Count, parallelOptions, (i, state) =>
            {
                var service = services[i];
                string procName = NativeMethods.GetProcessNameFromId((int)service.HostProcessId);

                _ = AllAppCpuData.TryAdd($"{procName}:{service.HostProcessId}", new FabricResourceUsageData<double>(
                        property: ErrorWarningProperty.CpuTime,
                        id: $"{procName}:{service.HostProcessId}",
                        dataCapacity: 10,
                        useCircularBuffer: true,
                        isParallel: true));

                serviceProcs.Enqueue((int)service.HostProcessId);
            });

            Assert.IsTrue(AllAppCpuData.Any() && serviceProcs.Any());
            Assert.IsTrue(serviceProcs.Count == AllAppCpuData.Count);

            TimeSpan duration = TimeSpan.FromSeconds(3);

            _ = Parallel.For(0, serviceProcs.Count, parallelOptions, (i, state) =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                int procId = serviceProcs.ElementAt(i);
                string procName = NativeMethods.GetProcessNameFromId(procId);
                CpuUsageProcess cpuUsage = new();

                while (sw.Elapsed <= duration)
                {
                    double cpu = cpuUsage.GetCurrentCpuUsagePercentage(procId, procName);

                    // procId is no longer mapped to process. see CpuUsageProcess/CpuUsageWin32 impls.
                    if (cpu < 0)
                    {
                        continue;
                    }

                    AllAppCpuData[$"{procName}:{procId}"].AddData(cpu);
                    Thread.Sleep(150);
                }
            });

            Assert.IsTrue(AllAppCpuData.All(d => d.Value.Data.Any()));
        }

        #endregion

        #region Dump Tests

        [TestMethod]
        public async Task AppObserver_DumpProcessOnWarning_SuccessfulDumpCreation()
        {
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_warnings_dmps.config.json"),
                DumpsPath = Path.Combine(_logger.LogFolderBasePath, "AppObserver", "MemoryDumps")
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
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new AppObserver(TestServiceContext)
            {
                MonitorDuration = TimeSpan.FromSeconds(1),
                JsonConfigPath = Path.Combine(Environment.CurrentDirectory, "PackageRoot", "Config", "AppObserver_errors_dmps.config.json"),
                DumpsPath = Path.Combine(_logger.LogFolderBasePath, "AppObserver", "MemoryDumps")
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
        public async Task ClusterObserver_ObserveAsync_AppMonitor_Successful_IsHealthy_Detects_Warning()
        {
            if (!await EnsureTestServicesExistAsync("fabric:/Voting"))
            {
                await DeployVotingAppAsync();
                await Task.Delay(TimeSpan.FromSeconds(15));
            }

            var serviceTelemetryData = new ServiceTelemetryData
            {
                ApplicationName = "fabric:/Voting",
                Code = FOErrorWarningCodes.AppErrorPrivateBytesMb,
                Description = "Service Test warning for CO test.",
                EntityType = EntityType.Service,
                Metric = ErrorWarningProperty.PrivateBytesMb,
                NodeName = NodeName,
                HealthState = HealthState.Warning,
                ObserverName = ObserverConstants.AppObserverName,
                Property = "ClusterObserver_App",
                ServiceName = "fabric:/Voting/VotingWeb",
                Source = "FOTest",
                Value = 1024
            };

            var serviceHealthReport = new HealthReport
            {
                AppName = new Uri(serviceTelemetryData.ApplicationName),
                Code = FOErrorWarningCodes.AppErrorPrivateBytesMb,
                HealthData = serviceTelemetryData,
                EntityType = EntityType.Service,
                HealthMessage = "Service Test warning for CO test.",
                HealthReportTimeToLive = TimeSpan.FromSeconds(60),
                NodeName = NodeName,
                Observer = ObserverConstants.AppObserverName,
                ServiceName = new Uri(serviceTelemetryData.ServiceName),
                SourceId = "ClusterObserver_App",
                State = serviceTelemetryData.HealthState,
            };

            using var foEtwListener = new FabricObserverEtwListener(_logger);
            var startDateTime = DateTime.Now;

            ClusterObserverManager.FabricServiceContext = TestServiceContext;
            ClusterObserverManager.EtwEnabled = true;
            ClusterObserverManager.TelemetryEnabled = true;

            var healtherReporter = new ObserverHealthReporter(_logger);
            healtherReporter.ReportHealthToServiceFabric(serviceHealthReport);

            await Task.Delay(TimeSpan.FromSeconds(5));

            // On a one-node cluster like your dev machine, pass true for ignoreDefaultQueryTimeout otherwise each FabricClient query will take 2 minutes 
            // to timeout in ClusterObserver.
            var obs = new ClusterObserver.ClusterObserver(TestServiceContext, ignoreDefaultQueryTimeout: true)
            {
                IsEtwProviderEnabled = true,
                EmitWarningDetails = true,
                ConfigurationSettings = new ConfigSettings(default, null)
                {
                    IsObserverEtwEnabled = true
                }
            };

            await obs.ObserveAsync(Token);

            // ClusterObserver will emit (ETW) the serialized instance of TelemetryDataBase type (ServiceTelemetry in this case).
            List<ServiceTelemetryData> serviceTelemData = foEtwListener.foEtwConverter.ServiceTelemetryData;

            Assert.IsNotNull(serviceTelemData);
            Assert.IsTrue(serviceTelemData.Count > 0);

            foreach (var data in serviceTelemData.Where(d => d.Property == "ClusterObserver_App"))
            {
                Assert.IsTrue(data.EntityType == EntityType.Service);
                Assert.IsTrue(data.HealthState == HealthState.Warning);
                Assert.IsTrue(data.Code == FOErrorWarningCodes.AppErrorPrivateBytesMb);
                Assert.IsTrue(data.Description == "Service Test warning for CO test.");
                Assert.IsTrue(data.Metric == ErrorWarningProperty.PrivateBytesMb);
                Assert.IsTrue(data.NodeName == NodeName);
                Assert.IsTrue(data.ObserverName == ObserverConstants.AppObserverName);
                Assert.IsTrue(data.Source == "FOTest");
                Assert.IsTrue(data.Value == 1024);
            }

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task ClusterObserver_ObserveAsync_NodeMonitor_Successful_IsHealthy_Detects_Warning()
        {
            var nodeTelemetryData = new NodeTelemetryData
            {
                Code = FOErrorWarningCodes.NodeWarningMemoryPercent,
                Description = "Machine Test warning for CO test.",
                EntityType = EntityType.Machine,
                Metric = ErrorWarningProperty.MemoryConsumptionPercentage,
                NodeName = NodeName,
                ObserverName = ObserverConstants.NodeObserverName,
                HealthState = HealthState.Warning,
                Property = "ClusterObserver_Node",
                Source = "FOTest",
                Value = 90
            };

            var nodeHealthReport = new HealthReport
            {
                Code = FOErrorWarningCodes.NodeErrorCpuPercent,
                EntityType = EntityType.Machine,
                HealthData = nodeTelemetryData,
                HealthMessage = "Machine Test warning for CO test.",
                HealthReportTimeToLive = TimeSpan.FromSeconds(60),
                NodeName = NodeName,
                Observer = ObserverConstants.NodeObserverName,
                SourceId = "ClusterObserver_Node",
                State = HealthState.Warning
            };

            using var foEtwListener = new FabricObserverEtwListener(_logger);
            var startDateTime = DateTime.Now;

            ClusterObserverManager.FabricServiceContext = TestServiceContext;
            ClusterObserverManager.EtwEnabled = true;
            ClusterObserverManager.TelemetryEnabled = true;

            var healtherReporter = new ObserverHealthReporter(_logger);
            healtherReporter.ReportHealthToServiceFabric(nodeHealthReport);

            await Task.Delay(TimeSpan.FromSeconds(5));

            // On a one-node cluster like your dev machine, pass true for ignoreDefaultQueryTimeout otherwise each FabricClient query will take 2 minutes 
            // to timeout in ClusterObserver.
            var obs = new ClusterObserver.ClusterObserver(TestServiceContext, ignoreDefaultQueryTimeout: true)
            {
                IsEtwProviderEnabled = true,
                EmitWarningDetails = true,
                ConfigurationSettings = new ConfigSettings(default, null)
                {
                    IsObserverEtwEnabled = true
                }
            };

            await obs.ObserveAsync(Token);

            // ClusterObserver will emit (ETW) the serialized instance of TelemetryDataBase type (NodeTelemetryData in this case).
            List<NodeTelemetryData> nodeTelemData = foEtwListener.foEtwConverter.NodeTelemetryData;

            Assert.IsNotNull(nodeTelemData);
            Assert.IsTrue(nodeTelemData.Count > 0);

            foreach (var data in nodeTelemData.Where(d => d.Property == "NodeObserver_App"))
            {
                Assert.IsTrue(data.EntityType == EntityType.Machine);
                Assert.IsTrue(data.HealthState == HealthState.Warning);
                Assert.IsTrue(data.Code == FOErrorWarningCodes.NodeWarningMemoryPercent);
                Assert.IsTrue(data.Description.Contains("Machine Test warning for CO test."));
                Assert.IsTrue(data.Metric == ErrorWarningProperty.MemoryConsumptionPercentage);
                Assert.IsTrue(data.NodeName == NodeName);
                Assert.IsTrue(data.ObserverName == ObserverConstants.NodeObserverName);
                Assert.IsTrue(data.Property == "ClusterObserver_Node");
                Assert.IsTrue(data.Source == "FOTest");
                Assert.IsTrue(data.Value == 90);
            }

            // observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);
        }

        [TestMethod]
        public async Task CertificateObserver_validCerts()
        {
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
        public async Task NetworkObserver_ObserveAsync_Successful_Warnings()
        {
            var startDateTime = DateTime.Now;

            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = false;

            using var obs = new NetworkObserver(TestServiceContext);
            await obs.ObserveAsync(Token);

            // Observer ran to completion with no errors.
            Assert.IsTrue(obs.LastRunDateTime > startDateTime);

            // The config file used for testing contains an endpoint that does not exist, so NetworkObserver
            // will put the related Application entity into Warning state.
            Assert.IsTrue(obs.HasActiveFabricErrorOrWarning);
        }

        [TestMethod]
        public async Task NetworkObserver_ObserveAsync_Successful_WritesLocalFile_ObsWebDeployed()
        {
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
            var outputFilePath = Path.Combine(_logger.LogFolderBasePath, "NetInfo.txt");

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
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await AppObserver_ObserveAsync_Successful_IsHealthy();

            List<ServiceTelemetryData> telemData = foEtwListener.foEtwConverter.ServiceTelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            foreach (var data in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ApplicationType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ServiceName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));

                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(data.ProcessStartTime)
                    && DateTime.TryParse(data.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(data.EntityType is EntityType.Service or EntityType.Process);
                Assert.IsTrue(data.ServicePackageActivationMode is "ExclusiveProcess"
                              or "SharedProcess");
                Assert.IsTrue(data.HealthState == HealthState.Invalid);
                Assert.IsTrue(data.ProcessId > 0);
                Assert.IsTrue(data.ObserverName == ObserverConstants.AppObserverName);
                Assert.IsTrue(data.Code == null);
                Assert.IsTrue(data.Description == null);
                Assert.IsTrue(data.Source == ObserverConstants.FabricObserverName);
                Assert.IsTrue(data.Value >= 0.0);
            }
        }

        [TestMethod]
        public async Task AppObserver_ETW_EventData_IsTelemetryData_HealthWarnings()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await AppObserver_ObserveAsync_Successful_WarningsGenerated();

            List<ServiceTelemetryData> telemData = foEtwListener.foEtwConverter.ServiceTelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            var warningEvents = telemData.Where(t => t.HealthState == HealthState.Warning);
            Assert.IsTrue(warningEvents.Any());

            foreach (var data in warningEvents)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ApplicationType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Code));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Description));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Property));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ServiceName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));

                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(data.ProcessStartTime)
                    && DateTime.TryParse(data.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(data.EntityType is EntityType.Service or EntityType.Process);
                Assert.IsTrue(data.ServicePackageActivationMode is "ExclusiveProcess"
                              or "SharedProcess");
                Assert.IsTrue(data.HealthState == HealthState.Warning);
                Assert.IsTrue(data.ProcessId > 0);
                Assert.IsTrue(data.Value > 0.0);
                Assert.IsTrue(data.ObserverName == ObserverConstants.AppObserverName);
                Assert.IsTrue(data.Source == $"{data.ObserverName}({data.Code})");
            }
        }

        // RG
        [TestMethod]
        public async Task AppObserver_ETW_EventData_RGEnabled_MemoryInMB_Or_MemoryInMBLimit_ValuesAreNonZero()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await AppObserver_ObserveAsync_Successful_IsHealthy();

            List<ServiceTelemetryData> telemData = foEtwListener.foEtwConverter.ServiceTelemetryData;

            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(t => t.ApplicationName == "fabric:/Voting").ToList();
            Assert.IsTrue(telemData.Any());

            foreach (var data in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ApplicationType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ServiceName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(data.ProcessStartTime)
                    && DateTime.TryParse(data.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(data.EntityType is EntityType.Service or EntityType.Process);
                Assert.IsTrue(data.ServicePackageActivationMode is "ExclusiveProcess"
                              or "SharedProcess");
                Assert.IsTrue(data.HealthState == HealthState.Invalid);
                Assert.IsTrue(data.ProcessId > 0);
                Assert.IsTrue(data.ObserverName == ObserverConstants.AppObserverName);
                Assert.IsTrue(data.Code == null);
                Assert.IsTrue(data.Description == null);
                Assert.IsTrue(data.Source == ObserverConstants.FabricObserverName);
                
                // RG
                if (data.ProcessName is "VotingData" or "VotingWeb" or "ConsoleApp6" or "ConsoleApp7")
                {
                    Assert.IsTrue(data.RGMemoryEnabled && data.RGAppliedMemoryLimitMb > 0);     
                }

                Assert.IsTrue(data.Value >= 0.0);
            }
        }

        // Private Bytes
        [TestMethod]
        public async Task AppObserver_ETW_PrivateBytes_Multiple_CodePackages_ValuesAreNonZero_Warnings_MB_Percent()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);
            await AppObserver_ObserveAsync_PrivateBytes_Successful_WarningsGenerated();
            List<ServiceTelemetryData> telemData = foEtwListener.foEtwConverter.ServiceTelemetryData;

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
            using var foEtwListener = new FabricObserverEtwListener(_logger);
            await AppObserver_ObserveAsync_PrivateBytes_Successful_WarningsGenerated();
            List<ServiceTelemetryData> telemData = foEtwListener.foEtwConverter.ServiceTelemetryData;
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
            using var foEtwListener = new FabricObserverEtwListener(_logger);
            await AppObserver_ObserveAsync_Successful_RGLimitWarningGenerated();
            List<ServiceTelemetryData> telemData = foEtwListener.foEtwConverter.ServiceTelemetryData;

            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(
                t => t.ApplicationName == "fabric:/Voting" && t.HealthState == HealthState.Warning).ToList();

            // 2 service code packages + 2 helper code packages (VotingData) * 1 metric = 4 warnings...
            Assert.IsTrue(telemData.All(t => t.Metric == ErrorWarningProperty.RGMemoryUsagePercent && telemData.Count == 4));
        }

        // DiskObserver: TelemetryData \\

        [TestMethod]
        public async Task DiskObserver_ETW_EventData_IsTelemetryData()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await DiskObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors();

            List<DiskTelemetryData> telemData = foEtwListener.foEtwConverter.DiskTelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            foreach (var data in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.DriveName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                if (data.Metric == ErrorWarningProperty.FolderSizeMB)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(data.FolderName));
                }
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Property));

                Assert.IsTrue(data.EntityType == EntityType.Disk);
                Assert.IsTrue(data.HealthState == HealthState.Invalid);
                Assert.IsTrue(data.ObserverName == ObserverConstants.DiskObserverName);
                Assert.IsTrue(data.Code == null);
                Assert.IsTrue(data.Description == null);
                Assert.IsTrue(data.Value > 0.0);
            }
        }

        [TestMethod]
        public async Task DiskObserver_ETW_EventData_IsTelemetryData_Warnings()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await DiskObserver_ObserveAsync_Successful_IsHealthy_WarningsOrErrors();

            List<DiskTelemetryData> telemData = foEtwListener.foEtwConverter.DiskTelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(d => d.HealthState == HealthState.Warning).ToList();
            Assert.IsTrue(telemData.Any());

            foreach (var data in telemData)
            {
                Assert.IsTrue(data.EntityType == EntityType.Disk);
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.DriveName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Code));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Description));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                if (data.Metric == ErrorWarningProperty.FolderSizeMB)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(data.FolderName));
                }
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Property));

                Assert.IsTrue(data.ObserverName == ObserverConstants.DiskObserverName);
                Assert.IsTrue(data.HealthState == HealthState.Warning);
                Assert.IsTrue(data.Value > 0.0);
                Assert.IsTrue(data.Source == $"{data.ObserverName}({data.Code})");
            }
        }

        // FabricSystemObserver: TelemetryData \\

        [TestMethod]
        public async Task FabricSystemObserver_ETW_EventData_Is_SystemServiceTelemetryData()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await FabricSystemObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrors();

            List<SystemServiceTelemetryData> telemData = foEtwListener.foEtwConverter.SystemServiceTelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            foreach (var data in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));

                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(data.ProcessStartTime)
                    && DateTime.TryParse(data.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(data.EntityType == EntityType.Application);
                Assert.IsTrue(data.HealthState == HealthState.Invalid);
                Assert.IsTrue(data.ProcessId > 0);
                Assert.IsTrue(data.ObserverName == ObserverConstants.FabricSystemObserverName);
                Assert.IsTrue(data.Code == null);
                Assert.IsTrue(data.Description == null);
                Assert.IsTrue(data.Source == ObserverConstants.FabricObserverName);
                Assert.IsTrue(data.Value >= 0.0);
            }
        }

        [TestMethod]
        public async Task FabricSystemObserver_ETW_EventData_Is_SystemServiceTelemetryData_Warnings()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await FabricSystemObserver_ObserveAsync_Successful_IsHealthy_MemoryWarningsOrErrorsDetected();

            List<SystemServiceTelemetryData> telemData = foEtwListener.foEtwConverter.SystemServiceTelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(d => d.HealthState == HealthState.Warning).ToList();
            Assert.IsTrue(telemData.Any());

            foreach (var data in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ApplicationName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ProcessName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));

                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(data.ProcessStartTime)
                    && DateTime.TryParse(data.ProcessStartTime, out DateTime startDate)
                    && startDate > DateTime.MinValue);

                Assert.IsTrue(data.EntityType == EntityType.Application);
                Assert.IsTrue(data.HealthState == HealthState.Warning);
                Assert.IsTrue(data.ProcessId > 0);
                Assert.IsTrue(data.ObserverName == ObserverConstants.FabricSystemObserverName);
                Assert.IsTrue(data.Code != null);
                Assert.IsTrue(data.Description != null);
                Assert.IsTrue(data.Property != null);
                Assert.IsTrue(data.Source == $"{data.ObserverName}({data.Code})");
                Assert.IsTrue(data.Value > 0.0);
            }
        }

        // NodeObserver: TelemetryData \\

        [TestMethod]
        public async Task NodeObserver_ETW_EventData_IsTelemetryData()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await NodeObserver_ObserveAsync_Successful_IsHealthy_NoWarningsOrErrorsDetected();

            List<NodeTelemetryData> telemData = foEtwListener.foEtwConverter.NodeTelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            foreach (var data in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));
                Assert.IsFalse(data.Property == null);

                Assert.IsTrue(data.EntityType == EntityType.Machine);
                Assert.IsTrue(data.HealthState == HealthState.Invalid);
                Assert.IsTrue(data.ObserverName == ObserverConstants.NodeObserverName);
                Assert.IsTrue(data.Code == null);
                Assert.IsTrue(data.Description == null);
                Assert.IsTrue(data.Value >= 0.0);
            }
        }

        [TestMethod]
        public async Task NodeObserver_ETW_EventData_IsTelemetryData_Warnings()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);

            await NodeObserver_ObserveAsync_Successful_IsHealthy_WarningsOrErrorsDetected();

            List<NodeTelemetryData> telemData = foEtwListener.foEtwConverter.NodeTelemetryData;
            
            Assert.IsNotNull(telemData);
            Assert.IsTrue(telemData.Count > 0);

            telemData = telemData.Where(d => d.HealthState == HealthState.Warning).ToList();
            Assert.IsTrue(telemData.Any());

            foreach (var data in telemData)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.NodeType));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ClusterId));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Metric));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.ObserverName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.OS));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Code));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Property));
                Assert.IsFalse(string.IsNullOrWhiteSpace(data.Description));


                Assert.IsTrue(data.EntityType == EntityType.Machine);
                Assert.IsTrue(data.ObserverName == ObserverConstants.NodeObserverName);
                Assert.IsTrue(data.HealthState == HealthState.Warning);
                Assert.IsTrue(data.Source == $"{data.ObserverName}({data.Code})");
                Assert.IsTrue(data.Value > 0.0);
            }

        }

        // NodeObserver: NodeSnapshotTelemetryData \\

        [TestMethod]
        public async Task NodeObserver_ETW_EventData_IsNodeSnapshotTelemetryData()
        {
            using var foEtwListener = new FabricObserverEtwListener(_logger);
            var startDateTime = DateTime.Now;
            ObserverManager.FabricServiceContext = TestServiceContext;
            ObserverManager.TelemetryEnabled = false;
            ObserverManager.EtwEnabled = true;

            using var obs = new NodeObserver(TestServiceContext)
            {
                IsEnabled = true,
                MonitorDuration = TimeSpan.FromSeconds(5),
                CpuWarningUsageThresholdPct = 90,
                ActivePortsWarningThreshold = 10000,
                MemoryWarningLimitPercent = 90,
                EphemeralPortsPercentWarningThreshold = 30,
                FirewallRulesWarningThreshold = 3000,
                IsEtwProviderEnabled = true
            };

            await obs.ObserveAsync(Token);
            NodeSnapshotTelemetryData telemData = foEtwListener.foEtwConverter.NodeSnapshotTelemetryData;

            Assert.IsNotNull(telemData);
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.SnapshotId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.SnapshotTimestamp));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.CodeVersion));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.ConfigVersion));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.FaultDomain));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.NodeId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.NodeInstanceId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.NodeName));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.NodeType));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.NodeDownAt));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.NodeUpAt));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.UpgradeDomain));
            Assert.IsTrue(string.IsNullOrWhiteSpace(telemData.InfrastructurePlacementID));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.NodeStatus));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.HealthState));
            Assert.IsFalse(string.IsNullOrWhiteSpace(telemData.IpAddressOrFQDN));
            Assert.IsFalse(telemData.IsNodeByNodeUpgradeInProgress);
            Assert.IsFalse(telemData.NodeDeactivationInfo == null);
        }

        // OSObserver: MachineTelemetryData \\

        [TestMethod]
        public async Task OSObserver_ETW_EventData_IsMachineTelemetryData()
        {
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

            if (OperatingSystem.IsWindows())
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