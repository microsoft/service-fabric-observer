// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsCpuUtilizationProvider : CpuUtilizationProvider
    {
        private PerformanceCounter performanceCounter = null;

        public WindowsCpuUtilizationProvider()
        {
            performanceCounter = new PerformanceCounter(
                                        categoryName: "Processor",
                                        counterName: "% Processor Time",
                                        instanceName: "_Total",
                                        readOnly: true);
        }

        public override float GetProcessorTimePercentage()
        {
            if (performanceCounter == null)
            {
                performanceCounter = new PerformanceCounter(
                                            categoryName: "Processor",
                                            counterName: "% Processor Time",
                                            instanceName: "_Total",
                                            readOnly: true);
            }

            float result = performanceCounter.NextValue();
            return result;
        }

        public override void Dispose()
        {
            performanceCounter?.Dispose();
            performanceCounter = null;
        }
    }
}
