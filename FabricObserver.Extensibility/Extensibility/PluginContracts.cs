using System;
using System.Fabric;
using Microsoft.Extensions.DependencyInjection;

namespace FabricObserver
{
    public static class PluginContracts
    {
        public static Type[] GetSharedTypes() =>
        [
            typeof(FabricObserverStartupAttribute),
            typeof(IFabricObserverStartup),
            typeof(IServiceCollection),
            // SDK types in plugin signatures must resolve to the host's Service Fabric assembly.
            typeof(FabricClient),
            typeof(StatelessServiceContext)
        ];
    }
}
