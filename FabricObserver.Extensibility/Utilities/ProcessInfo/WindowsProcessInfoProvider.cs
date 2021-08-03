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

        public override float GetProcessWorkingSetMb(int processId, bool getPrivateWorkingSet = false)
        {
            try
            {
                if (getPrivateWorkingSet)
                {
                    return GetProcessPrivateWorkingSetMb(processId);
                }

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
            catch(Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                Logger.LogWarning($"Exception getting working set for process {processId}:{Environment.NewLine}{e}");
                return 0F;
            }
        }

        // File Handles
        public override float GetProcessAllocatedHandles(int processId, StatelessServiceContext context)
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

        public override List<(string ProcName, int Pid)> GetChildProcessInfo(int processId)
        {
            if (processId < 1)
            {
                return null;
            }

            // Get child procs.
            List<(string ProcName, int Pid)> childProcesses = TupleGetChildProcessInfo(processId);

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
                List<(string ProcName, int Pid)> c1 = TupleGetChildProcessInfo(childProcesses[i].Pid);

                if (c1 != null && c1.Count > 0)
                {
                    childProcesses.AddRange(c1);

                    if (childProcesses.Count >= MaxDescendants)
                    {
                        return childProcesses.Take(MaxDescendants).ToList();
                    }

                    for (int j = 0; j < c1.Count; ++j)  
                    {
                        List<(string ProcName, int Pid)> c2 = TupleGetChildProcessInfo(c1[j].Pid);

                        if (c2 != null && c2.Count > 0)
                        {
                            childProcesses.AddRange(c2);

                            if (childProcesses.Count >= MaxDescendants)
                            {
                                return childProcesses.Take(MaxDescendants).ToList();
                            }

                            for (int k = 0; k < c2.Count; ++k)
                            {
                                List<(string ProcName, int Pid)> c3 = TupleGetChildProcessInfo(c2[k].Pid);

                                if (c3 != null && c3.Count > 0)
                                {
                                    childProcesses.AddRange(c3);

                                    if (childProcesses.Count >= MaxDescendants)
                                    {
                                        return childProcesses.Take(MaxDescendants).ToList();
                                    }

                                    for (int l = 0; l < c3.Count; ++l)
                                    {
                                        List<(string ProcName, int Pid)> c4 = TupleGetChildProcessInfo(c3[l].Pid);

                                        if (c4 != null && c4.Count > 0)
                                        {
                                            childProcesses.AddRange(c4);

                                            if (childProcesses.Count >= MaxDescendants)
                                            {
                                                return childProcesses.Take(MaxDescendants).ToList();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return childProcesses;
        }

        private List<(string procName, int pid)> TupleGetChildProcessInfo(int processId)
        {
            if (processId < 0)
            {
                return null;
            }

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

                                    if (childProcessNameObj.ToString() == "conhost.exe")
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

        private float GetProcessPrivateWorkingSetMb(int processId)
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
                                    ulong workingSet = (ulong)mObj.Properties["WorkingSetPrivate"].Value / 1024 / 1024;
                                    return workingSet;
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
    }
}