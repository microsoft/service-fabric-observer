namespace FabricObserver.Observers.Utilities
{
    internal static class MemInfoConstants
    {
        /*
        ** Source code: https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/fs/proc/meminfo.c
        */
        internal static readonly string MemTotal = nameof(MemTotal);

        internal static readonly string MemFree = nameof(MemFree);

        internal static readonly string SwapTotal = nameof(SwapTotal);

        internal static readonly string SwapFree = nameof(SwapFree);

        internal static readonly string VmallocTotal = nameof(VmallocTotal);

        internal static readonly string VmallocUsed = nameof(VmallocUsed);
    }
}
