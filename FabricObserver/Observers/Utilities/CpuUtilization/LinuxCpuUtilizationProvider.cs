using System;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    internal class LinuxCpuUtilizationProvider : CpuUtilizationProvider
    {
        private float uptimeInSeconds;
        private float idleTimeInSeconds;
        private float cpuUtilization;

        public override async Task<float> NextValueAsync()
        {
            if (this.uptimeInSeconds == -1)
            {
                throw new ObjectDisposedException(nameof(LinuxCpuUtilizationProvider));
            }

            (float ut, float it) = await LinuxProcFS.ReadUptimeAsync();

            if (ut == this.uptimeInSeconds)
            {
                return this.cpuUtilization;
            }

            this.cpuUtilization = 100 - ((it - this.idleTimeInSeconds) / (ut - this.uptimeInSeconds) / Environment.ProcessorCount * 100);

            if (this.cpuUtilization < 0)
            {
                this.cpuUtilization = 0;
            }

            this.uptimeInSeconds = ut;
            this.idleTimeInSeconds = it;

            return this.cpuUtilization;
        }

        protected override void Dispose(bool disposing)
        {
            // Nothing to do.
            this.uptimeInSeconds = -1;
        }
    }
}
