// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsCpuUtilizationProvider : CpuUtilizationProvider
    {
        private static PerformanceCounter systemCpuPerfCounter = null;
        FILETIME prevSysIdle, prevSysKernel, prevSysUser;

        private static PerformanceCounter SystemCpuPerfCounter
        {
            get
            {
                if (systemCpuPerfCounter == null)
                {
                    systemCpuPerfCounter = new PerformanceCounter("Processor", "% Processor Time","_Total", true);
                }

                return systemCpuPerfCounter;
            }
        }

        public override float GetProcessorTimePercentage()
        {
            return SystemCpuPerfCounter.NextValue();
        }

        float GetProcessorTimePercentageWin32()
        {
            float ret = 0.0f;

            if (!NativeMethods.GetSystemTimes(out FILETIME sysIdle, out FILETIME sysKernel, out FILETIME sysUser))
            {
                return 0;
            }

            if (prevSysIdle.dwLowDateTime == 0 && prevSysIdle.dwHighDateTime == 0)
            {
                prevSysIdle = sysIdle;
                prevSysKernel = sysKernel;
                prevSysUser = sysUser;
                Thread.Sleep(240);

                return GetProcessorTimePercentageWin32();
            }

            ulong sysIdleDiff, sysKernelDiff, sysUserDiff;
            sysIdleDiff = SubtractTimes(sysIdle, prevSysIdle);
            sysKernelDiff = SubtractTimes(sysKernel, prevSysKernel);
            sysUserDiff = SubtractTimes(sysUser, prevSysUser);

            ulong sysTotal = sysKernelDiff + sysUserDiff;
            ulong kernelTotal = sysKernelDiff - sysIdleDiff; // kernelTime - IdleTime = kernelTime, because sysKernel include IdleTime

            // sometimes kernelTime > idleTime
            if (sysTotal > 0)
            {   
                ret = (float)((kernelTotal + sysUserDiff) * 100.0 / sysTotal);
            }

            prevSysIdle = sysIdle;
            prevSysKernel = sysKernel;
            prevSysUser = sysUser;

            return ret;
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

        public override void Dispose()
        {
            systemCpuPerfCounter?.Dispose();
            systemCpuPerfCounter = null;
        }
    }
}
