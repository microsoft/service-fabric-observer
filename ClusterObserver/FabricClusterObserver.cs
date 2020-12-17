using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Runtime;

namespace ClusterObserver
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class FabricClusterObserver : StatelessService
    {
        private ClusterObserverManager observerManager;

        public FabricClusterObserver(StatelessServiceContext context)
            : base(context)
        {

        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            this.observerManager = new ClusterObserverManager(Context, cancellationToken);

            await Task.Factory.StartNew(() => this.observerManager.Start()).ConfigureAwait(true);
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
