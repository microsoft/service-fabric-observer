using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
//using System.Fabric.Query;
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
        Task PutDataRemote(string queueName,byte[] data);
        Task<List<byte[]>> GetDataRemote(string queueName);
        Task<Snapshot> GetSnapshotRemote();
        Task<List<Snapshot>> GetSnapshotsRemote(double milisecondsLow, double milisecondsHigh);

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

        public async Task PutDataRemote(string queueName,byte[] data)
        {
            await AddDataAsync(queueName, data);
            var x = await GetDataAsync(queueName);
            byte[] dd= await PeekFirstAsync(queueName);
        }

        public async Task<List<byte[]>> GetDataRemote(string queueName)
        {
            return await GetDataAsync(queueName);
        }

        public async Task<Snapshot> GetSnapshotRemote()
        {
            //Multiuple threads enter -> RunAsync -> this breaks -> it seems that the IRealiableQueue isn't thread safe 
            Debug.WriteLine("--------GetSnapshots----------"+Thread.CurrentThread.ManagedThreadId+"----------------------");
            System.Fabric.Query.NodeList nodeList = await SFUtilities.Instance.GetNodeListAsync();

            List<NodeData> HWList = new List<NodeData>();
            ClusterData sf = null;
            bool check=false;
            double minTime =await MinTimeStampInQueue(nodeList);
            bool success=true;
            if (minTime == -1) return null; //queues empty

            byte[] sfb = await PeekFirstAsync(ClusterData.queueName);
            if (sfb != null)
            {
                sf = (ClusterData)ByteSerialization.ByteArrayToObject(sfb);
                check = Snapshot.checkTime(minTime, sf.miliseconds);
                if (!check) success = false; //Snapshot must contain SFData
                else await DequeueAsync(ClusterData.queueName);
            }
            else  success=false; //QUEUE is empty
            foreach(var node in nodeList)
            {
                byte[] hwb = await PeekFirstAsync(node.NodeName);
                if (hwb == null) continue; // QUEUE in empty
                NodeData hw = (NodeData)ByteSerialization.ByteArrayToObject(hwb);
                check = Snapshot.checkTime(minTime, hw.miliseconds);
                if (check)
                {
                    HWList.Add(hw);
                    await DequeueAsync(node.NodeName);
                }
            }
            if (HWList.Count == 0) success=false; //Snapshot must have at least 1 HWData
            if(success)return new Snapshot(minTime,sf, HWList);
            return null; //Something failed
        }
       
        private async Task ProduceSnapshot() 
        {
            Debug.WriteLine("---------PRODUCE---------" + Thread.CurrentThread.ManagedThreadId + "----------------------");
            try
            {
                Snapshot snap = await GetSnapshotRemote();
                if (snap != null) await AddDataAsync(Snapshot.queueName, ByteSerialization.ObjectToByteArray(snap));
            }
            catch (Exception x)
            {
                Debug.WriteLine(x.ToString());
            }
        }
        private async Task<int> GetMinQueueCount()
        {
            int min_count = int.MaxValue;
            System.Fabric.Query.NodeList nodeList = await SFUtilities.Instance.GetNodeListAsync();

            
            using (var tx = this.StateManager.CreateTransaction())
            {
                int count;
                foreach(var node in nodeList)
                {
                    count =(int)(await(await this.StateManager.GetOrAddAsync<IReliableQueue<byte[]>>(node.NodeName)).GetCountAsync(tx));
                    if (count < min_count) min_count = count;
                }

                count = (int)(await (await this.StateManager.GetOrAddAsync<IReliableQueue<byte[]>>(ClusterData.queueName)).GetCountAsync(tx));
                if (count < min_count) min_count = count;

            }
            return min_count;
        }
        /// <summary>
        /// returns the min timestamp at the beginning of all queues
        /// if none return -1
        /// </summary>
        /// <param name="nodeList"></param>
        /// <returns></returns>
        private async Task<double> MinTimeStampInQueue(System.Fabric.Query.NodeList nodeList)
        {
            double timeStamp = -1;
            foreach(var node in nodeList)
            {
                var hw = await PeekFirstAsync(node.NodeName);
                if (hw != null)
                {
                    var data=(NodeData)ByteSerialization.ByteArrayToObject(hw);
                    if (data.miliseconds < timeStamp || timeStamp == -1) timeStamp = data.miliseconds;
                }
            }
            
            var sf = await PeekFirstAsync(ClusterData.queueName);
            if (sf != null)
            {
                var data = (ClusterData)ByteSerialization.ByteArrayToObject(sf);
                if (data.miliseconds < timeStamp || timeStamp == -1) timeStamp = data.miliseconds;
            }
                
            
            return timeStamp;
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

                //using (var tx = this.StateManager.CreateTransaction())
                //{
                //    var result = await myDictionary.TryGetValueAsync(tx, "Counter");

                //    ServiceEventSource.Current.ServiceMessage(this.Context, "Current Counter Value: {0}",
                //        result.HasValue ? result.Value.ToString() : "Value does not exist.");

                //    await myDictionary.AddOrUpdateAsync(tx, "Counter", 0, (key, value) => ++value);
                //    //myDictionary.

                //    // If an exception is thrown before calling CommitAsync, the transaction aborts, all changes are 
                //    // discarded, and nothing is saved to the secondary replicas.
                //    await tx.CommitAsync();
                //}

                Debug.WriteLine("---------RUN---------"+Thread.CurrentThread.ManagedThreadId+"----------------------");

                await Task.Delay(TimeSpan.FromMilliseconds(SFUtilities.intervalMiliseconds), cancellationToken);
                while(await GetMinQueueCount() > 1)
                {
                   await ProduceSnapshot(); 
                }
            }
        }


        public async Task AddDataAsync(string queueName,byte[] data)
        {
            var stateManager = this.StateManager;
            IReliableQueue<byte[]> reliableQueue = null ;
            while (reliableQueue == null) {
                try
                {
                    reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);
                }
                catch (Exception e) { }
            }
            using(var tx = stateManager.CreateTransaction())
            {
                await reliableQueue.EnqueueAsync(tx, data);
                await tx.CommitAsync();
                
            }
            

        }

        public async Task<byte[]> PeekFirstAsync(string queueName)
        {
            var stateManager = this.StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);
           
            using (var tx = stateManager.CreateTransaction())
            {
                var conditional = await reliableQueue.TryPeekAsync(tx);
                if (conditional.HasValue)
                    return conditional.Value; //why do I have to do this in a transaction ?!
                else return null;
            }
        }

        public async Task<byte[]> DequeueAsync(string queueName)
        {
            var stateManager = this.StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);

            using (var tx = stateManager.CreateTransaction())
            {
                var conditional = await reliableQueue.TryDequeueAsync(tx);
                await tx.CommitAsync();
                if (conditional.HasValue)
                    return conditional.Value; //why do I have to do this in a transaction ?!
                else return null;
            }
        }

        public async Task<List<byte[]>> GetDataAsync(string queueName)
        {
            if (queueName == null) return null;
            List < byte[] > list= new List<byte[]>();

            var stateManager = this.StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(queueName);
            try 
            { 
                using (var tx = stateManager.CreateTransaction())
                {
                    var iterator = (await reliableQueue.CreateEnumerableAsync(tx)).GetAsyncEnumerator();
                    //iterator.Reset(); // this is position -1 - before the first element in the collection
                
                    byte[] data=null;
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

        public async Task<List<Snapshot>> GetSnapshotsRemote(double milisecondsLow, double milisecondsHigh)
        {
            List<Snapshot> list = new List<Snapshot>();

            var stateManager = this.StateManager;
            var reliableQueue = await stateManager.GetOrAddAsync<IReliableQueue<byte[]>>(Snapshot.queueName);
            try
            {
                using (var tx = stateManager.CreateTransaction())
                {
                    var iterator = (await reliableQueue.CreateEnumerableAsync(tx)).GetAsyncEnumerator();
                    //iterator.Reset(); // this is position -1 - before the first element in the collection

                    byte[] data = null;
                    while (await iterator.MoveNextAsync(token))
                    {
                        data = iterator.Current;

                        if (data != null) {
                            Snapshot s = (Snapshot)ByteSerialization.ByteArrayToObject(data);
                            if (s.miliseconds >= milisecondsLow && s.miliseconds <= milisecondsHigh) list.Add(s);
                            else 
                            {//help garbage collector to free memory
                                s = null;
                                data = null;
                            }
                        } 
                    }
                }
            }
            catch (Exception e)
            {
                var x = e;
            }

            return list;
        }
    }
}
