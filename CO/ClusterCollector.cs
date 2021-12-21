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
            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ServiceEventSource.Current.ServiceMessage(this.Context, "Working-{0}", ++iterations);

                var (_, delta) = SFUtilities.getTime();

                await Task.Delay(TimeSpan.FromMilliseconds(delta), cancellationToken);
                ClusterData data = await CollectBatching(SFUtilities.intervalMiliseconds / 10, 7, cancellationToken);
                await AggregatorProxy.PutDataRemote(ClusterData.queueName, ByteSerialization.ObjectToByteArray(data));

            }
        }

        private async Task<ClusterData> CollectBatching(double intervalMiliseconds, int batchCount, CancellationToken cancellationToken)
        {
            List<ClusterData> clusterDataList = new List<ClusterData>();

            for (int j = 0; j < batchCount; j++)
            {
                // Collect Data
                var (primaryCount, replicaCount, instanceCount, count) = await SFUtilities.Instance.TupleGetDeployedCountsAsync();

                string nodeName = this.Context.NodeContext.NodeName;


                var (totalMiliseconds, _) = SFUtilities.getTime();
                var data = new ClusterData(totalMiliseconds, primaryCount, replicaCount, instanceCount, count);
                

                clusterDataList.Add(data);
                await Task.Delay(TimeSpan.FromMilliseconds(intervalMiliseconds), cancellationToken);

            }

            return ClusterData.AverageClusterData(clusterDataList);
        }
    }
}
