﻿using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using McMaster.NETCore.Plugins;
using System;
using FabricObserver;
using FabricObserver.Observers;
using System.Reflection;
using System.Linq;
using FabricObserver.Utilities;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;

namespace ClusterObserver
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class FabricClusterObserver(StatelessServiceContext context) : StatelessService(context)
    {
        private readonly Logger logger = new("ClusterObserverService");

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);

            await using ServiceProvider serviceProvider = services.BuildServiceProvider();
            using ClusterObserverManager observerManager = new(serviceProvider, cancellationToken);
            await observerManager.StartAsync();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            _ = services.AddScoped(typeof(ObserverBase), s => new ClusterObserver(Context));
            _ = services.AddSingleton(typeof(StatelessServiceContext), Context);

            LoadObserversFromPlugins(services);
        }

        /// <summary>
        /// This function will load observer plugin dlls from PackageRoot/Data/Plugins folder and add them to the ServiceCollection instance.
        /// </summary>
        /// <param name="services"></param>
        private void LoadObserversFromPlugins(IServiceCollection services)
        {
            string pluginsDir = Path.Combine(Context.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

            if (!Directory.Exists(pluginsDir))
            {
                return;
            }

            string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);

            if (pluginDlls.Length == 0)
            {
                return;
            }

            PluginLoader[] pluginLoaders = new PluginLoader[pluginDlls.Length];
            Type[] sharedTypes = [typeof(FabricObserverStartupAttribute), typeof(IFabricObserverStartup), typeof(IServiceCollection)];
            string dll = "";

            for (int i = 0; i < pluginDlls.Length; ++i)
            {
                dll = pluginDlls[i];
                PluginLoader loader = PluginLoader.CreateFromAssemblyFile(dll, sharedTypes, a => a.IsUnloadable = false);
                pluginLoaders[i] = loader;
            }

            for (int i = 0; i < pluginLoaders.Length; ++i)
            {
                var pluginLoader = pluginLoaders[i];
                Assembly pluginAssembly;

                try
                {
                    // If your plugin has native library dependencies (that's fine), then we will land in the catch (BadImageFormatException).
                    // This is by design. The Managed FO plugin assembly will successfully load, of course.
                    pluginAssembly = pluginLoader.LoadDefaultAssembly();
                    FabricObserverStartupAttribute[] startupAttributes = pluginAssembly.GetCustomAttributes<FabricObserverStartupAttribute>().ToArray();

                    for (int j = 0; j < startupAttributes.Length; ++j)
                    {
                        object startupObject = Activator.CreateInstance(startupAttributes[j].StartupType);

                        if (startupObject is IFabricObserverStartup fabricObserverStartup)
                        {
                            // The null parameter (re FabricClient) is used here *only to preserve the existing (historical, in use..) interface specification for IFabricObserverStartup*. 
                            // There is actually no longer a need to pass in a FabricClient instance as this is now a singleton instance managed by 
                            // FabricObserver.Extensibility.FabricClientUtilities that protects against premature disposal (by plugins, for example).
                            fabricObserverStartup.ConfigureServices(services, null, Context);
                        }
                        else
                        {
                            // This will bring down CO, which it should: This means your plugin is not supported. Fix your bug.
                            throw new InvalidPluginException($"{startupAttributes[j].StartupType.FullName} must implement IFabricObserverStartup.");
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException or BadImageFormatException or IOException)
                {
                    if (e is IOException)
                    {
                        string error = $"Plugin dll {dll} could not be loaded. {e.Message}";
                        HealthReport healthReport = new()
                        {
                            AppName = new Uri($"{Context.CodePackageActivationContext.ApplicationName}"),
                            EmitLogEvent = true,
                            HealthMessage = error,
                            EntityType = EntityType.Application,
                            HealthReportTimeToLive = TimeSpan.FromMinutes(10),
                            State = System.Fabric.Health.HealthState.Warning,
                            Property = "ClusterObserverPluginLoadError",
                            SourceId = $"ClusterObserverService-{Context.NodeContext.NodeName}",
                            NodeName = Context.NodeContext.NodeName,
                        };

                        ObserverHealthReporter observerHealth = new(logger);
                        observerHealth.ReportHealthToServiceFabric(healthReport);
                    }

                    continue;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    logger.LogError($"Unhandled exception in ClusterObserverService Instance: {e.Message}");
                    throw;
                }
            }
        }
    }
}
