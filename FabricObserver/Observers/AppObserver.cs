// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FabricObserver.Interfaces;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.Utilities.ServiceFabric;
using Microsoft.Win32.SafeHandles;

namespace FabricObserver.Observers
{
    // This observer monitors the behavior of user SF service processes (and their children) and signals Warning and Error based on user-supplied resource thresholds
    // in AppObserver.config.json. This observer will also emit telemetry (ETW, LogAnalytics/AppInsights) if enabled in Settings.xml (ObserverManagerConfiguration) and ApplicationManifest.xml (AppObserverEnableEtw).
    public sealed class AppObserver : ObserverBase
    {
        private const double KvsLvidsWarningPercentage = 75.0;
        private const double MaxRGMemoryInUsePercent = 90.0;
        private const double MaxRGCpuInUsePercent = 90.0;
        private const int MaxSameNamedProcesses = 50;

        // These are the concurrent data structures that hold all monitoring data for all application service targets for specific metrics.
        // In the case where machine has capable CPU configuration and AppObserverEnableConcurrentMonitoring is enabled, these ConcurrentDictionaries
        // will be read from and written to by multiple threads. In the case where concurrency is not possible (or not enabled), they will sort of act as "normal"
        // Dictionaries (not precisely) since the monitoring loop will always be sequential (exactly one thread, so no internal locking) and there will not be *any* concurrent reads/writes.
        // The modest cost in memory allocation in the sequential processing case is not an issue here.
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppMemDataMb;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppMemDataPercent;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppTotalActivePortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppEphemeralPortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppEphemeralPortsDataPercent;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppHandlesData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppThreadsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppKvsLvidsData;

        // Windows-only
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppPrivateBytesDataMb;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppPrivateBytesDataPercent;

        // Windows-only for now.
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppRGMemoryUsagePercent;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppRGCpuUsagePercent;

        // _userTargetList is the list of ApplicationInfo objects representing app/app types supplied in user configuration (AppObserver.config.json).
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<ApplicationInfo> userTargetList;

        // _deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied target list.
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<ApplicationInfo> deployedTargetList;

        // _deployedApps is the List of all apps currently deployed on the local Fabric node.
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        private List<DeployedApplication> deployedApps;

        private readonly Stopwatch stopwatch;
        private readonly object lockObj = new();
        private FabricClientUtilities fabricClientUtilities;
        private ParallelOptions parallelOptions;
        private string fileName;
        private int appCount;
        private int serviceCount;
        private bool hasSFUserProcCache;
        private NativeMethods.SafeObjectHandle handleToProcSnapshot = null;

        private NativeMethods.SafeObjectHandle Win32HandleToProcessSnapshot
        {
            get
            {
                // This is only useful for Windows.
                if (!IsWindows)
                {
                    return null;
                }

                if (!EnableChildProcessMonitoring)
                {
                    return null;
                }

                // If the more performant approach (see NativeMethods.cs) for getting child processes succeeded, then don't proceed.
                if (hasSFUserProcCache)
                {
                    return null;
                }

                if (handleToProcSnapshot == null)
                {
                    lock (lockObj)
                    {
                        if (handleToProcSnapshot == null)
                        {
                            handleToProcSnapshot = NativeMethods.CreateProcessSnapshot();

                            if (handleToProcSnapshot.IsInvalid)
                            {
                                string message = $"HandleToProcessSnapshot: Failed to get process snapshot with error code {Marshal.GetLastWin32Error()}";
                                ObserverLogger.LogWarning(message);
                                throw new Win32Exception(message);
                            }
                        }
                    }
                }

                return handleToProcSnapshot;
            }
        }

        // ReplicaOrInstanceList is the List of all replicas or instances that will be monitored during the current run.
        // List<T> is thread-safe for concurrent reads. There are no concurrent writes to this List.
        public List<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList;

        public int MaxChildProcTelemetryDataCount
        {
            get; set;
        }

        public bool EnableChildProcessMonitoring
        {
            get; set;
        }

        public string JsonConfigPath
        {
            get; set;
        }

        public bool EnableConcurrentMonitoring
        {
            get; set;
        }

        public bool EnableProcessDumps
        {
            get; set;
        }

        public bool EnableKvsLvidMonitoring
        {
            get; set;
        }

        public int OperationalHealthEvents
        {
            get; set;
        }

        public bool CheckPrivateWorkingSet
        {
            get; set;
        }

        public bool MonitorResourceGovernanceLimits
        {
            get; set;
        }

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public AppObserver(StatelessServiceContext context) : base(null, context)
        {
            stopwatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            ObserverLogger.LogInfo($"Started ObserveAsync.");

            if (RunInterval > TimeSpan.Zero && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                ObserverLogger.LogInfo($"ObserveAsync: RunInterval ({RunInterval}) has not elapsed. Exiting.");
                return;
            }

            Token = token;
            stopwatch.Restart();

            try
            {
                bool initialized = await InitializeAsync();

                if (!initialized)
                {
                    ObserverLogger.LogWarning("AppObserver was unable to initialize correctly due to misconfiguration. " +
                                              "Please check your AppObserver configuration settings.");
                    stopwatch.Reset();
                    CleanUp();
                    LastRunDateTime = DateTime.Now;
                    return;
                }
            }
            catch (FabricException fe)
            {
                if (fe.ErrorCode == FabricErrorCode.ApplicationNotFound || fe.ErrorCode == FabricErrorCode.ApplicationTypeNotFound)
                {
                    // Ignore these. These can happen when some target service was deleted while FO was gathering related data for entity, for example.
                }
                else
                {
                    throw;
                }
            }
            catch (Exception e)
            {
                ObserverLogger.LogError($"InitializeAsync failure: {e.Message}. Exiting AppObsever.");
                throw;
            }

            try
            {
                ParallelLoopResult result = await MonitorDeployedAppsAsync(token);

                if (result.IsCompleted)
                {
                    await ReportAsync(token);
                }

                stopwatch.Stop();
                RunDuration = stopwatch.Elapsed;

                if (EnableVerboseLogging)
                {
                    ObserverLogger.LogInfo($"Run Duration ({ReplicaOrInstanceList?.Count} service processes observed) {(parallelOptions.MaxDegreeOfParallelism == 1 ? "without" : "with")} " +
                                           $"Parallel Processing (MaxDegreeOfParallelism: {parallelOptions.MaxDegreeOfParallelism}): {RunDuration}.");
                }

                ObserverLogger.LogInfo($"Completed ObserveAsync.");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ObserverLogger.LogError($"Unhandled exception in ObserveAsync: {e.Message}");
                throw;
            }
            finally
            {
                CleanUp();
                LastRunDateTime = DateTime.Now;
            }
        }

        public override Task ReportAsync(CancellationToken token)
        {
            if (deployedTargetList.Count == 0)
            {
                return Task.CompletedTask;
            }

            ObserverLogger.LogInfo($"Started ReportAsync.");

            //DEBUG
            var stopwatch = Stopwatch.StartNew();
            TimeSpan healthReportTtl = GetHealthReportTTL();

            // This will run sequentially (with 1 thread) if the underlying CPU config does not meet the requirements for concurrency (e.g., if logical procs < 4).
            _ = Parallel.For(0, ReplicaOrInstanceList.Count, parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                }

                ReplicaOrInstanceMonitoringInfo repOrInst = ReplicaOrInstanceList[i];

                if (repOrInst.HostProcessId < 1)
                {
                    return;
                }

                // For use in process family tree monitoring.
                ConcurrentQueue<ChildProcessTelemetryData> childProcessTelemetryDataList = null;

                string processName = null;
                int processId = 0;
                ApplicationInfo app = null;
                bool hasChildProcs = EnableChildProcessMonitoring && repOrInst.ChildProcesses != null;

                if (hasChildProcs)
                {
                    childProcessTelemetryDataList = new ConcurrentQueue<ChildProcessTelemetryData>();
                }

                if (!deployedTargetList.Any(
                         a => (a.TargetApp != null && a.TargetApp == repOrInst.ApplicationName.OriginalString) ||
                              (a.TargetAppType != null && a.TargetAppType == repOrInst.ApplicationTypeName)))
                {
                    return;
                }

                app = deployedTargetList.First(
                        a => (a.TargetApp != null && a.TargetApp == repOrInst.ApplicationName.OriginalString) ||
                                (a.TargetAppType != null && a.TargetAppType == repOrInst.ApplicationTypeName));


                // process serviceIncludeList config items for a single app.
                if (app?.ServiceIncludeList != null)
                {
                    // Ensure the service is the one we are looking for.
                    if (deployedTargetList.Any(
                            a => a.ServiceIncludeList != null &&
                                    a.ServiceIncludeList.Contains(
                                        repOrInst.ServiceName.OriginalString.Remove(0, repOrInst.ApplicationName.OriginalString.Length + 1))))
                    {
                        // It could be the case that user config specifies multiple inclusion lists for a single app/type in user configuration. We want the correct service here.
                        app = deployedTargetList.First(
                                a => a.ServiceIncludeList != null &&
                                a.ServiceIncludeList.Contains(
                                    repOrInst.ServiceName.OriginalString.Remove(0, repOrInst.ApplicationName.OriginalString.Length + 1)));
                    }
                }

                processId = (int)repOrInst.HostProcessId;
                processName = repOrInst.HostProcessName;

                // Make sure the target process currently exists, otherwise why report on it (it was ephemeral as far as this run of AO is concerned).
                if (!EnsureProcess(processName, processId, repOrInst.HostProcessStartTime))
                {
                    return;
                }

                string appNameOrType = GetAppNameOrType(repOrInst);
                string id = $"{appNameOrType}:{processName}{processId}";

                // Locally Log (csv) CPU/Mem/FileHandles/Ports per app service process.
                if (EnableCsvLogging)
                {
                    // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                    // Please use ContainerObserver for SF container app service monitoring.
                    if (processName == "Fabric")
                    {
                        return;
                    }

                    // This lock is required.
                    lock (lockObj)
                    {
                        fileName = $"{processName}{(CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives ? "_" + DateTime.UtcNow.ToString("o") : string.Empty)}";

                        // BaseLogDataLogFolderPath is set in ObserverBase or a default one is created by CsvFileLogger.
                        // This means a new folder will be added to the base path.
                        if (CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives)
                        {
                            CsvFileLogger.DataLogFolder = processName;
                        }

                        // Log pid..
                        CsvFileLogger.LogData(fileName, id, "ProcessId", "", processId);

                        // Log resource usage data to CSV files.
                        LogAllAppResourceDataToCsv(id);
                    }
                }

                try
                {
                    // CPU Time (Percent)
                    if (AllAppCpuData != null && AllAppCpuData.ContainsKey(id))
                    {
                        var parentFrud = AllAppCpuData[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppCpuData.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<double>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        // Parent's and aggregated (summed) descendant process data (if any).
                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.CpuErrorLimitPercent,
                            app.CpuWarningLimitPercent,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // Working Set (MB)
                    if (AllAppMemDataMb != null && AllAppMemDataMb.ContainsKey(id))
                    {
                        var parentFrud = AllAppMemDataMb[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppMemDataMb.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<float>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.MemoryErrorLimitMb,
                            app.MemoryWarningLimitMb,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // Working Set (Percent)
                    if (AllAppMemDataPercent != null && AllAppMemDataPercent.ContainsKey(id))
                    {
                        var parentFrud = AllAppMemDataPercent[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppMemDataPercent.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<double>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.MemoryErrorLimitPercent,
                            app.MemoryWarningLimitPercent,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // Private Bytes (MB)
                    if (AllAppPrivateBytesDataMb != null && AllAppPrivateBytesDataMb.ContainsKey(id))
                    {
                        if (app.WarningPrivateBytesMb > 0 || app.ErrorPrivateBytesMb > 0)
                        {
                            var parentFrud = AllAppPrivateBytesDataMb[id];
                            int childProcCount = 0;

                            if (hasChildProcs)
                            {
                                var targetFruds = AllAppPrivateBytesDataMb.Where(f => f.Key.StartsWith(id));
                                ConcurrentDictionary<string, FabricResourceUsageData<float>> childProcDictionary = new();

                                foreach (var frud in targetFruds)
                                {
                                    // Parent.
                                    if (frud.Key == id)
                                    {
                                        continue;
                                    }

                                    _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                                }

                                childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                            }

                            ProcessResourceDataReportHealth(
                                parentFrud,
                                app.ErrorPrivateBytesMb,
                                app.WarningPrivateBytesMb,
                                healthReportTtl,
                                EntityType.Service,
                                processName,
                                repOrInst,
                                app.DumpProcessOnError && EnableProcessDumps,
                                app.DumpProcessOnWarning && EnableProcessDumps,
                                processId,
                                childProcCount);
                        }
                    }

                    // Private Bytes (Percent)
                    if (AllAppPrivateBytesDataPercent != null && AllAppPrivateBytesDataPercent.ContainsKey(id))
                    {
                        if (app.WarningPrivateBytesPercent > 0 || app.ErrorPrivateBytesPercent > 0)
                        {
                            var parentFrud = AllAppPrivateBytesDataPercent[id];
                            int childProcCount = 0;

                            if (hasChildProcs)
                            {
                                var targetFruds = AllAppPrivateBytesDataPercent.Where(f => f.Key.StartsWith(id));
                                ConcurrentDictionary<string, FabricResourceUsageData<double>> childProcDictionary = new();

                                foreach (var frud in targetFruds)
                                {
                                    // Parent.
                                    if (frud.Key == id)
                                    {
                                        continue;
                                    }

                                    _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                                }

                                childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                            }

                            ProcessResourceDataReportHealth(
                                parentFrud,
                                app.ErrorPrivateBytesPercent,
                                app.WarningPrivateBytesPercent,
                                healthReportTtl,
                                EntityType.Service,
                                processName,
                                repOrInst,
                                app.DumpProcessOnError && EnableProcessDumps,
                                app.DumpProcessOnWarning && EnableProcessDumps,
                                processId,
                                childProcCount);
                        }
                    }

                    // FO's RG support is Windows-only for now.
                    if (MonitorResourceGovernanceLimits)
                    {
                        // RG Memory Monitoring (Private Bytes Percent)
                        if (repOrInst.RGMemoryEnabled && AllAppRGMemoryUsagePercent != null && AllAppRGMemoryUsagePercent.ContainsKey(id))
                        {
                            var parentFrud = AllAppRGMemoryUsagePercent[id];
                            int childProcCount = 0;

                            if (hasChildProcs)
                            {
                                var targetFruds = AllAppRGMemoryUsagePercent.Where(f => f.Key.StartsWith(id));
                                ConcurrentDictionary<string, FabricResourceUsageData<double>> childProcDictionary = new();

                                foreach (var frud in targetFruds)
                                {
                                    // Parent.
                                    if (frud.Key == id)
                                    {
                                        continue;
                                    }

                                    _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                                }

                                childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                            }

                            ProcessResourceDataReportHealth(
                                parentFrud,
                                thresholdError: 0, // Only Warning Threshold is supported for RG reporting.
                                thresholdWarning: app.WarningRGMemoryLimitPercent > 0 ? app.WarningRGMemoryLimitPercent : MaxRGMemoryInUsePercent, // Default: 90%
                                healthReportTtl,
                                EntityType.Service,
                                processName,
                                repOrInst,
                                dumpOnError: false, // Not supported
                                dumpOnWarning: false, // Not supported
                                processId,
                                childProcCount);
                        }

                        // RG CPU Monitoring (CPU Time Percent)
                        if (repOrInst.RGCpuEnabled && AllAppRGCpuUsagePercent != null && AllAppRGCpuUsagePercent.ContainsKey(id))
                        {
                            var parentFrud = AllAppRGCpuUsagePercent[id];
                            int childProcCount = 0;

                            if (hasChildProcs)
                            {
                                var targetFruds = AllAppRGCpuUsagePercent.Where(f => f.Key.StartsWith(id));
                                ConcurrentDictionary<string, FabricResourceUsageData<double>> childProcDictionary = new();

                                foreach (var frud in targetFruds)
                                {
                                    // Parent.
                                    if (frud.Key == id)
                                    {
                                        continue;
                                    }

                                    _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                                }

                                childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                            }

                            ProcessResourceDataReportHealth(
                                parentFrud,
                                thresholdError: 0, // Only Warning Threshold is supported for RG reporting.
                                thresholdWarning: app.WarningRGCpuLimitPercent > 0 ? app.WarningRGCpuLimitPercent : MaxRGCpuInUsePercent, // Default: 90%
                                healthReportTtl,
                                EntityType.Service,
                                processName,
                                repOrInst,
                                dumpOnError: false, // Not supported
                                dumpOnWarning: false, // Not supported
                                processId,
                                childProcCount);
                        }
                    }

                    // TCP Ports - Active
                    if (AllAppTotalActivePortsData != null && AllAppTotalActivePortsData.ContainsKey(id))
                    {
                        var parentFrud = AllAppTotalActivePortsData[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppTotalActivePortsData.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<int>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.NetworkErrorActivePorts,
                            app.NetworkWarningActivePorts,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // TCP Ports Total - Ephemeral (port numbers fall in the dynamic range)
                    if (AllAppEphemeralPortsData != null && AllAppEphemeralPortsData.ContainsKey(id))
                    {
                        var parentFrud = AllAppEphemeralPortsData[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppEphemeralPortsData.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<int>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.NetworkErrorEphemeralPorts,
                            app.NetworkWarningEphemeralPorts,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // TCP Ports Percentage - Ephemeral (port numbers fall in the dynamic range)
                    if (AllAppEphemeralPortsDataPercent != null && AllAppEphemeralPortsDataPercent.ContainsKey(id))
                    {
                        var parentFrud = AllAppEphemeralPortsDataPercent[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppEphemeralPortsDataPercent.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<double>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.NetworkErrorEphemeralPortsPercent,
                            app.NetworkWarningEphemeralPortsPercent,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // Handles
                    if (AllAppHandlesData != null && AllAppHandlesData.ContainsKey(id))
                    {
                        var parentFrud = AllAppHandlesData[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppHandlesData.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<float>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.ErrorOpenFileHandles > 0 ? app.ErrorOpenFileHandles : app.ErrorHandleCount,
                            app.WarningOpenFileHandles > 0 ? app.WarningOpenFileHandles : app.WarningHandleCount,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // Threads
                    if (AllAppThreadsData != null && AllAppThreadsData.ContainsKey(id))
                    {
                        var parentFrud = AllAppThreadsData[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppThreadsData.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<int>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        ProcessResourceDataReportHealth(
                            parentFrud,
                            app.ErrorThreadCount,
                            app.WarningThreadCount,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // KVS LVIDs - Windows-only (EnableKvsLvidMonitoring will always be false otherwise)
                    if (EnableKvsLvidMonitoring && AllAppKvsLvidsData != null && AllAppKvsLvidsData.ContainsKey(id))
                    {
                        var parentFrud = AllAppKvsLvidsData[id];
                        int childProcCount = 0;

                        if (hasChildProcs)
                        {
                            var targetFruds = AllAppKvsLvidsData.Where(f => f.Key.StartsWith(id));
                            ConcurrentDictionary<string, FabricResourceUsageData<double>> childProcDictionary = new();

                            foreach (var frud in targetFruds)
                            {
                                // Parent.
                                if (frud.Key == id)
                                {
                                    continue;
                                }

                                _ = childProcDictionary.TryAdd(frud.Key, frud.Value);
                            }

                            childProcCount = ProcessChildProcs(ref childProcDictionary, ref childProcessTelemetryDataList, repOrInst, app, ref parentFrud, token);
                        }

                        // FO will warn if the stateful (Actor, for example) service process has used 75% or greater of available LVIDs. This is not configurable (and a temporary feature).
                        ProcessResourceDataReportHealth(
                            parentFrud,
                            0,
                            KvsLvidsWarningPercentage,
                            healthReportTtl,
                            EntityType.Service,
                            processName,
                            repOrInst,
                            app.DumpProcessOnError && EnableProcessDumps,
                            app.DumpProcessOnWarning && EnableProcessDumps,
                            processId,
                            childProcCount);
                    }

                    // Child proc info telemetry.
                    if (hasChildProcs && MaxChildProcTelemetryDataCount > 0 && childProcessTelemetryDataList != null && !childProcessTelemetryDataList.IsEmpty)
                    {
                        if (childProcessTelemetryDataList.All(c => c.ChildProcessInfo?.Count > 0))
                        {
                            if (IsEtwEnabled)
                            {
                                ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, childProcessTelemetryDataList.ToList());
                            }

                            if (IsTelemetryEnabled && EmitRawMetricTelemetry)
                            {
                                _ = TelemetryClient?.ReportMetricAsync(childProcessTelemetryDataList.ToList(), token);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    state.Stop();
                }
            });

            stopwatch.Stop();
            ObserverLogger.LogInfo($"Completed ReportAsync.");
            ObserverLogger.LogInfo($"ReportAsync run duration: {stopwatch.Elapsed}");
            return Task.CompletedTask;
        }

        // This runs each time ObserveAsync is run to ensure that any new app targets and config changes will
        // be up to date.
        public async Task<bool> InitializeAsync()
        {
            ObserverLogger.LogInfo($"Initializing AppObserver.");
            ReplicaOrInstanceList = new List<ReplicaOrInstanceMonitoringInfo>();
            userTargetList = new List<ApplicationInfo>();
            deployedTargetList = new List<ApplicationInfo>();

            // NodeName is passed here to not break unit tests, which include a mock service fabric context.
            fabricClientUtilities = new FabricClientUtilities(NodeName);
            deployedApps = await fabricClientUtilities.GetAllDeployedAppsAsync(Token, NodeName);

            // DEBUG - Perf
#if DEBUG
            var stopwatch = Stopwatch.StartNew();
#endif
            // Set properties with Application Parameter settings (housed in ApplicationManifest.xml) for this run.
            SetPropertiesFromApplicationSettings();

            if (IsWindows)
            {
                // If RefreshSFUserProcessDataCache returns false, then it means the internal impl failed. In this case, a different API (less efficient) will be used 
                // to get child processes, if enabled (see Win32HandleToProcessSnapshot property impl). This function refreshes 2 caches. It is not
                // only related to child process detection.
                hasSFUserProcCache = NativeMethods.RefreshSFUserProcessDataCache(getChildProcesses: EnableChildProcessMonitoring);
            }

            // Process JSON object configuration settings (housed in [AppObserver.config].json) for this run.
            if (!await ProcessJSONConfigAsync())
            {
                return false;
            }

            // Filter JSON targetApp setting format; try and fix malformed values, if possible.
            FilterTargetAppFormat();

            // Support for specifying single configuration JSON object for all applications.
            await ProcessGlobalThresholdSettingsAsync();

            // Support for specifying thresholds in application's manifest
            await ProcessAppManifestThresholdSettingsAsync();

            SetConcurrentMonitoringState();

            int settingsFail = 0;

            for (int i = 0; i < userTargetList.Count; i++)
            {
                if (Token.IsCancellationRequested)
                {
                    return false;
                }

                ApplicationInfo application = userTargetList[i];
                Uri appUri = null;

                try
                {
                    if (application.TargetApp != null)
                    {
                        appUri = new Uri(application.TargetApp);
                    }
                }
                catch (UriFormatException)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(application.TargetApp) && string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    settingsFail++;

                    // No required settings supplied for deployed application(s).
                    if (settingsFail == userTargetList.Count)
                    {
                        string message = "No required settings supplied for deployed applications in AppObserver.config.json. " +
                                         "You must supply either a targetApp or targetAppType setting.";

                        var healthReport = new Utilities.HealthReport
                        {
                            AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                            EmitLogEvent = true,
                            HealthMessage = message,
                            HealthReportTimeToLive = GetHealthReportTTL(),
                            Property = "AppMisconfiguration",
                            EntityType = EntityType.Application,
                            State = HealthState.Warning,
                            NodeName = NodeName,
                            Observer = ObserverConstants.AppObserverName,
                        };

                        // Generate a Service Fabric Health Report.
                        HealthReporter.ReportHealthToServiceFabric(healthReport);

                        // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                        if (IsTelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportHealthAsync(
                                    "AppMisconfiguration",
                                    HealthState.Warning,
                                    message,
                                    ObserverName,
                                    Token);
                        }

                        // ETW.
                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(
                                ObserverConstants.FabricObserverETWEventName,
                                new
                                {
                                    Property = "AppMisconfiguration",
                                    Level = "Warning",
                                    Message = message,
                                    ObserverName
                                });
                        }

                        OperationalHealthEvents++;
                        return false;
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(application.TargetAppType))
                {
                    await SetDeployedReplicaOrInstanceListAsync(null, application.TargetAppType);
                }
                else
                {
                    await SetDeployedReplicaOrInstanceListAsync(appUri);
                }
            }

            int repCount = ReplicaOrInstanceList.Count;

            // internal diagnostic telemetry \\

            // Do not emit the same service count data over and over again.
            if (repCount != serviceCount)
            {
                MonitoredServiceProcessCount = repCount;
                serviceCount = repCount;
            }
            else
            {
                MonitoredServiceProcessCount = 0;
            }

            // Do not emit the same app count data over and over again.
            if (deployedTargetList.Count != appCount)
            {
                MonitoredAppCount = deployedTargetList.Count;
                appCount = deployedTargetList.Count;
            }
            else
            {
                MonitoredAppCount = 0;
            }

            if (!EnableVerboseLogging)
            {
                return true;
            }
#if DEBUG
            for (int i = 0; i < deployedTargetList.Count; i++)
            {
                if (Token.IsCancellationRequested)
                {
                    return false;
                }

                ObserverLogger.LogInfo($"AppObserver settings applied to {deployedTargetList[i].TargetApp}:{Environment.NewLine}{deployedTargetList[i]}");
            }
#endif
            for (int i = 0; i < repCount; ++i)
            {
                if (Token.IsCancellationRequested)
                {
                    return false;
                }

                var rep = ReplicaOrInstanceList[i];
                ObserverLogger.LogInfo($"Will observe resource consumption by {rep.ServiceName?.OriginalString}({rep.HostProcessId}) on Node {NodeName}.");
            }
#if DEBUG
            stopwatch.Stop();
            ObserverLogger.LogInfo($"InitializeAsync run duration: {stopwatch.Elapsed}");
#endif
            return true;
        }

        private void SetConcurrentMonitoringState()
        {
            if (bool.TryParse(
                    GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableConcurrentMonitoringParameter), out bool enableConcurrency))
            {
                EnableConcurrentMonitoring = enableConcurrency;
            }
            ObserverLogger.LogInfo($"EnableConcurrentMonitoring = {EnableConcurrentMonitoring}");

            // Effectively sequential.
            int maxDegreeOfParallelism = 1;

            if (EnableConcurrentMonitoring)
            {
                _ = int.TryParse(GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxConcurrentTasksParameter), out maxDegreeOfParallelism);
                
                if (maxDegreeOfParallelism < -1 || maxDegreeOfParallelism == 0)
                {
                    ObserverLogger.LogWarning($"MaxConcurrentTasks setting is invalid ({maxDegreeOfParallelism}). Employing default value (25).");
                    maxDegreeOfParallelism = 25;
                }
            }
           
            ObserverLogger.LogInfo($"Environment.ProcessorCount = {Environment.ProcessorCount}");
            ObserverLogger.LogInfo($"MaxDegreeOfParallelism = {maxDegreeOfParallelism}");
            parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = Token,
                TaskScheduler = TaskScheduler.Default
            };
        }

        private bool PopulateAppInfoWithAppManifestThresholds(DeployedApplication deployedApp, ApplicationInfo appInfo) 
        {
            string appTypeVersion = null;
            ApplicationParameterList appParameters = null;
            ApplicationParameterList defaultParameters = null;

            ApplicationList appList =
                FabricClientInstance.QueryManager.GetApplicationListAsync(
                    deployedApp.ApplicationName,
                    ConfigurationSettings.AsyncTimeout,
                    Token).Result;

            ApplicationTypeList applicationTypeList =
                FabricClientInstance.QueryManager.GetApplicationTypeListAsync(
                    deployedApp.ApplicationTypeName,
                    ConfigurationSettings.AsyncTimeout,
                    Token).Result;

            if (appList?.Count > 0)
            {
                try
                {
                    if (appList.Any(app => app.ApplicationTypeName == deployedApp.ApplicationTypeName))
                    {
                        appTypeVersion = appList.First(app => app.ApplicationTypeName == deployedApp.ApplicationTypeName).ApplicationTypeVersion;
                        appParameters = appList.First(app => app.ApplicationTypeName == deployedApp.ApplicationTypeName).ApplicationParameters;
                    }

                    if (applicationTypeList.Any(app => app.ApplicationTypeVersion == appTypeVersion))
                    {
                        defaultParameters = applicationTypeList.First(app => app.ApplicationTypeVersion == appTypeVersion).DefaultParameters;
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {

                }
            }

            ApplicationParameterList parameters = new();
            FabricClientUtilities.AddParametersIfNotExists(parameters, appParameters);
            FabricClientUtilities.AddParametersIfNotExists(parameters, defaultParameters);

            bool informationAdded = false;

            // Working Set
            if (parameters.Contains(ObserverConstants.AppManifestMemoryWarningLimitMb))
            {
                if (long.TryParse(parameters[ObserverConstants.AppManifestMemoryWarningLimitMb].Value, out long memoryMbWarningThreshold) && memoryMbWarningThreshold > 0)
                {
                    appInfo.MemoryWarningLimitMb = memoryMbWarningThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestMemoryErrorLimitMb))
            {
                if (long.TryParse(parameters[ObserverConstants.AppManifestMemoryErrorLimitMb].Value, out long memoryMbErrorThreshold) && memoryMbErrorThreshold > 0)
                {
                    appInfo.MemoryErrorLimitMb = memoryMbErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestMemoryWarningLimitPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestMemoryWarningLimitPercent].Value, out double memoryPctWarningThreshold) && memoryPctWarningThreshold > 0)
                {
                    appInfo.MemoryWarningLimitPercent = memoryPctWarningThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestMemoryErrorLimitPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestMemoryErrorLimitPercent].Value, out double memoryPctErrorThreshold) && memoryPctErrorThreshold > 0)
                {
                    appInfo.MemoryErrorLimitPercent = memoryPctErrorThreshold;
                    informationAdded = true;
                }
            }
            // CPU
            if (parameters.Contains(ObserverConstants.AppManifestCpuErrorLimitPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestCpuErrorLimitPercent].Value, out double cpuPctErrorThreshold) && cpuPctErrorThreshold > 0)
                {
                    appInfo.CpuErrorLimitPercent = cpuPctErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestCpuWarningLimitPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestCpuWarningLimitPercent].Value, out double cpuPctWarningThreshold) && cpuPctWarningThreshold > 0)
                {
                    appInfo.CpuWarningLimitPercent = cpuPctWarningThreshold;
                    informationAdded = true;
                }
            }
            // Active Ports
            if (parameters.Contains(ObserverConstants.AppManifestNetworkErrorActivePorts))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestNetworkErrorActivePorts].Value, out int activePortsErrorThreshold) && activePortsErrorThreshold > 0)
                {
                    appInfo.NetworkErrorActivePorts = activePortsErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestNetworkWarningActivePorts))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestNetworkWarningActivePorts].Value, out int activePortsWarningThreshold) && activePortsWarningThreshold > 0)
                {
                    appInfo.NetworkWarningActivePorts = activePortsWarningThreshold;
                    informationAdded = true;
                }
            }
            // Ephemeral Ports
            if (parameters.Contains(ObserverConstants.AppManifestNetworkErrorEphemeralPorts))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestNetworkErrorEphemeralPorts].Value, out int ephemeralPortsErrorThreshold) && ephemeralPortsErrorThreshold > 0)
                {
                    appInfo.NetworkErrorEphemeralPorts = ephemeralPortsErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestNetworkWarningEphemeralPorts))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestNetworkWarningEphemeralPorts].Value, out int ephemeralPortsWarningThreshold) && ephemeralPortsWarningThreshold > 0)
                {
                    appInfo.NetworkWarningEphemeralPorts = ephemeralPortsWarningThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestNetworkErrorEphemeralPortsPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestNetworkErrorEphemeralPortsPercent].Value, out double ephemeralPortsPctErrorThreshold) && ephemeralPortsPctErrorThreshold > 0)
                {
                    appInfo.NetworkErrorEphemeralPortsPercent = ephemeralPortsPctErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestNetworkWarningEphemeralPortsPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestNetworkWarningEphemeralPortsPercent].Value, out double ephemeralPortsPctWarningThreshold) && ephemeralPortsPctWarningThreshold > 0)
                {
                    appInfo.NetworkWarningEphemeralPortsPercent = ephemeralPortsPctWarningThreshold;
                    informationAdded = true;
                }
            }
            // Dump Process
            if (parameters.Contains(ObserverConstants.AppManifestDumpProcessOnError))
            {
                if (bool.TryParse(parameters[ObserverConstants.AppManifestDumpProcessOnError].Value, out bool dumpProcessOnError))
                {
                    appInfo.DumpProcessOnError = dumpProcessOnError;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestDumpProcessOnWarning))
            {
                if (bool.TryParse(parameters[ObserverConstants.AppManifestDumpProcessOnWarning].Value, out bool dumpProcessOnWarning))
                {
                    appInfo.DumpProcessOnWarning = dumpProcessOnWarning;
                    informationAdded = true;
                }
            }
            // Handles
            if (parameters.Contains(ObserverConstants.AppManifestErrorOpenFileHandles))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestErrorOpenFileHandles].Value, out int openFileHandlesErrorThreshold) && openFileHandlesErrorThreshold > 0)
                {
                    appInfo.ErrorOpenFileHandles = openFileHandlesErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestWarningOpenFileHandles))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestWarningOpenFileHandles].Value, out int openFileHandlesWarningThreshold) && openFileHandlesWarningThreshold > 0)
                {
                    appInfo.WarningOpenFileHandles = openFileHandlesWarningThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestErrorHandleCount))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestErrorHandleCount].Value, out int handleCountErrorThreshold) && handleCountErrorThreshold > 0)
                {
                    appInfo.ErrorHandleCount = handleCountErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestWarningHandleCount))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestWarningHandleCount].Value, out int handleCountWarningThreshold) && handleCountWarningThreshold > 0)
                {
                    appInfo.WarningHandleCount = handleCountWarningThreshold;
                    informationAdded = true;
                }
            }
            // Threads
            if (parameters.Contains(ObserverConstants.AppManifestErrorThreadCount))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestErrorThreadCount].Value, out int threadCountErrorThreshold) && threadCountErrorThreshold > 0)
                {
                    appInfo.ErrorThreadCount = threadCountErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestWarningThreadCount))
            {
                if (int.TryParse(parameters[ObserverConstants.AppManifestWarningThreadCount].Value, out int threadCountWarningThreshold) && threadCountWarningThreshold > 0)
                {
                    appInfo.WarningThreadCount = threadCountWarningThreshold;
                    informationAdded = true;
                }
            }
            // Private Bytes
            if (parameters.Contains(ObserverConstants.AppManifestWarningPrivateBytesMb))
            {
                if (long.TryParse(parameters[ObserverConstants.AppManifestWarningPrivateBytesMb].Value, out long privateBytesMbWarningThreshold) && privateBytesMbWarningThreshold > 0)
                {
                    appInfo.WarningPrivateBytesMb = privateBytesMbWarningThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestErrorPrivateBytesMb))
            {
                if (long.TryParse(parameters[ObserverConstants.AppManifestErrorPrivateBytesMb].Value, out long privateBytesMbErrorThreshold) && privateBytesMbErrorThreshold > 0)
                {
                    appInfo.ErrorPrivateBytesMb = privateBytesMbErrorThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestWarningPrivateBytesPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestWarningPrivateBytesPercent].Value, out double privateBytesPctWarningThreshold) && privateBytesPctWarningThreshold > 0)
                {
                    appInfo.WarningPrivateBytesPercent = privateBytesPctWarningThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestErrorPrivateBytesPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestErrorPrivateBytesPercent].Value, out double privateBytesPctErrorThreshold) && privateBytesPctErrorThreshold > 0)
                {
                    appInfo.ErrorPrivateBytesPercent = privateBytesPctErrorThreshold;
                    informationAdded = true;
                }
            }
            // RG monitoring
            if (parameters.Contains(ObserverConstants.AppManifestWarningRGMemoryLimitPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestWarningRGMemoryLimitPercent].Value, out double rgMemoryThreshold) && rgMemoryThreshold > 0)
                {
                    appInfo.WarningRGMemoryLimitPercent = rgMemoryThreshold;
                    informationAdded = true;
                }
            }
            if (parameters.Contains(ObserverConstants.AppManifestWarningRGCpuLimitPercent))
            {
                if (double.TryParse(parameters[ObserverConstants.AppManifestWarningRGCpuLimitPercent].Value, out double rgCpuThreshold) && rgCpuThreshold > 0)
                {
                    appInfo.WarningRGCpuLimitPercent = rgCpuThreshold;
                    informationAdded = true;
                }
            }
            return informationAdded;
        }

        private async Task ProcessAppManifestThresholdSettingsAsync()
        {
            for (int i = 0; i < deployedApps.Count; i++)
            {
                Token.ThrowIfCancellationRequested();

                var app = deployedApps[i];

                try
                {
                    // Make sure deployed app is not a containerized app.
                    var codepackages = await FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(NodeName, app.ApplicationName, null, null, ConfigurationSettings.AsyncTimeout, Token);

                    if (codepackages.Count == 0)
                    {
                        continue;
                    }

                    int containerHostCount = codepackages.Count(c => c.HostType == HostType.ContainerHost);

                    // Ignore containerized apps. ContainerObserver is designed for those types of services.
                    if (containerHostCount > 0)
                    {
                        continue;
                    }

                    // AppObserver does not monitor SF system services.
                    if (app.ApplicationName.OriginalString == "fabric:/System")
                    {
                        continue;
                    }
                }
                catch (FabricException fe)
                {
                    ObserverLogger.LogWarning($"Handled FabricException from GetDeployedCodePackageListAsync call for app {app.ApplicationName.OriginalString}: {fe.Message}.");
                    continue;
                }
                // Don't create a brand new entry for an existing (specified in configuration) app target/type. Just update the appConfig instance with data supplied in the application's manifest.
                // If a threshold is supplied in the application's manifest, it will override all other settings.
                if (userTargetList.Any(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName))
                {
                    var existingAppConfig = userTargetList.FindAll(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName);

                    if (existingAppConfig == null || existingAppConfig.Count == 0)
                    {
                        continue;
                    }

                    for (int j = 0; j < existingAppConfig.Count; j++)
                    {
                        PopulateAppInfoWithAppManifestThresholds(app, existingAppConfig[j]);
                    }
                }
                else
                {
                    var appConfig = new ApplicationInfo
                    {
                        TargetApp = app.ApplicationName.OriginalString
                    };

                    if (PopulateAppInfoWithAppManifestThresholds(app, appConfig))
                    {
                        userTargetList.Add(appConfig);
                    }
                }
            }
        }

        private async Task ProcessGlobalThresholdSettingsAsync()
        {
            if (userTargetList == null || userTargetList.Count == 0)
            {
                return;
            }

            if (!userTargetList.Any(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*"))
            {
                return;
            }

            ObserverLogger.LogInfo($"Started processing of global (*/all) settings from appObserver.config.json.");

            ApplicationInfo application = userTargetList.First(app => app.TargetApp?.ToLower() == "all" || app.TargetApp == "*");

            for (int i = 0; i < deployedApps.Count; i++)
            {
                Token.ThrowIfCancellationRequested();

                var app = deployedApps[i];

                try
                {
                    // Make sure deployed app is not a containerized app.
                    var codepackages = await FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(NodeName, app.ApplicationName, null, null, ConfigurationSettings.AsyncTimeout, Token);

                    if (codepackages.Count == 0)
                    {
                        continue;
                    }

                    int containerHostCount = codepackages.Count(c => c.HostType == HostType.ContainerHost);

                    // Ignore containerized apps. ContainerObserver is designed for those types of services.
                    if (containerHostCount > 0)
                    {
                        continue;
                    }

                    // AppObserver does not monitor SF system services.
                    if (app.ApplicationName.OriginalString == "fabric:/System")
                    {
                        continue;
                    }
                }
                catch (FabricException fe)
                {
                    ObserverLogger.LogWarning($"Handled FabricException from GetDeployedCodePackageListAsync call for app {app.ApplicationName.OriginalString}: {fe.Message}.");
                    continue;
                }

                // App filtering: AppExcludeList, AppIncludeList. This is only useful when you are observing All/* applications for a range of thresholds.
                if (!string.IsNullOrWhiteSpace(application.AppExcludeList) && application.AppExcludeList.Contains(app.ApplicationName.OriginalString.Replace("fabric:/", string.Empty)))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(application.AppIncludeList) && !application.AppIncludeList.Contains(app.ApplicationName.OriginalString.Replace("fabric:/", string.Empty)))
                {
                    continue;
                }

                // Don't create a brand new entry for an existing (specified in configuration) app target/type. Just update the appConfig instance with data supplied in the All/* apps config entry.
                // Note that if you supply a conflicting setting (where you specify a threshold for a specific app target config item and also in a global config item), then the target-specific setting will be used.
                // E.g., if you supply a memoryWarningLimitMb threshold for an app named fabric:/MyApp and also supply a memoryWarningLimitMb threshold for all apps ("targetApp" : "All"),
                // then the threshold specified for fabric:/MyApp will remain in place for that app target. So, target specificity overrides any global setting.
                if (userTargetList.Any(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName))
                {
                    var existingAppConfig = userTargetList.FindAll(a => a.TargetApp == app.ApplicationName.OriginalString || a.TargetAppType == app.ApplicationTypeName);

                    if (existingAppConfig == null || existingAppConfig.Count == 0)
                    {
                        continue;
                    }

                    for (int j = 0; j < existingAppConfig.Count; j++)
                    {
                        // Service include/exclude lists
                        existingAppConfig[j].ServiceExcludeList = string.IsNullOrWhiteSpace(existingAppConfig[j].ServiceExcludeList) && !string.IsNullOrWhiteSpace(application.ServiceExcludeList) ? application.ServiceExcludeList : existingAppConfig[j].ServiceExcludeList;
                        existingAppConfig[j].ServiceIncludeList = string.IsNullOrWhiteSpace(existingAppConfig[j].ServiceIncludeList) && !string.IsNullOrWhiteSpace(application.ServiceIncludeList) ? application.ServiceIncludeList : existingAppConfig[j].ServiceIncludeList;

                        // Memory - Working Set (MB)
                        existingAppConfig[j].MemoryErrorLimitMb = existingAppConfig[j].MemoryErrorLimitMb == 0 && application.MemoryErrorLimitMb > 0 ? application.MemoryErrorLimitMb : existingAppConfig[j].MemoryErrorLimitMb;
                        existingAppConfig[j].MemoryWarningLimitMb = existingAppConfig[j].MemoryWarningLimitMb == 0 && application.MemoryWarningLimitMb > 0 ? application.MemoryWarningLimitMb : existingAppConfig[j].MemoryWarningLimitMb;

                        // Memory - Working Set (Percent)
                        existingAppConfig[j].MemoryErrorLimitPercent = existingAppConfig[j].MemoryErrorLimitPercent == 0 && application.MemoryErrorLimitPercent > 0 ? application.MemoryErrorLimitPercent : existingAppConfig[j].MemoryErrorLimitPercent;
                        existingAppConfig[j].MemoryWarningLimitPercent = existingAppConfig[j].MemoryWarningLimitPercent == 0 && application.MemoryWarningLimitPercent > 0 ? application.MemoryWarningLimitPercent : existingAppConfig[j].MemoryWarningLimitPercent;

                        // Memory - Private Bytes (MB)
                        existingAppConfig[j].ErrorPrivateBytesMb = existingAppConfig[j].ErrorPrivateBytesMb == 0 && application.ErrorPrivateBytesMb > 0 ? application.ErrorPrivateBytesMb : existingAppConfig[j].ErrorPrivateBytesMb;
                        existingAppConfig[j].WarningPrivateBytesMb = existingAppConfig[j].WarningPrivateBytesMb == 0 && application.WarningPrivateBytesMb > 0 ? application.WarningPrivateBytesMb : existingAppConfig[j].WarningPrivateBytesMb;

                        // Memory - Private Bytes (Percent)
                        existingAppConfig[j].ErrorPrivateBytesPercent = existingAppConfig[j].ErrorPrivateBytesPercent == 0 && application.ErrorPrivateBytesPercent > 0 ? application.ErrorPrivateBytesPercent : existingAppConfig[j].ErrorPrivateBytesPercent;
                        existingAppConfig[j].WarningPrivateBytesPercent = existingAppConfig[j].WarningPrivateBytesPercent == 0 && application.WarningPrivateBytesPercent > 0 ? application.WarningPrivateBytesPercent : existingAppConfig[j].WarningPrivateBytesPercent;

                        // CPU
                        existingAppConfig[j].CpuErrorLimitPercent = existingAppConfig[j].CpuErrorLimitPercent == 0 && application.CpuErrorLimitPercent > 0 ? application.CpuErrorLimitPercent : existingAppConfig[j].CpuErrorLimitPercent;
                        existingAppConfig[j].CpuWarningLimitPercent = existingAppConfig[j].CpuWarningLimitPercent == 0 && application.CpuWarningLimitPercent > 0 ? application.CpuWarningLimitPercent : existingAppConfig[j].CpuWarningLimitPercent;

                        // Active TCP Ports
                        existingAppConfig[j].NetworkErrorActivePorts = existingAppConfig[j].NetworkErrorActivePorts == 0 && application.NetworkErrorActivePorts > 0 ? application.NetworkErrorActivePorts : existingAppConfig[j].NetworkErrorActivePorts;
                        existingAppConfig[j].NetworkWarningActivePorts = existingAppConfig[j].NetworkWarningActivePorts == 0 && application.NetworkWarningActivePorts > 0 ? application.NetworkWarningActivePorts : existingAppConfig[j].NetworkWarningActivePorts;

                        // Active Ephemeral Ports
                        existingAppConfig[j].NetworkErrorEphemeralPorts = existingAppConfig[j].NetworkErrorEphemeralPorts == 0 && application.NetworkErrorEphemeralPorts > 0 ? application.NetworkErrorEphemeralPorts : existingAppConfig[j].NetworkErrorEphemeralPorts;
                        existingAppConfig[j].NetworkWarningEphemeralPorts = existingAppConfig[j].NetworkWarningEphemeralPorts == 0 && application.NetworkWarningEphemeralPorts > 0 ? application.NetworkWarningEphemeralPorts : existingAppConfig[j].NetworkWarningEphemeralPorts;
                        existingAppConfig[j].NetworkErrorEphemeralPortsPercent = existingAppConfig[j].NetworkErrorEphemeralPortsPercent == 0 && application.NetworkErrorEphemeralPortsPercent > 0 ? application.NetworkErrorEphemeralPortsPercent : existingAppConfig[j].NetworkErrorEphemeralPortsPercent;
                        existingAppConfig[j].NetworkWarningEphemeralPortsPercent = existingAppConfig[j].NetworkWarningEphemeralPortsPercent == 0 && application.NetworkWarningEphemeralPortsPercent > 0 ? application.NetworkWarningEphemeralPortsPercent : existingAppConfig[j].NetworkWarningEphemeralPortsPercent;

                        // DumpOnError
                        existingAppConfig[j].DumpProcessOnError = application.DumpProcessOnError == existingAppConfig[j].DumpProcessOnError ? application.DumpProcessOnError : existingAppConfig[j].DumpProcessOnError;

                        // DumpOnWarning
                        existingAppConfig[j].DumpProcessOnWarning = application.DumpProcessOnWarning == existingAppConfig[j].DumpProcessOnWarning ? application.DumpProcessOnWarning : existingAppConfig[j].DumpProcessOnWarning;

                        // Handles \\

                        // Legacy support (naming).
                        existingAppConfig[j].ErrorOpenFileHandles = existingAppConfig[j].ErrorOpenFileHandles == 0 && application.ErrorOpenFileHandles > 0 ? application.ErrorOpenFileHandles : existingAppConfig[j].ErrorOpenFileHandles;
                        existingAppConfig[j].WarningOpenFileHandles = existingAppConfig[j].WarningOpenFileHandles == 0 && application.WarningOpenFileHandles > 0 ? application.WarningOpenFileHandles : existingAppConfig[j].WarningOpenFileHandles;

                        // Updated naming.
                        existingAppConfig[j].ErrorHandleCount = existingAppConfig[j].ErrorHandleCount == 0 && application.ErrorHandleCount > 0 ? application.ErrorHandleCount : existingAppConfig[j].ErrorHandleCount;
                        existingAppConfig[j].WarningHandleCount = existingAppConfig[j].WarningHandleCount == 0 && application.WarningHandleCount > 0 ? application.WarningHandleCount : existingAppConfig[j].WarningHandleCount;

                        // Threads
                        existingAppConfig[j].ErrorThreadCount = existingAppConfig[j].ErrorThreadCount == 0 && application.ErrorThreadCount > 0 ? application.ErrorThreadCount : existingAppConfig[j].ErrorThreadCount;
                        existingAppConfig[j].WarningThreadCount = existingAppConfig[j].WarningThreadCount == 0 && application.WarningThreadCount > 0 ? application.WarningThreadCount : existingAppConfig[j].WarningThreadCount;

                        // RGMemoryLimitPercent
                        existingAppConfig[j].WarningRGMemoryLimitPercent = existingAppConfig[j].WarningRGMemoryLimitPercent == 0 && application.WarningRGMemoryLimitPercent > 0 ? application.WarningRGMemoryLimitPercent : existingAppConfig[j].WarningRGMemoryLimitPercent;

                        // RGCpuLimitPercent
                        existingAppConfig[j].WarningRGCpuLimitPercent = existingAppConfig[j].WarningRGCpuLimitPercent == 0 && application.WarningRGCpuLimitPercent > 0 ? application.WarningRGCpuLimitPercent : existingAppConfig[j].WarningRGCpuLimitPercent;
                    }
                }
                else
                {
                    var appConfig = new ApplicationInfo
                    {
                        TargetApp = app.ApplicationName.OriginalString,
                        TargetAppType = null,
                        AppExcludeList = application.AppExcludeList,
                        AppIncludeList = application.AppIncludeList,
                        ServiceExcludeList = application.ServiceExcludeList,
                        ServiceIncludeList = application.ServiceIncludeList,
                        CpuErrorLimitPercent = application.CpuErrorLimitPercent,
                        CpuWarningLimitPercent = application.CpuWarningLimitPercent,
                        MemoryErrorLimitMb = application.MemoryErrorLimitMb,
                        MemoryWarningLimitMb = application.MemoryWarningLimitMb,
                        MemoryErrorLimitPercent = application.MemoryErrorLimitPercent,
                        MemoryWarningLimitPercent = application.MemoryWarningLimitPercent,
                        NetworkErrorActivePorts = application.NetworkErrorActivePorts,
                        NetworkWarningActivePorts = application.NetworkWarningActivePorts,
                        NetworkErrorEphemeralPorts = application.NetworkErrorEphemeralPorts,
                        NetworkWarningEphemeralPorts = application.NetworkWarningEphemeralPorts,
                        NetworkErrorEphemeralPortsPercent = application.NetworkErrorEphemeralPortsPercent,
                        NetworkWarningEphemeralPortsPercent = application.NetworkWarningEphemeralPortsPercent,
                        DumpProcessOnError = application.DumpProcessOnError,
                        DumpProcessOnWarning = application.DumpProcessOnWarning,

                        // Supported Legacy Handle property naming.
                        ErrorOpenFileHandles = application.ErrorOpenFileHandles,
                        WarningOpenFileHandles = application.WarningOpenFileHandles,

                        ErrorHandleCount = application.ErrorHandleCount,
                        WarningHandleCount = application.WarningHandleCount,
                        ErrorThreadCount = application.ErrorThreadCount,
                        WarningThreadCount = application.WarningThreadCount,
                        ErrorPrivateBytesMb = application.ErrorPrivateBytesMb,
                        WarningPrivateBytesMb = application.WarningPrivateBytesMb,
                        ErrorPrivateBytesPercent = application.ErrorPrivateBytesPercent,
                        WarningPrivateBytesPercent = application.WarningPrivateBytesPercent,
                        WarningRGMemoryLimitPercent = application.WarningRGMemoryLimitPercent,
                        WarningRGCpuLimitPercent = application.WarningRGCpuLimitPercent
                    };

                    userTargetList.Add(appConfig);
                }
            }

            // Remove the All/* config item.
            _ = userTargetList.Remove(application);
            ObserverLogger.LogInfo($"Completed processing of global (*/all) settings from appObserver.config.json.");
        }

        private void FilterTargetAppFormat()
        {
            ObserverLogger.LogInfo($"Evaluating targetApp format. Will attempt to correct malformed values, if any.");
            for (int i = 0; i < userTargetList.Count; i++)
            {
                var target = userTargetList[i];

                // We are only filtering/fixing targetApp string format.
                if (string.IsNullOrWhiteSpace(target.TargetApp))
                {
                    continue;
                }

                if (target.TargetApp == "*" || target.TargetApp.ToLower() == "all")
                {
                    continue;
                }

                try
                {
                    /* Try and fix malformed app names, if possible. */

                    if (!target.TargetApp.StartsWith("fabric:/"))
                    {
                        target.TargetApp = target.TargetApp.Insert(0, "fabric:/");
                    }

                    if (target.TargetApp.Contains("://"))
                    {
                        target.TargetApp = target.TargetApp.Replace("://", ":/");
                    }

                    if (target.TargetApp.Contains(" "))
                    {
                        target.TargetApp = target.TargetApp.Replace(" ", string.Empty);
                    }

                    if (!Uri.IsWellFormedUriString(target.TargetApp, UriKind.RelativeOrAbsolute))
                    {
                        userTargetList.RemoveAt(i);

                        string msg = $"FilterTargetAppFormat: Unsupported TargetApp value: {target.TargetApp}. " +
                                     "Value must be a valid Uri string of format \"fabric:/MyApp\" OR just \"MyApp\". Ignoring targetApp.";

                        var healthReport = new Utilities.HealthReport
                        {
                            AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                            EmitLogEvent = true,
                            HealthMessage = msg,
                            HealthReportTimeToLive = GetHealthReportTTL(),
                            Property = "UnsupportedTargetAppValue",
                            EntityType = EntityType.Application,
                            State = HealthState.Warning,
                            NodeName = NodeName,
                            Observer = ObserverConstants.AppObserverName
                        };

                        // Generate a Service Fabric Health Report.
                        HealthReporter.ReportHealthToServiceFabric(healthReport);

                        if (IsTelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportHealthAsync(
                                    "UnsupportedTargetAppValue",
                                    HealthState.Warning,
                                    msg,
                                    ObserverName,
                                    Token);
                        }

                        if (IsEtwEnabled)
                        {
                            ObserverLogger.LogEtw(
                                ObserverConstants.FabricObserverETWEventName,
                                new
                                {
                                    Property = "UnsupportedTargetAppValue",
                                    Level = "Warning",
                                    Message = msg,
                                    ObserverName
                                });
                        }

                        OperationalHealthEvents++;
                    }
                }
                catch (ArgumentException)
                {

                }
            }
            ObserverLogger.LogInfo($"Completed targetApp evaluation.");
        }

        private async Task<bool> ProcessJSONConfigAsync()
        {
            ObserverLogger.LogInfo($"Processing Json configuration.");
            if (!File.Exists(JsonConfigPath))
            {
                string message = $"Will not observe resource consumption on node {NodeName} as no configuration file has been supplied.";
                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = true,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTTL(),
                    Property = "MissingAppConfiguration",
                    EntityType = EntityType.Application,
                    State = HealthState.Warning,
                    NodeName = NodeName,
                    Observer = ObserverConstants.AppObserverName,
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "MissingAppConfiguration",
                            HealthState.Warning,
                            message,
                            ObserverName,
                            Token);
                }

                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Property = "MissingAppConfiguration",
                            Level = "Warning",
                            Message = message,
                            ObserverName
                        });
                }

                OperationalHealthEvents++;
                IsUnhealthy = true;
                return false;
            }

            bool isJson = JsonHelper.IsJson<List<ApplicationInfo>>(await File.ReadAllTextAsync(JsonConfigPath));

            if (!isJson)
            {
                string message = "AppObserver's JSON configuration file is malformed. Please fix the JSON and redeploy FabricObserver if you want AppObserver to monitor service processes.";
                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = true,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTTL(),
                    Property = "JsonValidation",
                    EntityType = EntityType.Application,
                    State = HealthState.Warning,
                    NodeName = NodeName,
                    Observer = ObserverConstants.AppObserverName,
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "JsonValidation",
                            HealthState.Warning,
                            message,
                            ObserverName,
                            Token);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Property = "JsonValidation",
                            Level = "Warning",
                            Message = message,
                            ObserverName
                        });
                }

                OperationalHealthEvents++;
                IsUnhealthy = true;
                return false;
            }

            await using Stream stream = new FileStream(JsonConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var appInfo = JsonHelper.ReadFromJsonStream<ApplicationInfo[]>(stream);
            userTargetList.AddRange(appInfo);

            // Does the configuration have any objects (targets) defined?
            if (userTargetList.Count == 0)
            {
                string message = $"Please add targets to AppObserver's JSON configuration file and redeploy FabricObserver if you want AppObserver to monitor service processes.";

                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                    EmitLogEvent = true,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTTL(),
                    Property = "Misconfiguration",
                    EntityType = EntityType.Application,
                    State = HealthState.Warning,
                    NodeName = NodeName,
                    Observer = ObserverConstants.AppObserverName,
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);
                CurrentWarningCount++;

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "Misconfiguration",
                            HealthState.Warning,
                            message,
                            ObserverName,
                            Token);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Property = "Misconfiguration",
                            Level = "Warning",
                            Message = message,
                            ObserverName
                        });
                }

                OperationalHealthEvents++;
                IsUnhealthy = true;
                return false;
            }
            ObserverLogger.LogInfo($"Completed processing Json configuration.");
            return true;
        }

        /// <summary>
        /// Set properties with Application Parameter settings supplied by user.
        /// </summary>
        private void SetPropertiesFromApplicationSettings()
        {
            ObserverLogger.LogInfo($"Setting properties from application parameters.");
            // Config path.
            if (JsonConfigPath == null)
            {
                JsonConfigPath =
                    Path.Combine(ConfigPackage.Path, GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.ConfigurationFileNameParameter));

                ObserverLogger.LogInfo($"JsonConfigPath = {JsonConfigPath}");
            }

            // Private working set monitoring.
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MonitorPrivateWorkingSetParameter), out bool monitorWsPriv))
            {
                CheckPrivateWorkingSet = monitorWsPriv;
            }
            ObserverLogger.LogInfo($"MonitorPrivateWorkingSet = {CheckPrivateWorkingSet}");

            // Monitor RG limits. Windows-only.
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MonitorResourceGovernanceLimitsParameter), out bool monitorRG))
            {
                MonitorResourceGovernanceLimits = IsWindows && monitorRG;
            }
            ObserverLogger.LogInfo($"MonitorResourceGovernanceLimits = {MonitorResourceGovernanceLimits}");

            /* Child/Descendant proc monitoring config */
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableChildProcessMonitoringParameter), out bool enableDescendantMonitoring))
            {
                EnableChildProcessMonitoring = enableDescendantMonitoring;
            }
            ObserverLogger.LogInfo($"EnableChildProcessMonitoringParameter = {EnableChildProcessMonitoring}");

            if (int.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxChildProcTelemetryDataCountParameter), out int maxChildProcs))
            {
                MaxChildProcTelemetryDataCount = maxChildProcs;
            }
            ObserverLogger.LogInfo($"MaxChildProcTelemetryDataCount = {MaxChildProcTelemetryDataCount}");

            /* dumpProcessOnError/dumpProcessOnWarning config */
            if (bool.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableProcessDumpsParameter), out bool enableDumps))
            {
                EnableProcessDumps = enableDumps;

                if (string.IsNullOrWhiteSpace(DumpsPath) && enableDumps)
                {
                    SetDumpPath();
                }
            }
            ObserverLogger.LogInfo($"EnableProcessDumps = {EnableProcessDumps}");
            
            if (Enum.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.DumpTypeParameter), out DumpType dumpType))
            {
                DumpType = dumpType;
            }
            ObserverLogger.LogInfo($"DumpType = {DumpType}");

            if (int.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxDumpsParameter), out int maxDumps))
            {
                MaxDumps = maxDumps;
            }
            ObserverLogger.LogInfo($"MaxDumps = {MaxDumps}");

            if (TimeSpan.TryParse(
                GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.MaxDumpsTimeWindowParameter), out TimeSpan dumpTimeWindow))
            {
                MaxDumpsTimeWindow = dumpTimeWindow;
            }
            ObserverLogger.LogInfo($"MaxDumpsTimeWindow = {MaxDumpsTimeWindow}");

            // KVS LVID Monitoring - Windows-only.
            if (IsWindows && bool.TryParse(
                    GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.EnableKvsLvidMonitoringParameter), out bool enableLvidMonitoring))
            {
                // Observers that monitor LVIDs should ensure the static ObserverManager.CanInstallLvidCounter is true before attempting to monitor LVID usage.
                EnableKvsLvidMonitoring = enableLvidMonitoring && ObserverManager.IsLvidCounterEnabled;
                ObserverLogger.LogInfo($"EnableKvsLvidMonitoring = {EnableKvsLvidMonitoring}");
            }
            ObserverLogger.LogInfo($"Completed setting properties from application parameters.");
        }

        private int ProcessChildProcs<T>(
                        ref ConcurrentDictionary<string, FabricResourceUsageData<T>> childFruds,
                        ref ConcurrentQueue<ChildProcessTelemetryData> childProcessTelemetryDataList,
                        ReplicaOrInstanceMonitoringInfo repOrInst,
                        ApplicationInfo appInfo,
                        ref FabricResourceUsageData<T> parentFrud,
                        CancellationToken token) where T : struct
        {
            token.ThrowIfCancellationRequested();

            if (childProcessTelemetryDataList == null)
            {
                return 0;
            }

            ObserverLogger.LogInfo($"Started ProcessChildProcs.");
            int childProcCount = 0;

            try
            {
                var (childProcInfo, Sum) = TupleProcessChildFruds(ref childFruds, repOrInst, appInfo, token);

                if (childProcInfo == null || childProcInfo.ChildProcessInfo?.Count == 0)
                {
                    ObserverLogger.LogInfo($"ProcessChildProcs: ChildProcesssInfo collection is null or empty. Exiting.");
                    return 0;
                }

                childProcCount = childProcInfo.ChildProcessCount;
                string metric = parentFrud.Property;
                var parentDataAvg = parentFrud.AverageDataValue;
                double sumAllValues = Sum + parentDataAvg;
                childProcInfo.Metric = metric;
                childProcInfo.Value = sumAllValues;
                childProcessTelemetryDataList.Enqueue(childProcInfo);
                parentFrud.ClearData();
                parentFrud.AddData((T)Convert.ChangeType(sumAllValues, typeof(T)));
            }
            catch (Exception e) when (e is not (OperationCanceledException or TaskCanceledException or OutOfMemoryException))
            {
                ObserverLogger.LogWarning($"ProcessChildProcs - Failure processing descendants:{Environment.NewLine}{e}");
            }
            ObserverLogger.LogInfo($"Completed ProcessChildProcs.");
            return childProcCount;
        }

        private (ChildProcessTelemetryData childProcInfo, double Sum) TupleProcessChildFruds<T>(
                    ref ConcurrentDictionary<string, FabricResourceUsageData<T>> childFruds,
                    ReplicaOrInstanceMonitoringInfo repOrInst,
                    ApplicationInfo app,
                    CancellationToken token) where T : struct
        {
            ObserverLogger.LogInfo($"Started TupleProcessChildFruds.");
            var childProcs = repOrInst.ChildProcesses;

            if (!EnableChildProcessMonitoring || childProcs == null || token.IsCancellationRequested)
            {
                return (null, 0);
            }

            if (childProcs.Count == 0)
            {
                return (null, 0);
            }   

            int parentPid = (int)repOrInst.HostProcessId;

            // Make sure the parent process is still the droid we're looking for.
            if (!EnsureProcess(repOrInst.HostProcessName, parentPid, repOrInst.HostProcessStartTime))
            {
                ObserverLogger.LogInfo($"TupleProcessChildFruds: EnsureProcess for parent {repOrInst.HostProcessId} ({parentPid}) returned false. Exiting function.");
                return (null, 0);
            }

            double sumValues = 0;
            string metric = string.Empty;

            var childProcessInfoData = new ChildProcessTelemetryData
            {
                ApplicationName = repOrInst.ApplicationName.OriginalString,
                ServiceName = repOrInst.ServiceName.OriginalString,
                NodeName = NodeName,
                ProcessId = parentPid,
                ProcessName = repOrInst.HostProcessName,
                ProcessStartTime = repOrInst.HostProcessStartTime.ToString("o"),
                PartitionId = repOrInst.PartitionId.ToString(),
                ReplicaId = repOrInst.ReplicaOrInstanceId,
                ChildProcessCount = childProcs.Count,
                ChildProcessInfo = new List<ChildProcessInfo>()
            };

            string appNameOrType = GetAppNameOrType(repOrInst);
            string parentKey = $"{appNameOrType}:{repOrInst.HostProcessName}{parentPid}";

            for (int i = 0; i < childProcs.Count; ++i)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    int childPid = childProcs[i].Pid;
                    string childProcName = childProcs[i].procName;
                    string childFrudKey = $"{parentKey}:{childProcName}{childPid}";

                    // This means that the original child process detected at initialization time (SetInstanceOrReplicaMonitoringList)
                    // was no longer running by the time ComputeResourceUsage function ran. Ignore.
                    if (!childFruds.ContainsKey(childFrudKey))
                    {
                        ObserverLogger.LogInfo($"TupleProcessChildFruds: childFruds dictionary does not contain key {childFrudKey}. Ignoring.");
                        continue;
                    }

                    DateTime startTime = childProcs[i].ProcessStartTime;

                    // Is the process the one we think it is?
                    if (!EnsureProcess(childProcName, childPid, startTime))
                    {
                        ObserverLogger.LogInfo($"TupleProcessChildFruds: Child process {childProcName} with pid {childPid} is no longer running. Ignoring.");
                        continue;
                    }

                    // Add child proc resource usage to sum for metric.
                    FabricResourceUsageData<T> childFrud = childFruds[childFrudKey];
                    metric = childFrud.Property;
                    double value = childFrud.AverageDataValue;
                    sumValues += value;

                    // This data only matters if the user wants it for analysis.
                    if (IsEtwEnabled || IsTelemetryEnabled)
                    {
                        var childProcInfo = new ChildProcessInfo
                        {
                            ProcessId = childPid,
                            ProcessName = childProcName,
                            ProcessStartTime = startTime.ToString("o"),
                            Value = value
                        };
                        childProcessInfoData.ChildProcessInfo.Add(childProcInfo);
                    }

                    // Windows process dump support for descendant processes \\

                    if (IsWindows && EnableProcessDumps && (app.DumpProcessOnError || app.DumpProcessOnWarning))
                    {
                        string prop = childFrud.Property;
                        bool dump = false;

                        switch (prop)
                        {
                            case ErrorWarningProperty.CpuTime:
                                // Test error/warning threshold breach for supplied metric.
                                if (childFrud.IsUnhealthy(app.CpuErrorLimitPercent) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.CpuWarningLimitPercent)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.MemoryConsumptionMb:
                                if (childFrud.IsUnhealthy(app.MemoryErrorLimitMb) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.MemoryWarningLimitMb)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.MemoryConsumptionPercentage:
                                if (childFrud.IsUnhealthy(app.MemoryErrorLimitPercent) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.MemoryWarningLimitPercent)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.PrivateBytesMb:
                                if (childFrud.IsUnhealthy(app.ErrorPrivateBytesMb) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.WarningPrivateBytesMb)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.PrivateBytesPercent:
                                if (childFrud.IsUnhealthy(app.ErrorPrivateBytesPercent) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.WarningPrivateBytesPercent)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.ActiveTcpPorts:
                                if (childFrud.IsUnhealthy(app.NetworkErrorActivePorts) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.NetworkWarningActivePorts)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.ActiveEphemeralPorts:
                                if (childFrud.IsUnhealthy(app.NetworkErrorEphemeralPorts) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.NetworkWarningEphemeralPorts)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.ActiveEphemeralPortsPercentage:
                                if (childFrud.IsUnhealthy(app.NetworkErrorEphemeralPortsPercent) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.NetworkWarningEphemeralPortsPercent)))
                                {
                                    dump = true;
                                }
                                break;

                            // Legacy Handle metric name.
                            case ErrorWarningProperty.AllocatedFileHandles:
                                if (childFrud.IsUnhealthy(app.ErrorOpenFileHandles) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.WarningOpenFileHandles)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.HandleCount:
                                if (childFrud.IsUnhealthy(app.ErrorHandleCount) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.WarningHandleCount)))
                                {
                                    dump = true;
                                }
                                break;

                            case ErrorWarningProperty.ThreadCount:
                                if (childFrud.IsUnhealthy(app.ErrorThreadCount) || (app.DumpProcessOnWarning && childFrud.IsUnhealthy(app.WarningThreadCount)))
                                {
                                    dump = true;
                                }
                                break;
                        }

                        lock (lockObj)
                        {
                            if (dump)
                            {
                                ObserverLogger.LogInfo($"Starting dump code path for {repOrInst.HostProcessName}/{childProcName}/{childPid}.");

                                // Make sure the child process is still the one we're looking for.
                                if (EnsureProcess(childProcName, childPid, startTime))
                                {
                                    // DumpWindowsServiceProcess logs failure. Log success here with parent/child info.
                                    if (DumpWindowsServiceProcess(childPid, childProcName, prop))
                                    {
                                        ObserverLogger.LogInfo($"Successfully dumped {repOrInst.HostProcessName}/{childProcName}/{childPid}.");
                                    }
                                }
                                else
                                {
                                    ObserverLogger.LogInfo($"Will not dump child process: {childProcName}({childPid}) is no longer running.");
                                }
                                ObserverLogger.LogInfo($"Completed dump code path for {repOrInst.HostProcessName}/{childProcName}/{childPid}.");
                            }
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException and not TaskCanceledException and not OutOfMemoryException)
                {
                    ObserverLogger.LogWarning($"Failure processing descendant information for parent process with key {parentKey}: {e.Message}");
                    continue;
                }
            }

            ObserverLogger.LogInfo($"TupleProcessChildFruds: ChildProcessInfo.Count = {childProcessInfoData.ChildProcessInfo.Count}.");

            // Reset child proc count to the actual number of processed child proc info elements.
            childProcessInfoData.ChildProcessCount = childProcessInfoData.ChildProcessInfo.Count;
            
            try
            {
                // Order List<ChildProcessInfo> by Value descending.
                childProcessInfoData.ChildProcessInfo = childProcessInfoData.ChildProcessInfo.OrderByDescending(v => v.Value).ToList();

                // Cap size of List<ChildProcessInfo> to MaxChildProcTelemetryDataCount.
                if (childProcessInfoData.ChildProcessInfo.Count > MaxChildProcTelemetryDataCount)
                {
                    childProcessInfoData.ChildProcessInfo = childProcessInfoData.ChildProcessInfo.Take(MaxChildProcTelemetryDataCount).ToList();
                }
                ObserverLogger.LogInfo($"Successfully completed TupleProcessChildFruds...");
                return (childProcessInfoData, sumValues);
            }
            catch (ArgumentException ae)
            {
                ObserverLogger.LogWarning($"TupleProcessChildFruds - Failure processing descendants:{Environment.NewLine}{ae.Message}");
            }

            ObserverLogger.LogInfo($"Completed TupleProcessChildFruds with Warning.");
            return (null, 0);
        }

        private static string GetAppNameOrType(ReplicaOrInstanceMonitoringInfo repOrInst)
        {
            // targetType specified as TargetAppType name, which means monitor all apps of specified type.
            var appNameOrType = !string.IsNullOrWhiteSpace(repOrInst.ApplicationTypeName) ? repOrInst.ApplicationTypeName : repOrInst.ApplicationName.OriginalString.Replace("fabric:/", string.Empty);
            return appNameOrType;
        }

        private void SetDumpPath()
        {
            try
            {
                DumpsPath = Path.Combine(ObserverLogger.LogFolderBasePath, ObserverName, ObserverConstants.ProcessDumpFolderNameParameter);
                Directory.CreateDirectory(DumpsPath);
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                ObserverLogger.LogWarning($"Unable to create dump directory {DumpsPath}.");
                return;
            }
        }

        public Task<ParallelLoopResult> MonitorDeployedAppsAsync(CancellationToken token)
        {
            Stopwatch execTimer = Stopwatch.StartNew();
            ObserverLogger.LogInfo("Starting MonitorDeployedAppsAsync.");
            int capacity = ReplicaOrInstanceList.Count;
            var exceptions = new ConcurrentQueue<Exception>();
            AllAppCpuData ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppMemDataMb ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
            AllAppMemDataPercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppTotalActivePortsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppEphemeralPortsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();
            AllAppEphemeralPortsDataPercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
            AllAppHandlesData ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
            AllAppThreadsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<int>>();

            // Windows only.
            if (IsWindows)
            {
                AllAppPrivateBytesDataMb ??= new ConcurrentDictionary<string, FabricResourceUsageData<float>>();
                AllAppPrivateBytesDataPercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
                AllAppRGMemoryUsagePercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
                AllAppRGCpuUsagePercent ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();

                // LVID usage monitoring for Stateful KVS-based services (e.g., Actors).
                if (EnableKvsLvidMonitoring)
                {
                    AllAppKvsLvidsData ??= new ConcurrentDictionary<string, FabricResourceUsageData<double>>();
                }
            }

            // Perf
            ConcurrentQueue<int> threadData = new();

            ParallelLoopResult result = Parallel.For(0, ReplicaOrInstanceList.Count, parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    if (parallelOptions.MaxDegreeOfParallelism == -1 || parallelOptions.MaxDegreeOfParallelism > 1)
                    {
                        state.Stop();
                    }
                    else
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }

                // Perf
                threadData.Enqueue(Environment.CurrentManagedThreadId);

                var repOrInst = ReplicaOrInstanceList[i];
                var timer = new Stopwatch();
                int parentPid = (int)repOrInst.HostProcessId;
                string parentProcName = repOrInst.HostProcessName;
                bool checkCpu = false;
                bool checkMemMb = false;
                bool checkMemPct = false;
                bool checkMemPrivateBytesPct = false;
                bool checkMemPrivateBytes = false;
                bool checkAllPorts = false;
                bool checkEphemeralPorts = false;
                bool checkPercentageEphemeralPorts = false;
                bool checkHandles = false;
                bool checkThreads = false;
                bool checkLvids = false;
                var application = deployedTargetList?.First(
                                    app => app?.TargetApp?.ToLower() == repOrInst.ApplicationName?.OriginalString.ToLower() ||
                                    !string.IsNullOrWhiteSpace(app?.TargetAppType) &&
                                    app.TargetAppType?.ToLower() == repOrInst.ApplicationTypeName?.ToLower());


                // For hosted container apps, the host service is Fabric. AppObserver can't monitor these types of services.
                // Please use ContainerObserver for SF container app service monitoring.
                if (string.IsNullOrWhiteSpace(parentProcName) || parentProcName == "Fabric")
                {
                    return;
                }

                double rgMemoryPercentThreshold = 0.0;
                double rgCpuPercentThreshold = 0.0;

                ConcurrentDictionary<int, (string ProcName, DateTime ProcessStartTime)> procs;

                if (application?.TargetApp == null && application?.TargetAppType == null)
                {
                    // return in a parallel loop is equivalent to a standard loop's continue.
                    return;
                }

                // Make sure this is still the process we think it is.
                if (!EnsureProcess(parentProcName, parentPid, repOrInst.HostProcessStartTime))
                {
                    return;
                }

                try
                {
                    /* In order to provide accurate resource usage of an SF service process we need to also account for
                       any processes that the service process (parent) created/spawned (children). */

                    procs = new ConcurrentDictionary<int, (string ProcName, DateTime ProcessStartTime)>();

                    // Add parent to the process tree list since we want to monitor all processes in the family. If there are no child processes,
                    // then only the parent process will be in this dictionary.
                    _ = procs.TryAdd(parentPid, (parentProcName, repOrInst.HostProcessStartTime));

                    if (repOrInst.ChildProcesses != null && repOrInst.ChildProcesses.Count > 0)
                    {
                        for (int k = 0; k < repOrInst.ChildProcesses.Count; ++k)
                        {
                            if (token.IsCancellationRequested)
                            {
                                break;
                            }

                            // Make sure the child process still exists. Descendant processes are often ephemeral.
                            if (!EnsureProcess(repOrInst.ChildProcesses[k].procName, repOrInst.ChildProcesses[k].Pid, repOrInst.ChildProcesses[k].ProcessStartTime))
                            {
                                continue;
                            }

                            _ = procs.TryAdd(repOrInst.ChildProcesses[k].Pid, (repOrInst.ChildProcesses[k].procName, repOrInst.ChildProcesses[k].ProcessStartTime));
                        }
                    }

                    string appNameOrType = GetAppNameOrType(repOrInst);
                    string id = $"{appNameOrType}:{parentProcName}{parentPid}";

                    if (UseCircularBuffer)
                    {
                        capacity = DataCapacity > 0 ? DataCapacity : 5;
                    }
                    else if (CpuMonitorDuration > TimeSpan.Zero)
                    {
                        capacity = CpuMonitorDuration.Seconds * 4;
                    }

                    // CPU
                    if (application.CpuErrorLimitPercent > 0 || application.CpuWarningLimitPercent > 0)
                    {
                        _ = AllAppCpuData.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.CpuTime, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppCpuData != null && AllAppCpuData.ContainsKey(id))
                    {
                        AllAppCpuData[id].ClearData();
                        checkCpu = true;
                    }

                    // Memory - Working Set MB.
                    if (application.MemoryErrorLimitMb > 0 || application.MemoryWarningLimitMb > 0)
                    {
                        _ = AllAppMemDataMb.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.MemoryConsumptionMb, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppMemDataMb != null && AllAppMemDataMb.ContainsKey(id))
                    {
                        AllAppMemDataMb[id].ClearData();
                        checkMemMb = true;
                    }

                    // Memory - Working Set Percent.
                    if (application.MemoryErrorLimitPercent > 0 || application.MemoryWarningLimitPercent > 0)
                    {
                        _ = AllAppMemDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.MemoryConsumptionPercentage, id, capacity, UseCircularBuffer, EnableConcurrentMonitoring));
                    }

                    if (AllAppMemDataPercent != null && AllAppMemDataPercent.ContainsKey(id))
                    {
                        AllAppMemDataPercent[id].ClearData();
                        checkMemPct = true;
                    }

                    // Memory - Private Bytes MB. Windows-only.
                    if (IsWindows && application.ErrorPrivateBytesMb > 0 || application.WarningPrivateBytesMb > 0)
                    {
                        _ = AllAppPrivateBytesDataMb.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.PrivateBytesMb, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (IsWindows && AllAppPrivateBytesDataMb != null && AllAppPrivateBytesDataMb.ContainsKey(id))
                    {
                        AllAppPrivateBytesDataMb[id].ClearData();
                        checkMemPrivateBytes = true;
                    }

                    // Memory - Private Bytes (Percent). Windows-only.
                    if (IsWindows && application.ErrorPrivateBytesPercent > 0 || application.WarningPrivateBytesPercent > 0)
                    {
                        _ = AllAppPrivateBytesDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.PrivateBytesPercent, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (IsWindows && AllAppPrivateBytesDataPercent != null && AllAppPrivateBytesDataPercent.ContainsKey(id))
                    {
                        AllAppPrivateBytesDataPercent[id].ClearData();
                        checkMemPrivateBytesPct = true;
                    }

                    // Memory - RG monitoring. Windows-only for now.
                    if (MonitorResourceGovernanceLimits && repOrInst.RGMemoryEnabled && repOrInst.RGAppliedMemoryLimitMb > 0)
                    {
                        _ = AllAppRGMemoryUsagePercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.RGMemoryUsagePercent, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (IsWindows && AllAppRGMemoryUsagePercent != null && AllAppRGMemoryUsagePercent.ContainsKey(id))
                    {
                        rgMemoryPercentThreshold = application.WarningRGMemoryLimitPercent;

                        if (rgMemoryPercentThreshold > 0)
                        {
                            if (rgMemoryPercentThreshold < 1)
                            {
                                rgMemoryPercentThreshold = application.WarningRGMemoryLimitPercent * 100.0; // decimal to double.
                            }
                        }
                        else
                        {
                            rgMemoryPercentThreshold = MaxRGMemoryInUsePercent; // Default: 90%.
                        }

                        AllAppRGMemoryUsagePercent[id].ClearData();
                    }

                    // CPU - RG monitoring. Windows-only for now.
                    if (MonitorResourceGovernanceLimits && repOrInst.RGCpuEnabled && repOrInst.RGAppliedCpuLimitCores > 0)
                    {
                        _ = AllAppRGCpuUsagePercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.RGCpuUsagePercent, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (IsWindows && AllAppRGCpuUsagePercent != null && AllAppRGCpuUsagePercent.ContainsKey(id))
                    {
                        rgCpuPercentThreshold = application.WarningRGCpuLimitPercent;

                        if (rgCpuPercentThreshold > 0)
                        {
                            if (rgCpuPercentThreshold < 1)
                            {
                                rgCpuPercentThreshold = application.WarningRGCpuLimitPercent * 100.0; // decimal to double.
                            }
                        }
                        else
                        {
                            rgCpuPercentThreshold = MaxRGCpuInUsePercent; // Default: 90%.
                        }
                    }

                    // Active TCP Ports
                    if (application.NetworkErrorActivePorts > 0 || application.NetworkWarningActivePorts > 0)
                    {
                        _ = AllAppTotalActivePortsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.ActiveTcpPorts, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppTotalActivePortsData != null && AllAppTotalActivePortsData.ContainsKey(id))
                    {
                        AllAppTotalActivePortsData[id].ClearData();
                        checkAllPorts = true;
                    }

                    // Ephemeral TCP Ports - Total number.
                    if (application.NetworkErrorEphemeralPorts > 0 || application.NetworkWarningEphemeralPorts > 0)
                    {
                        _ = AllAppEphemeralPortsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.ActiveEphemeralPorts, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppEphemeralPortsData != null && AllAppEphemeralPortsData.ContainsKey(id))
                    {
                        AllAppEphemeralPortsData[id].ClearData();
                        checkEphemeralPorts = true;
                    }

                    // Ephemeral TCP Ports - Percentage in use of total available.
                    if (application.NetworkErrorEphemeralPortsPercent > 0 || application.NetworkWarningEphemeralPortsPercent > 0)
                    {
                        _ = AllAppEphemeralPortsDataPercent.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.ActiveEphemeralPortsPercentage, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppEphemeralPortsDataPercent != null && AllAppEphemeralPortsDataPercent.ContainsKey(id))
                    {
                        AllAppEphemeralPortsDataPercent[id].ClearData();
                        checkPercentageEphemeralPorts = true;
                    }

                    // Handles
                    if (application.ErrorOpenFileHandles > 0 || application.WarningOpenFileHandles > 0
                        || application.ErrorHandleCount > 0 || application.WarningHandleCount > 0)
                    {
                        _ = AllAppHandlesData.TryAdd(id, new FabricResourceUsageData<float>(ErrorWarningProperty.HandleCount, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppHandlesData != null && AllAppHandlesData.ContainsKey(id))
                    {
                        AllAppHandlesData[id].ClearData();
                        checkHandles = true;
                    }

                    // Threads
                    if (application.ErrorThreadCount > 0 || application.WarningThreadCount > 0)
                    {
                        _ = AllAppThreadsData.TryAdd(id, new FabricResourceUsageData<int>(ErrorWarningProperty.ThreadCount, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppThreadsData != null && AllAppThreadsData.ContainsKey(id))
                    {
                        AllAppThreadsData[id].ClearData();
                        checkThreads = true;
                    }

                    // KVS LVIDs percent (Windows-only)
                    // Note: This is a non-configurable Windows monitor and will be removed when SF ships with the latest version of ESE.
                    if (EnableKvsLvidMonitoring && AllAppKvsLvidsData != null && repOrInst.ServiceKind == ServiceKind.Stateful)
                    {
                        _ = AllAppKvsLvidsData.TryAdd(id, new FabricResourceUsageData<double>(ErrorWarningProperty.KvsLvidsPercent, id, 1, false, EnableConcurrentMonitoring));
                    }

                    if (AllAppKvsLvidsData != null && AllAppKvsLvidsData.ContainsKey(id))
                    {
                        AllAppKvsLvidsData[id].ClearData();
                        checkLvids = true;
                    }

                    // For Windows: Regardless of user setting, if there are more than 50 service processes with the same name, then FO will employ Win32 API (fast, lightweight).
                    bool usePerfCounter = IsWindows && CheckPrivateWorkingSet && ReplicaOrInstanceList.Count(p => p.HostProcessName == parentProcName) < MaxSameNamedProcesses;

                    // Compute the resource usage of the family of processes (each proc in the family tree). This is also parallelized and has real perf benefits when 
                    // a service process has mulitple descendants.
                    ComputeResourceUsage(
                        capacity,
                        parentPid,
                        checkCpu,
                        checkMemMb,
                        checkMemPct,
                        checkMemPrivateBytesPct,
                        checkMemPrivateBytes,
                        checkAllPorts,
                        checkEphemeralPorts,
                        checkPercentageEphemeralPorts,
                        checkHandles,
                        checkThreads,
                        checkLvids,
                        procs,
                        id,
                        repOrInst,
                        usePerfCounter,
                        rgMemoryPercentThreshold,
                        rgCpuPercentThreshold,
                        token);
                }
                catch (Exception e)
                {
                    exceptions.Enqueue(e);
                }
            });

            // Perf 
            string threads = string.Empty;
            int threadcount = threadData.Distinct().Count();
            threads = $", Threads: {threadcount}";
            threadData.Clear();
            threadData = null;
            execTimer.Stop();
            ObserverLogger.LogInfo($"MonitorDeployedAppsAsync Execution time: {execTimer.Elapsed}{threads}");
            execTimer = null;

            if (!exceptions.IsEmpty)
            {
                ObserverLogger.LogInfo("Completed MonitorDeployedAppsAsync with one or more exceptions inside task. Throwing inner exceptions aggregate.");
                throw new AggregateException(exceptions);
            }

            ObserverLogger.LogInfo("Completed MonitorDeployedAppsAsync.");
            return Task.FromResult(result);
        }

        private void ComputeResourceUsage(
                        int capacity,
                        int parentPid,
                        bool checkCpu,
                        bool checkMemMb,
                        bool checkMemPct,
                        bool checkMemPrivateBytesPct,
                        bool checkMemPrivateBytesMb,
                        bool checkAllPorts,
                        bool checkEphemeralPorts,
                        bool checkPercentageEphemeralPorts,
                        bool checkHandles,
                        bool checkThreads,
                        bool checkLvids,
                        ConcurrentDictionary<int, (string ProcName, DateTime ProcessStartTime)> processDictionary,
                        string id,
                        ReplicaOrInstanceMonitoringInfo repOrInst,
                        bool usePerfCounter,
                        double rgMemoryPercentThreshold,
                        double rgCpuPercentThreshold,
                        CancellationToken token)
        {
            _ = Parallel.For(0, processDictionary.Count, parallelOptions, (i, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    if (parallelOptions.MaxDegreeOfParallelism == -1 || parallelOptions.MaxDegreeOfParallelism > 1)
                    {
                        state.Stop();
                    }
                    else
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }

                var entry = processDictionary.ElementAt(i);
                string procName = entry.Value.ProcName;
                int procId = entry.Key;

                // Make sure this is still the process we're looking for.
                if (!EnsureProcess(procName, procId, entry.Value.ProcessStartTime))
                {
                    return;
                }

                // Handles/FDs
                if (checkHandles)
                {
                    float handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(procId, IsWindows ? null : CodePackage?.Path);

                    if (handles > 0F)
                    {
                        if (procId == parentPid)
                        {
                            AllAppHandlesData[id].AddData(handles);
                        }
                        else
                        {
                            _ = AllAppHandlesData.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<float>(
                                        ErrorWarningProperty.HandleCount,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        false,
                                        EnableConcurrentMonitoring));

                            AllAppHandlesData[$"{id}:{procName}{procId}"].AddData(handles);
                        }
                    }
                }

                // Threads
                if (checkThreads)
                {
                    int threads = 0;

                    if (!IsWindows)
                    {
                        // Lightweight on Linux..
                        threads = ProcessInfoProvider.GetProcessThreadCount(procId);
                    }
                    else
                    {
                        // Much faster, less memory.. employs Win32's PSSCaptureSnapshot/PSSQuerySnapshot.
                        threads = NativeMethods.GetProcessThreadCount(procId);
                    }

                    if (threads > 0)
                    {
                        // Parent process (the service process).
                        if (procId == parentPid)
                        {
                            AllAppThreadsData[id].AddData(threads);
                        }
                        else // Child proc spawned by the parent service process.
                        {
                            _ = AllAppThreadsData.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<int>(
                                        ErrorWarningProperty.ThreadCount,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        false,
                                        EnableConcurrentMonitoring));

                            AllAppThreadsData[$"{id}:{procName}{procId}"].AddData(threads);
                        }
                    }
                }

                // Total TCP ports usage
                if (checkAllPorts)
                {
                    // Parent process (the service process).
                    if (procId == parentPid)
                    {
                        AllAppTotalActivePortsData[id].AddData(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, CodePackage?.Path));
                    }
                    else // Child proc spawned by the parent service process.
                    {
                        _ = AllAppTotalActivePortsData.TryAdd(
                            $"{id}:{procName}{procId}",
                            new FabricResourceUsageData<int>(
                                ErrorWarningProperty.ActiveTcpPorts,
                                $"{id}:{procName}{procId}",
                                capacity,
                                false,
                                EnableConcurrentMonitoring));

                        AllAppTotalActivePortsData[$"{id}:{procName}{procId}"].AddData(OSInfoProvider.Instance.GetActiveTcpPortCount(procId, CodePackage?.Path));
                    }
                }

                // Ephemeral TCP ports usage - Raw count.
                if (checkEphemeralPorts)
                {
                    if (procId == parentPid)
                    {
                        AllAppEphemeralPortsData[id].AddData(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, CodePackage?.Path));
                    }
                    else
                    {
                        _ = AllAppEphemeralPortsData.TryAdd(
                                $"{id}:{procName}{procId}",
                                new FabricResourceUsageData<int>(
                                    ErrorWarningProperty.ActiveEphemeralPorts,
                                    $"{id}:{procName}{procId}",
                                    capacity,
                                    false,
                                    EnableConcurrentMonitoring));

                        AllAppEphemeralPortsData[$"{id}:{procName}{procId}"].AddData(OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, CodePackage?.Path));
                    }
                }

                // Ephemeral TCP ports usage - Percentage.
                if (checkPercentageEphemeralPorts)
                {
                    double usedPct = OSInfoProvider.Instance.GetActiveEphemeralPortCountPercentage(procId, CodePackage?.Path);

                    if (procId == parentPid)
                    {
                        AllAppEphemeralPortsDataPercent[id].AddData(usedPct);
                    }
                    else
                    {
                        _ = AllAppEphemeralPortsDataPercent.TryAdd(
                                $"{id}:{procName}{procId}",
                                new FabricResourceUsageData<double>(
                                    ErrorWarningProperty.ActiveEphemeralPortsPercentage,
                                    $"{id}:{procName}{procId}",
                                    capacity,
                                    false,
                                    EnableConcurrentMonitoring));

                        AllAppEphemeralPortsDataPercent[$"{id}:{procName}{procId}"].AddData(usedPct);
                    }
                }

                // KVS LVIDs
                if (IsWindows && checkLvids && repOrInst.HostProcessId == procId && repOrInst.ServiceKind == ServiceKind.Stateful)
                {
                    var lvidPct = ProcessInfoProvider.Instance.GetProcessKvsLvidsUsagePercentage(procName, Token, procId);

                    // ProcessGetCurrentKvsLvidsUsedPercentage internally handles exceptions and will always return -1 when it fails.
                    if (lvidPct > -1)
                    {
                        if (procId == parentPid)
                        {
                            AllAppKvsLvidsData[id].AddData(lvidPct);
                        }
                        else
                        {
                            _ = AllAppKvsLvidsData.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<double>(
                                        ErrorWarningProperty.KvsLvidsPercent,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                            AllAppKvsLvidsData[$"{id}:{procName}{procId}"].AddData(lvidPct);
                        }
                    }
                }

                // Memory \\

                // Private Bytes (MB) - Windows only.
                if (IsWindows && checkMemPrivateBytesMb)
                {
                    float memPb = ProcessInfoProvider.Instance.GetProcessPrivateBytesMb(procId);

                    if (procId == parentPid)
                    {
                        AllAppPrivateBytesDataMb[id].AddData(memPb);
                    }
                    else
                    {
                        _ = AllAppPrivateBytesDataMb.TryAdd(
                                $"{id}:{procName}{procId}",
                                new FabricResourceUsageData<float>(
                                        ErrorWarningProperty.PrivateBytesMb,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                        AllAppPrivateBytesDataMb[$"{id}:{procName}{procId}"].AddData(memPb);
                    }
                }

                // Private Bytes (Percent) - Windows only.
                if (IsWindows && checkMemPrivateBytesPct)
                {
                    float processPrivateBytesMb = ProcessInfoProvider.Instance.GetProcessPrivateBytesMb(procId);
                    var (CommitLimitGb, _) = OSInfoProvider.Instance.TupleGetSystemCommittedMemoryInfo();

                    // If this is not the case, then there is a systemic issue. The related function will have already locally logged/emitted etw with the error info.
                    if (CommitLimitGb > 0)
                    {
                        double usedPct = (double)(processPrivateBytesMb * 100) / (CommitLimitGb * 1024);

                        // parent process
                        if (procId == parentPid)
                        {
                            AllAppPrivateBytesDataPercent[id].AddData(Math.Round(usedPct, 4));
                        }
                        else // child process
                        {
                            _ = AllAppPrivateBytesDataPercent.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<double>(
                                        ErrorWarningProperty.PrivateBytesPercent,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                            AllAppPrivateBytesDataPercent[$"{id}:{procName}{procId}"].AddData(Math.Round(usedPct, 4));
                        }
                    }
                }

                // RG Memory (Percent) Monitoring - Windows-only (MonitorResourceGovernanceLimits will always be false for Linux for the time being).
                if (MonitorResourceGovernanceLimits && repOrInst.RGMemoryEnabled && rgMemoryPercentThreshold > 0)
                {
                    float memPb = ProcessInfoProvider.Instance.GetProcessPrivateBytesMb(procId);

                    // If this is not the case, then there is a systemic issue. The related function will have already locally logged/emitted etw with the error info.
                    if (memPb > 0)
                    {
                        if (procId == parentPid)
                        {
                            if (repOrInst.RGAppliedMemoryLimitMb > 0)
                            {
                                double pct = ((double)memPb / repOrInst.RGAppliedMemoryLimitMb) * 100;
                                AllAppRGMemoryUsagePercent[id].AddData(pct);
                            }
                        }
                        else
                        {
                            if (repOrInst.RGAppliedMemoryLimitMb > 0)
                            {
                                _ = AllAppRGMemoryUsagePercent.TryAdd(
                                        $"{id}:{procName}{procId}",
                                        new FabricResourceUsageData<double>(
                                                ErrorWarningProperty.RGMemoryUsagePercent,
                                                $"{id}:{procName}{procId}",
                                                capacity,
                                                UseCircularBuffer,
                                                EnableConcurrentMonitoring));

                                double pct = ((double)memPb / repOrInst.RGAppliedMemoryLimitMb) * 100;
                                AllAppRGMemoryUsagePercent[$"{id}:{procName}{procId}"].AddData(pct);
                            }
                        }
                    }
                }

                // Working Set.
                if (checkMemMb)
                {
                    if (procId == parentPid)
                    {
                        AllAppMemDataMb[id].AddData(ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, procName, Token, usePerfCounter));
                    }
                    else
                    {
                        _ = AllAppMemDataMb.TryAdd(
                                $"{id}:{procName}{procId}",
                                new FabricResourceUsageData<float>(
                                        ErrorWarningProperty.MemoryConsumptionMb,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                        AllAppMemDataMb[$"{id}:{procName}{procId}"].AddData(ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, procName, Token, usePerfCounter));
                    }
                }

                // Working Set (Percent).
                if (checkMemPct)
                {
                    float processMemMb = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(procId, procName, Token, usePerfCounter);
                    var (TotalMemoryGb, _, _) = OSInfoProvider.Instance.TupleGetSystemPhysicalMemoryInfo();

                    if (TotalMemoryGb > 0 && processMemMb > 0)
                    {
                        double usedPct = (float)(processMemMb * 100) / (TotalMemoryGb * 1024);

                        if (procId == parentPid)
                        {
                            AllAppMemDataPercent[id].AddData(Math.Round(usedPct, 2));
                        }
                        else
                        {
                            _ = AllAppMemDataPercent.TryAdd(
                                    $"{id}:{procName}{procId}",
                                    new FabricResourceUsageData<double>(
                                        ErrorWarningProperty.MemoryConsumptionPercentage,
                                        $"{id}:{procName}{procId}",
                                        capacity,
                                        UseCircularBuffer,
                                        EnableConcurrentMonitoring));

                            AllAppMemDataPercent[$"{id}:{procName}{procId}"].AddData(usedPct);
                        }
                    }
                }

                // CPU \\

                SafeProcessHandle procHandle = null;
                ICpuUsage cpuUsage;

                if (IsWindows)
                {
                    procHandle = NativeMethods.GetSafeProcessHandle(procId);

                    if (procHandle == null || procHandle.IsClosed || procHandle.IsInvalid)
                    {
                        return;
                    }

                    cpuUsage = new CpuUsageWin32();
                }
                else
                {
                    cpuUsage = new CpuUsageProcess();
                }

                // CpuMonitorDuration can't be set to greater than 10s.
                TimeSpan cpuMonitorDuration = CpuMonitorDuration <= TimeSpan.FromSeconds(10) ? CpuMonitorDuration : TimeSpan.FromSeconds(10);

                // CpuMonitorLoopSleepDuration can't be set to less than 500 milliseconds.
                TimeSpan cpuMonitorLoopSleepTime = CpuMonitorLoopSleepDuration;

                // At least one value is needed to compute CPU Time % (in fact, more than one is best on Windows). If the user misconfigures sleep time to be greater than monitor duration,
                // then we'll just set it to 1000 ms.
                if (cpuMonitorLoopSleepTime > cpuMonitorDuration)
                {
                    cpuMonitorLoopSleepTime = TimeSpan.FromMilliseconds(1000);
                }

                // Limit potential for high CPU usage by throttling max duration when monitoring CPU usage with multiple threads.
                if (EnableConcurrentMonitoring)
                {
                    if (cpuMonitorDuration >= TimeSpan.FromSeconds(5))
                    {
                        cpuMonitorDuration = TimeSpan.FromSeconds(5);

                        // Always force 1s sleep time for concurrent monitoring when duration is >= 5s.
                        cpuMonitorLoopSleepTime = TimeSpan.FromSeconds(1000);
                    }
                }

                Stopwatch timer = Stopwatch.StartNew();

                try
                {
#if DEBUG
                    ObserverLogger.LogInfo($"ComputeResourceUsage: Entering CPU monitor while loop. MonitorDuration = {CpuMonitorDuration}. CpuMonitorLoopSleepDuration = {CpuMonitorLoopSleepDuration}.");
#endif
                    while (timer.Elapsed <= cpuMonitorDuration)
                    {
                        if (token.IsCancellationRequested)
                        {
                            state.Stop();
                        }

                        if (checkCpu || (MonitorResourceGovernanceLimits && repOrInst.RGCpuEnabled && rgCpuPercentThreshold > 0))
                        {
                            double cpu = cpuUsage.GetCurrentCpuUsagePercentage(procId, IsWindows ? procName : null, procHandle);

                            // Process id is no longer mapped to expected process name or some internal error occured that is non-retryable. Ignore this process.
                            // See CpuUsageProcess.cs/CpuUsageWin32.cs impls.
                            if (cpu == -1)
                            {
                                return;
                            }

                            // CPU (all cores) \\
                            if (checkCpu)
                            {
                                if (procId == parentPid)
                                {
                                    AllAppCpuData[id].AddData(cpu);
                                }
                                else
                                {
                                    // Add new child proc entry if not already present in dictionary.
                                    _ = AllAppCpuData.TryAdd(
                                            $"{id}:{procName}{procId}",
                                            new FabricResourceUsageData<double>(
                                                ErrorWarningProperty.CpuTime,
                                                $"{id}:{procName}{procId}",
                                                capacity,
                                                UseCircularBuffer,
                                                EnableConcurrentMonitoring));

                                    AllAppCpuData[$"{id}:{procName}{procId}"].AddData(cpu);
                                }
                            }

                            if (MonitorResourceGovernanceLimits && repOrInst.RGCpuEnabled && repOrInst.RGAppliedCpuLimitCores > 0 && rgCpuPercentThreshold > 0)
                            {
                                double pct = cpu * Environment.ProcessorCount / repOrInst.RGAppliedCpuLimitCores;

                                if (procId == parentPid)
                                {
                                    AllAppRGCpuUsagePercent[id].AddData(pct);
                                }
                                else
                                {
                                    // Add new child proc entry if not already present in dictionary.
                                    _ = AllAppRGCpuUsagePercent.TryAdd(
                                            $"{id}:{procName}{procId}",
                                            new FabricResourceUsageData<double>(
                                                ErrorWarningProperty.RGCpuUsagePercent,
                                                $"{id}:{procName}{procId}",
                                                capacity,
                                                UseCircularBuffer,
                                                EnableConcurrentMonitoring));

                                    AllAppRGCpuUsagePercent[$"{id}:{procName}{procId}"].AddData(pct);
                                }
                            }
                        }

                        Thread.Sleep(cpuMonitorLoopSleepTime);
                    }
#if DEBUG
                    ObserverLogger.LogInfo($"ComputeResourceUsage: Exiting CPU monitoring while loop. Ran for {timer.Elapsed}.");
#endif
                }
                finally 
                {
                    if (IsWindows)
                    {
                        procHandle?.Dispose();
                        procHandle = null;
                    }

                    timer.Stop();
                    timer = null;
                }
            });
        }

        private async Task SetDeployedReplicaOrInstanceListAsync(Uri applicationNameFilter = null, string applicationType = null)
        {
            ObserverLogger.LogInfo("Starting SetDeployedReplicaOrInstanceListAsync.");
            List<DeployedApplication> depApps = null;

            // DEBUG - Perf
#if DEBUG
            var stopwatch = Stopwatch.StartNew();
#endif
            try
            {
                if (applicationNameFilter != null)
                {
                    depApps = deployedApps.FindAll(a => a.ApplicationName.Equals(applicationNameFilter));
                }
                else if (!string.IsNullOrWhiteSpace(applicationType))
                {
                    depApps = deployedApps.FindAll(a => a.ApplicationTypeName == applicationType);
                }
                else
                {
                    depApps = deployedApps;
                }
            }
            catch (ArgumentException ae)
            {
                ObserverLogger.LogWarning($"SetDeployedReplicaOrInstanceListAsync: Unable to process replica information:{Environment.NewLine}{ae}");
                return;
            }

            foreach (var userTarget in userTargetList)
            {
                for (int i = 0; i < depApps.Count; i++)
                {
                    if (Token.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        // TargetAppType supplied in user config, so set TargetApp on deployedApp instance by searching for it in the currently deployed application list.
                        if (userTarget.TargetAppType != null)
                        {
                            if (depApps[i].ApplicationTypeName != userTarget.TargetAppType)
                            {
                                continue;
                            }

                            userTarget.TargetApp = depApps[i].ApplicationName.OriginalString;
                        }

                        if (string.IsNullOrWhiteSpace(userTarget.TargetApp))
                        {
                            continue;
                        }

                        if (!depApps[i].ApplicationName.OriginalString.Equals(userTarget.TargetApp, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string[] filteredServiceList = null;
                        ServiceFilterType filterType = ServiceFilterType.None;

                        // Filter serviceInclude/Exclude config.
                        if (!string.IsNullOrWhiteSpace(userTarget.ServiceExcludeList))
                        {
                            filteredServiceList = userTarget.ServiceExcludeList.Replace(" ", string.Empty).Split(',');
                            filterType = ServiceFilterType.Exclude;
                        }
                        else if (!string.IsNullOrWhiteSpace(userTarget.ServiceIncludeList))
                        {
                            filteredServiceList = userTarget.ServiceIncludeList.Replace(" ", string.Empty).Split(',');
                            filterType = ServiceFilterType.Include;
                        }

                        List<ReplicaOrInstanceMonitoringInfo> replicasOrInstances =
                                await GetDeployedReplicasAsync(
                                        new Uri(userTarget.TargetApp),
                                        filteredServiceList,
                                        filterType,
                                        applicationType);

                        if (replicasOrInstances != null && replicasOrInstances.Count > 0)
                        {
                            ReplicaOrInstanceList.AddRange(replicasOrInstances);

                            var targets = userTargetList.Where(x => x.TargetApp != null && x.TargetApp == userTarget.TargetApp
                                                                 || x.TargetAppType != null && x.TargetAppType == userTarget.TargetAppType);

                            if (userTarget.TargetApp != null && !deployedTargetList.Any(r => r.TargetApp == userTarget.TargetApp))
                            {
                                deployedTargetList.AddRange(targets);
                            }

                            replicasOrInstances.Clear();
                        }

                        replicasOrInstances = null;
                    }
                    catch (Exception e) when (e is ArgumentException or FabricException or Win32Exception)
                    {
                        ObserverLogger.LogWarning(
                            $"SetDeployedReplicaOrInstanceListAsync: Unable to process replica information for {userTarget}: {e.Message}");
                    }
                }
            }

            depApps?.Clear();
            depApps = null;
            ObserverLogger.LogInfo("Completed SetDeployedReplicaOrInstanceListAsync.");
#if DEBUG
            stopwatch.Stop();
            ObserverLogger.LogInfo($"SetDeployedReplicaOrInstanceListAsync for {applicationNameFilter?.OriginalString} run duration: {stopwatch.Elapsed}");
#endif
        }

        private async Task<List<ReplicaOrInstanceMonitoringInfo>> GetDeployedReplicasAsync(
                                                                     Uri appName,
                                                                     string[] serviceFilterList = null,
                                                                     ServiceFilterType filterType = ServiceFilterType.None,
                                                                     string appTypeName = null)
        {
            ObserverLogger.LogInfo("Starting GetDeployedReplicasAsync.");
            // DEBUG - Perf
#if DEBUG
            var stopwatch = Stopwatch.StartNew();
#endif
            var deployedReplicaList = await FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(
                                                NodeName,
                                                appName,
                                                null,
                                                null,
                                                ConfigurationSettings.AsyncTimeout,
                                                Token);

            if (deployedReplicaList == null || !deployedReplicaList.Any())
            {
                return null;
            }

            List<DeployedServiceReplica> deployedReplicas;

            try
            {
                deployedReplicas = deployedReplicaList.DistinctBy(x => x.HostProcessId).ToList();
            }
            catch (Exception e) when (e is ArgumentException)
            {
                return null;
            }

#if DEBUG
            ObserverLogger.LogInfo($"QueryManager.GetDeployedReplicaListAsync for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
#endif
            var replicaMonitoringList = new ConcurrentQueue<ReplicaOrInstanceMonitoringInfo>();
            string appType = appTypeName;

            if (string.IsNullOrWhiteSpace(appType))
            {
                try
                {
                    if (deployedApps.Any(app => app.ApplicationName == appName))
                    {
                        appType = deployedApps.First(app => app.ApplicationName == appName).ApplicationTypeName;
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {

                }
            }

            SetInstanceOrReplicaMonitoringList(
                appName,
                appType,
                serviceFilterList,
                filterType,
                deployedReplicas,
                replicaMonitoringList);

            ObserverLogger.LogInfo("Completed GetDeployedReplicasAsync.");
#if DEBUG
            stopwatch.Stop();
            ObserverLogger.LogInfo($"GetDeployedReplicasAsync for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
#endif
            return replicaMonitoringList.ToList();
        }

        private void SetInstanceOrReplicaMonitoringList(
                        Uri appName,
                        string appTypeName,
                        string[] filterList,
                        ServiceFilterType filterType,
                        List<DeployedServiceReplica> deployedReplicaList,
                        ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> replicaMonitoringList)
        {
            ObserverLogger.LogInfo("Starting SetInstanceOrReplicaMonitoringList.");
            // DEBUG - Perf
#if DEBUG
            var stopwatch = Stopwatch.StartNew();
#endif
            _ = Parallel.For(0, deployedReplicaList.Count, parallelOptions, (i, state) =>
            {
                if (Token.IsCancellationRequested)
                {
                    state.Stop();
                }

                var deployedReplica = deployedReplicaList[i];
                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                switch (deployedReplica)
                {
                    case DeployedStatefulServiceReplica statefulReplica when statefulReplica.ReplicaRole is ReplicaRole.Primary or ReplicaRole.ActiveSecondary:
                    {
                        if (filterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = filterList.Any(s => statefulReplica.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    return;
                            }
                        }

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statefulReplica.HostProcessId,
                            ReplicaOrInstanceId = statefulReplica.ReplicaId,
                            PartitionId = statefulReplica.Partitionid,
                            ReplicaRole = statefulReplica.ReplicaRole,
                            ServiceKind = statefulReplica.ServiceKind,
                            ServiceName = statefulReplica.ServiceName,
                            ServiceManifestName = statefulReplica.ServiceManifestName,
                            ServiceTypeName = statefulReplica.ServiceTypeName,
                            ServicePackageActivationId = statefulReplica.ServicePackageActivationId,
                            ServicePackageActivationMode = string.IsNullOrWhiteSpace(statefulReplica.ServicePackageActivationId) ?
                                            ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                            ReplicaStatus = statefulReplica.ReplicaStatus
                        };

                        /* In order to provide accurate resource usage of an SF service process we need to also account for
                        any processes (children) that the service process (parent) created/spawned. */

                        if (EnableChildProcessMonitoring && replicaInfo?.HostProcessId > 0)
                        {
                            // DEBUG - Perf
#if DEBUG
                            var sw = Stopwatch.StartNew();
#endif
                            List<(string ProcName, int Pid, DateTime ProcessStartTime)> childPids =
                            ProcessInfoProvider.Instance.GetChildProcessInfo((int)statefulReplica.HostProcessId, Win32HandleToProcessSnapshot);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                                ObserverLogger.LogInfo($"{replicaInfo?.ServiceName}({replicaInfo?.HostProcessId}):{Environment.NewLine}Child procs (name, id, startDate): {string.Join(" ", replicaInfo.ChildProcesses)}");
                            }
#if DEBUG
                            sw.Stop();
                            ObserverLogger.LogInfo($"EnableChildProcessMonitoring block run duration: {sw.Elapsed}");
#endif
                        }
                        break;
                    }
                    case DeployedStatelessServiceInstance statelessInstance:
                    {
                        if (filterList != null && filterType != ServiceFilterType.None)
                        {
                            bool isInFilterList = filterList.Any(s => statelessInstance.ServiceName.OriginalString.ToLower().Contains(s.ToLower()));

                            switch (filterType)
                            {
                                case ServiceFilterType.Include when !isInFilterList:
                                case ServiceFilterType.Exclude when isInFilterList:
                                    return;
                            }
                        }

                        replicaInfo = new ReplicaOrInstanceMonitoringInfo
                        {
                            ApplicationName = appName,
                            ApplicationTypeName = appTypeName,
                            HostProcessId = statelessInstance.HostProcessId,
                            ReplicaOrInstanceId = statelessInstance.InstanceId,
                            PartitionId = statelessInstance.Partitionid,
                            ReplicaRole = ReplicaRole.None,
                            ServiceKind = statelessInstance.ServiceKind,
                            ServiceName = statelessInstance.ServiceName,
                            ServiceManifestName = statelessInstance.ServiceManifestName,
                            ServiceTypeName = statelessInstance.ServiceTypeName,
                            ServicePackageActivationId = statelessInstance.ServicePackageActivationId,
                            ServicePackageActivationMode = string.IsNullOrWhiteSpace(statelessInstance.ServicePackageActivationId) ?
                                            ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                            ReplicaStatus = statelessInstance.ReplicaStatus
                        };

                        if (EnableChildProcessMonitoring && replicaInfo?.HostProcessId > 0)
                        {
                            // DEBUG - Perf
#if DEBUG
                            var sw = Stopwatch.StartNew();
#endif
                            List<(string ProcName, int Pid, DateTime ProcessStartTime)> childPids =
                                ProcessInfoProvider.Instance.GetChildProcessInfo((int)statelessInstance.HostProcessId, Win32HandleToProcessSnapshot);

                            if (childPids != null && childPids.Count > 0)
                            {
                                replicaInfo.ChildProcesses = childPids;
                                ObserverLogger.LogInfo($"{replicaInfo?.ServiceName}({replicaInfo?.HostProcessId}):{Environment.NewLine}Child procs (name, id, startDate): {string.Join(" ", replicaInfo.ChildProcesses)}");
                            }
#if DEBUG
                            sw.Stop();
                            ObserverLogger.LogInfo($"EnableChildProcessMonitoring block run duration: {sw.Elapsed}");
#endif
                            }
                        break;
                    }
                }

                if (replicaInfo != null && replicaInfo.HostProcessId > 0 && !ReplicaOrInstanceList.Any(r => r.HostProcessId == replicaInfo.HostProcessId))
                {
                    if (IsWindows)
                    {
                        // This will be null if GetProcessNameFromId fails. It will fail when the target process is inaccessible due to user privilege, for example.
                        replicaInfo.HostProcessName = NativeMethods.GetProcessNameFromId((int)replicaInfo.HostProcessId);
                        int errorCode = Marshal.GetLastWin32Error();

                        if (replicaInfo.HostProcessName == null)
                        {
                            // DEBUG (When AppObserverEnableVerboseLogging is true).
                            ObserverLogger.LogInfo($"Unable to get information for process {replicaInfo.HostProcessId} ({replicaInfo.ServiceName.OriginalString}). Win32 Error Code: {errorCode}.");
                            
                            // Make sure the issue is a user privilege problem before emitting the related warning.
                            if (errorCode == NativeMethods.ERROR_ACCESS_DENIED)
                            {
                                SendServiceProcessElevatedWarning(replicaInfo.ApplicationName?.OriginalString, replicaInfo.ServiceName?.OriginalString);
                            }

                            // Do not add replica to repOrInst list.
                            return;
                        }
                    }
                    else // Linux
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
                            // Do not add replica to repOrInst list.
                            return;
                        }
                    }

                    // This will be DateTime.MinValue when the target process is inaccessible due to user privilege, for example.
                    // We shouldn't get here given the code above if some process is running at a higher priv than FO.. but if the process *is* inaccessible for some reason at this point,
                    // then we'll not add the replica to the list.
                    replicaInfo.HostProcessStartTime = GetProcessStartTime((int)replicaInfo.HostProcessId);

                    if (IsWindows)
                    {
                        int errorCode = Marshal.GetLastWin32Error();

                        if (errorCode == NativeMethods.ERROR_ACCESS_DENIED)
                        {
                            SendServiceProcessElevatedWarning(replicaInfo.ApplicationName?.OriginalString, replicaInfo.ServiceName?.OriginalString);
                        }
                    }

                    if (replicaInfo.HostProcessStartTime == DateTime.MinValue)
                    {
                        // Do not add replica to repOrInst list.
                        return;
                    }

                    ProcessServiceConfiguration(appTypeName, deployedReplica.CodePackageName, replicaInfo);

                    // null HostProcessName means the service process can't be monitored. If Fabric is the hosting process, then this is a Guest Executable or helper code package.
                    if (!string.IsNullOrWhiteSpace(replicaInfo.HostProcessName) && replicaInfo.HostProcessName != "Fabric")
                    {
                        replicaMonitoringList.Enqueue(replicaInfo);
                    }

                    ProcessMultipleHelperCodePackages(appName, appTypeName, deployedReplica, ref replicaMonitoringList, replicaInfo.HostProcessName == "Fabric");
                }
            });
            ObserverLogger.LogInfo("Completed SetInstanceOrReplicaMonitoringList.");
#if DEBUG
            stopwatch.Stop();
            ObserverLogger.LogInfo($"SetInstanceOrReplicaMonitoringList for {appName.OriginalString} run duration: {stopwatch.Elapsed}");
#endif
        }

        private void SendServiceProcessElevatedWarning(string appName, string serviceName)
        {
            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(serviceName))
            {
                return;
            }

            // DEBUG.
            ObserverLogger.LogInfo($"{serviceName} is running as Admin or System user on Windows and can't be monitored by FabricObserver, which is running as Network Service.");

            if (ObserverManager.ObserverFailureHealthStateLevel != HealthState.Unknown)
            {
                string message = $"{serviceName} is running as Admin or System user on Windows and can't be monitored by FabricObserver, which is running as Network Service. " +
                                 $"You can configure FabricObserver to run as Admin or System user on Windows to solve this problem. It is best that you first determine if {serviceName} really needs to run as Admin or System user on Windows. " +
                                 $"In the meantime, you can easily configure AppObserver to ignore this particular service by adding the following config object to AppObserver.config.json:{Environment.NewLine}" +
                                 $"{{" + Environment.NewLine +
                                 $"      \"targetApp\": \"{appName.Remove(0, "fabric:/".Length)}\"," + Environment.NewLine +
                                 $"      \"serviceExcludeList\": \"{serviceName.Remove(0, appName.Length + 1)}\"" + Environment.NewLine +
                                 $"}}";

                string property = $"RestrictedAccess({serviceName})";
                var healthReport = new Utilities.HealthReport
                {
                    ServiceName = ServiceName,
                    EmitLogEvent = EnableVerboseLogging,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTTL(),
                    Property = property,
                    EntityType = EntityType.Service,
                    State = ObserverManager.ObserverFailureHealthStateLevel,
                    NodeName = NodeName,
                    Observer = ObserverName
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            property,
                            ObserverManager.ObserverFailureHealthStateLevel,
                            message,
                            ObserverName,
                            Token,
                            serviceName);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Property = property,
                            Level = ObserverManager.ObserverFailureHealthStateLevel.ToString(),
                            Message = message,
                            ObserverName,
                            ServiceName = serviceName
                        });
                }
            }
        }

        private void ProcessServiceConfiguration(string appTypeName, string codepackageName, ReplicaOrInstanceMonitoringInfo replicaInfo)
        {
            // ResourceGovernance/AppTypeVer/ServiceTypeVer.
            ObserverLogger.LogInfo($"Starting ProcessServiceConfiguration check for {replicaInfo.ServiceName.OriginalString}.");

            if (string.IsNullOrWhiteSpace(appTypeName))
            {
                return;
            }

            try
            {
                string appTypeVersion = null;
                ApplicationParameterList appParameters = null;
                ApplicationParameterList defaultParameters = null;

                ApplicationList appList =
                    FabricClientInstance.QueryManager.GetApplicationListAsync(
                        replicaInfo.ApplicationName,
                        ConfigurationSettings.AsyncTimeout,
                        Token).Result;

                ApplicationTypeList applicationTypeList =
                    FabricClientInstance.QueryManager.GetApplicationTypeListAsync(
                        appTypeName,
                        ConfigurationSettings.AsyncTimeout,
                        Token).Result;

                if (appList?.Count > 0)
                {
                    try
                    {
                        if (appList.Any(app => app.ApplicationTypeName == appTypeName))
                        {
                            appTypeVersion = appList.First(app => app.ApplicationTypeName == appTypeName).ApplicationTypeVersion;
                            appParameters = appList.First(app => app.ApplicationTypeName == appTypeName).ApplicationParameters;
                            replicaInfo.ApplicationTypeVersion = appTypeVersion;
                        }

                        if (applicationTypeList.Any(app => app.ApplicationTypeVersion == appTypeVersion))
                        {
                            defaultParameters = applicationTypeList.First(app => app.ApplicationTypeVersion == appTypeVersion).DefaultParameters;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {

                    }

                    if (!string.IsNullOrWhiteSpace(appTypeVersion))
                    {
                        // RG - Windows-only. Linux is not supported yet.
                        if (MonitorResourceGovernanceLimits)
                        {
                            string appManifest =
                                      FabricClientInstance.ApplicationManager.GetApplicationManifestAsync(
													 appTypeName,
													 appTypeVersion,
													 ConfigurationSettings.AsyncTimeout,
												     Token).Result;
                            string svcManifest =
									  FabricClientInstance.ServiceManager.GetServiceManifestAsync(
                                                     appTypeName, 
                                                     appTypeVersion,
                                                     replicaInfo.ServiceManifestName,
                                                     ConfigurationSettings.AsyncTimeout,
                                                     Token).Result;

                            if (!string.IsNullOrWhiteSpace(appManifest) && appManifest.Contains($"<{ObserverConstants.RGPolicyNodeName} ") || appManifest.Contains($"<{ObserverConstants.RGSvcPkgPolicyNodeName} "))
                            {
                                ApplicationParameterList parameters = new();
                                FabricClientUtilities.AddParametersIfNotExists(parameters, appParameters);
                                FabricClientUtilities.AddParametersIfNotExists(parameters, defaultParameters);

                                // RG Memory
                                (replicaInfo.RGMemoryEnabled, replicaInfo.RGAppliedMemoryLimitMb) =
                                    fabricClientUtilities.TupleGetMemoryResourceGovernanceInfo(appManifest, replicaInfo.ServiceManifestName, codepackageName, parameters);
                                
                                if (replicaInfo.RGMemoryEnabled)
                                {
                                    ObserverLogger.LogInfo($"RG Memory enabled. Applied Memory Limit MB: {replicaInfo.RGAppliedMemoryLimitMb}");
                                }

                                // RG Cpu
                                (replicaInfo.RGCpuEnabled, replicaInfo.RGAppliedCpuLimitCores) =
                                    fabricClientUtilities.TupleGetCpuResourceGovernanceInfo(appManifest, svcManifest, replicaInfo.ServiceManifestName, codepackageName, parameters);
                            
                                if (replicaInfo.RGCpuEnabled) 
                                {
                                    ObserverLogger.LogInfo($"RG CPU enabled. Applied Cpu limit cores: {replicaInfo.RGAppliedCpuLimitCores}");
                                }
                            }
                        }

                        // ServiceTypeVersion
                        var serviceList =
                            FabricClientInstance.QueryManager.GetServiceListAsync(
                                replicaInfo.ApplicationName,
                                replicaInfo.ServiceName,
                                ConfigurationSettings.AsyncTimeout,
                                Token).Result;

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
                            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                            {

                            }
                        }
                    }
                }
            }
            catch (Exception e) when (e is AggregateException or FabricException or TaskCanceledException or TimeoutException or XmlException)
            {
                if (e is not TaskCanceledException)
                {
                    ObserverLogger.LogWarning($"Handled: Failed to process Service configuration for {replicaInfo.ServiceName.OriginalString} with exception '{e.Message}'");
                }
                // move along
            }
            ObserverLogger.LogInfo($"Completed ProcessServiceConfiguration for {replicaInfo.ServiceName.OriginalString}.");
        }

        private void ProcessMultipleHelperCodePackages(
                        Uri appName,
                        string appTypeName,
                        DeployedServiceReplica deployedReplica,
                        ref ConcurrentQueue<ReplicaOrInstanceMonitoringInfo> repsOrInstancesInfo,
                        bool isHostedByFabric)
        {
            ObserverLogger.LogInfo($"Starting ProcessMultipleHelperCodePackages for {deployedReplica.ServiceName} (isHostedByFabric = {isHostedByFabric})");
            
            if (repsOrInstancesInfo == null)
            {
                ObserverLogger.LogInfo($"repsOrInstanceList is null. Exiting ProcessMultipleHelperCodePackages");
                return;
            }

            try
            {
                DeployedCodePackageList codepackages =
                    FabricClientInstance.QueryManager.GetDeployedCodePackageListAsync(
                        NodeName,
                        appName,
                        deployedReplica.ServiceManifestName,
                        null,
                        ConfigurationSettings.AsyncTimeout,
                        Token).Result;

                ReplicaOrInstanceMonitoringInfo replicaInfo = null;

                // Check for multiple code packages or GuestExecutable service (Fabric is the host).
                if (codepackages.Count < 2 && !isHostedByFabric)
                {
                    ObserverLogger.LogInfo($"Completed ProcessMultipleHelperCodePackages.");
                    return;
                }

                if (!codepackages.Any(c => c.CodePackageName != deployedReplica.CodePackageName))
                {
                    ObserverLogger.LogInfo($"No helper code packages detected. Completed ProcessMultipleHelperCodePackages.");
                    return;
                }

                var helperCodePackages = codepackages.Where(c => c.CodePackageName != deployedReplica.CodePackageName);

                foreach (var codepackage in helperCodePackages)
                {
                    if (Token.IsCancellationRequested)
                    {
                        return;
                    }

                    int procId = (int)codepackage.EntryPoint.ProcessId; // The actual process id of the helper or guest executable binary.
                    string procName = null;

                    if (IsWindows)
                    {
                        try
                        {
                            procName = NativeMethods.GetProcessNameFromId(procId);
                            
                            if (procName == null)
                            {
                                continue;
                            }
                        }
                        catch (Win32Exception)
                        {
                            // Process no longer running or access denied.
                            continue;
                        }
                    }
                    else // Linux
                    {
                        using (var proc = Process.GetProcessById(procId))
                        {
                            try
                            {
                                procName = proc.ProcessName;
                            }
                            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException)
                            {
                                // Process no longer running.
                                continue;
                            }
                        }
                    }

                    // Make sure process is still the process we're looking for.
                    if (!EnsureProcess(procName, procId, GetProcessStartTime(procId)))
                    {
                        continue;
                    }

                    // This ensures that support for multiple CodePackages and GuestExecutable services fit naturally into AppObserver's *existing* implementation.
                    replicaInfo = new ReplicaOrInstanceMonitoringInfo
                    {
                        ApplicationName = appName,
                        ApplicationTypeName = appTypeName,
                        HostProcessId = procId,
                        HostProcessName = procName,
                        HostProcessStartTime = GetProcessStartTime(procId),
                        ReplicaOrInstanceId = deployedReplica is DeployedStatefulServiceReplica replica ?
                                                replica.ReplicaId : ((DeployedStatelessServiceInstance)deployedReplica).InstanceId,
                        PartitionId = deployedReplica.Partitionid,
                        ReplicaRole = deployedReplica is DeployedStatefulServiceReplica rep ? rep.ReplicaRole : ReplicaRole.None,
                        ServiceKind = deployedReplica.ServiceKind,
                        ServiceName = deployedReplica.ServiceName,
                        ServiceManifestName = codepackage.ServiceManifestName,
                        ServiceTypeName = deployedReplica.ServiceTypeName,
                        ServicePackageActivationId = string.IsNullOrWhiteSpace(codepackage.ServicePackageActivationId) ?
                                                        deployedReplica.ServicePackageActivationId : codepackage.ServicePackageActivationId,
                        ServicePackageActivationMode = string.IsNullOrWhiteSpace(codepackage.ServicePackageActivationId) ?
                                                        ServicePackageActivationMode.SharedProcess : ServicePackageActivationMode.ExclusiveProcess,
                        ReplicaStatus = deployedReplica is DeployedStatefulServiceReplica r ?
                                            r.ReplicaStatus : ((DeployedStatelessServiceInstance)deployedReplica).ReplicaStatus,
                    };

                    // If Helper binaries launch child processes, AppObserver will monitor them, too.
                    if (EnableChildProcessMonitoring && procId > 0)
                    {
                        // DEBUG - Perf
#if DEBUG
                        var sw = Stopwatch.StartNew();
#endif
                        List<(string ProcName, int Pid, DateTime ProcessStartTime)> childPids =
                            ProcessInfoProvider.Instance.GetChildProcessInfo(procId, Win32HandleToProcessSnapshot);

                        if (childPids != null && childPids.Count > 0)
                        {
                            replicaInfo.ChildProcesses = childPids;
                            ObserverLogger.LogInfo($"{replicaInfo?.ServiceName}({procId}):{Environment.NewLine}Child procs (name, id, startDate): {string.Join(" ", replicaInfo.ChildProcesses)}");
                        }
#if DEBUG
                        sw.Stop();
                        ObserverLogger.LogInfo($"EnableChildProcessMonitoring block run duration: {sw.Elapsed}");
#endif
                    }

                    // ResourceGovernance/AppTypeVer/ServiceTypeVer.
                    ProcessServiceConfiguration(appTypeName, codepackage.CodePackageName, replicaInfo);

                    if (replicaInfo != null && replicaInfo.HostProcessId > 0 &&
                        !repsOrInstancesInfo.Any(r => r.HostProcessId == replicaInfo.HostProcessId && r.HostProcessName == replicaInfo.HostProcessName))
                    {
                        repsOrInstancesInfo.Enqueue(replicaInfo);
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException or FabricException or TaskCanceledException or TimeoutException)
            {
                ObserverLogger.LogInfo($"ProcessMultipleHelperCodePackages: Handled Exception: {e.Message}");
            }
            ObserverLogger.LogInfo($"Completed ProcessMultipleHelperCodePackages.");
        }

        private void LogAllAppResourceDataToCsv(string key)
        {
            if (!EnableCsvLogging)
            {
                return;
            }

            try
            {
                // CPU Time
                if (AllAppCpuData != null && AllAppCpuData.ContainsKey(key))
                {
                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.CpuTime,
                        "Average",
                        AllAppCpuData.First(x => x.Key == key).Value.AverageDataValue);

                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.CpuTime,
                        "Peak",
                        AllAppCpuData.First(x => x.Key == key).Value.MaxDataValue);
                }

                // Memory - Working set \\

                if (AllAppMemDataMb != null && AllAppMemDataMb.ContainsKey(key))
                {
                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.MemoryConsumptionMb,
                        "Average",
                        AllAppMemDataMb[key].AverageDataValue);

                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.MemoryConsumptionMb,
                        "Peak",
                        AllAppMemDataMb[key].MaxDataValue);
                }

                if (AllAppMemDataPercent != null && AllAppMemDataPercent.ContainsKey(key))
                {
                    CsvFileLogger.LogData(
                       fileName,
                       key,
                       ErrorWarningProperty.MemoryConsumptionPercentage,
                       "Average",
                       AllAppMemDataPercent[key].AverageDataValue);

                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.MemoryConsumptionPercentage,
                        "Peak",
                        AllAppMemDataPercent[key].MaxDataValue);
                }

                // Memory - Private Bytes \\

                if (IsWindows)
                {
                    if (AllAppPrivateBytesDataMb != null && AllAppPrivateBytesDataMb.ContainsKey(key))
                    {
                        if (AllAppPrivateBytesDataMb.Any(x => x.Key == key))
                        {
                            CsvFileLogger.LogData(
                                fileName,
                                key,
                                ErrorWarningProperty.PrivateBytesMb,
                                "Average",
                                AllAppPrivateBytesDataMb[key].AverageDataValue);

                            CsvFileLogger.LogData(
                                fileName,
                                key,
                                ErrorWarningProperty.PrivateBytesMb,
                                "Peak",
                                AllAppPrivateBytesDataMb[key].MaxDataValue);
                        }
                    }

                    if (AllAppPrivateBytesDataPercent != null && AllAppPrivateBytesDataPercent.ContainsKey(key))
                    {
                        if (AllAppPrivateBytesDataPercent.Any(x => x.Key == key))
                        {
                            CsvFileLogger.LogData(
                               fileName,
                               key,
                               ErrorWarningProperty.PrivateBytesPercent,
                               "Average",
                               AllAppPrivateBytesDataPercent[key].AverageDataValue);

                            CsvFileLogger.LogData(
                                fileName,
                                key,
                                ErrorWarningProperty.PrivateBytesPercent,
                                "Peak",
                                AllAppPrivateBytesDataPercent[key].MaxDataValue);
                        }
                    }
                }

                // Ports \\

                if (AllAppTotalActivePortsData != null && AllAppTotalActivePortsData.ContainsKey(key))
                {
                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.ActiveTcpPorts,
                        "Total",
                        AllAppTotalActivePortsData[key].MaxDataValue);
                }

                if (AllAppEphemeralPortsData != null && AllAppEphemeralPortsData.ContainsKey(key))
                {
                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.ActiveEphemeralPorts,
                        "Total",
                        AllAppEphemeralPortsData[key].MaxDataValue);
                }

                // Handles
                if (AllAppHandlesData != null && AllAppHandlesData.ContainsKey(key))
                {
                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.AllocatedFileHandles,
                        "Total",
                        AllAppHandlesData[key].MaxDataValue);
                }

                // Threads
                if (AllAppThreadsData != null && AllAppThreadsData.ContainsKey(key))
                {
                    CsvFileLogger.LogData(
                        fileName,
                        key,
                        ErrorWarningProperty.ThreadCount,
                        "Total",
                        AllAppHandlesData[key].MaxDataValue);
                }
            }
            catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                ObserverLogger.LogWarning($"Failure generating CSV data: {e.Message}");
            }

            DataTableFileLogger.Flush();
        }

        private bool EnsureProcess(string procName, int procId, DateTime processStartTime)
        {
            if (string.IsNullOrWhiteSpace(procName) || procId < 1)
            {
                return false;
            }

            // Linux.
            if (!IsWindows)
            {
                try
                {
                    using (Process proc = Process.GetProcessById(procId))
                    {
                        return proc.ProcessName == procName && proc.StartTime == processStartTime;
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or SystemException)
                {
                    return false;
                }
            }

            // Windows.
            try
            {
                return string.Compare(
                        NativeMethods.GetProcessNameFromId(procId),
                        procName, StringComparison.OrdinalIgnoreCase) == 0 
                        && processStartTime == GetProcessStartTime(procId);
            }
            catch (Exception e) when (e is ArgumentException or SystemException or Win32Exception)
            {
                return false;
            }
        }

        public void CleanUp()
        {
            ObserverLogger.LogInfo("Starting CleanUp...");
            deployedTargetList?.Clear();
            deployedTargetList = null;

            userTargetList?.Clear();
            userTargetList = null;

            ReplicaOrInstanceList?.Clear();
            ReplicaOrInstanceList = null;

            deployedApps?.Clear();
            deployedApps = null;

            if (AllAppCpuData != null && AllAppCpuData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppCpuData?.Clear();
                AllAppCpuData = null;
            }

            if (AllAppEphemeralPortsData != null && AllAppEphemeralPortsData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppEphemeralPortsData?.Clear();
                AllAppEphemeralPortsData = null;
            }

            if (AllAppEphemeralPortsDataPercent != null && AllAppEphemeralPortsDataPercent.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppEphemeralPortsDataPercent?.Clear();
                AllAppEphemeralPortsDataPercent = null;
            }

            if (AllAppHandlesData != null && AllAppHandlesData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppHandlesData?.Clear();
                AllAppHandlesData = null;
            }

            if (AllAppMemDataMb != null && AllAppMemDataMb.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppMemDataMb?.Clear();
                AllAppMemDataMb = null;
            }

            if (AllAppMemDataPercent != null && AllAppMemDataPercent.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppMemDataPercent?.Clear();
                AllAppMemDataPercent = null;
            }

            if (AllAppTotalActivePortsData != null && AllAppTotalActivePortsData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppTotalActivePortsData?.Clear();
                AllAppTotalActivePortsData = null;
            }

            if (AllAppThreadsData != null && AllAppThreadsData.All(frud => !frud.Value.ActiveErrorOrWarning))
            {
                AllAppThreadsData?.Clear();
                AllAppThreadsData = null;
            }

            // Windows-only cleanup.
            if (IsWindows)
            {
                if (AllAppKvsLvidsData != null && AllAppKvsLvidsData.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppKvsLvidsData?.Clear();
                    AllAppKvsLvidsData = null;
                }

                if (AllAppPrivateBytesDataMb != null && AllAppPrivateBytesDataMb.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppPrivateBytesDataMb?.Clear();
                    AllAppPrivateBytesDataMb = null;
                }

                if (AllAppPrivateBytesDataPercent != null && AllAppPrivateBytesDataPercent.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppPrivateBytesDataPercent?.Clear();
                    AllAppPrivateBytesDataPercent = null;
                }

                if (AllAppRGMemoryUsagePercent != null && AllAppRGMemoryUsagePercent.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppRGMemoryUsagePercent?.Clear();
                    AllAppRGMemoryUsagePercent = null;
                }

                if (AllAppRGCpuUsagePercent != null && AllAppRGCpuUsagePercent.All(frud => !frud.Value.ActiveErrorOrWarning))
                {
                    AllAppRGCpuUsagePercent?.Clear();
                    AllAppRGCpuUsagePercent = null;
                }

                if (handleToProcSnapshot != null)
                {
                    handleToProcSnapshot.Dispose();
                    GC.KeepAlive(handleToProcSnapshot);
                    handleToProcSnapshot = null;
                }

                if (hasSFUserProcCache)
                {
                    NativeMethods.ClearSFUserProcessDataCache();
                }
            }
            ObserverLogger.LogInfo("Completed CleanUp...");
        }
    }
}