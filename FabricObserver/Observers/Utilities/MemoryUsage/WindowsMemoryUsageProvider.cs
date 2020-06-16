using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    internal class WindowsMemoryUsageProvider : MemoryUsageProvider
    {
        private PerformanceCounter memCommittedBytesPerfCounter = new PerformanceCounter(categoryName: "Memory", counterName: "Committed Bytes", readOnly: true);

        internal override ulong GetCommittedBytes()
        {
            return (ulong)memCommittedBytesPerfCounter.NextValue();
        }
    }
}