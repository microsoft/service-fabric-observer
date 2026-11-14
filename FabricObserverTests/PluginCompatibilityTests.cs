using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using FabricObserver;
using FabricObserver.Observers;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FabricObserverTests
{
    [TestClass]
    public class PluginCompatibilityTests
    {
        [DataTestMethod]
        [DataRow("CompatibilityPlugin", "Net8CompatibilityPlugin.dll", ".NETCoreApp,Version=v8.0")]
        [DataRow("CurrentCompatibilityPlugin", "Net10CompatibilityPlugin.dll", ".NETCoreApp,Version=v10.0")]
        public void PluginSharesHostContractsAndRegistersObserver(string directory, string fileName, string framework)
        {
            string pluginPath = Path.Combine(AppContext.BaseDirectory, directory, fileName);
            using PluginLoader loader = PluginLoader.CreateFromAssemblyFile(
                pluginPath, PluginContracts.GetSharedTypes(), options => options.IsUnloadable = true);

            Assembly assembly = loader.LoadDefaultAssembly();
            Assert.AreEqual(framework, assembly.GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName);
            Assert.AreEqual(10, Environment.Version.Major);

            FabricObserverStartupAttribute attribute = assembly.GetCustomAttributes<FabricObserverStartupAttribute>().Single();
            IFabricObserverStartup startup = (IFabricObserverStartup)Activator.CreateInstance(attribute.StartupType);
            ServiceCollection services = new();
            startup.ConfigureServices(services, null, null);

            Assert.AreEqual(1, services.Count(service => service.ServiceType == typeof(ObserverBase)));
            Assert.AreEqual(new Version(3, 3, 3, 0), typeof(IFabricObserverStartup).Assembly.GetName().Version);
        }
    }
}
