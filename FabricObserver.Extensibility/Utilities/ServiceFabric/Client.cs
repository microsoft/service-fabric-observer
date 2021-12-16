using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Query;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Utilities.ServiceFabric
{
    public class Client
    {
        private readonly FabricClient _fabricClient;
        private readonly ServiceContext _serviceContext;
        private readonly ParallelOptions _parallelOptions;

        public Client(FabricClient fabricClient, ServiceContext serviceContext)
        {
            _fabricClient = fabricClient;
            _serviceContext = serviceContext;
            int maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));
            _parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount >= 4 ? maxDegreeOfParallelism : 1,
                TaskScheduler = TaskScheduler.Default
            };
        }

        /// <summary>
        /// Gets a list of all deployed applications on the current (local) node (the Fabric node on which this function is called).
        /// </summary>
        /// <param name="token">CancellationToken instance</param>
        /// <param name="appNameFilter">Optional ApplicatioName filter Uri</param>
        /// <returns>A List of DeployedApplication objects representing all deployed apps on the local node.</returns>
        public async Task<List<DeployedApplication>> GetAllLocalDeployedAppsAsync(CancellationToken token, Uri appNameFilter = null)
        {
            string nodeName = _serviceContext.NodeContext.NodeName;

            // Get info for 50 apps at a time that are deployed to the same node this FO instance is running on.
            var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(nodeName)
            {
                IncludeHealthState = false,
                MaxResults = 50,
                ApplicationNameFilter = appNameFilter
            };

            var appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => _fabricClient.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                                   deployedAppQueryDesc,
                                                                                   TimeSpan.FromSeconds(120),
                                                                                   token),
                                        token);

            // DeployedApplicationList is a wrapper around List, but does not support AddRange.. Thus, cast it ToList and add to the temp list, then iterate through it.
            // In reality, this list will never be greater than, say, 1000 apps deployed to a node, but it's a good idea to be prepared since AppObserver supports
            // all-app service process monitoring with a very simple configuration pattern.
            var apps = appList.ToList();

            // The GetDeployedApplicationPagedList api will set a continuation token value if it knows it did not return all the results in one swoop.
            // Check that it is not null, and make a new query passing back the token it gave you.
            while (appList.ContinuationToken != null)
            {
                token.ThrowIfCancellationRequested();

                deployedAppQueryDesc.ContinuationToken = appList.ContinuationToken;
                appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => _fabricClient.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                                   deployedAppQueryDesc,
                                                                                   TimeSpan.FromSeconds(120),
                                                                                   token),
                                        token);

                apps.AddRange(appList.ToList());

                // TODO: Add random wait (ms) impl, include cluster size in calc.
                await Task.Delay(250, token);
            }

            return apps;
        }

        /// <summary>
        /// Gets a list of all replicas (stateful and stateless) on the local node.
        /// </summary>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns>A List of ReplicaOrInstanceMonitoringInfo objects representing all replicas in any status (consumer should filter Status per need) on the local node.</returns>
        public async Task<List<ReplicaOrInstanceMonitoringInfo>> GetAllLocalReplicasOrInstances(CancellationToken token)
        {
            string nodeName = _serviceContext.NodeContext.NodeName;
            List<ReplicaOrInstanceMonitoringInfo> repList = new List<ReplicaOrInstanceMonitoringInfo>();
            List<DeployedApplication> appList = await GetAllLocalDeployedAppsAsync(token);

            foreach (DeployedApplication app in appList)
            {
                token.ThrowIfCancellationRequested();

                var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                   () => _fabricClient.QueryManager.GetDeployedReplicaListAsync(nodeName, app.ApplicationName),
                                                   token);

                repList.AddRange(GetInstanceOrReplicaMonitoringList(app.ApplicationName, deployedReplicaList, token));
            }

            return repList;
        }

        private List<ReplicaOrInstanceMonitoringInfo> GetInstanceOrReplicaMonitoringList(
                                                            Uri appName,
                                                            DeployedServiceReplicaList deployedReplicaList,
                                                            CancellationToken token)
        {
            ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList = new ConcurrentQueue<ReplicaOrInstanceMonitoringInfo>();
            _parallelOptions.CancellationToken = token;

            _ = Parallel.For(0, deployedReplicaList.Count, _parallelOptions, (i, state) =>
            {
                token.ThrowIfCancellationRequested();

                var deployedReplica = deployedReplicaList[i];
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica statefulReplica when statefulReplica.ReplicaRole == ReplicaRole.Primary || statefulReplica.ReplicaRole == ReplicaRole.ActiveSecondary:
                    {
                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            HostProcessId = statefulReplica.HostProcessId,
                            ServiceKind = statefulReplica.ServiceKind,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ServiceName = statefulReplica.ServiceName,
                            ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                            Status = statefulReplica.ReplicaStatus
                        };

                        /* In order to provide accurate resource usage of an SF service process we need to also account for
                        any processes (children) that the service process (parent) created/spawned. */

                        List<(string ProcName, int Pid)> childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId);

                        if (childPids != null && childPids.Count > 0)
                        {
                            replicaInfo.ChildProcesses = childPids;          
                        }

                        break;
                    }
                    case DeployedStatelessServiceInstance statelessInstance:
                    {
                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            HostProcessId = statelessInstance.HostProcessId,
                            ServiceKind = statelessInstance.ServiceKind,
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ServiceName = statelessInstance.ServiceName,
                            ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                            Status = statelessInstance.ReplicaStatus
                        };

                        List<(string ProcName, int Pid)> childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId);

                        if (childPids != null && childPids.Count > 0)
                        {
                            replicaInfo.ChildProcesses = childPids;
                        }

                        break;
                    }
                }

                if (replicaInfo != null)
                {
                    replicaMonitoringList.Enqueue(replicaInfo);
                }
            });

            return replicaMonitoringList.ToList();
        }

        /// <summary>
        /// Builds a List of tuple (string ServiceName, int Pid) from a List of ReplicaOrInstanceMonitoringInfo.
        /// </summary>
        /// <param name="repOrInsts">List of ReplicaOrInstanceMonitoringInfo</param>
        /// <returns>A List of tuple (string ServiceName, int Pid) representing all services supplied in the ReplicaOrInstanceMonitoringInfo instance.</returns>
        public List<(string ServiceName, int Pid)> GetServiceNamesAndPids(List<ReplicaOrInstanceMonitoringInfo> repOrInsts)
        {
            List<(string ServiceName, int Pid)> pids = new List<(string ServiceName, int Pid)>();

            foreach (var repOrInst in repOrInsts)
            {
                pids.Add((repOrInst.ServiceName.OriginalString, (int)repOrInst.HostProcessId));
            }

            return pids;
        }
    }
}
