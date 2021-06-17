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
        private readonly FabricClient fabricClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserver"/> class.
        /// </summary>
        /// <param name="context">StatelessServiceContext.</param>
        public FabricObserver(StatelessServiceContext context)
            : base(context)
        {
            fabricClient = new FabricClient();
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        /// <returns>a Task.</returns>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);

            await using ServiceProvider serviceProvider = services.BuildServiceProvider();
            using var observerManager = new ObserverManager(serviceProvider, fabricClient, cancellationToken);
            await observerManager.StartObserversAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// This function will add observer instances, both static (so, part of the FO impl) and dynamic (so, observer plugin dlls).
        /// </summary>
        /// <param name="services">ServiceCollection collection instance.</param>
        private void ConfigureServices(IServiceCollection services)
        {
            _ = services.AddScoped(typeof(ObserverBase), s => new AppObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new CertificateObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new DiskObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new FabricSystemObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new NetworkObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new NodeObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new OSObserver(fabricClient, Context));
            _ = services.AddScoped(typeof(ObserverBase), s => new SFConfigurationObserver(fabricClient, Context));
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

            var pluginLoaders = new List<PluginLoader>(pluginDlls.Length);

            Type[] sharedTypes = { typeof(FabricObserverStartupAttribute), typeof(IFabricObserverStartup), typeof(IServiceCollection) };

            foreach (string pluginDll in pluginDlls)
            {
                PluginLoader loader = PluginLoader.CreateFromAssemblyFile(pluginDll, sharedTypes);
                pluginLoaders.Add(loader);
            }

            foreach (PluginLoader pluginLoader in pluginLoaders)
            {
                Assembly pluginAssembly;

                try
                {
                    // If your plugin has native deps, this will fail at this call. That's OK.
                    // We only want to load your plugin eventually, anyway (see startupObject code below).
                    // Since your plugin's deps (managed and native) all live in the same folder, there will be no dll resolution failures
                    // when your plugin needs to access some dependency (and it needs to access some dependency...).
                    pluginAssembly = pluginLoader.LoadDefaultAssembly();
                }
                catch (Exception e)
                {
                    string s = e.Message;
                    if (e.HResult == -2147024885 || e.Message.ToLower().Contains("bad il"))
                    {
                        continue;
                    }
                    else
                    {
                        throw;
                    }
                }

                // Note: This is a micro-optimization (including the for below it). It could just as well be left as IEnumerable and then foreach'd over.
                // It is unlikely that there will be 1000s of plugins...
                FabricObserverStartupAttribute[] startupAttributes = pluginAssembly.GetCustomAttributes<FabricObserverStartupAttribute>().ToArray();

                for (int i = 0; i < startupAttributes.Length; ++i)
                {
                    object startupObject = Activator.CreateInstance(startupAttributes[i].StartupType);

                    if (startupObject is IFabricObserverStartup fabricObserverStartup)
                    {
                        fabricObserverStartup.ConfigureServices(services, fabricClient, Context);
                    }
                    else
                    {
                        throw new InvalidOperationException($"{startupAttributes[i].StartupType.FullName} must implement IFabricObserverStartup.");
                    }
                }
            }
        }
    }
}