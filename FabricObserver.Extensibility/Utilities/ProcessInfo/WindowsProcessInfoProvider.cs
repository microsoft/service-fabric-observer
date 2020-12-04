// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    // Since we only create a single instance of WindowsProcessInfoProvider, it is OK
    // to not dispose memProcessPrivateWorkingSetCounter.
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
    public class WindowsProcessInfoProvider : ProcessInfoProvider
    {
        private readonly PerformanceCounter memProcessPrivateWorkingSetCounter = new PerformanceCounter();
        private readonly object perfCounterLock = new object();

        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            const string CategoryName = "Process";
            const string CounterName = "Working Set - Private";

            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException ex)
            {
                // "Process with an Id of 12314 is not running."
                Logger.LogError(ex.Message);
                return 0;
            }

            lock (this.perfCounterLock)
            {
                try
                {
                    this.memProcessPrivateWorkingSetCounter.CategoryName = CategoryName;
                    this.memProcessPrivateWorkingSetCounter.CounterName = CounterName;
                    this.memProcessPrivateWorkingSetCounter.InstanceName = process.ProcessName;

                    return this.memProcessPrivateWorkingSetCounter.NextValue() / (1024 * 1024);
                }
                catch (Exception e)
                {
                    if (e is ArgumentNullException || e is PlatformNotSupportedException
                        || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                    {
                        Logger.LogError($"{CategoryName} {CounterName} PerfCounter handled error: " + e);

                        // Don't throw.
                        return 0F;
                    }

                    Logger.LogError($"{CategoryName} {CounterName} PerfCounter unhandled error: " + e);

                    throw;
                }
            }
        }
    }
}
