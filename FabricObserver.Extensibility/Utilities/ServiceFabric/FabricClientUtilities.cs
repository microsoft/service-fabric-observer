// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.XPath;

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
                    catch (Exception e) when (e is ObjectDisposedException || e is InvalidComObjectException)
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
            int maxDegreeOfParallelism = Convert.ToInt32(Math.Ceiling(Environment.ProcessorCount * 0.25 * 1.0));
            parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount >= 4 ? maxDegreeOfParallelism : 1,
                TaskScheduler = TaskScheduler.Default
            };

            if (string.IsNullOrEmpty(nodeName))
            {
                this.nodeName = FabricRuntime.GetNodeContext().NodeName;
            }
            else
            {
                this.nodeName = nodeName;
            }

            isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
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
            var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(!string.IsNullOrEmpty(nodeName) ? nodeName : this.nodeName)
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
        public async Task<List<ReplicaOrInstanceMonitoringInfo>> GetAllDeployedReplicasOrInstances(bool includeChildProcesses, CancellationToken token, string nodeName = null)
        {
            List<ReplicaOrInstanceMonitoringInfo> repList = new List<ReplicaOrInstanceMonitoringInfo>();
            List<DeployedApplication> appList = await GetAllDeployedAppsAsync(token);
            NativeMethods.SafeObjectHandle handleToSnapshot = null;

            if (isWindows && includeChildProcesses)
            {
                handleToSnapshot = NativeMethods.CreateToolhelp32Snapshot((uint)NativeMethods.CreateToolhelp32SnapshotFlags.TH32CS_SNAPPROCESS, 0);
            }

            try
            {
                foreach (DeployedApplication app in appList)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();

                        var deployedReplicaList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                           () => FabricClientSingleton.QueryManager.GetDeployedReplicaListAsync(
                                                                    !string.IsNullOrWhiteSpace(nodeName) ? nodeName : this.nodeName, app.ApplicationName),
                                                           token);

                        repList.AddRange(
                                GetInstanceOrReplicaMonitoringList(
                                    app.ApplicationName,
                                    app.ApplicationTypeName ?? appList.First(a => a.ApplicationName == app.ApplicationName).ApplicationTypeName,
                                    deployedReplicaList,
                                    includeChildProcesses,
                                    handleToSnapshot,
                                    token));   
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                    {

                    }
                }

                return repList.Count > 0 ? repList.Distinct().ToList() : repList;
            }
            finally
            {
                if (isWindows && includeChildProcesses)
                {
                    handleToSnapshot?.Dispose();
                    handleToSnapshot = null;
                }
            }
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
                                                        DeployedServiceReplicaList deployedReplicaList,
                                                        bool includeChildProcesses,
                                                        NativeMethods.SafeObjectHandle handleToSnapshot,
                                                        CancellationToken token)
        {
            ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList = new ConcurrentQueue<ReplicaOrInstanceMonitoringInfo>();
            parallelOptions.CancellationToken = token;

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
                        if (includeChildProcesses && !handleToSnapshot.IsInvalid)
                        {
                            List<(string ProcName, int Pid)> childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId, handleToSnapshot);

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

                        if (includeChildProcesses && !handleToSnapshot.IsInvalid)
                        {
                            List<(string ProcName, int Pid)> childPids = ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId, handleToSnapshot);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                            }
                        }

                        break;
                    }    
                }

                if (replicaInfo?.HostProcessId > 0)
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
                        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
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
        public List<(string ServiceName, string ProcName, int Pid)> GetServiceProcessInfo(List<ReplicaOrInstanceMonitoringInfo> repOrInsts)
        {
            List<(string ServiceName, string ProcName, int Pid)> pids = new List<(string ServiceName, string ProcName, int Pid)>();

            foreach (var repOrInst in repOrInsts)
            {
                try
                {
                    if (isWindows)
                    {
                        string procName = NativeMethods.GetProcessNameFromId((int)repOrInst.HostProcessId);
                        pids.Add((repOrInst.ServiceName.OriginalString, procName, (int)repOrInst.HostProcessId));
                    }
                    else
                    {
                        using (var proc = Process.GetProcessById((int)repOrInst.HostProcessId))
                        {
                            pids.Add((repOrInst.ServiceName.OriginalString, proc.ProcessName, (int)repOrInst.HostProcessId));
                        }
                    }

                    // Child processes?
                    if (repOrInst.ChildProcesses != null && repOrInst.ChildProcesses.Count > 0)
                    {
                        foreach (var (procName, Pid) in repOrInst.ChildProcesses)
                        {
                            pids.Add((repOrInst.ServiceName.OriginalString, procName, Pid));
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                {
                    // process with supplied pid may not be running..
                    continue;
                }
            }

            return pids;
        }

        public void ProcessServiceConfiguration(string appTypeName, string codepackageName, ref ReplicaOrInstanceMonitoringInfo replicaInfo)
        {
            if (string.IsNullOrWhiteSpace(appTypeName))
            {
                return;
            }

            try
            {
                string appTypeVersion = null;
                var appList =
                    FabricClientSingleton.QueryManager.GetApplicationListAsync(replicaInfo.ApplicationName)?.Result;

                if (appList?.Count > 0)
                {
                    try
                    {
                        if (appList.Any(app => app.ApplicationTypeName == appTypeName))
                        {
                            appTypeVersion = appList.First(app => app.ApplicationTypeName == appTypeName).ApplicationTypeVersion;
                            replicaInfo.ApplicationTypeVersion = appTypeVersion;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                    {

                    }

                    // RG - Windows-only. Linux is not supported yet.
                    if (!string.IsNullOrWhiteSpace(appTypeVersion))
                    {
                        if (isWindows)
                        {
                            string appManifest = FabricClientSingleton.ApplicationManager.GetApplicationManifestAsync(appTypeName, appTypeVersion)?.Result;

                            if (!string.IsNullOrWhiteSpace(appManifest))
                            {
                                (replicaInfo.RGEnabled, replicaInfo.RGMemoryLimitMb) =
                                    TupleGetResourceGovernanceInfo(appManifest, replicaInfo.ServiceManifestName, codepackageName);
                            }
                        }

                        // ServiceTypeVersion
                        var serviceList =
                        FabricClientSingleton.QueryManager.GetServiceListAsync(
                                replicaInfo.ApplicationName, replicaInfo.ServiceName)?.Result;

                        if (serviceList?.Count > 0)
                        {
                            try
                            {
                                Uri serviceName = replicaInfo.ServiceName;

                                if (serviceList.Any(s => s.ServiceName == serviceName))
                                {
                                    replicaInfo.ServiceTypeVersion = serviceList.First(s => s.ServiceName == serviceName).ServiceManifestVersion;
                                }
                            }
                            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                            {

                            }
                        }
                    }
                }
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is TimeoutException || e is XmlException)
            {
                // move along
            }
        }

        public void ProcessMultipleHelperCodePackages(
                        Uri appName,
                        string appTypeName,
                        DeployedServiceReplica deployedReplica,
                        ref ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> repsOrInstancesInfo,
                        bool isHostedByFabric,
                        bool includeChildProcesses,
                        NativeMethods.SafeObjectHandle handleToSnapshot = null)
        {
            try
            {
                DeployedCodePackageList codepackages = FabricClientSingleton.QueryManager.GetDeployedCodePackageListAsync(
                                                        nodeName,
                                                        appName,
                                                        deployedReplica.ServiceManifestName,
                                                        null).Result;

                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                // Check for multiple code packages or GuestExecutable service (so, Fabric is host).
                if (codepackages.Count > 1 || isHostedByFabric)
                {
                    foreach (var codepackage in codepackages)
                    {
                        // The code package does not belong to a deployed replica, so this is the droid we're looking for (a helper code package or guest executable).
                        if (codepackage.CodePackageName != deployedReplica.CodePackageName)
                        {
                            int procId = (int)codepackage.EntryPoint.ProcessId; // The actual process id of the helper or guest executable binary.
                            string procName = null;

                            // Process class is a CPU bottleneck on Windows.
                            if (isWindows)
                            {
                                procName = NativeMethods.GetProcessNameFromId(procId);
                            }
                            else // Linux
                            {
                                using (var proc = Process.GetProcessById(procId))
                                {
                                    try
                                    {
                                        procName = proc.ProcessName;
                                    }
                                    catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException || e is ArgumentException)
                                    {
                                        
                                    }
                                }
                            }

                            // Make sure procName lookup worked and if so that it is still the process we're looking for.
                            if (string.IsNullOrWhiteSpace(procName) || !EnsureProcess(procName, procId))
                            {
                                continue;
                            }

                            // It doesn't matter that the helper CodePackage or guest executable does not have any replicas (not hosted by Fabric). This basic construct ReplicaOrInstanceMonitoringInfo is used in several places.
                            // The key information for the helper/guest executable binary case is the ServiceManifestName, the parent's ServiceName/Type, its HostProcessId and its HostProcessName.
                            // This ensures that support for multiple CodePackages and guest executable services fit naturally into AppObserver's *existing* implementation.
                            replicaInfo = new ReplicaOrInstanceMonitoringInfo
                            {
                                ApplicationName = appName,
                                ApplicationTypeName = appTypeName,
                                HostProcessId = procId,
                                HostProcessName = procName,
                                ReplicaOrInstanceId = 0,
                                PartitionId = null,
                                ReplicaRole = ReplicaRole.None,
                                ServiceKind = ServiceKind.Invalid,
                                ServiceName = deployedReplica.ServiceName,
                                ServiceManifestName = codepackage.ServiceManifestName,
                                ServiceTypeName = deployedReplica.ServiceTypeName,
                                ServicePackageActivationId = codepackage.ServicePackageActivationId,
                                ServicePackageActivationMode = string.IsNullOrWhiteSpace(codepackage.ServicePackageActivationId) ?
                                                ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                                ReplicaStatus = ServiceReplicaStatus.Invalid
                            };

                            // If Helper binaries launch child processes, AppObserver will monitor them, too.
                            if (includeChildProcesses && procId > 0)
                            {
                                // DEBUG - Perf
                                //var sw = Stopwatch.StartNew();
                                List<(string ProcName, int Pid)> childPids;

                                if (isWindows)
                                {
                                    childPids = ProcessInfoProvider.Instance.GetChildProcessInfo(procId, handleToSnapshot);
                                }
                                else
                                {
                                    childPids = ProcessInfoProvider.Instance.GetChildProcessInfo(procId);
                                }

                                if (childPids != null && childPids.Count > 0)
                                {
                                    replicaInfo.ChildProcesses = childPids;
                                }
                                //sw.Stop();
                                //ObserverLogger.LogInfo($"EnableChildProcessMonitoring block run duration: {sw.Elapsed}");
                            }

                            // ResourceGovernance/AppTypeVer/ServiceTypeVer.
                            ProcessServiceConfiguration(appTypeName, codepackage.CodePackageName, ref replicaInfo);
                        }

                        if (replicaInfo != null && replicaInfo.HostProcessId > 0 && !repsOrInstancesInfo.Any(r => r.HostProcessId == replicaInfo.HostProcessId))
                        {
                            repsOrInstancesInfo.Enqueue(replicaInfo);
                        }
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FabricException || e is TaskCanceledException || e is TimeoutException)
            {
                
            }
        }

        private (bool IsRGEnabled, double MemoryLimitMb) TupleGetResourceGovernanceInfo(string appManifestXml, string servicePkgName, string codepackageName)
        {
            if (string.IsNullOrWhiteSpace(appManifestXml))
            {
  
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(servicePkgName))
            {
                return (false, 0);
            }

            if (string.IsNullOrWhiteSpace(codepackageName))
            {
                return (false, 0);
            }

            // Don't waste cycles with XML parsing if you can easily get a hint first..
            if (!appManifestXml.Contains($"<{ObserverConstants.RGPolicyNodeName} "))
            {
                return (false, 0);
            }

            // Safe XML pattern - *Do not use LoadXml*.
            var appManifestXdoc = new XmlDocument { XmlResolver = null };

            using (var sreader = new StringReader(appManifestXml))
            {
                using (var xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null }))
                {
                    appManifestXdoc?.Load(xreader);
                    return TupleGetResourceGovernanceInfoFromAppManifest(ref appManifestXdoc, servicePkgName, codepackageName);
                }
            }
        }

        private (bool IsRGEnabled, double MemoryLimitMb) TupleGetResourceGovernanceInfoFromAppManifest(ref XmlDocument xDoc, string servicePkgName, string codepackageName)
        {
            if (xDoc == null)
            {
                return (false, 0);
            }

            try
            {
                // Find the correct manifest import for specified service package name (servicePkgName arg).
                // There will generally be multiple RG limits set per application (so, per service settings).
                var sNode =
                    xDoc.DocumentElement?.SelectSingleNode(
                        $"//*[local-name()='{ObserverConstants.ServiceManifestImport}'][*[local-name()='{ObserverConstants.ServiceManifestRef}' and @*[local-name()='{ObserverConstants.ServiceManifestName}' and . ='{servicePkgName}']]]");

                if (sNode == null)
                {
                    return (false, 0);
                }

                XmlNodeList childNodes = sNode.ChildNodes;

                foreach (XmlNode node in childNodes)
                {
                    if (node.Name != ObserverConstants.PoliciesNodeName)
                    {
                        continue;
                    }

                    foreach (XmlNode polNode in node.ChildNodes)
                    {
                        if (polNode.Name != ObserverConstants.RGPolicyNodeName)
                        {
                            continue;
                        }

                        string codePackageRef = polNode.Attributes[ObserverConstants.CodePackageRef]?.Value;

                        if (codePackageRef != codepackageName)
                        {
                            continue;
                        }

                        // Get the rg policy memory attribute. It can only be either "MemoryInMB" or "MemoryInMBLimit"
                        XmlAttribute memAttr = null;

                        if (polNode.Attributes[ObserverConstants.RGMemoryInMB] != null)
                        {
                            memAttr = polNode.Attributes[ObserverConstants.RGMemoryInMB];
                        }
                        else if (polNode.Attributes[ObserverConstants.RGMemoryInMBLimit] != null)
                        {
                            memAttr = polNode.Attributes[ObserverConstants.RGMemoryInMBLimit];
                        }

                        // Not the droid we're looking for.
                        if (memAttr == null || string.IsNullOrWhiteSpace(memAttr.Value))
                        {
                            continue;
                        }

                        // App Parameter support: This means user has specified the absolute memory value in an Application Parameter.
                        if (memAttr.Value.StartsWith("["))
                        {
                            XmlNode parametersNode = xDoc.DocumentElement?.SelectSingleNode($"//*[local-name()='{ObserverConstants.Parameters}']");
                            XmlNode parameterNode =
                                parametersNode?.SelectSingleNode($"//*[local-name()='{ObserverConstants.Parameter}' and @Name='{memAttr.Value.Substring(1, memAttr.Value.Length - 1)}']");
                            XmlAttribute attr = parameterNode?.Attributes?[ObserverConstants.DefaultValue];
                            memAttr.Value = attr.Value;
                        }

                        return (true, double.TryParse(memAttr.Value, out double mem) ? mem : 0);
                    }
                }
            }
            catch (XPathException)
            {
                return (false, 0);
            }

            return (false, 0);
        }

        private bool EnsureProcess(string procName, int procId)
        {
            if (string.IsNullOrWhiteSpace(procName) || procId < 1)
            {
                return false;
            }

            if (!isWindows)
            {
                try
                {
                    using (Process proc = Process.GetProcessById(procId))
                    {
                        return proc.ProcessName == procName;
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                {
                    return false;
                }
            }

            // net core's ProcessManager.EnsureState is a CPU bottleneck on Windows.
            return NativeMethods.GetProcessNameFromId(procId) == procName;
        }
    }
}
