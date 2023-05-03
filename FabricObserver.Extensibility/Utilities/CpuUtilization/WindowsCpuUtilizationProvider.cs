// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsCpuUtilizationProvider : CpuUtilizationProvider
    {
        private const string ProcessorCategoryName = "Processor";
        private const string ProcessorTimePct = "% Processor Time";
        private const string ProcessorTimeInstanceName = "_Total";

        // \Processor(_Total)\% Processor Time
        // This counter includes all processors on the system. The value range is 0 - 100.
        private static PerformanceCounter systemCpuPerfCtr = null;
        
        private static PerformanceCounter SystemMemoryPerfCtr
        {
            get 
            { 
                if (systemCpuPerfCtr == null) 
                {
                    systemCpuPerfCtr = new(ProcessorCategoryName, ProcessorTimePct, ProcessorTimeInstanceName);
                }

                return systemCpuPerfCtr;
            }
        }

        public override float GetProcessorTimePercentage()
        {
            return SystemMemoryPerfCtr.NextValue();
        }

        public override void Dispose()
        {
            systemCpuPerfCtr?.Dispose();
            systemCpuPerfCtr = null;
        }
    }
}
