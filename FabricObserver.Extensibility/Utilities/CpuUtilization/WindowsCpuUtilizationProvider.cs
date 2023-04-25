// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsCpuUtilizationProvider : CpuUtilizationProvider
    {
        private static PerformanceCounter systemCpuPerfCtr = null;
        private FILETIME prevSysIdle, prevSysKernel, prevSysUser;

        private static PerformanceCounter SystemCpuPerfCtr
        {
            get 
            {
                systemCpuPerfCtr ??= new("Processor", "% Processor Time", "_Total");
                return systemCpuPerfCtr; 
            }
        }

        public override float GetProcessorTimePercentage()
        {
            return SystemCpuPerfCtr.NextValue();
        }

        private float GetProcessorTimePercentageWin32()
        {
            float cpuTotalPct = 0.0f;

            if (!NativeMethods.GetSystemTimes(out FILETIME sysIdle, out FILETIME sysKernel, out FILETIME sysUser))
            {
                CpuInfoLogger.LogWarning($"GetProcessorTimePercentageWin32 failure: GetSystemTimes failed with error code {Marshal.GetLastWin32Error()}");
                return cpuTotalPct;
            }

            // First run.
            if (prevSysIdle.dwLowDateTime == 0 && prevSysIdle.dwHighDateTime == 0)
            {
                prevSysIdle = sysIdle;
                prevSysKernel = sysKernel;
                prevSysUser = sysUser;
                Thread.Sleep(200);
                return GetProcessorTimePercentageWin32();
            }

            ulong sysIdleDiff, sysKernelDiff, sysUserDiff;
            sysIdleDiff = SubtractTimes(sysIdle, prevSysIdle);
            sysKernelDiff = SubtractTimes(sysKernel, prevSysKernel);
            sysUserDiff = SubtractTimes(sysUser, prevSysUser);

            ulong sysTotal = sysKernelDiff + sysUserDiff;
            ulong kernelTotal = sysKernelDiff - sysIdleDiff; 

            if (sysTotal > 0)
            {   
                cpuTotalPct = (float)((kernelTotal + sysUserDiff) * 100.0) / sysTotal;
            }

            prevSysIdle = sysIdle;
            prevSysKernel = sysKernel;
            prevSysUser = sysUser;

            return cpuTotalPct;
        }

        private static ulong SubtractTimes(FILETIME currentFileTime, FILETIME lastUpdateFileTime)
        {
            ulong currentTime = unchecked(((ulong)currentFileTime.dwHighDateTime << 32) | (uint)currentFileTime.dwLowDateTime);
            ulong lastUpdateTime = unchecked(((ulong)lastUpdateFileTime.dwHighDateTime << 32) | (uint)lastUpdateFileTime.dwLowDateTime);

            if ((currentTime - lastUpdateTime) < 0)
            {
                return 0;
            }

            return currentTime - lastUpdateTime;
        }

        public override void Dispose()
        {
            systemCpuPerfCtr?.Dispose();
            systemCpuPerfCtr = null;
        }
    }
}
