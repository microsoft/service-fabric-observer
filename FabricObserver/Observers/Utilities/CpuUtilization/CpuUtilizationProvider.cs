using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    internal abstract class CpuUtilizationProvider : IDisposable
    {
        protected CpuUtilizationProvider()
        {
        }

        public abstract Task<float> NextValueAsync();

        public void Dispose()
        {
            this.Dispose(disposing: true);
        }

        internal static CpuUtilizationProvider Create()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsCpuUtilizationProvider();
            }
            else
            {
                return new LinuxCpuUtilizationProvider();
            }
        }

        protected abstract void Dispose(bool disposing);
    }
}
