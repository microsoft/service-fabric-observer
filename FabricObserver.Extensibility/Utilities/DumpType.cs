namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Type of dump to generate, mini to full.
    /// </summary>
    public enum DumpType
    {
        /// <summary>
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithIndirectlyReferencedMemory |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpScanMemory;
        /// </summary>
        Mini,

        /// <summary>
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithPrivateReadWriteMemory |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules;
        /// </summary>
        MiniPlus,

        /// <summary>
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemory |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules;
        /// </summary>
        Full
    }
}