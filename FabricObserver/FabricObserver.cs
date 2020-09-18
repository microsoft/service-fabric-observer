// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers;
using McMaster.NETCore.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Runtime;

namespace FabricObserver
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class FabricObserver : StatelessService
    {
        private ObserverManager observerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserver"/> class.
        /// </summary>
        /// <param name="context">StatelessServiceContext.</param>
        public FabricObserver(StatelessServiceContext context)
            : base(context)
        {
            
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        /// <returns>a Task.</returns>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            ServiceCollection services = new ServiceCollection();
            ConfigureServices(services);

            using ServiceProvider serviceProvider = services.BuildServiceProvider();
            this.observerManager = new ObserverManager(serviceProvider, cancellationToken);
            await this.observerManager.StartObserversAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// This function will add observer instances, both static (so, part of the FO impl) and dynamic (so, observer plugin dlls).
        /// </summary>
        /// <param name="services">ServiceCollection collection instance.</param>
        private void ConfigureServices(ServiceCollection services)
        {
            _ = services.AddScoped(typeof(ObserverBase), typeof(AppObserver));
            _ = services.AddScoped(typeof(ObserverBase), typeof(CertificateObserver));
            _ = services.AddScoped(typeof(ObserverBase), typeof(DiskObserver));
            _ = services.AddScoped(typeof(ObserverBase), typeof(FabricSystemObserver));
            _ = services.AddScoped(typeof(ObserverBase), typeof(NetworkObserver));
            _ = services.AddScoped(typeof(ObserverBase), typeof(NodeObserver));
            _ = services.AddScoped(typeof(ObserverBase), typeof(OSObserver));
            _ = services.AddScoped(typeof(ObserverBase), typeof(SFConfigurationObserver));
            _ = services.AddSingleton(typeof(StatelessServiceContext), Context);

            LoadObserversFromPlugins(services);
        }

        /// <summary>
        /// This function will load observer plugin dlls from PackageRoot/Data/Plugins folder and add them to the ServiceCollection instance.
        /// </summary>
        /// <param name="services"></param>
        private void LoadObserversFromPlugins(ServiceCollection services)
        {
            string pluginsDir = Path.Combine(Context.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

            if (!Directory.Exists(pluginsDir))
            {
                return;
            }

            string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.TopDirectoryOnly);

            if (pluginDlls.Length == 0)
            {
                return;
            }
           
            /*if (IsRunningAsSystem())
            {
                return;
            }*/
           
            List<PluginLoader> pluginLoaders = new List<PluginLoader>(capacity: pluginDlls.Length);

            Type[] sharedTypes = new[] { typeof(FabricObserverStartupAttribute), typeof(IFabricObserverStartup), typeof(IServiceCollection) };

            foreach (string pluginDll in pluginDlls)
            {
                PluginLoader loader = PluginLoader.CreateFromAssemblyFile(
                    pluginDll,
                    sharedTypes);

                pluginLoaders.Add(loader);
            }

            foreach (PluginLoader pluginLoader in pluginLoaders)
            {
                Assembly pluginAssembly = pluginLoader.LoadDefaultAssembly();

                FabricObserverStartupAttribute[] startupAttributes =
                    pluginAssembly.GetCustomAttributes<FabricObserverStartupAttribute>().ToArray();

                for (int i = 0; i < startupAttributes.Length; ++i)
                {
                    object startupObject = Activator.CreateInstance(startupAttributes[i].StartupType);

                    if (startupObject is IFabricObserverStartup fabricObserverStartup)
                    {
                        fabricObserverStartup.ConfigureServices(services);
                    }
                    else
                    {
                        throw new InvalidOperationException($"{startupAttributes[i].StartupType.FullName} must implement IFabricObserverStartup.");
                    }
                }
            }
        }

        private bool IsRunningAsSystem()
        {
            Process systemProc = null;

            try
            {
                var fabricSystemProc = Process.GetProcessesByName("FabricHost");
                systemProc = fabricSystemProc.Length > 0 ? fabricSystemProc[0] : null;
                var mem = systemProc?.WorkingSet64;
               
                return true;
            }
            catch
            {
             
            }
            finally
            {
                systemProc?.Dispose();
            }

            return false;
        }
    }
}