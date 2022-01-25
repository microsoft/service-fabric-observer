// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsProcessInfoProvider : ProcessInfoProvider
    {
        private const int MaxDescendants = 50;
       
        public override float GetProcessWorkingSetMb(int processId, string procName = null, bool getPrivateWorkingSet = false)
        {
            if (!string.IsNullOrWhiteSpace(procName) && getPrivateWorkingSet)
            {
                return GetProcessPrivateWorkingSetMbFromPerfCounter(procName, processId);
            }

            return NativeGetProcessWorkingSet(processId); 
        }

        public override float GetProcessAllocatedHandles(int processId, StatelessServiceContext context = null, bool useProcessObject = false)
        {
            if (useProcessObject)
            {
                return ProcessGetProcessAllocatedHandles(processId);
            }
            else
            {
                return WmiGetProcessAllocatedHandles(processId);
            }
        }

        public override List<(string ProcName, int Pid)> GetChildProcessInfo(int parentPid)
        {
            if (parentPid < 1)
            {
                return null;
            }

            // Get descendant procs.
            List<(string ProcName, int Pid)> childProcesses = TupleGetChildProcessesWin32(parentPid);

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
                List<(string ProcName, int Pid)> c1 = TupleGetChildProcessesWin32(childProcesses[i].Pid);

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
                    List<(string ProcName, int Pid)> c2 = TupleGetChildProcessesWin32(c1[j].Pid);

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
                        List<(string ProcName, int Pid)> c3 = TupleGetChildProcessesWin32(c2[k].Pid);

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
                            List<(string ProcName, int Pid)> c4 = TupleGetChildProcessesWin32(c3[l].Pid);

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

        public override double GetProcessKvsLvidsUsagePercentage(string procName, int procId = -1)
        {
            try
            {
                string internalProcName = procName;

                if (procId > 0)
                {
                    internalProcName = GetInternalProcessNameFromPerfCounter(procName, procId);
                }

                using (var performanceCounter = new PerformanceCounter(
                                                    categoryName: "Windows Fabric Database",
                                                    counterName: "Long-Value Maximum LID",
                                                    instanceName: internalProcName,
                                                    readOnly: true))
                {
                    float result = performanceCounter.NextValue();
                    double usedPct = (double)(result * 100) / int.MaxValue;
                    return usedPct;
                }
            }
            catch (Exception e) when (e is InvalidOperationException)
            {
                // We land here when the target service process is not using KVS (which we can't determine by querying SF (yet))
                // OR the performance counter is not found/enabled.
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Exception querying Long-Value Maximum LID counter:{Environment.NewLine}{e}");
            }

            // This means failure. Consumer should handle the case when the result is less than 0..
            return -1;
        }

        private float ProcessGetProcessAllocatedHandles(int processId)
        {
            if (processId < 0)
            {
                return -1F;
            }

            try
            {
                using (Process p = Process.GetProcessById(processId))
                {
                    p.Refresh();
                    return p.HandleCount;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is SystemException)
            {
                return -1F;
            }
        }

        private float WmiGetProcessAllocatedHandles(int processId)
        {
            if (processId < 0)
            {
                return 0F;
            }

            string query = $"select handlecount from win32_process where processid = {processId}";

            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    var results = searcher.Get();

                    if (results.Count == 0)
                    {
                        return 0F;
                    }

                    using (ManagementObjectCollection.ManagementObjectEnumerator enumerator = results.GetEnumerator())
                    {
                        while (enumerator.MoveNext())
                        {
                            try
                            {
                                using (ManagementObject mObj = (ManagementObject)enumerator.Current)
                                {
                                    uint procHandles = (uint)mObj.Properties["handlecount"].Value;
                                    return procHandles;
                                }
                            }
                            catch (Exception e) when (e is ArgumentException || e is ManagementException)
                            {
                                Logger.LogWarning($"[Inner try-catch] Handled Exception in GetProcessAllocatedHandles: {e}");
                                continue;
                            }
                        }
                    }
                }
            }
            catch (ManagementException me)
            {
                Logger.LogWarning($"[Outer try-catch] Handled Exception in GetProcessAllocatedHandles: {me}");
            }

            return 0F;
        }

        private List<(string procName, int pid)> TupleGetChildProcessesWin32(int processId)
        {
            if (processId < 0)
            {
                return null;
            }

            string[] ignoreProcessList = new string[] { "conhost", "csrss", "svchost", "wininit", "lsass" };
            List<(string procName, int pid)> childProcesses = null;

            try
            {
                var childProcs = NativeMethods.GetChildProcesses(processId);

                for (int i = 0; i < childProcs.Count; ++i)
                {
                    var proc = childProcs[i];   

                    if (!ignoreProcessList.Contains(proc.ProcessName))
                    {
                        if (childProcesses == null)
                        {
                            childProcesses = new List<(string procName, int pid)>();
                        }

                        childProcesses.Add((proc.ProcessName, proc.Id));
                    }

                    // Clean up Process objects.
                    proc.Dispose();
                }
            }
            catch (Win32Exception we)
            {
                Logger.LogInfo($"Handled Exception in TupleGetChildProcesses: {we}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Unhandled Exception (non-crashing) in TupleGetChildProcesses: {ex}");
            }

            return childProcesses;
        }

        // This is *really* expensive when run concurrently on node with lots of monitored services..
        private List<(string procName, int pid)> TupleGetChildProcessesWmi(int processId)
        {
            if (processId < 0)
            {
                return null;
            }

            string[] ignoreProcessList = new string[] { "conhost.exe", "csrss.exe", "svchost.exe", "wininit.exe" };
            List<(string procName, int pid)> childProcesses = null;
            string query = $"select caption,processid from win32_process where parentprocessid = {processId}";

            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    var results = searcher.Get();

                    using (ManagementObjectCollection.ManagementObjectEnumerator enumerator = results.GetEnumerator())
                    {
                        while (enumerator.MoveNext())
                        {
                            try
                            {
                                using (ManagementObject mObj = (ManagementObject)enumerator.Current)
                                {
                                    object childProcessIdObj = mObj.Properties["processid"].Value;
                                    object childProcessNameObj = mObj.Properties["caption"].Value;

                                    if (childProcessIdObj == null || childProcessNameObj == null)
                                    {
                                        continue;
                                    }

                                    if (ignoreProcessList.Contains(childProcessNameObj.ToString()))
                                    {
                                        continue;
                                    }

                                    if (childProcesses == null)
                                    {
                                        childProcesses = new List<(string procName, int pid)>();
                                    }

                                    int childProcessId = Convert.ToInt32(childProcessIdObj);
                                    string procName = childProcessNameObj.ToString();

                                    childProcesses.Add((procName, childProcessId));
                                }
                            }
                            catch (Exception e) when (e is ArgumentException || e is ManagementException)
                            {
                                Logger.LogWarning($"[Inner try-catch (enumeration)] Handled Exception in GetChildProcesses: {e}");
                                continue;
                            }
                        }
                    }
                }
            }
            catch (ManagementException me)
            {
                Logger.LogWarning($"[Outer try-catch] Handled Exception in GetChildProcesses: {me}");
            }

            return childProcesses;
        }

        // This function is CPU intensive for large service deployments (large number of observed processes).
        private float WmiGetProcessPrivateWorkingSetMb(int processId)
        {
            if (processId < 0)
            {
                return 0F;
            }

            string query = $"select WorkingSetPrivate from Win32_PerfRawData_PerfProc_Process where IDProcess = {processId}";

            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    var results = searcher.Get();

                    if (results.Count == 0)
                    {
                        return 0F;
                    }

                    using (ManagementObjectCollection.ManagementObjectEnumerator enumerator = results.GetEnumerator())
                    {
                        while (enumerator.MoveNext())
                        {
                            try
                            {
                                using (ManagementObject mObj = (ManagementObject)enumerator.Current)
                                {
                                    ulong workingSet = (ulong)mObj.Properties["WorkingSetPrivate"].Value;
                                    float privWorkingSetMb = Convert.ToSingle(workingSet);
                                    return privWorkingSetMb / 1024 / 1024;
                                }
                            }
                            catch (Exception e) when (e is ArgumentException || e is ManagementException)
                            {
                                Logger.LogWarning($"[Inner try-catch (enumeration)] Handled Exception in GetProcessPrivateWorkingSet: {e}");
                                continue;
                            }
                        }
                    }
                }
            }
            catch (ManagementException me)
            {
                Logger.LogWarning($"[Outer try-catch] Handled Exception in GetProcessPrivateWorkingSet: {me}");
            }

            return 0F;
        }

        private float NativeGetProcessWorkingSet(int processId)
        {
            try
            {
                NativeMethods.PROCESS_MEMORY_COUNTERS_EX memoryCounters;
                memoryCounters.cb = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESS_MEMORY_COUNTERS_EX));

                using (Process p = Process.GetProcessById(processId))
                {
                    if (!NativeMethods.GetProcessMemoryInfo(p.Handle, out memoryCounters, memoryCounters.cb))
                    {
                        throw new Win32Exception($"GetProcessMemoryInfo returned false. Error Code is {Marshal.GetLastWin32Error()}");
                    }

                    return memoryCounters.WorkingSetSize.ToInt64() / 1024 / 1024;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                Logger.LogWarning($"NativeGetProcessWorkingSet: Exception getting working set for process {processId}:{Environment.NewLine}{e}");
                return 0F;
            }
        }

        private float GetProcessPrivateWorkingSetMbFromPerfCounter(string procName, int procId)
        {
            string internalProcName = GetInternalProcessNameFromPerfCounter(procName, procId);
            
            try
            {
                if (!string.IsNullOrWhiteSpace(internalProcName))
                {
                    using (PerformanceCounter memoryCounter = new PerformanceCounter("Process", "Working Set - Private", internalProcName, true))
                    {
                        return memoryCounter.NextValue() / 1024 / 1024;
                    }
                }

                Logger.LogWarning($"Unable to get private working set for process {procName} with id {procId}. Returning 0.");
                return 0F;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                Logger.LogWarning($"Handled: Unable to get private working set for process {procName} with id {procId}. Returning 0.{Environment.NewLine}{e}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Unhandled (no-throw): Unable to get private working set for process {procName} with id {procId}. Returning 0.{Environment.NewLine}{ex}");
            }

            return 0F;
        }

        private string GetInternalProcessNameFromPerfCounter(string procName, int procId)
        {
            string internalProcName = string.Empty;

            try
            {
                var category = new PerformanceCounterCategory("Process");
                string[] instanceNames = category.GetInstanceNames().Where(x => x.Contains(procName)).ToArray();

                using (var nameCounter = new PerformanceCounter("Process", "ID Process", true))
                {
                    for (int i = 0; i < instanceNames.Length; ++i)
                    {
                        nameCounter.InstanceName = instanceNames[i];

                        if (nameCounter.NextValue() != procId)
                        {
                            continue;
                        }

                        return instanceNames[i];
                    }
                }
            }
            catch(Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception || ex is UnauthorizedAccessException)
            {
                Logger.LogInfo($"Handled: Unable to determine internal process name for {procName} with id {procId}{Environment.NewLine}{ex}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Unhandled (no-throw): Unable to determine internal process name for {procName} with id {procId}{Environment.NewLine}{ex}");
            }

            return null;
        }
    }
}