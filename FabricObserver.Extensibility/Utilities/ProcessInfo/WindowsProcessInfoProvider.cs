// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
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

            // Full Working Set (Private + Shared).
            return NativeGetProcessFullWorkingSetMb(processId); 
        }

        public override float GetProcessAllocatedHandles(int processId, string configPath = null)
        {
            return NativeGetProcessHandleCount(processId);
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

        private List<(string procName, int pid)> TupleGetChildProcessesWin32(int processId)
        {
            if (processId <= 0)
            {
                return null;
            }

            try
            {
                List<(string procName, int procId)> childProcs = NativeMethods.GetChildProcesses(processId);

                if (childProcs?.Count == 0)
                {
                    return null;
                }

                return childProcs;
            }
            catch (Exception e) when (e is Win32Exception) // e.g., process is no longer running.
            {
                Logger.LogWarning($"Handled Exception in TupleGetChildProcesses:{Environment.NewLine}{e}");
            }
            catch (Exception e)
            {
                Logger.LogError($"Unhandled Exception in TupleGetChildProcesses:{Environment.NewLine}{e}");
                throw;
            }

            return null;
        }

        public override double GetProcessKvsLvidsUsagePercentage(string procName, int procId = -1)
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
                    internalProcName = GetInternalProcessNameFromPerfCounter(procName, procId);
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
                Logger.LogWarning($"GetProcessKvsLvidsUsagePercentage: Handled Win32Exception:{Environment.NewLine}{ioe}");
            }
            catch (Win32Exception we)
            {
                // Internal exception querying counter (Win32 code). There is nothing to do here. Log the details. Most likely transient.
                Logger.LogWarning($"GetProcessKvsLvidsUsagePercentage: Handled Win32Exception:{Environment.NewLine}{we}");
            }
            finally
            {
                performanceCounter?.Dispose();
                performanceCounter = null;
            }

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
                using (Process process = Process.GetProcessById(processId))
                {
                    process.Refresh();
                    return process.HandleCount;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is SystemException)
            {
                return -1F;
            }
        }

        private float NativeGetProcessFullWorkingSetMb(int processId)
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

                    long workingSetSizeMb = memoryCounters.WorkingSetSize.ToInt64() / 1024 / 1024;

                    return workingSetSizeMb; 
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                Logger.LogWarning($"NativeGetProcessWorkingSet: Exception getting working set for process {processId}:{Environment.NewLine}{e}");
                return 0F;
            }
        }

        private int NativeGetProcessHandleCount(int processId)
        {
            try
            {
                using (Process p = Process.GetProcessById(processId))
                {
                    if (!NativeMethods.GetProcessHandleCount(p.Handle, out uint handles))
                    {
                        Logger.LogWarning($"GetProcessHandleCount: Failed with Win32 error code {Marshal.GetLastWin32Error()}. Trying a different approach (Process obj).");

                        // Try a different approach employing Process object.
                        handles = (uint)GetProcessAllocatedHandles(p);
                    }
       
                    return (int)handles;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                // Access denied (FO is running as a less privileged user than the target process).
                if (e is Win32Exception && (e as Win32Exception).NativeErrorCode != 5)
                {
                    Logger.LogWarning($"NativeGetProcessHandleCount: Exception getting working set for process {processId}:{Environment.NewLine}{e}");
                }
            }

            return -1;
        }

        private int GetProcessAllocatedHandles(Process process)
        {
            if (process == null)
            {
                return 0;
            }

            process.Refresh();
            int count = process.HandleCount;

            return count;
        }

        private float GetProcessPrivateWorkingSetMbFromPerfCounter(string procName, int procId)
        {
            string internalProcName = GetInternalProcessNameFromPerfCounter(procName, procId);
            PerformanceCounter memoryCounter = null;

            try
            {
               memoryCounter = new PerformanceCounter("Process", "Working Set - Private", internalProcName, true);
               return memoryCounter.NextValue() / 1024 / 1024;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                Logger.LogWarning($"GetProcessPrivateWorkingSetMbFromPerfCounter: Handled: Unable to get private working set for process {procName} with id {procId}. Returning 0.{Environment.NewLine}{e}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"GetProcessPrivateWorkingSetMbFromPerfCounter: Unhandled (no-throw): Unable to get private working set for process {procName} with id {procId}. Returning 0.{Environment.NewLine}{ex}");
                throw;
            }
            finally
            {
                memoryCounter?.Dispose();
                memoryCounter = null;
            }

            return 0F;
        }

        // NOTE: If you have several service processes of the *same name*, this will add *significant* processing time and CPU usage to AppObserver.
        // Consider not enabling Private Working Set memory on AppObserver if that is the case. Instead, the Full Working Set should be measured (Private + Shared),
        // computed using a native API call (fast). See ApplicationManifest.xml for comments.
        private string GetInternalProcessNameFromPerfCounter(string procName, int procId)
        {
            string[] instanceNames;
            PerformanceCounter nameCounter = null;

            try
            {
                var category = new PerformanceCounterCategory("Process");
                instanceNames = category.GetInstanceNames().Where(x => x.Contains($"{procName}#")).ToArray();
                int count = instanceNames.Length;

                if (count == 0 || instanceNames.All(inst => inst == string.Empty))
                {
                    return procName;
                }

                nameCounter = new PerformanceCounter("Process", "ID Process", true);

                for (int i = 0; i < count; i++)
                {
                    nameCounter.InstanceName = instanceNames[i];

                    if (procId != nameCounter.NextValue())
                    {
                        continue;
                    }

                    return nameCounter.InstanceName;
                }
            }
            catch(Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is UnauthorizedAccessException)
            {
                Logger.LogWarning($"GetInternalProcessNameFromPerfCounter: Handled Exception - Unable to determine internal process name for {procName} with id {procId}{Environment.NewLine}{ex}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"GetInternalProcessNameFromPerfCounter Unhandled Exception - Unable to determine internal process name for {procName} with id {procId}{Environment.NewLine}{ex}");
                throw;
            }
            finally
            {
                nameCounter?.Dispose();
                nameCounter = null;
                instanceNames = null;
            }

            return procName;
        }
    }
}