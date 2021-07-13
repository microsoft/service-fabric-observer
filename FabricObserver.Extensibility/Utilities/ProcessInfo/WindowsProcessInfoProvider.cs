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

namespace FabricObserver.Observers.Utilities
{
    public class WindowsProcessInfoProvider : ProcessInfoProvider
    {
        const string ProcessCategoryName = "Process";
        const string WorkingSetCounterName = "Working Set - Private";
        const string FileHandlesCounterName = "Handle Count";
        private readonly object memPerfCounterLock = new object();
        private readonly object fileHandlesPerfCounterLock = new object();
        private const int MaxDescendants = 50;
        private PerformanceCounter memProcessPrivateWorkingSetCounter = new PerformanceCounter
        {
            CategoryName = ProcessCategoryName,
            CounterName = WorkingSetCounterName,
            ReadOnly = true
        };

        private PerformanceCounter processFileHandleCounter = new PerformanceCounter
        {
            CategoryName = ProcessCategoryName,
            CounterName = FileHandlesCounterName,
            ReadOnly = true
        };

        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            string processName;

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    processName = process.ProcessName;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
            {
                // "Process with an Id of 12314 is not running."
                return 0F;
            }

            lock (memPerfCounterLock)
            {
                try
                {
                    memProcessPrivateWorkingSetCounter.InstanceName = processName;
                    return memProcessPrivateWorkingSetCounter.NextValue() / (1024 * 1024);
                }
                catch (Exception e) when (e is ArgumentNullException || e is Win32Exception || e is UnauthorizedAccessException)
                {
                    // Don't throw.
                    return 0F;
                }
                catch (Exception e)
                { 
                    Logger.LogError($"{ProcessCategoryName} {WorkingSetCounterName} PerfCounter unhandled error:{Environment.NewLine}{e}");
                    throw;
                }
            }
        }

        // File Handles
        public override float GetProcessAllocatedHandles(int processId, StatelessServiceContext context)
        {
            if (processId < 0)
            {
                return -1F;
            }

            string processName;

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    processName = process.ProcessName;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
            {
                // "Process with an Id of 12314 is not running."
                return -1F;
            }

            lock (fileHandlesPerfCounterLock)
            {
                try
                {
                    processFileHandleCounter.InstanceName = processName;
                    return processFileHandleCounter.NextValue();
                }
                catch (Exception e) when (e is InvalidOperationException || e is Win32Exception || e is UnauthorizedAccessException)
                {
                    // Don't throw.
                    return -1F;
                }
                catch (Exception e)
                {
                    Logger.LogError($"{ProcessCategoryName} {FileHandlesCounterName} PerfCounter unhandled error:{Environment.NewLine}{e}");
                    throw;
                }
            }
        }

        public override List<(string ProcName, int Pid)> GetChildProcessInfo(int processId)
        {
            if (processId < 1)
            {
                return null;
            }

            // Get child procs.
            List<(string procName, int pid)> childProcesses = TupleGetChildProcessInfo(processId);

            if (childProcesses == null)
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
                List<(string procName, int pid)> c1 = TupleGetChildProcessInfo(childProcesses[i].pid);

                if (c1?.Count > 0)
                {
                    childProcesses.AddRange(c1);

                    if (childProcesses.Count >= MaxDescendants)
                    {
                        return childProcesses.Take(MaxDescendants).ToList();
                    }

                    for (int j = 0; j < c1.Count; ++j)  
                    {
                        List<(string procName, int pid)> c2 = TupleGetChildProcessInfo(c1[j].pid);

                        if (c2?.Count > 0)
                        {
                            childProcesses.AddRange(c2);

                            if (childProcesses.Count >= MaxDescendants)
                            {
                                return childProcesses.Take(MaxDescendants).ToList();
                            }

                            for (int k = 0; k < c2.Count; ++k)
                            {
                                List<(string procName, int pid)> c3 = TupleGetChildProcessInfo(c2[k].pid);

                                if (c3?.Count > 0)
                                {
                                    childProcesses.AddRange(c3);

                                    if (childProcesses.Count >= MaxDescendants)
                                    {
                                        return childProcesses.Take(MaxDescendants).ToList();
                                    }

                                    for (int l = 0; l < c3.Count; ++l)
                                    {
                                        List<(string procName, int pid)> c4 = TupleGetChildProcessInfo(c3[l].pid);

                                        if (c4?.Count > 0)
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

        public float GetProcessPrivateWorkingSetInMB(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return 0F;
            }

            lock (memPerfCounterLock)
            {
                try
                {
                    memProcessPrivateWorkingSetCounter.InstanceName = processName;
                    return memProcessPrivateWorkingSetCounter.NextValue() / (1024 * 1024);
                }
                catch (Exception e) when (e is ArgumentNullException || e is Win32Exception || e is UnauthorizedAccessException)
                {
                    // Don't throw.
                    return 0F;
                }
                catch (Exception e)
                {
                    Logger.LogError($"{ProcessCategoryName} {WorkingSetCounterName} PerfCounter unhandled error:{Environment.NewLine}{e}");
                    throw;
                }
            }
        }

        private List<(string procName, int pid)> TupleGetChildProcessInfo(int processId)
        {
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
                Logger.LogWarning($"[Containing try-catch] Handled Exception in GetChildProcesses: {me}");
            }

            return childProcesses;
        }

        protected override void Dispose(bool disposing)
        {
            this.memProcessPrivateWorkingSetCounter?.Dispose();
            this.memProcessPrivateWorkingSetCounter = null;

            this.processFileHandleCounter?.Dispose();
            this.processFileHandleCounter = null;
        }
    }
}