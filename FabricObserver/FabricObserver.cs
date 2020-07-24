// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers;
using FabricObserver.Observers.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Communication.Runtime;

namespace FabricObserver
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class FabricObserver : Microsoft.ServiceFabric.Services.Runtime.StatelessService
    {
        private ObserverManager observerManager;
        private ServiceCollection services;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserver"/> class.
        /// </summary>
        /// <param name="context">StatelessServiceContext.</param>
        public FabricObserver(StatelessServiceContext context)
            : base(context)
        {
            context.CodePackageActivationContext.ConfigurationPackageModifiedEvent += this.CodePackageActivationContext_ConfigurationPackageModifiedEvent;
        }

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return Array.Empty<ServiceInstanceListener>();
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        /// <returns>a Task.</returns>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            this.observerManager = new ObserverManager(this.Context, cancellationToken);

            this.services = new ServiceCollection();
            await this.ConfigureServices();
            using ServiceProvider serviceProvider = this.services.BuildServiceProvider();
            await this.observerManager.StartObserversAsync(serviceProvider).ConfigureAwait(false);
        }

        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            // Check/apply config changes. So, if there is a new observer in the Settings.xml file and it's enabled, load it...
            if (e.NewPackage.Settings.Sections.Count > e.OldPackage.Settings.Sections.Count)
            {
                // A new section was added and this means it's a new observer since top level observer config must live in Settings.xml.
                var newObsSection = e.NewPackage.Settings.Sections[^1];

                if (newObsSection.Parameters.Any(p => p.Name == "Enabled" && p.Value.ToLower() == "true"))
                {
                    await this.LoadObserversFromPluginsAsync(true);
                }
            }
        }

        private async Task ConfigureServices()
        {
            _ = this.services.AddScoped(typeof(IObserver), typeof(AppObserver));
            _ = this.services.AddScoped(typeof(IObserver), typeof(CertificateObserver));
            _ = this.services.AddScoped(typeof(IObserver), typeof(DiskObserver));
            _ = this.services.AddScoped(typeof(IObserver), typeof(FabricSystemObserver));
            _ = this.services.AddScoped(typeof(IObserver), typeof(NetworkObserver));
            _ = this.services.AddScoped(typeof(IObserver), typeof(OsObserver));
            _ = this.services.AddScoped(typeof(IObserver), typeof(SfConfigurationObserver));

            await this.LoadObserversFromPluginsAsync();
        }

        private Task LoadObserversFromPluginsAsync(bool isConfigUpdate = false)
        {
            string pluginsDir = Path.Combine(this.Context.CodePackageActivationContext.GetDataPackageObject("Data").Path, "plugins");

            if (Directory.Exists(pluginsDir))
            {
                string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.TopDirectoryOnly);

                foreach (string pluginDll in pluginDlls)
                {
                    Assembly pluginAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginDll);
                    FabricObserverStartupAttribute[] startupAttributes =
                        pluginAssembly.GetCustomAttributes<FabricObserverStartupAttribute>().ToArray();

                    for (int i = 0; i < startupAttributes.Length; ++i)
                    {
                        object startupObject = Activator.CreateInstance(startupAttributes[i].StartupType);

                        /*if (this.observerManager.Observers.Contains((IObserver)startupObject))
                        {
                            while (true)
                            {
                                var isObserverRunning =
                                    this.observerManager.Observers.Any(
                                         o => o.ObserverName == ((IObserver)startupObject).ObserverName && o.IsRunning);

                                if (!isObserverRunning)
                                {
                                    this.observerManager.Observers.Remove((IObserver)startupObject);

                                    break;
                                }
                                else
                                {
                                    await Task.Delay(1000);
                                }
                            }
                        }*/

                        this.services.AddScoped(typeof(IObserver), startupObject.GetType());

                        /*if (isConfigUpdate)
                        {
                            this.observerManager.Observers.Add((IObserver)startupObject);
                        }*/

                        /*if (startupObject is IFabricObserverStartup fabricObserverStartup)
                        {
                            // this.services.AddScoped(typeof(IObserver), startupObject.GetType());
                            fabricObserverStartup.ConfigureServices(this.services);
                        }*/
                    }
                }
            }

            return Task.CompletedTask;
        }
    }
}
