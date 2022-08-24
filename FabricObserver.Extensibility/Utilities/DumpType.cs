// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Type of dump to generate, mini to full.
    /// </summary>
    public enum DumpType
    {
        /// <summary>
        /// Smallest useful dump.
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpNormal |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo;
        /// </summary>
        Mini,

        /// <summary>
        /// Larger then Mini with more information. This is the default dump type.
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithPrivateReadWriteMemory |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithTokenInformation;
        /// </summary>
        MiniPlus,

        /// <summary>
        /// The larget dump that contains full memory.
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemory |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
        /// NativeMethods.MINIDUMP_TYPE.MiniDumpWithTokenInformation;
        /// </summary>
        Full
    }
}