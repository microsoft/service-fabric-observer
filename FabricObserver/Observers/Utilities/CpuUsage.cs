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
        private DateTime prevTime = DateTime.MinValue;
        private DateTime currTime = DateTime.MinValue;
        private TimeSpan prevTotalProcessorTime;
        private TimeSpan currTotalProcessorTime;

        public int GetCpuUsageProcess(Process p)
        {
            if (p == null || p.HasExited)
            {
                return 0;
            }

            if (this.prevTime == DateTime.MinValue)
            {
                this.prevTime = DateTime.Now;
                this.prevTotalProcessorTime = p.TotalProcessorTime;
            }
            else
            {
                this.currTime = DateTime.Now;
                this.currTotalProcessorTime = p.TotalProcessorTime;
                var currentUsage = (this.currTotalProcessorTime.TotalMilliseconds - this.prevTotalProcessorTime.TotalMilliseconds) / this.currTime.Subtract(this.prevTime).TotalMilliseconds;
                double cpuUsuage = currentUsage / Environment.ProcessorCount;
                this.prevTime = this.currTime;
                this.prevTotalProcessorTime = this.currTotalProcessorTime;
                return (int)(cpuUsuage * 100);
            }

            return 0;
        }
    }
}