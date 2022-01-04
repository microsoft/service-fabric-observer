using Aggregator.Data;
using FabricObserver.Observers.Utilities;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace Aggregator.Collectors
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"> T is the type that's being collected by this class</typeparam>
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
            // CT: you can use FO Logger to stuff to Collector folder.
            logger = new Logger("Collector");
            healthReporter = new ObserverHealthReporter(logger, fabricClient);
        }

        public async Task RunAsync(string ReliableQueueName) 
        {
            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                new Uri("fabric:/Internship/Aggregator"),
                new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                );
            string nodeName = Context.NodeContext.NodeName;

            Thread batchingThread = new Thread(async ()=>await Batching(SFUtilities.intervalMiliseconds / 10));
            batchingThread.Start();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {

                    var (_, delta) = SFUtilities.getTime();
                    await Task.Delay(TimeSpan.FromMilliseconds(delta), cancellationToken);
                    T averageData = default(T);
                    lock (lockObj)
                    {
                        if (dataList.Count == 0) continue;
                        var dummyObject = dataList[0];
                        averageData = dummyObject.AverageData(dataList);
                        dataList.Clear();
                    }

                    await AggregatorProxy.PutDataRemote(ReliableQueueName, ByteSerialization.ObjectToByteArray(averageData));
                }
                
                catch (Exception e) when (!(e is OperationCanceledException))
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
                    //fabricClient.HealthManager.ReportHealth(appHealthReport);

                    throw; // CT: Fix the bugs. This will take down the replica/service process. SF will then restart it, per usual.
                }
            }
        }

        private async Task Batching(double intervalMiliseconds)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                T data =await CollectData();
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
