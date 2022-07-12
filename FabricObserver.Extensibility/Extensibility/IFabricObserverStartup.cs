// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using Microsoft.Extensions.DependencyInjection;

namespace FabricObserver
{
    /// <summary>
    /// FabricObserver plugin startup interface.
    /// </summary>
    public interface IFabricObserverStartup
    {
        /// <summary>
        /// This function is called by FabricObserver during observer instance construction. You must implement this function in your Startup class for your plugin.
        /// </summary>
        /// <param name="services">ServiceCollection instance.</param>
        /// <param name="fabricClient">FabricClient instance. Note: This is only here to preserve historical specification of IFabricObserverStartup. 
        /// FabricObserver manages a static singleton instance of FabricClient that is used by all observers and related FO objects. Singleton impl protects against
        /// premature disposal by, say, a plugin.</param>
        /// <param name="context">StatelessServiceContext instance. Injected by FabricObserver.</param>
        void ConfigureServices(IServiceCollection services, FabricClient fabricClient, StatelessServiceContext context);
    }
}
