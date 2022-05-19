using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Query;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Utilities.ServiceFabric
{
    /// <summary>
    /// Utility class that supplies useful Service Fabric client API wrapper functions.
    /// </summary>
    public class FabricClientUtilities
    {
        private readonly ParallelOptions parallelOptions;
        private readonly string nodeName;

        // This is the FC singleton that will be used for the lifetime of this FO instance.
        private static FabricClient fabricClient = null;
        private static readonly object lockObj = new object();
        private IntPtr HandleToSnapshot = IntPtr.Zero;
        private readonly bool isWindows;

        /// <summary>
        /// The singleton FabricClient instance that is used throughout FabricObserver.
        /// </summary>
        public static FabricClient FabricClientSingleton
        {
            get
            {
                if (fabricClient == null)
                {
                    lock (lockObj)
                    {
                        if (fabricClient == null)
                        {
                            fabricClient = new FabricClient();
                            fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
                            fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
                            return fabricClient;
                        }
                    }
                }
                else
                {
                    try
                    {
                        // This call with throw an ObjectDisposedException if fabricClient was disposed by, say, a plugin or if the runtime
                        // disposed of it for some random (unlikely..) reason. This is just a test to ensure it is not in a disposed state.
                        if (fabricClient.Settings.HealthReportSendInterval > TimeSpan.MinValue)
                        {
                            return fabricClient;
                        }
                    }
                    catch (Exception e) when (e is ObjectDisposedException || e is System.Runtime.InteropServices.InvalidComObjectException)
                    {
                        lock (lockObj)
                        {
                            fabricClient = null;
                            fabricClient = new FabricClient();
                            fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
                            fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
                            return fabricClient;
                        }
                    }
                }

                return fabricClient;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public FabricClientUtilities(string NodeName)
        {
            int maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));
            parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount >= 4 ? maxDegreeOfParallelism : 1,
                TaskScheduler = TaskScheduler.Default
            };

            nodeName = NodeName;
            isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public FabricClientUtilities(string NodeName, IntPtr handleToSnapshot)
        {
            int maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));
            parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount >= 4 ? maxDegreeOfParallelism : 1,
                TaskScheduler = TaskScheduler.Default
            };

            nodeName = NodeName;
            HandleToSnapshot = handleToSnapshot;
            isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        /// <summary>
        /// Gets a list of all deployed applications on the current (local) node (the Fabric node on which this function is called).
        /// </summary>
        /// <param name="token">CancellationToken instance</param>
        /// <param name="appNameFilter">Optional ApplicatioName filter Uri</param>
        /// <returns>A List of DeployedApplication objects representing all deployed apps on the local node.</returns>
        public async Task<List<DeployedApplication>> GetAllLocalDeployedAppsAsync(CancellationToken token, Uri appNameFilter = null)
        {
            // Get info for 50 apps at a time that are deployed to the same node this FO instance is running on.
            var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(nodeName)
            {
                IncludeHealthState = false,
                MaxResults = 50,
                ApplicationNameFilter = appNameFilter
            };

            var appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricClientSingleton.QueryManager.GetDeployedApplicationPagedListAsync(
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
                                        () => FabricClientSingleton.QueryManager.GetDeployedApplicationPagedListAsync(
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
            List<ReplicaOrInstanceMonitoringInfo> repList = new List<ReplicaOrInstanceMonitoringInfo>();
            List<DeployedApplication> appList = await GetAllLocalDeployedAppsAsync(token);

            foreach (DeployedApplication app in appList)
            {
                token.ThrowIfCancellationRequested();

                var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                   () => FabricClientSingleton.QueryManager.GetDeployedReplicaListAsync(nodeName, app.ApplicationName),
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
            parallelOptions.CancellationToken = token;
            bool createdSnapshotHandle = false;

            // This means the ctor that doesn't take an IntPtr arg was used to construct this instance or a caller supplied IntPtr.Zero
            // for the arg in the ctor that does takes an IntPtr..
            if (isWindows && HandleToSnapshot == IntPtr.Zero)
            {
                HandleToSnapshot = NativeMethods.CreateToolhelp32Snapshot((uint)NativeMethods.CreateToolhelp32SnapshotFlags.TH32CS_SNAPPROCESS, 0);
                createdSnapshotHandle = true;
            }

            _ = Parallel.For(0, deployedReplicaList.Count, parallelOptions, (i, state) =>
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
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ReplicaRole = statefulReplica.ReplicaRole,
                            ServiceKind = statefulReplica.ServiceKind,
                            ServiceName = statefulReplica.ServiceName,
                            ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                            ReplicaStatus = statefulReplica.ReplicaStatus
                        };

                        /* In order to provide accurate resource usage of an SF service process we need to also account for
                        any processes (children) that the service process (parent) created/spawned. */

                        List<(string ProcName, int Pid)> childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId, HandleToSnapshot);

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
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ReplicaRole = ReplicaRole.None,
                            ServiceKind = statelessInstance.ServiceKind,
                            ServiceName = statelessInstance.ServiceName,
                            ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                            ReplicaStatus = statelessInstance.ReplicaStatus
                        };

                        List<(string ProcName, int Pid)> childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId, HandleToSnapshot);

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

            if (createdSnapshotHandle)
            {
                NativeMethods.ReleaseHandle(HandleToSnapshot);
            }

            return replicaMonitoringList.ToList();
        }

        /// <summary>
        /// Builds a List of tuple (string ServiceName, string ProcName, int Pid) from a List of ReplicaOrInstanceMonitoringInfo.
        /// </summary>
        /// <param name="repOrInsts">List of ReplicaOrInstanceMonitoringInfo</param>
        /// <returns>A List of tuple (string ServiceName, string ProcName, int Pid) representing all services supplied in the ReplicaOrInstanceMonitoringInfo instance.</returns>
        public List<(string ServiceName, string ProcName, int Pid)> GetServiceProcessInfo(List<ReplicaOrInstanceMonitoringInfo> repOrInsts)
        {
            List<(string ServiceName, string ProcName, int Pid)> pids = new List<(string ServiceName, string ProcName, int Pid)>();

            foreach (var repOrInst in repOrInsts)
            {
                try
                {
                    using (var proc = Process.GetProcessById((int)repOrInst.HostProcessId))
                    {
                        pids.Add((repOrInst.ServiceName.OriginalString, proc.ProcessName, (int)repOrInst.HostProcessId));
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                {
                    // process with supplied pid may not be running..
                    continue;
                }
            }

            return pids;
        }
    }
}
