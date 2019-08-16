// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;

namespace FabricObserver.Utilities
{
    // .NET Standard Process-based impl (cross-platform)
    internal class CpuUsage
    {
        private DateTime prevTime = DateTime.MinValue, currTime = DateTime.MinValue;
        private TimeSpan prevTotalProcessorTime, currTotalProcessorTime;

        public int GetCpuUsageProcess(Process p)
        {
            if (p == null || p.HasExited)
            {
                return 0;
            }

            if (prevTime == DateTime.MinValue)
            {
                prevTime = DateTime.Now;
                prevTotalProcessorTime = p.TotalProcessorTime;
            }
            else
            {
                currTime = DateTime.Now;
                currTotalProcessorTime = p.TotalProcessorTime;
                var currentUsage = (currTotalProcessorTime.TotalMilliseconds - prevTotalProcessorTime.TotalMilliseconds) / currTime.Subtract(prevTime).TotalMilliseconds;
                double cpuUsuage = currentUsage / Environment.ProcessorCount;
                prevTime = currTime;
                prevTotalProcessorTime = currTotalProcessorTime;
                return (int)(cpuUsuage * 100);
            }

            return 0;
        }
    }
}