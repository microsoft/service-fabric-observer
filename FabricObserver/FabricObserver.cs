// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
//using FabricObserver.Tests;

namespace FabricObserver
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class FabricObserver : StatelessService
    {
        private ObserverManager observerManager = null;

        public FabricObserver(StatelessServiceContext context) : base(context)
        {
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
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            observerManager = new ObserverManager(Context, cancellationToken);

            await Task.Factory.StartNew(() => this.observerManager.StartObservers()).ConfigureAwait(true);
        }

        protected override Task OnCloseAsync(CancellationToken cancellationToken)
        {
            if (this.observerManager != null)
            {
                this.observerManager.Dispose();
            }

            return base.OnCloseAsync(cancellationToken);
        }
    }
}
