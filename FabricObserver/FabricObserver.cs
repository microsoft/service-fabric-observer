// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers;
using FabricObserver.Observers.Interfaces;
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
            this.ConfigureServices(services);

            using ServiceProvider serviceProvider = services.BuildServiceProvider();
            this.observerManager = new ObserverManager(serviceProvider, cancellationToken);
            await this.observerManager.StartObserversAsync().ConfigureAwait(false);
        }

        private void ConfigureServices(ServiceCollection services)
        {
            _ = services.AddScoped(typeof(IObserver), typeof(AppObserver));
            _ = services.AddScoped(typeof(IObserver), typeof(CertificateObserver));
            _ = services.AddScoped(typeof(IObserver), typeof(DiskObserver));
            _ = services.AddScoped(typeof(IObserver), typeof(FabricSystemObserver));
            _ = services.AddScoped(typeof(IObserver), typeof(NetworkObserver));
            _ = services.AddScoped(typeof(IObserver), typeof(NodeObserver));
            _ = services.AddScoped(typeof(IObserver), typeof(OsObserver));
            _ = services.AddScoped(typeof(IObserver), typeof(SfConfigurationObserver));
            _ = services.AddSingleton(typeof(StatelessServiceContext), this.Context);

            this.LoadObserversFromPlugins(services);
        }

        private void LoadObserversFromPlugins(ServiceCollection services)
        {
            string pluginsDir = Path.Combine(this.Context.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

            if (!Directory.Exists(pluginsDir))
            {
                return;
            }

            string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.TopDirectoryOnly);

            if (pluginDlls.Length == 0)
            {
                return;
            }

            List<PluginLoader> pluginLoaders = new List<PluginLoader>(capacity: pluginDlls.Length);

            Type[] sharedTypes = new[] { typeof(FabricObserverStartupAttribute), typeof(IFabricObserverStartup) };

            if (sharedTypes.Length == 0)
            {
                return;
            }

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
                        // Log....
                        //throw new InvalidOperationException($"{startupAttributes[i].StartupType.FullName} must implement IFabricObserverStartup.");
                    }
                }
            }
        }
    }
}