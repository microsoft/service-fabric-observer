using Aggregator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.Linq;
using System.Threading.Tasks;


namespace API.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class DataController : ControllerBase
    {

        

        public DataController()
        {
            
        }

        [HttpGet]
        public async Task<string> Get([FromQuery]string NodeName)
        {
            string response = "";

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );

            List<byte[]> originList=await AggregatorProxy.GetDataRemote(NodeName);
            List<NodeData> targetList=originList.ConvertAll<NodeData>(data=>(NodeData)ByteSerialization.ByteArrayToObject(data));
            foreach(var data in targetList)
            {
                response += data.ToString();
            }
            return response;
        }

        [HttpGet]
        [Route("CustomMetrics")]
        public async Task<string> CustomMetrics()
        {
            string response = "";

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );

            List<byte[]> originList = await AggregatorProxy.GetDataRemote(ClusterData.queueName);
            List<ClusterData> targetList = originList.ConvertAll<ClusterData>(data => (ClusterData)ByteSerialization.ByteArrayToObject(data));
            foreach (var data in targetList)
            {
                response += data.ToString();
            }
            return response;
        }

        [HttpGet]
        [Route("AllSnapshots")]
        public async Task<string> allSnapshots()
        {
            string response = "";

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );

            List<byte[]> originList = await AggregatorProxy.GetDataRemote(Snapshot.queueName);
            List<Snapshot> targetList = originList.ConvertAll<Snapshot>(data => (Snapshot)ByteSerialization.ByteArrayToObject(data));
            foreach (var data in targetList)
            {
                response += data.ToString();
            }
            return response;
        }
        [HttpGet]
        [Route("AllSnapshotsDetails")]
        public async Task<string> allSnapshotsDetails()
        {
            string response = "";

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );

            List<byte[]> originList = await AggregatorProxy.GetDataRemote(Snapshot.queueName);
            List<Snapshot> targetList = originList.ConvertAll<Snapshot>(data => (Snapshot)ByteSerialization.ByteArrayToObject(data));
            foreach (var data in targetList)
            {
                response += data.ToStringAllData();
            }
            return response;
        }

        [HttpGet]
        [Route("NodeAdvice")]
        public async Task<string> NodeAdvice([FromQuery] string NodeName)
        {
            string response = "";

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );

            List<byte[]> originList = await AggregatorProxy.GetDataRemote(Snapshot.queueName);
            List<Snapshot> targetList = originList.ConvertAll<Snapshot>(data => (Snapshot)ByteSerialization.ByteArrayToObject(data));
            
            NodeData data = Snapshot.AverageNodeData(NodeName, targetList);

            response +=
                "\n Node: " + NodeName +
                "\n Number of Snaphsot: " + targetList.Count +
                "\n Average Node CPU: " + data.hardware.Cpu +
                "\n Average Node RAM: " + data.hardware.PercentInUse +
                "\n Average Node Disk: " + data.hardware.DiskPercentageInUse();
            foreach(var process in data.processList)
            {
                response += process.ToString();
            }

                return response;
        }
        [HttpGet]
        [Route("ClusterAdvice")]
        public async Task<string> ClusterAdvice()
        {
            string response = "";

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );
            var (milisecondsLow, _) = SFUtilities.getTime();
            milisecondsLow -= SFUtilities.intervalMiliseconds * 10;
            double milisecondsHigh = milisecondsLow + SFUtilities.intervalMiliseconds * 5;
            //List<byte[]> originList = await AggregatorProxy.GetDataRemote(Snapshot.queueName);
            //List<Snapshot> targetList = originList.ConvertAll<Snapshot>(data => (Snapshot)ByteSerialization.ByteArrayToObject(data));
            List<Snapshot> targetList = await AggregatorProxy.GetSnapshotsRemote(double.MinValue,double.MaxValue);

            Snapshot data = Snapshot.AverageClusterData(targetList);
            NodeData nodeData = data.nodeMetrics[0].AverageData(data.nodeMetrics);
            response +=
                
                "\n Number of Snaphsot: " + targetList.Count +
                "\n Average primary: "+data.customMetrics.allCounts.PrimaryCount+
                "\n Average replica: " + data.customMetrics.allCounts.ReplicaCount +
                "\n Average instance: " + data.customMetrics.allCounts.InstanceCount +
                "\n Average count: " + data.customMetrics.allCounts.Count +
                "\n Average CPU: " + nodeData.hardware.Cpu +
                "\n Average RAM: " + nodeData.hardware.PercentInUse +
                "\n Average Disk: " + nodeData.hardware.DiskPercentageInUse();
            foreach (var process in nodeData.processList)
            {
                response += process.ToString();
            }

            float scalingFactorCpu = (100 - nodeData.hardware.Cpu) / nodeData.sfHardware.Cpu;
            float scalingFactorRam = (float)((100 - nodeData.hardware.PercentInUse) / nodeData.sfHardware.PercentInUse);
            float scalingFactor = Math.Min(scalingFactorRam, scalingFactorCpu);

            response += "\n \n \n \n You can scale out your cluster porportionally " + scalingFactor + " times";

            return response;
        }
        [HttpGet]
        [Route("DeleteAllSnapshots")]
        public async Task<string> DeleteSnapshots()
        {
            

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );

            await AggregatorProxy.DeleteAllSnapshotsRemote();
            return "ClearAsync() is part of the ReliableCollection, but the method is not implemented for the reliable queue ?!";
        }
        //[HttpGet]
        //[Route("Snapshot")]
        //public async Task<string> GetSnapshot()
        //{
        //    string response = "";

        //    var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
        //            new Uri("fabric:/Internship/Aggregator"),
        //            new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
        //            );

        //    Snapshot snap = await AggregatorProxy.GetSnapshotRemote();
        //    //snap.GetNodeProcessesHardwareData("_Node_3");
        //    if (snap == null) return "Empty queue";
        //    response+=
        //        "\n Average cluster capacity: "+snap.CalculateAverageCapacity()+
        //        "\n Average resource usage: "+snap.AverageClusterResourseUsage();
        //    return response;
        //}


        [HttpGet]
        [Route("info")]
        public async Task<string> Info([FromQuery] string NodeName)
        {
            string res;
            var fabricClient = new FabricClient();
            var queryManager = fabricClient.QueryManager;
            var appList =await queryManager.GetDeployedApplicationListAsync(NodeName);
            Dictionary<long, Uri> pid=new Dictionary<long, Uri>();
            foreach(var app in appList)
            {
                var replicaList =await queryManager.GetDeployedReplicaListAsync(NodeName, app.ApplicationName);
                foreach(var replica in replicaList)
                {
                    //replica.ServiceName
                    pid.Add(replica.HostProcessId, replica.ServiceName);
                }
            }
            return pid.ToString();
            //return "A";
        }
        [HttpGet]
        [Route("infoo")]
        public async Task<string> Infoo([FromQuery] string NodeName)
        {
            //return "B";
            //int instances = 0;
            string res;
            var fabricClient = new FabricClient();
            var queryManager = fabricClient.QueryManager;
            var clusterLoad=await queryManager.GetClusterLoadInformationAsync();
            res =clusterLoad.ToString();
            ApplicationList appList = await queryManager.GetApplicationListAsync();
            Uri appUri;
            int primaryCount = 0; 
            int replicaCount = 0; //Primary + secondary for statefull services
            int instanceCount = 0; //Replica count for stateless services
            int count = 0; //replicaCount + instanceCount
            foreach(Application app in appList)
            {
                //appUri= app.ApplicationName.AbsoluteUri;
                appUri = app.ApplicationName;
                ServiceList servicesList =await queryManager.GetServiceListAsync(appUri);
                foreach(Service service in servicesList)
                {
                    var serviceUri = service.ServiceName;
                    var partitionList = await queryManager.GetPartitionListAsync(serviceUri);
                    bool isStatefull = typeof(StatefulService).IsInstanceOfType(service);
                    primaryCount += isStatefull ? partitionList.Count : 0;
                    foreach(var partition in partitionList)
                    {
                        int cnt = (await fabricClient.QueryManager.GetReplicaListAsync(partition.PartitionInformation.Id)).Where(r => r.ReplicaStatus == ServiceReplicaStatus.Ready).Count();
                        if (isStatefull) replicaCount += cnt;
                        else instanceCount += cnt;
                    }
                }
                count = instanceCount + replicaCount;
            }
            
            return res;
            //var partitions = await fabricClient.QueryManager.GetPartitionListAsync(new Uri("fabric:/AppName/ServiceName"));
            //foreach (var partition in partitions)
            //{
            //    instances += (await fabricClient.QueryManager.GetReplicaListAsync(partition.PartitionInformation.Id)).Where(r => r.ReplicaStatus == ServiceReplicaStatus.Ready).Count();
            //}

            //return instances.ToString();
        }
    }
}
