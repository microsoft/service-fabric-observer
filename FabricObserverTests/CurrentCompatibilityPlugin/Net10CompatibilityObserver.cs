using System;
using System.Fabric;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver;
using FabricObserver.Observers;
using Microsoft.Extensions.DependencyInjection;

[assembly: FabricObserverStartup(typeof(FabricObserver.CurrentCompatibilityPlugin.Net10CompatibilityStartup))]

namespace FabricObserver.CurrentCompatibilityPlugin
{
    public sealed class Net10CompatibilityStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services, FabricClient fabricClient, StatelessServiceContext context)
        {
            services.AddScoped(typeof(ObserverBase), _ => new Net10CompatibilityObserver(fabricClient, context));
        }
    }

    public sealed class Net10CompatibilityObserver : ObserverBase
    {
        public Net10CompatibilityObserver(FabricClient fabricClient, StatelessServiceContext context)
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
                "FABRIC_OBSERVER_CURRENT_PLUGIN_LOG", EnvironmentVariableTarget.Machine);

            if (!string.IsNullOrEmpty(evidencePath))
            {
                File.AppendAllText(evidencePath, $"net10.0 plugin executed on {Environment.Version}{Environment.NewLine}");
            }

            return Task.CompletedTask;
        }
    }
}
