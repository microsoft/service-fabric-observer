// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsCpuUtilizationProvider : CpuUtilizationProvider
    {
        private PerformanceCounter performanceCounter = new PerformanceCounter()
        {
            CategoryName = "Processor",
            CounterName = "% Processor Time",
            InstanceName = "_Total",
            ReadOnly = true,
        };

        public override Task<float> NextValueAsync()
        {
            PerformanceCounter perfCounter = performanceCounter;

            if (perfCounter == null)
            {
                throw new ObjectDisposedException(nameof(WindowsCpuUtilizationProvider));
            }

            // warm up counter.
            _ = perfCounter.NextValue();

            float result = perfCounter.NextValue();

            return Task.FromResult(result);
        }

        protected override void Dispose(bool disposing)
        {
            performanceCounter?.Dispose();
            performanceCounter = null;
        }
    }
}
