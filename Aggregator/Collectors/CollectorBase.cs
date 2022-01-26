using Aggregator.Data;
using FabricObserver.Observers.Utilities;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace Aggregator.Collectors
{
    /// <summary>
    /// This is the base class that implements all the generic logic for collecting data. 
    /// Data is collected locally on each node in a seperate thread that runs Batching.
    /// Batching - appends to the shared list 'dataList' on a fixed interval by collecting data. This is the producer.
    /// The main thread is the consumer. It consumes the list and produces a single data point that represents the average for that interval
    /// After producing the average data point it sends it to the Aggreagtor.
    /// There are 2 frequencies (periods):
    /// 1) batching
    /// 2) aggregating
    /// batching interval must be less than aggregating interval
    /// TODO: add a config file that allows the customer to set these 2 intervals
    /// While type T can be any concrete implementation of DataBase it shoud be NodeData or ClusterData
    /// NodeData - if you want to run this collector on every node - FabricObserver logic
    /// ClusterData - if you want to run this collector on a single node - ClusterObserver logic
    /// </summary>
    /// <typeparam name="T">T should be either NodeData or ClusterData</typeparam>
    public abstract class CollectorBase<T> where T : DataBase<T>
    {
        private readonly FabricClient fabricClient;
        private readonly ObserverHealthReporter healthReporter;
        private readonly Logger logger;
        protected ServiceContext Context;
        protected CancellationToken cancellationToken;
        
        /// <summary>
        /// batching appends to this list
        /// </summary>
        protected List<T> dataList = new List<T>();
        
        /// <summary>
        /// mutex for dataList
        /// </summary>
        protected readonly object lockObj = new object();

        public CollectorBase(ServiceContext context, CancellationToken cancellationToken)
        {
            Context = context;
            this.cancellationToken = cancellationToken;
            fabricClient = new FabricClient();
            logger = new Logger("Collector");
            healthReporter = new ObserverHealthReporter(logger, fabricClient);
        }

        public async Task RunAsync(string ReliableQueueName) 
        {
            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                                    new Uri("fabric:/Internship/Aggregator"),
                                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0));

            string nodeName = Context.NodeContext.NodeName;
            Thread batchingThread = new Thread(async ()=>await Batching(SFUtilities.intervalMiliseconds / 10));
            batchingThread.Start();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {

                    var (_, delta) = SFUtilities.TupleGetTime();
                    await Task.Delay(TimeSpan.FromMilliseconds(delta), cancellationToken);
                    T averageData = default(T);

                    lock (lockObj)
                    {
                        if (dataList.Count == 0) continue;
                        averageData = dataList[0].AverageData(dataList);
                        dataList.Clear();
                    }

                    await AggregatorProxy.PutDataRemote(ReliableQueueName, ByteSerialization.ObjectToByteArray(averageData));
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    var appHealthReport = new HealthReport
                    {
                        AppName = new Uri("fabric:/Internship"),
                        Code = "99",
                        HealthMessage = $"Unhandled exception collecting perf data: {e}",
                        NodeName = Context.NodeContext.NodeName,
                        PartitionId = Context.PartitionId,
                        ReplicaOrInstanceId = Context.ReplicaOrInstanceId,
                        ReportType = HealthReportType.Application,
                        ServiceName = Context.ServiceName,
                        SourceId = "Collector",
                        Property = "Data Collection Failure",
                        State = HealthState.Warning,
                        HealthReportTimeToLive = TimeSpan.FromMinutes(10),
                        EmitLogEvent = true, // This info will also be written to C:\fabric_observer_logs\Collector folder
                    };

                    healthReporter.ReportHealthToServiceFabric(appHealthReport);

                    throw; // Fix the bugs.
                }
            }
        }

        private async Task Batching(double intervalMiliseconds)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                T data = await CollectData();

                lock (lockObj)
                {
                    dataList.Add(data);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(intervalMiliseconds), cancellationToken);
            }
        }

        protected abstract Task<T> CollectData();
    }
}
