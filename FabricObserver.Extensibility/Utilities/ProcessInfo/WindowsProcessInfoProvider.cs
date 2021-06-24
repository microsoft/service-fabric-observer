// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
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
                Logger.LogWarning($"Handled Exception in GetProcessPrivateWorkingSetInMB: {e.Message}");
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
                    Logger.LogWarning($"{ProcessCategoryName} {WorkingSetCounterName} PerfCounter handled error:{Environment.NewLine}{e}");

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
                Logger.LogWarning($"Handled Exception in GetProcessAllocatedHandles: {e.Message}");
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
                    Logger.LogWarning($"{ProcessCategoryName} {FileHandlesCounterName} PerfCounter handled error:{Environment.NewLine}{e}");

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

        public override List<Process> GetChildProcesses(Process process)
        {
            List<Process> childProcesses = new List<Process>();
            string query = $"select processid from win32_process where parentprocessid = {process.Id}";

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
                                    object childProcessObj = mObj.Properties["processid"].Value;

                                    if (childProcessObj == null)
                                    {
                                        continue;
                                    }

                                    Process childProcess = Process.GetProcessById(Convert.ToInt32(childProcessObj));
                                    
                                    if (childProcess != null)
                                    {
                                        if (childProcess.ProcessName == "conhost")
                                        {
                                            continue;
                                        }

                                        childProcesses.Add(childProcess);

                                        // Now get child of child, if exists.
                                        List<Process> grandChildren = GetChildProcesses(childProcess);

                                        if (grandChildren?.Count > 0)
                                        {
                                            childProcesses.AddRange(grandChildren);
                                        }
                                    }
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