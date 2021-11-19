using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aggregator;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using Microsoft.ServiceFabric.Services.Runtime;


//This is a test class for ClusterObserver

namespace ClusterCollector
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class ClusterCollector : StatelessService
    {
        

        public ClusterCollector(StatelessServiceContext context)
            : base(context)
        { }

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
            // TODO: Replace the following sample code with your own logic 
            //       or remove this RunAsync override if it's not needed in your service.

            long iterations = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ServiceEventSource.Current.ServiceMessage(this.Context, "Working-{0}", ++iterations);

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                Debug.WriteLine("----------- "+await SFUtilities.Instance.TupleGetDeployedCountsAsync());
                var (primaryCount, replicaCount, instanceCount, count) = await SFUtilities.Instance.TupleGetDeployedCountsAsync();
                var data = new SFData(primaryCount,replicaCount,instanceCount,count);
                var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );
                await AggregatorProxy.PutDataRemote(SFData.queueName, ByteSerialization.ObjectToByteArray(data));
            }
        }
    }
}
