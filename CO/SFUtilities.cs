using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.Linq;
using System.Threading.Tasks;
using static System.Fabric.FabricClient;

namespace ClusterCollector
{
    public class SFUtilities
    {
        private static SFUtilities instance;
        private static readonly object lockObj = new object();
        private FabricClient fabricClient;
        private QueryClient queryManager;

        protected SFUtilities(){
            fabricClient = new FabricClient();
            queryManager = fabricClient.QueryManager;
        }

        public static SFUtilities Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (lockObj)
                    {
                        if (instance == null)
                        {
                            instance = new SFUtilities();
                        }
                    }
                }

                return instance;
            }
        }

        /// <summary>
        /// Return total primaryCount, replicaCount, instanceCount, count.
        /// </summary>
        public async Task<(int primaryCount, int replicaCount, int instanceCount, int count)> TupleGetDeployedCountsAsync()
        {
            ApplicationList appList = await queryManager.GetApplicationListAsync();
            Uri appUri;
            int primaryCount = 0;
            int replicaCount = 0; //Primary + secondary for statefull services
            int instanceCount = 0; //Replica count for stateless services
            int count = 0; //replicaCount + instanceCount
            foreach (Application app in appList)
            {
                //appUri= app.ApplicationName.AbsoluteUri;
                appUri = app.ApplicationName;
                ServiceList servicesList = await queryManager.GetServiceListAsync(appUri);
                foreach (Service service in servicesList)
                {
                    var serviceUri = service.ServiceName;
                    var partitionList = await queryManager.GetPartitionListAsync(serviceUri);
                    bool isStatefull = typeof(StatefulService).IsInstanceOfType(service);
                    primaryCount += isStatefull ? partitionList.Count : 0;
                    foreach (var partition in partitionList)
                    {
                        int cnt = (await fabricClient.QueryManager.GetReplicaListAsync(partition.PartitionInformation.Id)).Where(r => r.ReplicaStatus == ServiceReplicaStatus.Ready).Count();
                        if (isStatefull) replicaCount += cnt;
                        else instanceCount += cnt;
                    }
                }
                
            }
            count = instanceCount + replicaCount;
            return (primaryCount, replicaCount, instanceCount, count);
        }
        
        public async Task<NodeList> GetNodeListAsync()
        {
            return await queryManager.GetNodeListAsync();
            
        }

    }
}
