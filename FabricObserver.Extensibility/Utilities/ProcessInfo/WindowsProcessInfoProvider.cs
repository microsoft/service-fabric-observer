// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;
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
        private static readonly object _lock = new object();
        private readonly object _lockUpdate = new object();
        private volatile bool hasWarnedProcessNameLength = false;
        private DateTime sameNamedProcCacheLastUpdated = DateTime.MinValue;
        private TimeSpan maxLifetimeForProcCache = TimeSpan.FromMinutes(3);
        private readonly ConcurrentDictionary<string, List<(string InternalName, int Pid)>> _procCache =
            new ConcurrentDictionary<string, List<(string InternalName, int Pid)>>();

        public override float GetProcessWorkingSetMb(int processId, string procName, CancellationToken token, bool getPrivateWorkingSet = false)
        {
            if (string.IsNullOrWhiteSpace(procName) || processId <= 0)
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

            return GetProcessMemoryMbWin32(processId, getPrivateBytes:true);
        }

        public override float GetProcessAllocatedHandles(int processId, string configPath = null)
        {
            return GetProcessHandleCountWin32(processId);
        }

        public override List<(string ProcName, int Pid)> GetChildProcessInfo(int parentPid, NativeMethods.SafeObjectHandle handleToSnapshot = null)
        {
            // Get descendant procs.
            List<(string ProcName, int Pid)> childProcesses = TupleGetChildProcessesWin32(parentPid, handleToSnapshot);

            if (childProcesses == null || childProcesses.Count == 0)
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
                List<(string ProcName, int Pid)> c1 = TupleGetChildProcessesWin32(childProcesses[i].Pid, handleToSnapshot);

                if (c1 == null || c1.Count <= 0)
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
                    List<(string ProcName, int Pid)> c2 = TupleGetChildProcessesWin32(c1[j].Pid, handleToSnapshot);

                    if (c2 == null || c2.Count <= 0)
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
                        List<(string ProcName, int Pid)> c3 = TupleGetChildProcessesWin32(c2[k].Pid, handleToSnapshot);

                        if (c3 == null || c3.Count <= 0)
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
                            List<(string ProcName, int Pid)> c4 = TupleGetChildProcessesWin32(c3[l].Pid, handleToSnapshot);

                            if (c4 == null || c4.Count <= 0)
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

            return childProcesses;
        }

        public override double GetProcessKvsLvidsUsagePercentage(string procName, CancellationToken token, int procId = -1)
        {
            if (string.IsNullOrWhiteSpace(procName))
            {
                return -1;
            }

            const string categoryName = "Windows Fabric Database";
            const string counterName = "Long-Value Maximum LID";
            string internalProcName = procName;
            PerformanceCounter performanceCounter = null;

            try
            {
                // This is the case when the caller expects there could be multiple instances of the same process.
                if (procId > 0)
                {
                    int procCount = Process.GetProcessesByName(procName).Length;

                    if (procCount == 0)
                    {
                        return 0;
                    }

                    // Make sure the correct process is the one we compute memory usage for (re: multiple processes of the same name..).
                    if (procCount >= MaxSameNamedProcesses && DateTime.UtcNow.Subtract(sameNamedProcCacheLastUpdated) >= maxLifetimeForProcCache)
                    {
                        lock (_lockUpdate)
                        {
                            if (procCount >= MaxSameNamedProcesses && DateTime.UtcNow.Subtract(sameNamedProcCacheLastUpdated) >= maxLifetimeForProcCache)
                            {
                                // Looking up pids using "ID Process" counter is way too slow. Implementing a short-lived cache containing procName keys 
                                // and List of (internal procName, pid) tuple values is a satisfactory (though not perfect, not best..) solution for what FO needs.
                                RefreshSameNamedProcCache(procName, token);
                            }
                        }
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
                        Logger.LogWarning($"GetProcessKvsLvidsUsagePercentage (Returning -1): Handled Exception from GetInternalProcessName.{Environment.NewLine}" +
                                          $"The specified process (name: {procName}, pid: {procId}) isn't the droid we're looking for: {e.Message}");
                        return -1;
                    }
                }

                /* Check to see if the supplied instance (process) exists in the category. */

                if (!PerformanceCounterCategory.InstanceExists(internalProcName, categoryName))
                {
                    return -1;
                }

                /* A note on exception handling:
                   AppObserver and FSO check ObserverManager.IsLvidCounterEnabled before calling this function. Therefore, chances of encountering
                   an exception when creating the PC or when calling its NextValue function is highly unlikely. That said, exceptions happen...
                   The target counter is accessible to processes running as Network User (so, no UnauthorizedAccessException).
                   categoryName and counterName are never null (they are const strings).
                   Only two possible exceptions can happen here: IOE and Win32Exception. */

                performanceCounter = new PerformanceCounter(
                                            categoryName,
                                            counterName,
                                            instanceName: internalProcName,
                                            readOnly: true);

                float result = performanceCounter.NextValue();
                double usedPct = (double)(result * 100) / int.MaxValue;

                return usedPct;
            }
            catch (InvalidOperationException ioe)
            {
                // The Counter layout for the Category specified is invalid? This can happen if a user messes around with Reg key values. Not likely.
                Logger.LogWarning($"GetProcessKvsLvidsUsagePercentage: Handled Win32Exception:{Environment.NewLine}{ioe.Message}");
            }
            catch (Win32Exception we)
            {
                // Internal exception querying counter (Win32 code). There is nothing to do here. Log the details. Most likely transient.
                Logger.LogWarning($"GetProcessKvsLvidsUsagePercentage: Handled Win32Exception:{Environment.NewLine}{we.Message}");
            }
            finally
            {
                performanceCounter?.Dispose();
                performanceCounter = null;
            }

            return -1;
        }

        private List<(string procName, int pid)> TupleGetChildProcessesWin32(int processId, NativeMethods.SafeObjectHandle handleToSnapshot)
        {
            try
            {
                List<(string procName, int procId)> childProcs = NativeMethods.GetChildProcesses(processId, handleToSnapshot);

                if (childProcs == null || childProcs.Count == 0)
                {
                    return null;
                }

                return childProcs;
            }

            catch (Exception e) when (e is Win32Exception) // e.g., process is no longer running.
            {
                Logger.LogWarning($"Handled Exception in TupleGetChildProcessesWin32:{Environment.NewLine}{e.Message}");
            }
            catch (Exception e)
            {
                // Log the full error(including stack trace) for debugging purposes.
                Logger.LogError($"Unhandled Exception in TupleGetChildProcessesWin32:{Environment.NewLine}{e}");
                throw;
            }

            return null;
        }

        private int GetProcessHandleCountWin32(int processId)
        {
            SafeProcessHandle handle = null;

            try
            {
                uint handles = 0;
                handle = NativeMethods.GetSafeProcessHandle((uint)processId);

                if (handle.IsInvalid || !NativeMethods.GetProcessHandleCount(handle, out handles))
                {
                    // The related Observer will have logged any privilege related failure.
                    if (Marshal.GetLastWin32Error() != 5)
                    {
                        Logger.LogWarning($"GetProcessHandleCountWin32 for process id {processId}: Failed with Win32 error code {Marshal.GetLastWin32Error()}.");
                    }
                }

                return (int)handles;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                // Access denied (FO is running as a less privileged user than the target process).
                if (e is Win32Exception && (e as Win32Exception).NativeErrorCode != 5)
                {
                    Logger.LogWarning($"GetProcessHandleCountWin32: Exception getting working set for process {processId}:{Environment.NewLine}{e.Message}");
                }

                return -1;
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
        private float GetProcessMemoryMbWin32(int processId, bool getPrivateBytes = false)
        {
            if (processId < 1)
            {
                Logger.LogWarning($"GetProcessMemoryMbWin32: Process ID is an unsupported value ({processId}). Returning 0F.");
                return 0F;
            }

            SafeProcessHandle handle = null;

            try
            {
                NativeMethods.PROCESS_MEMORY_COUNTERS_EX memoryCounters;
                memoryCounters.cb = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESS_MEMORY_COUNTERS_EX));
                handle = NativeMethods.GetSafeProcessHandle((uint)processId);

                if (handle.IsInvalid || !NativeMethods.GetProcessMemoryInfo(handle, out memoryCounters, memoryCounters.cb))
                {
                    throw new Win32Exception($"GetProcessMemoryInfo failed with Win32 error {Marshal.GetLastWin32Error()}");
                }

                if (getPrivateBytes)
                {
                    return memoryCounters.PrivateUsage.ToUInt64() / 1024 / 1024;
                }

                return memoryCounters.WorkingSetSize.ToUInt64() / 1024 / 1024;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                Logger.LogWarning($"GetProcessMemoryMbWin32: Exception getting working set for process {processId}: {e.Message}");
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
                Logger.LogWarning($"GetProcessMemoryMbPerfCounter: Unsupported process information provided ({procName ?? "null"}, {procId})");
                return 0F;
            }

            if (NativeMethods.GetProcessNameFromId(procId) != procName)
            {
                // The related Observer will have logged any privilege related failure.
                if (Marshal.GetLastWin32Error() != 5)
                {
                    Logger.LogWarning($"GetProcessMemoryMbPerfCounter: The specified process (name: {procName}, pid: {procId}) isn't the droid we're looking for. " +
                                      $"Error Code: {Marshal.GetLastWin32Error()}");
                }

                return 0F;
            }

            // Handle the case where supplied process name exceeds the maximum length (64) supported by PerformanceCounter's InstanceName field (.NET Core 3.1).
            // This should be very rare given this is a Windows/.NET platform restriction and users should understand the limits of the platform they use. However,
            // the documentation (and source code comments) are confusing: One (doc) says 128 chars is max value. The other (source code comment) says 127. In reality, 
            // it is 64 for .NET Core 3.1, based on my tests..
            if (procName.Length >= MaxInstanceNameLengthTruncated)
            {
                // Only log this once to limit disk IO noise and log file size.
                if (!hasWarnedProcessNameLength)
                {
                    lock (_lock)
                    {
                        if (!hasWarnedProcessNameLength)
                        {
                            Logger.LogWarning(
                                $"Process name {procName} exceeds max length (64) for InstanceName (.NET Core 3.1). Supplying Full Working Set (Private + Shared) value instead (no PerformanceCounter usage). " +
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

                Logger.LogWarning($"GetProcessMemoryMbPerfCounter (Returning 0): Handled Exception from GetInternalProcessName.{Environment.NewLine}" +
                                  $"The specified process (name: {procName}, pid: {procId}) isn't the droid we're looking for: {e.Message}");
#endif
                return 0F;
            }

            try
            {
                using (PerformanceCounter perfCounter = new PerformanceCounter("Process", perfCounterName, internalProcName, true))
                {
                    return perfCounter.NextValue() / 1024 / 1024;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is UnauthorizedAccessException || e is Win32Exception)
            {
                Logger.LogWarning($"Handled exception in GetProcessMemoryMbPerfCounter: Returning 0.{Environment.NewLine}{e.Message}");
            }
            catch (Exception e)
            {
                // Log the full error (including stack trace) for debugging purposes.
                Logger.LogWarning($"Unhandled exception in GetProcessMemoryMbPerfCounter:{Environment.NewLine}{e}");
                throw;
            }

            return 0F;
        }

        private string GetInternalProcessName(string procName, int pid, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                if (NativeMethods.GetProcessNameFromId(pid) != procName)
                {
                    // The related Observer will have logged any privilege related failure.
                    if (Marshal.GetLastWin32Error() != 5)
                    {
                        Logger.LogWarning($"GetInternalProcessName: Process Name ({procName}) is no longer mapped to supplied ID ({pid}): {Marshal.GetLastWin32Error()}.");
                    }

                    return null;
                }

                int procCount = Process.GetProcessesByName(procName).Length;

                if (procCount == 1)
                {
                    return procName;
                }

                if (procCount < MaxSameNamedProcesses)
                {
                    return GetInternalProcNameFromId(procName, pid, token);
                }

                if (_procCache.ContainsKey(procName) && _procCache[procName].Any(inst => inst.Pid == pid))
                {
                    return _procCache[procName].First(inst => inst.Pid == pid).InternalName;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
#if DEBUG
                Logger.LogWarning($"GetInternalProcessName: Failure getting data from cache. Name: {procName}, Pid: {pid}");
#endif
            }
            catch (Exception e) when (!(e is InvalidOperationException || e is OperationCanceledException || e is TaskCanceledException))
            {
                // Log the full error (including stack trace) for debugging purposes. Note: Caller must handle InvalidOperationException as in this case it likely means
                // the process no longer exists with the same id (or internal name). So, don't re-throw as Unhandled here.
                Logger.LogError(
                    $"Unhandled exception in GetInternalProcessName: Unable to determine internal process name for {procName} with id {pid}{Environment.NewLine}{e}");

                throw;
            }

            return procName;
        }

        private string GetInternalProcNameFromId(string procName, int pid, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            PerformanceCounter cnt = null;

            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Process");
                var instances = cat.GetInstanceNames().Where(inst => inst == procName || inst.StartsWith($"{procName}#"));
                cnt = new PerformanceCounter("Process", "ID Process", true);

                foreach (string instance in instances)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        cnt.InstanceName = instance;
                        var sample = cnt.NextSample();

                        if (pid != (int)sample.RawValue)
                        {
                            continue;
                        }

                        return instance;
                    }
                    catch (Exception e) when (e is InvalidOperationException || e is UnauthorizedAccessException || e is Win32Exception)
                    {

                    }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is UnauthorizedAccessException || e is Win32Exception)
            {

            }
            finally
            {
                cnt?.Dispose();
                cnt = null;
            }

            return procName;
        }

        private void RefreshSameNamedProcCache(string procName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            PerformanceCounter cnt = null;

            try
            {
                _procCache.Clear();
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Process");
                var instances = cat.GetInstanceNames().Where(inst => inst == procName || inst.StartsWith($"{procName}#"));
                cnt = new PerformanceCounter("Process", "ID Process", true);

                foreach (string instance in instances)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        cnt.InstanceName = instance;
                        var sample = cnt.NextSample();

                        if (!_procCache.ContainsKey(procName))
                        {
                            _ = _procCache.TryAdd(procName, new List<(string InternalName, int Pid)> { (instance, (int)sample.RawValue) });
                        }
                        else
                        {
                            _procCache[procName].Add((instance, (int)sample.RawValue));
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception || e is UnauthorizedAccessException)
                    {

                    }
                }

                sameNamedProcCacheLastUpdated = DateTime.UtcNow;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception || e is UnauthorizedAccessException)
            {

            }
            finally
            {
                cnt?.Dispose();
                cnt = null;
            }
        }
    }
}