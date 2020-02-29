// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using System.Security;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace FabricObserver.Observers.Utilities
{
    [SuppressUnmanagedCodeSecurity]
    internal static class NativeMethods
    {
        internal enum NT_STATUS
        {
            STATUS_SUCCESS = 0x00000000,
            STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005L),
            STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004L),
            STATUS_NO_MEMORY = unchecked((int)0xC0000017L),
        }

        internal enum SYSTEM_INFORMATION_CLASS
        {
            SystemBasicInformation = 0,
            SystemPerformanceInformation = 2,
            SystemTimeOfDayInformation = 3,
            SystemProcessInformation = 5,
            SystemProcessorPerformanceInformation = 8,
            SystemHandleInformation = 16,
            SystemInterruptInformation = 23,
            SystemExceptionInformation = 33,
            SystemRegistryQuotaInformation = 37,
            SystemLookasideInformation = 45,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetSystemTimes(
                    out FILETIME lpIdleTime,
                    out FILETIME lpKernelTime,
                    out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(
            IntPtr hProcess,
            out FILETIME lpCreationTime,
            out FILETIME lpExitTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        [DllImport("kernel32.dll")]
        internal static extern uint GetTickCount();

        [StructLayout(LayoutKind.Explicit, Size = 8)]
        internal struct LARGE_INTEGER
        {
            [FieldOffset(0)]
            public ulong QuadPart;
            [FieldOffset(0)]
            public ulong LowPart;
            [FieldOffset(4)]
            public ulong HighPart;
        }

        [DllImport("ntdll.dll")]
        internal static extern NT_STATUS NtQuerySystemInformation(
            [In] SYSTEM_INFORMATION_CLASS SystemInformationClass,
            [In] IntPtr SystemInformation,
            [In] int SystemInformationLength,
            [Out] out int ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumePathName(
            string lpszVolumeName,
            char[] lpszVolumePathNames,
            uint cchBufferLength);

        // Process dump support.
        [DllImport(
            "dbghelp.dll",
            EntryPoint = "MiniDumpWriteDump",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            uint processId,
            SafeHandle hFile,
            MINIDUMP_TYPE dumpType,
            IntPtr expParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);

        [Flags]
        public enum MINIDUMP_TYPE : int
        {
            MiniDumpNormal = 0x00000000,
            MiniDumpWithDataSegs = 0x00000001,
            MiniDumpWithFullMemory = 0x00000002,
            MiniDumpWithHandleData = 0x00000004,
            MiniDumpFilterMemory = 0x00000008,
            MiniDumpScanMemory = 0x00000010,
            MiniDumpWithUnloadedModules = 0x00000020,
            MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
            MiniDumpFilterModulePaths = 0x00000080,
            MiniDumpWithProcessThreadData = 0x00000100,
            MiniDumpWithPrivateReadWriteMemory = 0x00000200,
            MiniDumpWithoutOptionalData = 0x00000400,
            MiniDumpWithFullMemoryInfo = 0x00000800,
            MiniDumpWithThreadInfo = 0x00001000,
            MiniDumpWithCodeSegs = 0x00002000,
            MiniDumpWithoutAuxiliaryState = 0x00004000,
            MiniDumpWithFullAuxiliaryState = 0x00008000,
            MiniDumpWithPrivateWriteCopyMemory = 0x00010000,
            MiniDumpIgnoreInaccessibleMemory = 0x00020000,
            MiniDumpWithTokenInformation = 0x00040000,
            MiniDumpWithModuleHeaders = 0x00080000,
            MiniDumpFilterTriage = 0x00100000,
            MiniDumpValidTypeFlags = 0x001fffff,
        }
    }
}
