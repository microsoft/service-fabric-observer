using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aggregator;
using Aggregator.Collectors;
using FabricObserver.Observers.Utilities;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using Microsoft.ServiceFabric.Services.Runtime;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace Collector
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class Collector : StatelessService
    {
        // CT: Don't create a new FabricClient each iteration. Just use one instance. This is important for memory use.
        private readonly FabricClient fabricClient;
        private readonly ObserverHealthReporter healthReporter;
        private readonly Logger logger;

        public Collector(StatelessServiceContext context)
            : base(context)
        {
            fabricClient = new FabricClient();
            
            // CT: you can use FO Logger to stuff to Collector folder.
            logger = new Logger("Collector");
            healthReporter = new ObserverHealthReporter(logger, fabricClient);
        }

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return new ServiceInstanceListener[0];
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            NodeCollector c = new NodeCollector(Context, cancellationToken);
            await c.RunAsync(Context.NodeContext.NodeName);
        }

        
    }
}
