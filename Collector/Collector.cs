using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aggregator;
using FabricObserver.Observers.Utilities;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using Microsoft.ServiceFabric.Services.Runtime;

namespace Collector
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class Collector : StatelessService
    {
        public Collector(StatelessServiceContext context)
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

                var (_, delta) = SFUtilities.getTime(); 
                

                await Task.Delay(TimeSpan.FromMilliseconds(delta), cancellationToken);


                // Collect Data
                float cpu = CpuUtilizationProvider.Instance.GetProcessorTimePercentage();
                var (TotalMemoryGb, MemoryInUseMb, PercentInUse) = OSInfoProvider.Instance.TupleGetMemoryInfo();
                DriveInfo[] allDrives = DriveInfo.GetDrives();
                string nodeName = this.Context.NodeContext.NodeName;

                //Remote Procedure Call to Aggregator
                var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );
                var (totalMiliseconds, _) = SFUtilities.getTime();


                Dictionary<long, Uri> pids = await SFUtilities.Instance.GetDeployedProcesses(this.Context.NodeContext.NodeName);

                //for instead of Parallel.For because Parallel.For bursts the CPU before we load it and this is more lightweigh on the cpu even though timing isn't exact. This is more relevant
                for(int i = 0; i<pids.Count; i++)
                {
                    long pid=pids.ElementAt(i).Key;
                    var (cpuPercentage, ram) = await SFUtilities.Instance.TupleGetResourceUsageForProcess(pid);
                }

                var data = new NodeData(totalMiliseconds);
                data.hardware = new Hardware(cpu, TotalMemoryGb, MemoryInUseMb, PercentInUse, allDrives);
                

                //await AggregatorProxy.PutDataRemote(nodeName,ByteSerialization.ObjectToByteArray(data));
                
                //This isn't FIFO
                AggregatorProxy.PutDataRemote(nodeName, ByteSerialization.ObjectToByteArray(data));


            }


        }
    }
}
