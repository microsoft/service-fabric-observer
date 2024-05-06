// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Runtime.Versioning;

namespace FabricObserver.Observers.Utilities
{
    [SupportedOSPlatform("windows")]
    public class WindowsCpuUtilizationProvider : CpuUtilizationProvider
    {
        private const string ProcessorCategoryName = "Processor";
        private const string ProcessorTimePct = "% Processor Time";
        private const string ProcessorTimeInstanceName = "_Total";

        // \Processor(_Total)\% Processor Time
        // This counter includes all processors on the system. The value range is 0 - 100.
        private static PerformanceCounter systemCpuPerfCounter = null;

        private static PerformanceCounter SystemCpuPerfCounter
        {
            get 
            { 
                systemCpuPerfCounter ??= new(ProcessorCategoryName, ProcessorTimePct, ProcessorTimeInstanceName);
                return systemCpuPerfCounter;
            }
        }

        public override float GetProcessorTimePercentage()
        {
            return SystemCpuPerfCounter.NextValue();
        }

        public override void Dispose()
        {
            systemCpuPerfCounter?.Dispose();
            systemCpuPerfCounter = null;
        }
    }
}
