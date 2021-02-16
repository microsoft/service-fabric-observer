// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;

namespace FabricObserver.Observers.Utilities
{
    // Since we only create a single instance of WindowsProcessInfoProvider, it is OK
    // to not dispose counters.
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
    public class WindowsProcessInfoProvider : ProcessInfoProvider
    {
        const string CategoryName = "Process";

        private readonly PerformanceCounter memProcessPrivateWorkingSetCounter = new PerformanceCounter();
        private readonly PerformanceCounter processFileHandleCounter = new PerformanceCounter();
        private readonly object memPerfCounterLock = new object();
        private readonly object fileHandlesPerfCounterLock = new object();

        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            const string WorkingSetCounterName = "Working Set - Private";
            string processName;

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    processName = process.ProcessName;
                }
            }
            catch (ArgumentException ex)
            {
                // "Process with an Id of 12314 is not running."
                Logger.LogError(ex.Message);
                return 0F;
            }

            lock (memPerfCounterLock)
            {
                try
                {
                    memProcessPrivateWorkingSetCounter.CategoryName = CategoryName;
                    memProcessPrivateWorkingSetCounter.CounterName = WorkingSetCounterName;
                    memProcessPrivateWorkingSetCounter.InstanceName = processName;

                    return memProcessPrivateWorkingSetCounter.NextValue() / (1024 * 1024);
                }
                catch (Exception e) when (e is ArgumentNullException || e is PlatformNotSupportedException ||
                                          e is Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogError($"{CategoryName} {WorkingSetCounterName} PerfCounter handled error:{Environment.NewLine}{e}");

                    // Don't throw.
                    return 0F;
                }
                catch (Exception e)
                { 
                    Logger.LogError($"{CategoryName} {WorkingSetCounterName} PerfCounter unhandled error:{Environment.NewLine}{e}");

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

            const string FileHandlesCounterName = "Handle Count";
            string processName;

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    processName = process.ProcessName;
                }
            }
            catch (ArgumentException ex)
            {
                // "Process with an Id of 12314 is not running."
                Logger.LogError(ex.Message);
                return -1F;
            }

            lock (fileHandlesPerfCounterLock)
            {
                try
                {
                    processFileHandleCounter.CategoryName = CategoryName;
                    processFileHandleCounter.CounterName = FileHandlesCounterName;
                    processFileHandleCounter.InstanceName = processName;

                    return processFileHandleCounter.NextValue();
                }
                catch (Exception e) when (e is ArgumentNullException || e is PlatformNotSupportedException ||
                                          e is Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogError($"{CategoryName} {FileHandlesCounterName} PerfCounter handled error:{Environment.NewLine}{e}");

                    // Don't throw.
                    return -1F;
                }
                catch (Exception e)
                {
                    Logger.LogError($"{CategoryName} {FileHandlesCounterName} PerfCounter unhandled error:{Environment.NewLine}{e}");

                    throw;
                }
            }
        }
    }
}