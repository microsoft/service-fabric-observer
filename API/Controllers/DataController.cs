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
            List<HardwareData> targetList=originList.ConvertAll<HardwareData>(data=>(HardwareData)ByteSerialization.ByteArrayToObject(data));
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

            List<byte[]> originList = await AggregatorProxy.GetDataRemote(SFData.queueName);
            List<SFData> targetList = originList.ConvertAll<SFData>(data => (SFData)ByteSerialization.ByteArrayToObject(data));
            foreach (var data in targetList)
            {
                response += data.ToString();
            }
            return response;
        }

        [HttpGet]
        [Route("Snapshot")]
        public async Task<string> Snapshot()
        {
            string response = "";

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );

            //CalculateAverageCapacity
            return response;
        }


        [HttpGet]
        [Route("info")]
        public async Task<string> Info([FromQuery] string NodeName)
        {
            return "A";
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
