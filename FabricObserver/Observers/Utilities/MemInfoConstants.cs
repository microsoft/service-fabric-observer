using NLog.LayoutRenderers;

namespace FabricObserver.Observers.Utilities
{
    internal static class MemInfoConstants
    {
        /*
        ** Source code: https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/fs/proc/meminfo.c
        */
        internal const string MemTotal = nameof(MemTotal);

        internal const string MemFree = nameof(MemFree);

        internal const string SwapTotal = nameof(SwapTotal);

        internal const string SwapFree = nameof(SwapFree);

        internal const string VmallocTotal = nameof(VmallocTotal);

        internal const string VmallocUsed = nameof(VmallocUsed);

        internal const string MemAvailable = nameof(MemAvailable);
    }
}
