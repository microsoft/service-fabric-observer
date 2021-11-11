using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace Aggregator
{

    public interface IMyCommunication : IService
    {
        Task PutData(string NodeName,Data data);
    }
    /// <summary>
    /// An instance of this class is created for each service replica by the Service Fabric runtime.
    /// </summary>
    internal sealed class Aggregator : StatefulService,IMyCommunication
    {
        protected CancellationToken token { get; set; }

        public Aggregator(StatefulServiceContext context)
            : base(context)
        { }

        public async Task PutData(string NodeName,Data data)
        {
            Debug.WriteLine("Aggregator");
            Debug.WriteLine("------ From node : " + NodeName + " -------");
            Debug.WriteLine("------ cpu: "+data.Cpu+" -------");
            Debug.WriteLine("------ toatal memory: " + data.TotalMemoryGb + " -------");
            Debug.WriteLine("------ memory in use: " + data.MemoryInUseMb + " -------");
            Debug.WriteLine("------ % of memeory: " + data.PercentInUse + " -------");
            foreach( var d in data.allDrives)
            {
                Debug.WriteLine("------ Drive name: " + d.Name + " -------");
                Debug.WriteLine("------ Drive total space: " + d.TotalDiskSpaceGB + " -------");
                Debug.WriteLine("------ Drive available space: " + d.AvailableDiskSpaceGB + " -------");

            }

            await AddDataAsync(NodeName, data);
            var x = await GetDataAsync(NodeName);
            Data dd= await PeekFirstAsync(NodeName);



        }



        /// <summary>
        /// Optional override to create listeners (e.g., HTTP, Service Remoting, WCF, etc.) for this service replica to handle client or user requests.
        /// </summary>
        /// <remarks>
        /// For more information on service communication, see https://aka.ms/servicefabricservicecommunication
        /// </remarks>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return this.CreateServiceRemotingReplicaListeners();
        }

        /// <summary>
        /// This is the main entry point for your service replica.
        /// This method executes when this replica of your service becomes primary and has write status.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service replica.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            // TODO: Replace the following sample code with your own logic 
            //       or remove this RunAsync override if it's not needed in your service.
            this.token = cancellationToken;
            var myDictionary = await this.StateManager.GetOrAddAsync<IReliableDictionary<string, long>>("myDictionary");
            

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using (var tx = this.StateManager.CreateTransaction())
                {
                    var result = await myDictionary.TryGetValueAsync(tx, "Counter");

                    ServiceEventSource.Current.ServiceMessage(this.Context, "Current Counter Value: {0}",
                        result.HasValue ? result.Value.ToString() : "Value does not exist.");

                    await myDictionary.AddOrUpdateAsync(tx, "Counter", 0, (key, value) => ++value);
                    //myDictionary.

                    // If an exception is thrown before calling CommitAsync, the transaction aborts, all changes are 
                    // discarded, and nothing is saved to the secondary replicas.
                    await tx.CommitAsync();
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        public async Task AddDataAsync(string nodeName,Data data)
        {
            var stateManager = this.StateManager;
            IReliableQueue<Data> reliableQueue = null ;
            while (reliableQueue == null) {
                try
                {
                    reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<Data>>(nodeName);
                }
                catch (Exception e) { }
            }
            using(var tx = stateManager.CreateTransaction())
            {
                await reliableQueue.EnqueueAsync(tx, data);
                await tx.CommitAsync();
                
            }
            

        }

        public async Task<Data> PeekFirstAsync(string nodeName)
        {
            var stateManager = this.StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<Data>>(nodeName);

            using (var tx = stateManager.CreateTransaction())
            {
                
                return (await reliableQueue.TryPeekAsync(tx)).Value; //why do I have to do this in a transaction ?!
                
            }
        }

        public async Task<List<Data>> GetDataAsync(string nodeName)
        {
            List < Data > list= new List<Data>();

            var stateManager = this.StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<Data>>(nodeName);
            try 
            { 
                using (var tx = stateManager.CreateTransaction())
                {
                    var iterator = (await reliableQueue.CreateEnumerableAsync(tx)).GetAsyncEnumerator();
                    //iterator.Reset(); // this is position -1 - before the first element in the collection
                
                    Data data=null;
                    while (await iterator.MoveNextAsync(token))
                    {
                        data = iterator.Current;

                        if (data != null) list.Add(data);
                    }
                }
            }
            catch(Exception e)
            {
                var x = e;
            }

            return list;
        }
    }
}
