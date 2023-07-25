// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Management.ServiceModel;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

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
        private static readonly object lockObj = new();
        private readonly bool isWindows;
        private readonly Logger logger;
        private static readonly XmlSerializer applicationManifestSerializer = new (typeof(ApplicationManifestType));
        private static readonly XmlSerializer serviceManifestSerializer = new (typeof(ServiceManifestType));

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
                        // disposed of it for some reason (FO replica restart, for example). This is just a test to ensure it is not in a disposed state.
                        if (fabricClient.Settings.HealthReportSendInterval > TimeSpan.MinValue)
                        {
                            return fabricClient;
                        }
                    }
                    catch (FabricObjectClosedException)
                    {
                        lock (lockObj)
                        {
                            fabricClient = null;
                            fabricClient.Dispose();
                            fabricClient = new FabricClient();
                            fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
                            fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
                            return fabricClient;
                        }
                    }
                    catch (Exception e) when (e is ObjectDisposedException or InvalidComObjectException)
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
        /// <param name="NodeName">Name of the Fabric node FabricObserver instance is running on.</param>
        public FabricClientUtilities(string nodeName = null)
        {
            logger = new Logger("FabClientUtil");
            int maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));
            parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount >= 4 ? maxDegreeOfParallelism : 1,
                TaskScheduler = TaskScheduler.Default
            };

            if (string.IsNullOrWhiteSpace(nodeName))
            {
                this.nodeName = FabricRuntime.GetNodeContext().NodeName;
            }
            else
            {
                this.nodeName = nodeName;
            }

            isWindows = OperatingSystem.IsWindows();
        }

        /// <summary>
        /// Gets a list of all deployed applications on the current (local) node (the Fabric node on which this function is called).
        /// </summary>
        /// <param name="token">CancellationToken instance</param>
        /// <param name="nodeName">Optional Fabric node name. By default, the local node where this code is running is the target.</param>
        /// <param name="appNameFilter">Optional ApplicatioName filter Uri</param>
        /// <returns>A List of DeployedApplication objects representing all deployed apps on the local node.</returns>
        public async Task<List<DeployedApplication>> GetAllDeployedAppsAsync(CancellationToken token, string nodeName = null, Uri appNameFilter = null)
        {
            // Get info for 50 apps at a time that are deployed to the same node this FO instance is running on.
            var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(!string.IsNullOrWhiteSpace(nodeName) ? nodeName : this.nodeName)
            {
                IncludeHealthState = false,
                MaxResults = 50,
                ApplicationNameFilter = appNameFilter
            };

            var appList = await FabricClientSingleton.QueryManager.GetDeployedApplicationPagedListAsync(deployedAppQueryDesc, TimeSpan.FromSeconds(120), token);

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
                appList = await FabricClientSingleton.QueryManager.GetDeployedApplicationPagedListAsync(deployedAppQueryDesc, TimeSpan.FromSeconds(120), token);

                apps.AddRange(appList.ToList());
                await Task.Delay(250, token);
            }

            return apps;
        }

        /// <summary>
        /// Gets a list of all replicas (stateful and stateless) on the local node.
        /// </summary>
        /// <param name="includeChildProcesses">Whether or not to include the desendant processes of the service replica's host process.</param>
        /// <param name="nodeName">Optional. If specified, the Fabric node to get deployed replicas. By default, the local node where this code is running is the target.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns>A List of ReplicaOrInstanceMonitoringInfo objects representing all replicas in any status (consumer should filter Status per need) on the local (or specified) node.</returns>
        public async Task<List<ReplicaOrInstanceMonitoringInfo>> GetAllDeployedReplicasOrInstancesAsync(bool includeChildProcesses, CancellationToken token, string nodeName = null)
        {
            List<ReplicaOrInstanceMonitoringInfo> repList = new();
            List<DeployedApplication> appList = await GetAllDeployedAppsAsync(token);

            if (isWindows && !NativeMethods.RefreshSFUserProcessDataCache(getChildProcesses: includeChildProcesses))
            {
                string message = "Can't observe child processes. Failure getting process ids on the system.";
                logger.LogWarning(message);
                logger.LogEtw(
                    ObserverConstants.FabricObserverETWEventName,
                    new
                    {
                        Level = "Warning",
                        Message = message,
                        Source = "FabricClientUtilities::GetAllDeployedReplicasOrInstances"
                    });
            }
            
            try
            {
                foreach (DeployedApplication app in appList)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();

                        var deployedReplicaList = 
                            await FabricClientSingleton.QueryManager.GetDeployedReplicaListAsync(nodeName ?? this.nodeName, app.ApplicationName, null, null, TimeSpan.FromSeconds(60), token);

                        if (deployedReplicaList == null || !deployedReplicaList.Any())
                        {
                            // Application has no deployed replicas.
                            continue;
                        }

                        List<DeployedServiceReplica> deployedReplicas;

                        try
                        {
                            deployedReplicas = deployedReplicaList.DistinctBy(x => x.HostProcessId).ToList();
                        }
                        catch (Exception e) when (e is ArgumentException)
                        {
                            continue;
                        }

                        var repOrInstances = 
                            GetInstanceOrReplicaMonitoringList(
                                    app.ApplicationName,
                                    app.ApplicationTypeName ?? appList.First(a => a.ApplicationName == app.ApplicationName).ApplicationTypeName,
                                    deployedReplicas,
                                    includeChildProcesses,
                                    null,
                                    token);

                        repList.AddRange(repOrInstances);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {

                    }
                }
            }
            finally
            {
                if (isWindows && includeChildProcesses)
                {
                    NativeMethods.ClearSFUserProcessDataCache();
                }
            }

            return repList;
        }

        /// <summary>
        /// Returns a list of ReplicaOrInstanceMonitoringInfo objects that will contain child process information if handleToSnapshot is provided.
        /// </summary>
        /// <param name="appName">Name of the target application.</param>
        /// <param name="applicationTypeName">Type name of the target application</param>
        /// <param name="deployedReplicaList">List of deployed replicas.</param>
        /// <param name="handleToSnapshot">Handle to process snapshot.</param>
        /// <param name="token">Cancellation Token</param>
        /// <returns></returns>
        private List<ReplicaOrInstanceMonitoringInfo> GetInstanceOrReplicaMonitoringList(
                                                        Uri appName,
                                                        string applicationTypeName,
                                                        List<DeployedServiceReplica> deployedReplicaList,
                                                        bool includeChildProcesses,
                                                        NativeMethods.SafeObjectHandle handleToSnapshot,
                                                        CancellationToken token)
        {
            ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList = new();
            parallelOptions.CancellationToken = token;

            _ = Parallel.For(0, deployedReplicaList.Count, parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                }

                var deployedReplica = deployedReplicaList[i];
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica statefulReplica when statefulReplica.ReplicaRole is ReplicaRole.Primary or ReplicaRole.ActiveSecondary:
                    {
                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = applicationTypeName,
                            HostProcessId = statefulReplica.HostProcessId,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ReplicaRole = statefulReplica.ReplicaRole,
                            ServiceKind = statefulReplica.ServiceKind,
                            ServiceName = statefulReplica.ServiceName,
                            ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                            ServicePackageActivationMode = string.IsNullOrWhiteSpace(statefulReplica.ServicePackageActivationId) ?
                                ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                            ReplicaStatus = statefulReplica.ReplicaStatus
                        };

                        /* In order to provide accurate resource usage of an SF service process we need to also account for
                        any processes (children) that the service process (parent) created/spawned. */
                        if (includeChildProcesses)
                        {
                            List<(string ProcName, int Pid, DateTime ProcessStartTime)> childPids = null;
                            childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId, handleToSnapshot);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                            }
                        }

                        break;
                    }
                    case DeployedStatelessServiceInstance statelessInstance:
                    {
                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = applicationTypeName,
                            HostProcessId = statelessInstance.HostProcessId,
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ReplicaRole = ReplicaRole.None,
                            ServiceKind = statelessInstance.ServiceKind,
                            ServiceName = statelessInstance.ServiceName,
                            ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                            ServicePackageActivationMode = string.IsNullOrWhiteSpace(statelessInstance.ServicePackageActivationId) ?
                                ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                            ReplicaStatus = statelessInstance.ReplicaStatus
                        };

                        if (includeChildProcesses)
                        {
                            List<(string ProcName, int Pid, DateTime ProcessStartTime)> childPids = null;
                            childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId, handleToSnapshot);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                            }
                        }

                        break;
                    }
                }

                if (replicaInfo?.HostProcessId > 0 && !replicaMonitoringList.Any(r => r.HostProcessId == replicaInfo.HostProcessId))
                {
                    if (isWindows)
                    {
                        replicaInfo.HostProcessName = NativeMethods.GetProcessNameFromId((int)replicaInfo.HostProcessId);
                    }
                    else
                    {
                        try
                        {
                            using (Process p = Process.GetProcessById((int)replicaInfo.HostProcessId))
                            {
                                replicaInfo.HostProcessName = p.ProcessName;
                            }
                        }
                        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
                        {

                        }
                    }

                    replicaMonitoringList.Enqueue(replicaInfo);
                }
            });

            return replicaMonitoringList.ToList();
        }

        /// <summary>
        /// Builds a List of tuple (string ServiceName, string ProcName, int Pid) from a List of ReplicaOrInstanceMonitoringInfo.
        /// </summary>
        /// <param name="repOrInsts">List of ReplicaOrInstanceMonitoringInfo</param>
        /// <returns>A List of tuple (string ServiceName, string ProcName, int Pid) representing all services supplied in the ReplicaOrInstanceMonitoringInfo instance, including child processes of each service, if any.</returns>
        public List<(string ServiceName, string ProcName, int Pid, DateTime ProcessStartTime)> GetServiceProcessInfo(List<ReplicaOrInstanceMonitoringInfo> repOrInsts)
        {
            List<(string ServiceName, string ProcName, int Pid, DateTime ProcessStartTime)> pids = new();

            foreach (var repOrInst in repOrInsts)
            {
                try
                {
                    if (isWindows)
                    {
                        string procName = NativeMethods.GetProcessNameFromId((int)repOrInst.HostProcessId);
                        pids.Add((repOrInst.ServiceName.OriginalString, procName, (int)repOrInst.HostProcessId, NativeMethods.GetProcessStartTime((int)repOrInst.HostProcessId)));
                    }
                    else
                    {
                        using (var proc = Process.GetProcessById((int)repOrInst.HostProcessId))
                        {
                            pids.Add((repOrInst.ServiceName.OriginalString, proc.ProcessName, (int)repOrInst.HostProcessId, proc.StartTime));
                        }
                    }

                    // Child processes?
                    if (repOrInst.ChildProcesses != null && repOrInst.ChildProcesses.Count > 0)
                    {
                        foreach (var (procName, Pid, processStartTime) in repOrInst.ChildProcesses)
                        {
                            pids.Add((repOrInst.ServiceName.OriginalString, procName, Pid, processStartTime));
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // process with supplied pid may not be running..
                    continue;
                }
            }

            return pids;
        }

        /// <summary>
        /// Provides ApplicationType and ApplicationType version for specifed Application name.
        /// </summary>
        /// <param name="appName">Application name.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>Tuple: (string AppType, string AppTypeVersion)</returns>
        public static async Task<(string AppType, string AppTypeVersion)> TupleGetApplicationTypeInfo(Uri appName, CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return (null, null);
                }

                var appList =
                    await FabricClientSingleton.QueryManager.GetApplicationListAsync(appName, TimeSpan.FromSeconds(90), cancellationToken);

                if (appList?.Count > 0)
                {
                    string appType = appList[0].ApplicationTypeName;
                    string appTypeVersion = appList[0].ApplicationTypeVersion;

                    return (appType, appTypeVersion);
                }
            }
            catch (Exception e) when (e is FabricException or TaskCanceledException or OperationCanceledException)
            {

            }

            return (null, null);
        }

        /// <summary>
        /// Provides ServiceType name and Service Manifest version given an Application name and Service name.
        /// </summary>
        /// <param name="appName">Application name.</param>
        /// <param name="serviceName">Service name.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>Tuple: (string ServiceType, string ServiceManifestVersion, ServiceMetadata ServiceMetaData)</returns>
        public static async Task<(string ServiceType, string ServiceManifestVersion, ServiceMetadata ServiceMetaData)> TupleGetServiceTypeInfoAsync(Uri appName, Uri serviceName, CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return (null, null, null);
                }

                var serviceList =
                    await FabricClientSingleton.QueryManager.GetServiceListAsync(appName, serviceName, TimeSpan.FromSeconds(90), cancellationToken);

                if (serviceList?.Count > 0)
                {
                    string serviceType = serviceList[0].ServiceTypeName;
                    string serviceManifestVersion = serviceList[0].ServiceManifestVersion;
                    ServiceMetadata serviceMetadata = serviceList[0].ServiceMetadata;

                    return (serviceType, serviceManifestVersion, serviceMetadata);
                }
            }
            catch (Exception e) when (e is FabricException or TaskCanceledException or OperationCanceledException)
            {

            }

            return (null, null, null);
		}

        /// <summary>
        /// Processes values for application parameters, returning the specified value in use for application parameter variables.
        /// If the appParamValue is not a variable name or the supplied ApplicationParameterList is null, then the function just returns the supplied appParamValue.
        /// </summary>
        /// <param name="appParamValue">The value of an Application parameter.</param>
        /// <param name="parameters">ApplicationParameterList instance that contains all app parameter values.</param>
        /// <returns>String representation of the actual parameter value in the case where the supplied value is a variable name, 
        /// else the value specified in appParamValue.</returns>
        public static string ParseAppParameterValue(string appParamValue, ApplicationParameterList parameters)
        {
            if (parameters == null || string.IsNullOrWhiteSpace(appParamValue))
            {
                return appParamValue;
            }

            // Application parameter value specified as a Service Fabric Application Manifest variable.
            if (appParamValue.StartsWith("["))
            {
                appParamValue = appParamValue.Replace("[", string.Empty).Replace("]", string.Empty);

                if (parameters.TryGetValue(appParamValue, out ApplicationParameter parameter))
                {
                    return parameter.Value;
                }
            }
            
            return appParamValue;
        }

        /// <summary>
        /// Populates one ApplicationParameterList with missing parameters from another ApplicationParameterList.
        /// </summary>
        /// <param name="toParameters">ApplicationParameterList to be populated</param>
        /// <param name="fromParameters">ApplicationParameterList to be used</param>
        public static void AddParametersIfNotExists(ApplicationParameterList toParameters, ApplicationParameterList fromParameters)
        {
            // If toParameters is passed in as null, then make it a new instance.
            toParameters ??= new ApplicationParameterList();

            if (fromParameters != null)
            {
                foreach (var parameter in fromParameters)
                {
                    try
                    {
                        if (!toParameters.Contains(parameter.Name))
                        {
                            toParameters.Add(new ApplicationParameter() { Name = parameter.Name, Value = parameter.Value });
                        }
                    }
                    catch (ArgumentException)
                    {

                    }
                }
            }
        }

        /// <summary>
        /// Windows-only. Gets RG Memory limit information for a code package.
        /// </summary>
        /// <param name="appManifestXml">Application Manifest</param>
        /// <param name="servicePkgName">Service Package name</param>
        /// <param name="codepackageName">Code Package name</param>
        /// <param name="parameters">Application Parameter List, populated with both application and default parameters</param>
        /// <returns>A Tuple containing a boolean value (whether or not RG memory is enabled) and a double value (the absolute limit in megabytes)</returns>
        public (bool IsMemoryRGEnabled, double MemoryLimitMb) TupleGetMemoryResourceGovernanceInfo(string appManifestXml, string servicePkgName, string codepackageName, ApplicationParameterList parameters)
        {
            logger.LogInfo("Starting TupleGetMemoryResourceGovernanceInfo.");

            if (!isWindows)
            {
                logger.LogInfo("Completing TupleGetMemoryResourceGovernanceInfo: OS not yet supported.");
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(appManifestXml))
            {
                logger.LogInfo($"Invalid value for {nameof(appManifestXml)}: {appManifestXml}. Exiting TupleGetMemoryResourceGovernanceInfo.");
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(servicePkgName))
            {
                logger.LogInfo($"Invalid value for {nameof(servicePkgName)}: {servicePkgName}. Exiting TupleGetMemoryResourceGovernanceInfo.");
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(codepackageName))
            {
                logger.LogInfo($"Invalid value for {nameof(codepackageName)}: {codepackageName}. Exiting TupleGetMemoryResourceGovernanceInfo.");
                return (false, 0);
            }

            // Don't waste cycles with XML parsing if you can easily get a hint first..
            if (!appManifestXml.Contains($"<{ObserverConstants.RGPolicyNodeName} "))
            {
                return (false, 0);
            }

            // Parse XML to find the necessary policy
            
            ApplicationManifestType applicationManifest = null;

            using (var sreader = new StringReader(appManifestXml))
            {
                applicationManifest = (ApplicationManifestType)applicationManifestSerializer.Deserialize(sreader);
            }

            foreach (var import in applicationManifest.ServiceManifestImport)
            {
                if (import.ServiceManifestRef.ServiceManifestName == servicePkgName)
                {
                    if (import.Policies != null)
                    {
                        for (int policyIndex = 0; policyIndex < import.Policies.Length; policyIndex++)
                        {
                            var policy = import.Policies[policyIndex];

                            if (policy is ResourceGovernancePolicyType resourceGovernancePolicy)
                            {
                                resourceGovernancePolicy.MemoryInMBLimit = ParseAppParameterValue(resourceGovernancePolicy.MemoryInMBLimit, parameters);
                                resourceGovernancePolicy.MemoryInMB = ParseAppParameterValue(resourceGovernancePolicy.MemoryInMB, parameters);
                                
                                if (resourceGovernancePolicy.CodePackageRef == codepackageName)
                                {
                                    double RGMemoryLimitMb = 0;

                                    if (double.TryParse(resourceGovernancePolicy.MemoryInMBLimit, out double memInMbLimit) && memInMbLimit > 0)
                                    {
                                        RGMemoryLimitMb = memInMbLimit;
                                    }
                                    else if (double.TryParse(resourceGovernancePolicy.MemoryInMB, out double memInMb) && memInMb > 0)
                                    {
                                        RGMemoryLimitMb = memInMb;
                                    }

                                    return (RGMemoryLimitMb > 0, RGMemoryLimitMb);
                                }
                            }
                        }
                    }
                }
            }

            return (false, 0);
        }

        /// <summary>
        /// Windows-only. Gets RG Cpu limit information for a code package.
        /// </summary>
        /// <param name="appManifestXml">Application Manifest</param>
        /// <param name="servicePkgName">Service Package name</param>
        /// <param name="codepackageName">Code Package name</param>
        /// <param name="parameters">Application Parameter List, populated with both application and default parameters</param>
        /// <returns>A Tuple containing a boolean value (whether or not RG cpu is enabled) and a double value (the absolute limit in cores)</returns>
        public (bool IsCpuRGEnabled, double CpuLimitCores) TupleGetCpuResourceGovernanceInfo(string appManifestXml, string svcManifestXml, string servicePkgName, string codepackageName, ApplicationParameterList parameters)
        {
            logger.LogInfo("Starting TupleGetCpuResourceGovernanceInfo.");

            if (!isWindows)
            {
                logger.LogInfo("Completing TupleGetCpuResourceGovernanceInfo: OS not yet supported.");
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(appManifestXml))
            {
                logger.LogInfo($"Invalid value for {nameof(appManifestXml)}: {appManifestXml}. Exiting TupleGetCpuResourceGovernanceInfo.");
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(svcManifestXml))
            {
                logger.LogInfo($"Invalid value for {nameof(svcManifestXml)}: {svcManifestXml}. Exiting TupleGetCpuResourceGovernanceInfo.");
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(servicePkgName))
            {
                logger.LogInfo($"Invalid value for {nameof(servicePkgName)}: {servicePkgName}. Exiting TupleGetCpuResourceGovernanceInfo.");
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(codepackageName))
            {
                logger.LogInfo($"Invalid value for {nameof(codepackageName)}: {codepackageName}. Exiting TupleGetCpuResourceGovernanceInfo.");
                return (false, 0);
            }

            // TOTHINK: Shouldn't this also contain a check for "ServicePackageResourceGovernancePolicy" node?
            // Don't waste cycles with XML parsing if you can easily get a hint first..
            if (!appManifestXml.Contains($"<{ObserverConstants.RGPolicyNodeName} ") && !appManifestXml.Contains($"<{ObserverConstants.RGSvcPkgPolicyNodeName} "))
            {
                return (false, 0);
            }


            // Parse XML to find the necessary policies

            ApplicationManifestType applicationManifest = null;

            using (var sreader = new StringReader(appManifestXml))
            {
                applicationManifest = (ApplicationManifestType)applicationManifestSerializer.Deserialize(sreader);
            }

            // We need the service manifest to get the code package count

            ServiceManifestType serviceManifest = null;

            using (var sreader = new StringReader(svcManifestXml))
            {
                serviceManifest = (ServiceManifestType)serviceManifestSerializer.Deserialize(sreader);
            }
                
            foreach (var import in applicationManifest.ServiceManifestImport)
            {
                if (import.ServiceManifestRef.ServiceManifestName == servicePkgName)
                {
                    if (import.Policies != null)
                    {
                        double TotalCpuCores = 0;
                        double CpuShares = 0;
                        double CpuSharesSum = 0;
                        int RGPoliciesCount = 0;

                        for (int policyIndex = 0; policyIndex < import.Policies.Length; policyIndex++)
                        {
                            var policy = import.Policies[policyIndex];

                            if (policy is ResourceGovernancePolicyType resourceGovernancePolicy)
                            {
                                RGPoliciesCount++;
                                resourceGovernancePolicy.CpuShares = ParseAppParameterValue(resourceGovernancePolicy.CpuShares, parameters);

                                if (string.IsNullOrWhiteSpace(resourceGovernancePolicy.CpuShares))
                                {
                                    return (false, 0);
                                }

                                if (!double.TryParse(resourceGovernancePolicy.CpuShares, out double cpuShares))
                                {
                                    return (false, 0);
                                }

                                if (cpuShares == 0)
                                {
                                    CpuSharesSum += 1;

                                    if (resourceGovernancePolicy.CodePackageRef == codepackageName)
                                    {
                                        CpuShares = 1;
                                    }
                                }
                                else
                                {
                                    CpuSharesSum += cpuShares;

                                    if (resourceGovernancePolicy.CodePackageRef == codepackageName)
                                    {
                                        CpuShares = cpuShares;
                                    }
                                }
                            }
                            else if (policy is ServicePackageResourceGovernancePolicyType servicePackagePolicy)
                            {
                                servicePackagePolicy.CpuCoresLimit = ParseAppParameterValue(servicePackagePolicy.CpuCoresLimit, parameters);
                                servicePackagePolicy.CpuCores = ParseAppParameterValue(servicePackagePolicy.CpuCores, parameters);
                                
                                if (string.IsNullOrWhiteSpace(servicePackagePolicy.CpuCores) && string.IsNullOrWhiteSpace(servicePackagePolicy.CpuCoresLimit))
                                {
                                    return (false, 0);
                                }

                                if (double.TryParse(servicePackagePolicy.CpuCoresLimit, out double cpuLimit) && cpuLimit > 0)
                                {
                                    TotalCpuCores = cpuLimit;
                                }
                                else if (double.TryParse(servicePackagePolicy.CpuCores, out double cpuCores) && cpuCores > 0)
                                {
                                    TotalCpuCores = cpuCores;
                                }
                            }
                        }

                        if (TotalCpuCores == 0)
                        {
                            return (false, 0);
                        }
                        else
                        {
                            // If we didn't find the RG policy for the required CodePackage, we assign it the default.
                            if (CpuShares == 0)
                            {
                                CpuShares = 1;
                            }

                            // We need to count all CodePackages which don't have an RG policy defined. 
                            CpuSharesSum += serviceManifest.CodePackage.Length - RGPoliciesCount; 

                            double CpuLimitCores = TotalCpuCores * CpuShares / CpuSharesSum;
                            return (true, CpuLimitCores);
                        }
                    }
                }
            }

            return (false, 0);
        }

        public async Task ClearFabricObserverHealthReportsAsync(bool ignoreDefaultQueryTimeout, CancellationToken cancellationToken)
        {
            try
            {
                var clusterQueryDesc = new ClusterHealthQueryDescription
                {
                    EventsFilter = new HealthEventsFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    ApplicationsFilter = new ApplicationHealthStatesFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    NodesFilter = new NodeHealthStatesFilter
                    {
                        HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                    },
                    HealthPolicy = new ClusterHealthPolicy(),
                    HealthStatisticsFilter = new ClusterHealthStatisticsFilter
                    {
                        ExcludeHealthStatistics = false,
                        IncludeSystemApplicationHealthStatistics = true
                    }
                };

                ClusterHealth clusterHealth =
                        await FabricClientSingleton.HealthManager.GetClusterHealthAsync(
                                clusterQueryDesc,
                                TimeSpan.FromSeconds(90),
                                cancellationToken);

                // Cluster is healthy. Nothing to do here.
                if (clusterHealth.AggregatedHealthState == HealthState.Ok)
                {
                    return;
                }

                // Process node health.
                if (clusterHealth.NodeHealthStates != null && clusterHealth.NodeHealthStates.Count > 0)
                {
                    try
                    {
                        await RemoveNodeHealthReportsAsync(clusterHealth.NodeHealthStates, ignoreDefaultQueryTimeout, cancellationToken);
                    }
                    catch (Exception e) when (e is FabricException or TimeoutException)
                    {
#if DEBUG
                        logger.LogInfo($"Handled Exception in ReportClusterHealthAsync::Node: {e.Message}");
#endif
                    }
                }

                // Process Application/Service health.
                if (clusterHealth.ApplicationHealthStates != null && clusterHealth.ApplicationHealthStates.Count > 0)
                {
                    foreach (var app in clusterHealth.ApplicationHealthStates)
                    {
                        try
                        {
                            if (app.ApplicationName.OriginalString == ObserverConstants.SystemAppName)
                            {
                                await RemoveApplicationHealthReportsAsync(app, ignoreDefaultQueryTimeout, cancellationToken);
                            }

                            var appHealth =
                                await FabricClientSingleton.HealthManager.GetApplicationHealthAsync(
                                        app.ApplicationName,
                                        TimeSpan.FromSeconds(90),
                                        cancellationToken);


                            if (appHealth.ServiceHealthStates != null && 
                                appHealth.ServiceHealthStates.Any(
                                    s => s.AggregatedHealthState == HealthState.Error || s.AggregatedHealthState == HealthState.Warning))
                            {
                                foreach (var service in appHealth.ServiceHealthStates)
                                {
                                    if (service.AggregatedHealthState == HealthState.Ok)
                                    {
                                        continue;
                                    }

                                    await RemoveServiceHealthReportsAsync(service, ignoreDefaultQueryTimeout, cancellationToken);
                                }
                            }
                            
                            // NetworkObserver/FSO.
                            await RemoveApplicationHealthReportsAsync(app, ignoreDefaultQueryTimeout, cancellationToken);
                            
                        }
                        catch (Exception e) when (e is FabricException or TimeoutException)
                        {
#if DEBUG
                            logger.LogInfo($"Handled Exception in ReportClusterHealthAsync::Application: {e.Message}");
#endif
                        }
                    }
                }
            }
            catch (Exception e) when (e is FabricException or TimeoutException)
            {
                string msg = $"Handled transient exception in ClearFabricObserverHealthReportsAsync: {e.Message}";

                // Log it locally.
                logger.LogWarning(msg);
            }
            catch (Exception e) when (e is not (OperationCanceledException or TaskCanceledException))
            {
                string msg = $"Unhandled exception in ClearFabricObserverHealthReportsAsync:{Environment.NewLine}{e}";

                // Log it locally.
                logger.LogError(msg);

                // Fix the bug.
                throw;
            }
        }

        private async Task RemoveServiceHealthReportsAsync(ServiceHealthState service, bool ignoreDefaultQueryTimeout, CancellationToken cancellationToken)
        {
            ServiceHealthQueryDescription serviceHealthQueryDescription = new (service.ServiceName)
            {
                EventsFilter = new HealthEventsFilter
                {
                    HealthStateFilterValue = HealthStateFilter.Error | HealthStateFilter.Warning
                },
                HealthPolicy = new ApplicationHealthPolicy(),
                HealthStatisticsFilter = new ServiceHealthStatisticsFilter
                {
                    ExcludeHealthStatistics = false
                }
            };
            var serviceHealth = await FabricClientSingleton.HealthManager.GetServiceHealthAsync(
                                        serviceHealthQueryDescription,
                                        ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(90),
                                        cancellationToken);

            if (serviceHealth == null || serviceHealth.AggregatedHealthState == HealthState.Ok)
            {
                return;
            }

            var serviceHealthEvents =
                serviceHealth.HealthEvents.Where(
                    e => 
                        JsonHelper.TryDeserializeObject(e.HealthInformation.Description, out TelemetryDataBase telemetryDataBase)
                        && telemetryDataBase.NodeName == this.nodeName
                        && (e.HealthInformation.SourceId.StartsWith(ObserverConstants.AppObserverName)
                            || e.HealthInformation.SourceId.StartsWith(ObserverConstants.ContainerObserverName))).ToList();

            if (!serviceHealthEvents.Any())
            {
                return;
            }

            var healthReport = new HealthReport
            {
                Code = FOErrorWarningCodes.Ok,
                HealthMessage = $"Clearing existing FabricObserver Health Reports as the service is stopping or starting.",
                State = HealthState.Ok,
                NodeName = nodeName,
                ServiceName = service.ServiceName,
                EntityType = EntityType.Service
            };
            ObserverHealthReporter healthReporter = new(logger);

            foreach (HealthEvent healthEvent in serviceHealthEvents)
            {
                try
                {
                    healthReport.Property = healthEvent.HealthInformation.Property;
                    healthReport.SourceId = healthEvent.HealthInformation.SourceId;
                    healthReporter.ReportHealthToServiceFabric(healthReport);
                }
                catch (FabricException)
                {

                }
            }
        }

        private async Task RemoveApplicationHealthReportsAsync(ApplicationHealthState app, bool ignoreDefaultQueryTimeout, CancellationToken cancellationToken)
        {
            var appHealth = await FabricClientSingleton.HealthManager.GetApplicationHealthAsync(
                                    app.ApplicationName,
                                    ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(90),
                                    cancellationToken);

            if (appHealth == null || appHealth.AggregatedHealthState == HealthState.Ok)
            {
                return;
            }

            var appHealthEvents =
                appHealth.HealthEvents.Where(
                    e => 
                       JsonHelper.TryDeserializeObject(e.HealthInformation.Description, out TelemetryDataBase telemetryDataBase)
                       && telemetryDataBase.NodeName == this.nodeName
                       && (e.HealthInformation.SourceId.StartsWith(ObserverConstants.AppObserverName)
                           || e.HealthInformation.SourceId.StartsWith(ObserverConstants.FabricSystemObserverName)
                           || e.HealthInformation.SourceId.StartsWith(ObserverConstants.NetworkObserverName))).ToList();

            if (!appHealthEvents.Any())
            {
                return;
            }

            var healthReport = new HealthReport
            {
                Code = FOErrorWarningCodes.Ok,
                HealthMessage = $"Clearing existing FabricObserver Health Reports as the service is stopping or starting.",
                State = HealthState.Ok,
                NodeName = nodeName,
                AppName = app.ApplicationName,
                EntityType = EntityType.Application
            };
            ObserverHealthReporter healthReporter = new(logger);

            foreach (HealthEvent healthEvent in appHealthEvents)
            {
                try
                {
                    healthReport.Property = healthEvent.HealthInformation.Property;
                    healthReport.SourceId = healthEvent.HealthInformation.SourceId;
                    healthReporter.ReportHealthToServiceFabric(healthReport);
                }
                catch (FabricException)
                {

                }
            }
        }

        private async Task RemoveNodeHealthReportsAsync(IEnumerable<NodeHealthState> nodeHealthStates, bool ignoreDefaultQueryTimeout, CancellationToken cancellationToken)
        {
            // Scope to node where this FO instance is running.
            nodeHealthStates = nodeHealthStates.Where(n => n.NodeName == this.nodeName);

            foreach (var nodeHealthState in nodeHealthStates)
            {
                var nodeHealth = await FabricClientSingleton.HealthManager.GetNodeHealthAsync(
                                        nodeHealthState.NodeName,
                                        ignoreDefaultQueryTimeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(90), 
                                        cancellationToken);

                if (nodeHealth == null || nodeHealth.AggregatedHealthState == HealthState.Ok)
                {
                    return;
                }

                var nodeHealthEvents =
                    nodeHealth.HealthEvents.Where(
                        e => e.HealthInformation.SourceId.StartsWith(ObserverConstants.CertificateObserverName)
                          || e.HealthInformation.SourceId.StartsWith(ObserverConstants.DiskObserverName)
                          || e.HealthInformation.SourceId.StartsWith(ObserverConstants.FabricSystemObserverName)
                          || e.HealthInformation.SourceId.StartsWith(ObserverConstants.NodeObserverName)
                          || e.HealthInformation.SourceId.StartsWith(ObserverConstants.OSObserverName)).ToList();

                if (!nodeHealthEvents.Any())
                {
                    return;
                }

                var healthReport = new HealthReport
                {
                    Code = FOErrorWarningCodes.Ok,
                    HealthMessage = $"Clearing existing FabricObserver Health Reports as the service is stopping or starting.",
                    State = HealthState.Ok,
                    NodeName = this.nodeName,
                    EntityType = EntityType.Machine
                };
                ObserverHealthReporter healthReporter = new(logger);

                foreach (HealthEvent healthEvent in nodeHealthEvents)
                {
                    try
                    {                     
                        healthReport.Property = healthEvent.HealthInformation.Property;
                        healthReport.SourceId = healthEvent.HealthInformation.SourceId;
                        healthReporter.ReportHealthToServiceFabric(healthReport);
                    }
                    catch (FabricException)
                    {

                    }
                }
            }
        }
    }
}
