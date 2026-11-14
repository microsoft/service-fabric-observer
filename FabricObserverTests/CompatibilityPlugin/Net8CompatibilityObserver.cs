using System;
using System.Fabric;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver;
using FabricObserver.Observers;
using Microsoft.Extensions.DependencyInjection;

[assembly: FabricObserverStartup(typeof(FabricObserver.CompatibilityPlugin.Net8CompatibilityStartup))]

namespace FabricObserver.CompatibilityPlugin
{
    public sealed class Net8CompatibilityStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services, FabricClient fabricClient, StatelessServiceContext context)
        {
            services.AddScoped(typeof(ObserverBase), _ => new Net8CompatibilityObserver(fabricClient, context));
        }
    }

    public sealed class Net8CompatibilityObserver : ObserverBase
    {
        public Net8CompatibilityObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
        }

        public override Task ObserveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ReportAsync(token);
        }

        public override Task ReportAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string evidencePath = Environment.GetEnvironmentVariable(
                "FABRIC_OBSERVER_COMPATIBILITY_LOG", EnvironmentVariableTarget.Machine);

            if (!string.IsNullOrEmpty(evidencePath))
            {
                File.AppendAllText(evidencePath, $"net8.0 plugin executed on {Environment.Version}{Environment.NewLine}");
            }

            return Task.CompletedTask;
        }
    }
}
