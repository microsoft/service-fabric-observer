// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserverWeb
{
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.IO;
    using System.Net;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
    using Microsoft.ServiceFabric.Services.Communication.Runtime;
    using Microsoft.ServiceFabric.Services.Runtime;

    /// <summary>
    /// The FabricRuntime creates an instance of this class for each service type instance.
    /// </summary>
    internal sealed class FabricObserverWeb : StatelessService, IDisposable
    {
        private FabricClient fabricClient;
        private bool disposed = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserverWeb"/> class.
        /// </summary>
        /// <param name="context">service context...</param>
        public FabricObserverWeb(StatelessServiceContext context)
            : base(context)
        {
            this.fabricClient = new FabricClient();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            this.Dispose(true);
        }

        /// <summary>
        /// Optional override to create listeners (like tcp, http) for this service instance.
        /// </summary>
        /// <returns>The collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return new ServiceInstanceListener[]
            {
                new ServiceInstanceListener(serviceContext =>
                    new KestrelCommunicationListener(serviceContext, "ServiceEndpoint", (url, listener) =>
                    {
                        ServiceEventSource.Current.ServiceMessage(serviceContext, $"Starting Kestrel on {url}");

                        return new WebHostBuilder()
                                    .UseKestrel(opt =>
                                    {
                                        int port = serviceContext.CodePackageActivationContext.GetEndpoint("ServiceEndpoint").Port;
                                        opt.Listen(IPAddress.Loopback, port, listenOptions =>
                                        {
                                            listenOptions.NoDelay = true;
                                        });
                                    })
                                    .ConfigureServices(services => services.AddSingleton(serviceContext))
                                    .ConfigureServices(services => services.AddSingleton(this.fabricClient))
                                    .UseContentRoot(Directory.GetCurrentDirectory())
                                    .UseStartup<Startup>()
                                    .UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.None)
                                    .UseUrls("http://localhost:5000") // localhost only, by default...
                                    .Build();
                    })),
            };
        }

        private void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    if (this.fabricClient != null)
                    {
                        this.fabricClient.Dispose();
                        this.fabricClient = null;
                    }
                }

                this.disposed = true;
            }
        }
    }
}