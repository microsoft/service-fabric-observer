// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsCpuUtilizationProvider : CpuUtilizationProvider
    {
        // \Processor(_Total)\% Processor Time
        // This counter includes all processors on the system. The value range is 0 - 100.
        private static PerformanceCounter systemCpuPerfCtr = new("Processor", "% Processor Time", "_Total");

        public override float GetProcessorTimePercentage()
        {
            systemCpuPerfCtr ??= new("Processor", "% Processor Time", "_Total");
            return systemCpuPerfCtr.NextValue();
        }

        public override void Dispose()
        {
            systemCpuPerfCtr?.Dispose();
            systemCpuPerfCtr = null;
        }
    }
}
