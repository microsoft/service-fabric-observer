// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace FabricObserver.Observers.Utilities
{
    // .NET Standard Process-based impl (cross-platform)
    public class CpuUsage
    {
        private DateTime prevTime = DateTime.MinValue;
        private DateTime currentTimeTime = DateTime.MinValue;
        private TimeSpan prevTotalProcessorTime;
        private TimeSpan currentTotalProcessorTime;

        /// <summary>
        /// This function computes the total percentage of all cpus that the supplied process is currently using.
        /// </summary>
        /// <param name="procId">Target Process object</param>
        /// <returns>CPU percentage in use as double value</returns>
        public double GetCpuUsagePercentageProcess(int procId)
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
            catch (Exception e) when (e is ArgumentException || e is Win32Exception || e is InvalidOperationException || e is NotSupportedException)
            {

            }

            return 0.0;
        }
    }
}