using System;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver;
using FabricObserver.Observers;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ServiceFabric.Mocks;

namespace FabricObserverTests
{
    [TestClass]
    public class PluginCompatibilityTests
    {
        [DataTestMethod]
        [DataRow("CompatibilityPlugin", "Net8CompatibilityPlugin.dll", ".NETCoreApp,Version=v8.0", "Net8CompatibilityObserver")]
        [DataRow("CurrentCompatibilityPlugin", "Net10CompatibilityPlugin.dll", ".NETCoreApp,Version=v10.0", "Net10CompatibilityObserver")]
        public async Task PluginSharesHostContractsAndExecutesObserver(string directory, string fileName, string framework, string observerName)
        {
            string pluginPath = Path.Combine(AppContext.BaseDirectory, directory, fileName);
            using PluginLoader loader = PluginLoader.CreateFromAssemblyFile(
                pluginPath, PluginContracts.GetSharedTypes(), options => options.IsUnloadable = true);

            Assembly assembly = loader.LoadDefaultAssembly();
            Assert.AreEqual(framework, assembly.GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName);
            Assert.AreEqual(10, Environment.Version.Major);

            FabricObserverStartupAttribute attribute = assembly.GetCustomAttributes<FabricObserverStartupAttribute>().Single();
            IFabricObserverStartup startup = (IFabricObserverStartup)Activator.CreateInstance(attribute.StartupType);
            Uri serviceName = new("fabric:/PluginCompatibility/ObserverService");
            MockCodePackageActivationContext activationContext = new(
                "fabric:/PluginCompatibility", "PluginCompatibilityType", "Code", "1.0.0.0",
                Guid.NewGuid().ToString(), Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath(), "FabricObserverPkg", "1.0.0.0")
            {
                ConfigurationPackage = ObserverTests.BuildConfigurationPackageFromSettingsFile(
                    Path.Combine(AppContext.BaseDirectory, "PackageRoot", "Config", "Settings.xml"))
            };
            StatelessServiceContext context = new(
                new NodeContext("_PluginTestNode", new NodeId(0, 1), 0, "NodeType0", "TEST.MACHINE"),
                activationContext, "FabricObserverType", serviceName, null, Guid.NewGuid(), 1);
            ServiceCollection services = new();
            startup.ConfigureServices(services, null, context);

            Assert.AreEqual(1, services.Count(service => service.ServiceType == typeof(ObserverBase)));
            Assert.AreEqual(new Version(3, 3, 3, 0), typeof(IFabricObserverStartup).Assembly.GetName().Version);

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();
            ObserverBase observer = scope.ServiceProvider.GetRequiredService<ObserverBase>();
            Assert.AreEqual(observerName, observer.ObserverName);
            Assert.AreEqual(context.NodeContext.NodeName, observer.NodeName);
            Assert.AreEqual(serviceName, observer.ServiceName);
            await observer.ObserveAsync(CancellationToken.None);
            await observer.ReportAsync(CancellationToken.None);
        }
    }
}
