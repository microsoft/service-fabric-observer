// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsProcessInfoProvider : ProcessInfoProvider
    {
        private const int MaxDescendants = 50;
        private const int MaxSameNamedProcesses = 50;
        private const int MaxInstanceNameLengthTruncated = 64;
        private const string ProcessCategoryName = "Process";
        private const string ProcessMemoryCounterName = "Working Set - Private";
        private const string ProcessIDCounterName = "ID Process";
        private const string WinFabDbCategoryName = "Windows Fabric Database";
        private const string LvidCounterName = "Long-Value Maximum LID";
        private static readonly object lockObj = new();
        private static readonly object lockUpdate = new();
        private volatile bool hasWarnedProcessNameLength = false;
        private static PerformanceCounter memCounter = null;
        private static PerformanceCounter internalProcNameCounter = null;
        private static PerformanceCounter lvidCounter = null;
        private static PerformanceCounterCategory performanceCounterCategory = null;
        public readonly static ConcurrentDictionary<string, (string procName, int procId, DateTime processStartTime)> InstanceNameDictionary = new();

        private static PerformanceCounter ProcessWorkingSetCounter
        {
            get
            {
                if (memCounter == null)
                {
                    lock (lockObj)
                    {
                        if (memCounter == null)
                        {
                            memCounter = new(ProcessCategoryName, ProcessMemoryCounterName, true);
                        }
                    }
                }
                return memCounter;
            }
        }

        private static PerformanceCounter ProcNameCounter
        {
            get
            {
                if (internalProcNameCounter == null)
                {
                    lock (lockObj)
                    {
                        if (internalProcNameCounter == null)
                        {
                            internalProcNameCounter = new(ProcessCategoryName, ProcessIDCounterName, true);
                        }
                    }
                }
                return internalProcNameCounter;
            }
        }

        private static PerformanceCounter LvidCounter
        {
            get
            {
                if (lvidCounter == null)
                {
                    lock (lockObj)
                    {
                        if (lvidCounter == null)
                        {
                            lvidCounter = new(WinFabDbCategoryName, LvidCounterName, true);
                        }
                    }
                }
                return lvidCounter;
            }
        }

        private static PerformanceCounterCategory PerfCounterProcessCategory
        {
            get
            {
                if (performanceCounterCategory == null)
                {
                    lock (lockObj)
                    {
                        if (performanceCounterCategory == null)
                        {
                            performanceCounterCategory = new(ProcessCategoryName);
                        }
                    }
                }
                return performanceCounterCategory;
            }
        }

        public override float GetProcessWorkingSetMb(int processId, string procName, CancellationToken token, bool getPrivateWorkingSet = false)
        {
            if (string.IsNullOrWhiteSpace(procName) || processId <= 0)
            {
                return 0F;
            }

            if (NativeMethods.GetProcessNameFromId(processId) != procName) 
            { 
                return 0F; 
            }

            if (getPrivateWorkingSet)
            {
                // Private Working Set from Perf Counter (Working Set - Private). Very slow when there are lots of *same-named* processes.
                return GetProcessMemoryMbPerfCounter(procName, processId, token);
            }

            // Full Working Set (Private + Shared) from psapi.dll. Very fast.
            return GetProcessMemoryMbWin32(processId);
        }

        /// <summary>
        /// Gets the specified process's private memory usage, defined as the Commit Charge value in bytes for the process with the specified processId. 
        /// Commit Charge is the total amount of private memory that the memory manager has committed for a running process.)
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <returns>Current Private Bytes usage in Megabytes.</returns>
        public override float GetProcessPrivateBytesMb(int processId)
        {
            if (processId <= 0)
            {
                return 0F;
            }

            return GetProcessMemoryMbWin32(processId, getPrivateBytes: true);
        }

        public override float GetProcessAllocatedHandles(int processId, string configPath = null)
        {
            return GetProcessHandleCountWin32(processId);
        }

        public override List<(string ProcName, int Pid, DateTime ProcessStartTime)> GetChildProcessInfo(int parentPid, NativeMethods.SafeObjectHandle handleToSnapshot)
        {
            // Get descendant procs.
            List<(string ProcName, int Pid, DateTime ProcessStartTime)> childProcesses = TupleGetChildProcessesWin32(parentPid, handleToSnapshot);

            if (childProcesses == null || !childProcesses.Any())
            {
                return null;
            }

            if (childProcesses.Count >= MaxDescendants)
            {
                return childProcesses.Take(MaxDescendants).ToList();
            }

            // Get descendant proc at max depth = 5 and max number of descendants = 50. 
            for (int i = 0; i < childProcesses.Count; ++i)
            {
                List<(string ProcName, int Pid, DateTime ProcessStartTime)> c1 = TupleGetChildProcessesWin32(childProcesses[i].Pid, handleToSnapshot);

                if (c1 == null || !c1.Any())
                {
                    continue;
                }

                childProcesses.AddRange(c1);

                if (childProcesses.Count >= MaxDescendants)
                {
                    return childProcesses.Take(MaxDescendants).ToList();
                }

                for (int j = 0; j < c1.Count; ++j)
                {
                    List<(string ProcName, int Pid, DateTime ProcessStartTime)> c2 = TupleGetChildProcessesWin32(c1[j].Pid, handleToSnapshot);

                    if (c2 == null || !c2.Any())
                    {
                        continue;
                    }

                    childProcesses.AddRange(c2);

                    if (childProcesses.Count >= MaxDescendants)
                    {
                        return childProcesses.Take(MaxDescendants).ToList();
                    }

                    for (int k = 0; k < c2.Count; ++k)
                    {
                        List<(string ProcName, int Pid, DateTime ProcessStartTime)> c3 = TupleGetChildProcessesWin32(c2[k].Pid, handleToSnapshot);

                        if (c3 == null || !c3.Any())
                        {
                            continue;
                        }

                        childProcesses.AddRange(c3);

                        if (childProcesses.Count >= MaxDescendants)
                        {
                            return childProcesses.Take(MaxDescendants).ToList();
                        }

                        for (int l = 0; l < c3.Count; ++l)
                        {
                            List<(string ProcName, int Pid, DateTime ProcessStartTime)> c4 = TupleGetChildProcessesWin32(c3[l].Pid, handleToSnapshot);

                            if (c4 == null || !c4.Any())
                            {
                                continue;
                            }

                            childProcesses.AddRange(c4);

                            if (childProcesses.Count >= MaxDescendants)
                            {
                                return childProcesses.Take(MaxDescendants).ToList();
                            }
                        }
                    }
                }
            }

            if (childProcesses != null && childProcesses.Count > 1)
            {
                try
                {
                    childProcesses = childProcesses.DistinctBy(p => p.Pid).ToList();
                }
                catch (ArgumentException)
                {

                }
            }

            return childProcesses;
        }

        private static List<(string procName, int pid, DateTime ProcessStartTime)> 
            TupleGetChildProcessesWin32(int processId, NativeMethods.SafeObjectHandle handleToSnapshot)
        {
            try
            {
                string parentProcName = NativeMethods.GetProcessNameFromId(processId);
                List<(string procName, int procId, DateTime ProcessStartTime)> childProcs =
                    NativeMethods.GetChildProcesses(processId, parentProcName, handleToSnapshot);

                if (childProcs == null || childProcs.Count == 0)
                {
                    return null;
                }

                return childProcs;
            }

            catch (Win32Exception we) // e.g., process is no longer running.
            {
                ProcessInfoLogger.LogWarning($"Handled Exception in TupleGetChildProcessesWin32: {we.Message}. " +
                                             $"Error code: {we.NativeErrorCode}. Process Id: {processId}");
            }
            catch (Exception e)
            {
                // Log the full error(including stack trace) for debugging purposes.
                ProcessInfoLogger.LogError($"Unhandled Exception in TupleGetChildProcessesWin32:{Environment.NewLine}{e}");
                throw;
            }

            return null;
        }

        public override double GetProcessKvsLvidsUsagePercentage(string procName, CancellationToken token, int procId = 0)
        {
            if (string.IsNullOrWhiteSpace(procName) || token.IsCancellationRequested)
            {
                return -1;
            }

            string internalProcName = procName;

            try
            {
                // This is the case when the caller expects there could be multiple instances of the same process.
                if (procId > 0)
                {
                    int procCount = NativeMethods.GetSFUserServiceProcessCountByName(procName);

                    if (procCount == 0)
                    {
                        return 0;
                    }

                    try
                    {
                        internalProcName = GetInternalProcessName(procName, procId, token);

                        if (internalProcName == null)
                        {
                            return -1;
                        }
                    }
                    catch (InvalidOperationException e)
                    {
                        ProcessInfoLogger.LogWarning(
                            $"GetProcessKvsLvidsUsagePercentage (Returning -1): Handled Exception from GetInternalProcessName.{Environment.NewLine}" +
                            $"The specified process (name: {procName}, pid: {procId}) isn't the droid we're looking for: {e.Message}");

                        return -1;
                    }
                }

                /* Check to see if the supplied instance (process) exists in the category. */

                if (!PerformanceCounterCategory.InstanceExists(internalProcName, WinFabDbCategoryName))
                {
                    return -1;
                }

                /* A note on exception handling:
                   AppObserver and FSO check ObserverManager.IsLvidCounterEnabled before calling this function. Therefore, chances of encountering
                   an exception when creating the PC or when calling its NextValue function is highly unlikely. That said, exceptions happen...
                   The target counter is accessible to processes running as Network User (so, no UnauthorizedAccessException).
                   categoryName and counterName are never null (they are const strings).
                   Only two possible exceptions can happen here: IOE and Win32Exception. */

                lock (lockUpdate)
                {
                    LvidCounter.InstanceName = internalProcName;
                    float result = LvidCounter.NextValue();
                    double usedPct = (double)(result * 100) / int.MaxValue;

                    return usedPct;
                }
            }
            catch (InvalidOperationException ioe)
            {
                // The Counter layout for the Category specified is invalid? This can happen if a user messes around with Reg key values. Not likely.
                ProcessInfoLogger.LogWarning($"GetProcessKvsLvidsUsagePercentage: Handled InvalidOperationException: {ioe.Message}");
            }
            catch (Win32Exception we)
            {
                // Internal exception querying counter (Win32 code). There is nothing to do here. Log the details. Most likely transient.
                ProcessInfoLogger.LogWarning($"GetProcessKvsLvidsUsagePercentage: Handled Win32Exception: {we.Message}");
            }

            return -1;
        }

        private static int GetProcessHandleCountWin32(int processId)
        {
            SafeProcessHandle handle = null;

            try
            {
                uint handles = 0;
                handle = NativeMethods.GetSafeProcessHandle(processId);

                if (handle.IsInvalid || !NativeMethods.GetProcessHandleCount(handle, out handles))
                {
                    // The related Observer will have logged any privilege related failure.
                    if (Marshal.GetLastWin32Error() != 5)
                    {
                        ProcessInfoLogger.LogWarning($"GetProcessHandleCountWin32 for process id {processId}: Failed with Win32 error code {Marshal.GetLastWin32Error()}.");
                    }
                }

                return (int)handles;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Access denied (FO is running as a less privileged user than the target process).
                if (e is Win32Exception && (e as Win32Exception).NativeErrorCode != 5)
                {
                    ProcessInfoLogger.LogWarning($"GetProcessHandleCountWin32: Exception getting working set for process {processId}:{Environment.NewLine}{e.Message}");
                }

                return 0;
            }
            finally
            {
                handle?.Dispose();
                handle = null;
            }
        }

        /// <summary>
        /// Gets memory usage for a process with specified processId. 
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <param name="getPrivateBytes">Whether or not to return Private Bytes (The Commit Charge value in bytes for this process. 
        /// Commit Charge is the total amount of private memory that the memory manager has committed for a running process.)</param>
        /// <returns>Process memory usage expressed as Megabytes.</returns>
        private static float GetProcessMemoryMbWin32(int processId, bool getPrivateBytes = false)
        {
            if (processId < 1)
            {
                ProcessInfoLogger.LogWarning($"GetProcessMemoryMbWin32: Process ID is an unsupported value ({processId}). Returning 0F.");
                return 0F;
            }

            SafeProcessHandle handle = null;

            try
            {
                NativeMethods.PROCESS_MEMORY_COUNTERS_EX memoryCounters;
                memoryCounters.cb = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESS_MEMORY_COUNTERS_EX));
                handle = NativeMethods.GetSafeProcessHandle(processId);

                if (handle.IsInvalid)
                {
                    throw new Win32Exception($"NativeMethods.GetSafeProcessHandle returned invalid handle: error {Marshal.GetLastWin32Error()}");
                }

                if (!NativeMethods.GetProcessMemoryInfo(handle, out memoryCounters, memoryCounters.cb))
                {
                    throw new Win32Exception($"NativeMethods.GetProcessMemoryInfo failed with Win32 error {Marshal.GetLastWin32Error()}");
                }

                if (getPrivateBytes)
                {
                    return memoryCounters.PrivateUsage.ToUInt64() / 1024 / 1024;
                }

                return memoryCounters.WorkingSetSize.ToUInt64() / 1024 / 1024;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
                string message = $"GetProcessMemoryMbWin32({processId}) failure: {e.Message}";
                ProcessInfoLogger.LogWarning(message);
                ProcessInfoLogger.LogEtw(
                    ObserverConstants.FabricObserverETWEventName,
                    new
                    {
                        Error = message,
                        EntityType = EntityType.Service.ToString(),
                        ProcessId = processId.ToString(),
                        GetPrivateBytes = getPrivateBytes,
                        Level = "Warning"
                    });

                return 0F;
            }
            finally
            {
                handle?.Dispose();
                handle = null;
            }
        }

        private float GetProcessMemoryMbPerfCounter(string procName, int procId, CancellationToken token, string perfCounterName = "Working Set - Private")
        {
            if (string.IsNullOrWhiteSpace(procName) || procId < 1)
            {
                string message = $"GetProcessMemoryMbPerfCounter: Unsupported process information provided ({procName ?? "null"}, {procId})";
                ProcessInfoLogger.LogWarning(message);
                ProcessInfoLogger.LogEtw(
                    ObserverConstants.FabricObserverETWEventName,
                    new
                    {
                        Level = "Warning",
                        Message = message,
                        EntityType = EntityType.Service.ToString(),
                        ProcessName = procName ?? "null",
                        ProcessId = procId.ToString()
                    });

                return 0F;
            }

            try
            {
                if (!NativeMethods.GetProcessNameFromId(procId).Equals(procName, StringComparison.OrdinalIgnoreCase))
                {
                    return 0F;
                }
            }
            catch (Win32Exception ex) 
            {
                // The related Observer will have logged any privilege related failure.
                if (ex.NativeErrorCode != 5)
                {
                    string message = $"GetProcessMemoryMbPerfCounter: The specified process (name: {procName}, pid: {procId}) isn't the droid we're looking for. " +
                                     $"Win32 error: {Marshal.GetLastWin32Error()}";

                    ProcessInfoLogger.LogWarning(message);
                    ProcessInfoLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Level = "Warning",
                            Message = message,
                            EntityType = EntityType.Service.ToString(),
                            ProcessName = procName,
                            ProcessId = procId.ToString()
                        });
                }

                return 0F;
            }

            // Handle the case where supplied process name exceeds the maximum length (64) supported by PerformanceCounter's InstanceName field (.NET Core).
            // This should be very rare given this is a Windows/.NET platform restriction and users should understand the limits of the platform they use. However,
            // the documentation is confusing: One (doc) says 128 chars is the max value. The other (source code comment) says 127. In reality, 
            // it is 64 for .NET Core, based on my tests.
            if (procName.Length >= MaxInstanceNameLengthTruncated)
            {
                // Only log this once to limit disk IO noise and log file size.
                if (!hasWarnedProcessNameLength)
                {
                    lock (lockObj)
                    {
                        if (!hasWarnedProcessNameLength)
                        {
                            ProcessInfoLogger.LogWarning(
                                $"GetProcessMemoryMbPerfCounter: Process name {procName} exceeds max length (64) for Performance Counter InstanceName (.NET Core) property. " +
                                $"Supplying Full Working Set (Private + Shared, Win32 API) value instead. " +
                                $"Will not log this again until FO restarts.");

                            hasWarnedProcessNameLength = true;
                        }
                    }
                }
                return GetProcessMemoryMbWin32(procId);
            }

            string internalProcName;

            try
            {
                internalProcName = GetInternalProcessName(procName, procId, token);

                if (internalProcName == null)
                {
                    return 0F;
                }
            }
#if RELEASE
            catch (InvalidOperationException)
            {
# endif
#if DEBUG
            catch (InvalidOperationException e)
            {
                // Most likely the process isn't the one we are looking for (current procId no longer maps to internal procName as contained in the same-named proc data cache).

                ProcessInfoLogger.LogWarning(
                    $"GetProcessMemoryMbPerfCounter (Returning 0): Handled Exception from GetInternalProcessName.{Environment.NewLine}" +
                    $"The specified process (name: {procName}, pid: {procId}) isn't the droid we're looking for: {e.Message}");
#endif
                return 0F;
            }

            try
            {
                lock (lockUpdate)
                {
                    ProcessWorkingSetCounter.InstanceName = internalProcName;
                    _ = ProcessWorkingSetCounter.RawValue;
                    Thread.Sleep(1000);

                    return ProcessWorkingSetCounter.NextValue() / 1024 / 1024;
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
            {
                string message = $"Handled exception in GetProcessMemoryMbPerfCounter. Returning 0. Exception message: {e.Message}";
                ProcessInfoLogger.LogWarning(message);
                ProcessInfoLogger.LogEtw(
                    ObserverConstants.FabricObserverETWEventName,
                    new
                    {
                        Level = "Warning",
                        Message = message,
                        EntityType = EntityType.Service.ToString(),
                        ProcessName = procName,
                        ProcessId = procId.ToString()
                    });
            }
            catch (Exception e)
            {
                // Log the full error (including stack trace) for debugging purposes.
                ProcessInfoLogger.LogWarning($"Unhandled exception in GetProcessMemoryMbPerfCounter. Exception:{Environment.NewLine}{e}");
                throw;
            }

            return 0F;
        }

        private static string GetInternalProcessName(string procName, int pid, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                string pName = NativeMethods.GetProcessNameFromId(pid);

                if (pName == null || !pName.Equals(procName, StringComparison.OrdinalIgnoreCase))
                {
                    // The related Observer will have logged any privilege related failure.
                    ProcessInfoLogger.LogInfo($"GetInternalProcessName: Process Name ({procName}) is no longer mapped to supplied ID ({pid}).");
                    return null;
                }

                int procCount = NativeMethods.GetSFUserServiceProcessCountByName(procName);

                if (procCount == 0)
                {
                    ProcessInfoLogger.LogInfo($"GetInternalProcessName: Process Name ({procName}) is no longer mapped to supplied ID ({pid}).");
                    return null;
                }

                if (procCount == 1)
                {
                    return procName;
                }

                if (procCount < MaxSameNamedProcesses)
                {
                    return GetInternalProcNameFromId(procName, pid, token);
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
#if DEBUG
                ProcessInfoLogger.LogWarning($"GetInternalProcessName Failure: {e.Message}. ProcName = {procName}, Pid = {pid}");
#endif
            }
            catch (Exception e) when (e is not (OperationCanceledException or TaskCanceledException))
            {
                // Log the full error (including stack trace) for debugging purposes. 
                ProcessInfoLogger.LogError(
                    $"Unhandled exception in GetInternalProcessName: Unable to determine internal process name for {procName} with id {pid}{Environment.NewLine}{e}");

                throw;
            }

            return procName;
        }

        private static string GetInternalProcNameFromId(string procName, int pid, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                if (InstanceNameDictionary != null && InstanceNameDictionary.Any(p => p.Value.procName.Equals(procName, StringComparison.OrdinalIgnoreCase)))
                {
                    var instanceData = InstanceNameDictionary.Where(p => p.Value.procName == procName).ToArray();

                    if (instanceData.Any(p => p.Value.procId == pid))
                    {
                        for (int i = 0; i < instanceData.Length; ++i)
                        {
                            var instance = instanceData[i];
                            DateTime processStartTime = instance.Value.processStartTime;
                            string processName = instance.Value.procName;
                            string key = instance.Key;

                            if (instance.Value.procId == pid)
                            {
                                // Same process?
                                if (NativeMethods.GetProcessNameFromId(pid).Equals(processName, StringComparison.OrdinalIgnoreCase)
                                    && NativeMethods.GetProcessStartTime(pid) == processStartTime)
                                {
                                    return instance.Key;
                                }

                                // Remove from cache.
                                _ = InstanceNameDictionary.TryRemove(key, out _);
                            }
                        }
                    }

                    // If we get here, then none of the related processes are current. Remove all related existing instance names from the cache.
                    for (int i = 0; i < instanceData.Length; ++i)
                    {
                        var instance = instanceData[i];
                        string key = instance.Key;
                        _ = InstanceNameDictionary.TryRemove(key, out _);
                    }
                }

                var instances =
                    PerfCounterProcessCategory.GetInstanceNames().Where(
                        inst => inst.Equals(procName, StringComparison.OrdinalIgnoreCase)
                             || inst.StartsWith($"{procName}#", StringComparison.OrdinalIgnoreCase));

                foreach (string instance in instances)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        lock (lockUpdate)
                        {
                            ProcNameCounter.InstanceName = instance;
                            var sample = ProcNameCounter.NextSample();

                            if (pid != (int)sample.RawValue)
                            {
                                continue;
                            }

                            _ = InstanceNameDictionary.TryAdd(instance, (procName, pid, NativeMethods.GetProcessStartTime(pid)));
                            return instance;
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
                    {

                    }
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
            {

            }

            return null;
        }
    }
}