// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using FabricObserver.Interfaces;
using Microsoft.Win32.SafeHandles;

namespace FabricObserver.Observers.Utilities
{
    // Cross plaform impl, but used only for Linux. For Windows, Utilities.CpuUsageWin32 employs a much more efficient impl than .NET's Process class.
    public class CpuUsageProcess : ICpuUsage
    {
        private DateTime prevTime = DateTime.MinValue;
        private DateTime currentTimeTime = DateTime.MinValue;
        private TimeSpan prevTotalProcessorTime;
        private TimeSpan currentTotalProcessorTime;

        /// <summary>
        /// This function computes process CPU time as a percentage of all processors. It employs .NET core's Process object, which is efficient on Linux, but not
        /// Windows. In the latter case, use CpuUsageWin32.GetCurrentCpuUsagePercentage instead.
        /// </summary>
        /// <param name="procId">Target Process object</param>
        /// <param name="procName">Optional process name.</param>
        /// <param name="procHandle">Optional (Windows only) safe process handle.</param>
        /// <returns>CPU Time percentage for supplied procId. If the process is no longer running, then -1 will be returned.</returns>
        public double GetCurrentCpuUsagePercentage(int procId, string procName = null, SafeProcessHandle procHandle = null)
        {
            try
            {
                using (Process p = Process.GetProcessById(procId))
                {
                    // First run.
                    if (prevTime == DateTime.MinValue)
                    {
                        prevTime = DateTime.Now;
                        prevTotalProcessorTime = p.TotalProcessorTime;
                        Thread.Sleep(50);
                    }
                    
                    currentTimeTime = DateTime.Now;
                    currentTotalProcessorTime = p.TotalProcessorTime;
                    double currentUsage = (currentTotalProcessorTime.TotalMilliseconds - prevTotalProcessorTime.TotalMilliseconds) / currentTimeTime.Subtract(prevTime).TotalMilliseconds;
                    double cpuUsage = currentUsage / Environment.ProcessorCount;
                    prevTime = currentTimeTime;
                    prevTotalProcessorTime = currentTotalProcessorTime;

                    return cpuUsage * 100.0;
                }
            }
            catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException)
            {
                ProcessInfoProvider.ProcessInfoLogger.LogWarning($"GetCurrentCpuUsagePercentage(NET6 Process impl) failure ({procId},{procName}): {e.Message}");

                // Caller should ignore this result. Don't want to use an Exception here.
                return -1;
            }
        }
    }
}