using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    internal class WindowsCpuUtilizationProvider : CpuUtilizationProvider
    {
        private PerformanceCounter performanceCounter = new PerformanceCounter(categoryName: "Processor", counterName: "% Processor Time", instanceName: "_Total", readOnly: true);

        public override Task<float> NextValueAsync()
        {
            PerformanceCounter perfCounter = this.performanceCounter;

            if (perfCounter == null)
            {
                throw new ObjectDisposedException(nameof(WindowsCpuUtilizationProvider));
            }

            float result = perfCounter.NextValue();
            return Task.FromResult(result);
        }

        protected override void Dispose(bool disposing)
        {
            this.performanceCounter?.Dispose();
            this.performanceCounter = null;
        }
    }
}
