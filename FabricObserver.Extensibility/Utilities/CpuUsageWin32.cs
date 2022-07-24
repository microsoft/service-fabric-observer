// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices.ComTypes;
using FabricObserver.Interfaces;
using System.Threading;

namespace FabricObserver.Observers.Utilities
{
    public class CpuUsageWin32 : ICpuUsage
    {
        private FILETIME processTimesLastUserTime, processTimesLastKernelTime, systemTimesLastUserTime, systemTimesLastKernelTime;
        bool hasRunOnce = false;

        /// <summary>
        /// This function computes the total percentage of all cpus that the supplied process is currently using.
        /// </summary>
        /// <param name="procId">The target process identifier.</param>
        /// <param name="procName">The name of the process.</param>
        /// <returns>CPU percentage in use as double value</returns>
        public double GetCurrentCpuUsagePercentage(int procId, string procName)
        {
            SafeProcessHandle sProcHandle = NativeMethods.GetSafeProcessHandle((uint)procId);
            
            if (sProcHandle.IsInvalid)
            {
                return 0;
            }

            try
            {
                if (!NativeMethods.GetProcessTimes(sProcHandle, out _, out _, out FILETIME processTimesRawKernelTime, out FILETIME processTimesRawUserTime))
                {
                    return 0;
                }

                if (!NativeMethods.GetSystemTimes(out _, out FILETIME systemTimesRawKernelTime, out FILETIME systemTimesRawUserTime))
                {
                    return 0;
                }

                ulong processTimesDelta =
                    SubtractTimes(processTimesRawUserTime, processTimesLastUserTime) + SubtractTimes(processTimesRawKernelTime, processTimesLastKernelTime);
                ulong systemTimesDelta =
                    SubtractTimes(systemTimesRawUserTime, systemTimesLastUserTime) + SubtractTimes(systemTimesRawKernelTime, systemTimesLastKernelTime);
                double cpuUsage = (double)systemTimesDelta == 0 ? 0 : processTimesDelta * 100 / (double)systemTimesDelta;
                UpdateTimes(processTimesRawUserTime, processTimesRawKernelTime, systemTimesRawUserTime, systemTimesRawKernelTime);

                if (!hasRunOnce)
                {
                    Thread.Sleep(100);
                    hasRunOnce = true;
                    return GetCurrentCpuUsagePercentage(procId, procName);
                }
    
                return cpuUsage;
            }
            finally
            {
                sProcHandle?.Dispose();
                sProcHandle = null;
            }
        }

        private void UpdateTimes(FILETIME processTimesRawUserTime, FILETIME processTimesRawKernelTime, FILETIME systemTimesRawUserTime, FILETIME systemTimesRawKernelTime)
        {
            // Process times
            processTimesLastUserTime.dwHighDateTime = processTimesRawUserTime.dwHighDateTime;
            processTimesLastUserTime.dwLowDateTime = processTimesRawUserTime.dwLowDateTime;
            processTimesLastKernelTime.dwHighDateTime = processTimesRawKernelTime.dwHighDateTime;
            processTimesLastKernelTime.dwLowDateTime = processTimesRawKernelTime.dwLowDateTime;

            // System times
            systemTimesLastUserTime.dwHighDateTime = systemTimesRawUserTime.dwHighDateTime;
            systemTimesLastUserTime.dwLowDateTime = systemTimesRawUserTime.dwLowDateTime;
            systemTimesLastKernelTime.dwHighDateTime = systemTimesRawKernelTime.dwHighDateTime;
            systemTimesLastKernelTime.dwLowDateTime = systemTimesRawKernelTime.dwLowDateTime;
        }

        private ulong SubtractTimes(FILETIME currentFileTime, FILETIME lastUpdateFileTime)
        {
            ulong currentTime = unchecked(((ulong)currentFileTime.dwHighDateTime << 32) | (uint)currentFileTime.dwLowDateTime);
            ulong lastUpdateTime = unchecked(((ulong)lastUpdateFileTime.dwHighDateTime << 32) | (uint)lastUpdateFileTime.dwLowDateTime);

            if ((currentTime - lastUpdateTime) < 0)
            {
                return 0;
            }

            return currentTime - lastUpdateTime;
        }
    }
}
