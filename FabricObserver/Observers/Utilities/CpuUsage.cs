// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    // .NET Standard Process-based impl (cross-platform)
    internal class CpuUsage
    {
        private DateTime prevTime = DateTime.MinValue;
        private DateTime currentTimeTime = DateTime.MinValue;
        private TimeSpan prevTotalProcessorTime;
        private TimeSpan currentTotalProcessorTime;

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
                this.currentTimeTime = DateTime.Now;
                this.currentTotalProcessorTime = p.TotalProcessorTime;
                var currentUsage = (this.currentTotalProcessorTime.TotalMilliseconds - this.prevTotalProcessorTime.TotalMilliseconds) / this.currentTimeTime.Subtract(this.prevTime).TotalMilliseconds;
                var cpuUsage = currentUsage / Environment.ProcessorCount;
                this.prevTime = this.currentTimeTime;
                this.prevTotalProcessorTime = this.currentTotalProcessorTime;
                return (int)(cpuUsage * 100);
            }

            return 0;
        }
    }
}