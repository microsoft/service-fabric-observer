// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Win32.SafeHandles;
using NLog.LayoutRenderers;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Win32 PInvoke helper methods. 
    /// </summary>
    [SuppressUnmanagedCodeSecurity]
    public static class NativeMethods
    {
        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int ERROR_SUCCESS = 0;
        private const int STILL_ACTIVE = 259;
        private const int ERROR_NO_MORE_ITEMS = 259;
        private const int ERROR_INSUFFICIENT_BUFFER_SIZE = 122;
        private const int MAX_PATH = 260;
        private const int SystemProcessInformation = 5;
        private static readonly Logger logger = new("NativeMethods");
        private static readonly string[] ignoreProcessList = new string[]
        {
            "AggregatorHost.exe", "backgroundTaskHost.exe", "CcmExec.exe", "com.docker.service",
            "conhost.exe", "csrss.exe", "dwm.exe", "esif_uf.exe", "fontdrvhost.exe",
            "lsass.exe", "LsaIso.exe", "services.exe", "smss.exe", "svchost.exe",
            "System", "System interrupts", "Secure System", "Registry",
            "taskhostw.exe", "TextInputHost.exe", "wininit.exe", "winlogon.exe",
            "WmiPrvSE.exe", "WUDFHost.exe", "vmcompute.exe", "vmms.exe", "vmwp.exe", "vmmem"
        };
        private static readonly string[] ignoreFabricSystemServicesList = new string[]
        {
            "Fabric.exe", "FabricHost.exe", "FabricApplicationGateway.exe", "FabricCAS.exe",
            "FabricDCA.exe", "FabricDnsService.exe", "FabricFAS.exe", "FabricGateway.exe",
            "FabricHost.exe", "FabricIS.exe", "FabricRM.exe", "FabricUS.exe"
        };
        private static object lockObj = new object();

        [Flags]
        public enum CreateToolhelp32SnapshotFlags : uint
        {
            /// <summary>
            /// Indicates that the snapshot handle is to be inheritable.
            /// </summary>
            TH32CS_INHERIT = 0x80000000,

            /// <summary>
            /// Includes all heaps of the process specified in th32ProcessID in the snapshot.
            /// To enumerate the heaps, see Heap32ListFirst.
            /// </summary>
            TH32CS_SNAPHEAPLIST = 0x00000001,

            /// <summary>
            /// Includes all modules of the process specified in th32ProcessID in the snapshot.
            /// To enumerate the modules, see <see cref="Module32First(SafeObjectHandle,MODULEENTRY32*)"/>.
            /// If the function fails with <see cref="Win32ErrorCode.ERROR_BAD_LENGTH"/>, retry the function until
            /// it succeeds.
            /// <para>
            /// 64-bit Windows:  Using this flag in a 32-bit process includes the 32-bit modules of the process
            /// specified in th32ProcessID, while using it in a 64-bit process includes the 64-bit modules.
            /// To include the 32-bit modules of the process specified in th32ProcessID from a 64-bit process, use
            /// the <see cref="TH32CS_SNAPMODULE32"/> flag.
            /// </para>
            /// </summary>
            TH32CS_SNAPMODULE = 0x00000008,

            /// <summary>
            /// Includes all 32-bit modules of the process specified in th32ProcessID in the snapshot when called from
            /// a 64-bit process.
            /// This flag can be combined with <see cref="TH32CS_SNAPMODULE"/> or <see cref="TH32CS_SNAPALL"/>.
            /// If the function fails with <see cref="Win32ErrorCode.ERROR_BAD_LENGTH"/>, retry the function until it
            /// succeeds.
            /// </summary>
            TH32CS_SNAPMODULE32 = 0x00000010,

            /// <summary>
            /// Includes all processes in the system in the snapshot. To enumerate the processes, see
            /// <see cref="Process32First(SafeObjectHandle,PROCESSENTRY32*)"/>.
            /// </summary>
            TH32CS_SNAPPROCESS = 0x00000002,

            /// <summary>
            /// Includes all threads in the system in the snapshot. To enumerate the threads, see
            /// Thread32First.
            /// <para>
            /// To identify the threads that belong to a specific process, compare its process identifier to the
            /// th32OwnerProcessID member of the THREADENTRY32 structure when
            /// enumerating the threads.
            /// </para>
            /// </summary>
            TH32CS_SNAPTHREAD = 0x00000004,

            /// <summary>
            /// Includes all processes and threads in the system, plus the heaps and modules of the process specified in
            /// th32ProcessID.
            /// </summary>
            TH32CS_SNAPALL = TH32CS_SNAPHEAPLIST | TH32CS_SNAPMODULE | TH32CS_SNAPPROCESS | TH32CS_SNAPTHREAD,
        }

        [Flags]
        public enum MINIDUMP_TYPE
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
            MiniDumpValidTypeFlags = 0x001fffff
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            /// <summary>
            /// Size of the structure, in bytes. You must set this member before calling GlobalMemoryStatusEx.
            /// </summary>
            public uint dwLength;

            /// <summary>
            /// Number between 0 and 100 that specifies the approximate percentage of physical memory that is in use (0 indicates no memory use and 100 indicates full memory use).
            /// </summary>
            public uint dwMemoryLoad;

            /// <summary>
            /// Total size of physical memory, in bytes.
            /// </summary>
            public ulong ullTotalPhys;

            /// <summary>
            /// Size of physical memory available, in bytes.
            /// </summary>
            public ulong ullAvailPhys;

            /// <summary>
            /// Size of the committed memory limit, in bytes. This is physical memory plus the size of the page file, minus a small overhead.
            /// </summary>
            public ulong ullTotalPageFile;

            /// <summary>
            /// Size of available memory to commit, in bytes. The limit is ullTotalPageFile.
            /// </summary>
            public ulong ullAvailPageFile;

            /// <summary>
            /// Total size of the user mode portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullTotalVirtual;

            /// <summary>
            /// Size of unreserved and uncommitted memory in the user mode portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullAvailVirtual;

            /// <summary>
            /// Size of unreserved and uncommitted memory in the extended portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullAvailExtendedVirtual;

            /// <summary>
            /// Initializes a new instance of the <see cref="T:MEMORYSTATUSEX"/> class.
            /// </summary>
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PerformanceInformation
        {
            /// <summary>The size of this structure, in bytes.</summary>
            public uint cb;

            /// <summary>The number of pages currently committed by the system. Note that committing
            /// pages (using VirtualAlloc with MEM_COMMIT) changes this value immediately; however,
            /// the physical memory is not charged until the pages are accessed.</summary>
            public IntPtr CommitTotal;

            /// <summary>The current maximum number of pages that can be committed by the system
            /// without extending the paging file(s). This number can change if memory is added
            /// or deleted, or if pagefiles have grown, shrunk, or been added. If the paging
            /// file can be extended, this is a soft limit.</summary>
            public IntPtr CommitLimit;

            /// <summary>The maximum number of pages that were simultaneously in the committed state
            /// since the last system reboot.</summary>
            public IntPtr CommitPeak;

            /// <summary>The amount of actual physical memory, in pages.</summary>
            public IntPtr PhysicalTotal;

            /// <summary>The amount of physical memory currently available, in pages. This is the
            /// amount of physical memory that can be immediately reused without having to write
            /// its contents to disk first. It is the sum of the size of the standby, free, and
            /// zero lists.</summary>
            public IntPtr PhysicalAvailable;

            /// <summary>The amount of system cache memory, in pages. This is the size of the
            /// standby list plus the system working set.</summary>
            public IntPtr SystemCache;

            /// <summary>The sum of the memory currently in the paged and nonpaged kernel pools, in pages.</summary>
            public IntPtr KernelTotal;

            /// <summary>The memory currently in the paged kernel pool, in pages.</summary>
            public IntPtr KernelPaged;

            /// <summary>The memory currently in the nonpaged kernel pool, in pages.</summary>
            public IntPtr KernelNonpaged;

            /// <summary>The size of a page, in bytes.</summary>
            public IntPtr PageSize;

            /// <summary>The current number of open handles.</summary>
            public uint HandleCount;

            /// <summary>The current number of processes.</summary>
            public uint ProcessCount;

            /// <summary>The current number of threads.</summary>
            public uint ThreadCount;
        }

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            All = 0x001F0FFF,
            Terminate = 0x00000001,
            CreateThread = 0x00000002,
            VirtualMemoryOperation = 0x00000008,
            VirtualMemoryRead = 0x00000010,
            VirtualMemoryWrite = 0x00000020,
            DuplicateHandle = 0x00000040,
            CreateProcess = 0x000000080,
            SetQuota = 0x00000100,
            SetInformation = 0x00000200,
            QueryInformation = 0x00000400,
            QueryLimitedInformation = 0x00001000,
            Synchronize = 0x00100000
        }

        // Networking \\
        // Credit: http://pinvoke.net/default.aspx/iphlpapi/GetExtendedTcpTable.html

        public enum TCP_TABLE_CLASS
        {
            TCP_TABLE_BASIC_LISTENER,
            TCP_TABLE_BASIC_CONNECTIONS,
            TCP_TABLE_BASIC_ALL,
            TCP_TABLE_OWNER_PID_LISTENER,
            TCP_TABLE_OWNER_PID_CONNECTIONS,
            TCP_TABLE_OWNER_PID_ALL,
            TCP_TABLE_OWNER_MODULE_LISTENER,
            TCP_TABLE_OWNER_MODULE_CONNECTIONS,
            TCP_TABLE_OWNER_MODULE_ALL
        }

        public enum MIB_TCP_STATE
        {
            MIB_TCP_STATE_CLOSED = 1,
            MIB_TCP_STATE_LISTEN = 2,
            MIB_TCP_STATE_SYN_SENT = 3,
            MIB_TCP_STATE_SYN_RCVD = 4,
            MIB_TCP_STATE_ESTAB = 5,
            MIB_TCP_STATE_FIN_WAIT1 = 6,
            MIB_TCP_STATE_FIN_WAIT2 = 7,
            MIB_TCP_STATE_CLOSE_WAIT = 8,
            MIB_TCP_STATE_CLOSING = 9,
            MIB_TCP_STATE_LAST_ACK = 10,
            MIB_TCP_STATE_TIME_WAIT = 11,
            MIB_TCP_STATE_DELETE_TCB = 12,
            MIB_TCP_STATE_BOUND = 100
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCP6TABLE_OWNER_PID
        {
            public uint dwNumEntries;

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 1)]
            public MIB_TCP6ROW_OWNER_PID[] table;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] localAddr;
            public uint localScopeId;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] localPort;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] remoteAddr;
            public uint remoteScopeId;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] remotePort;
            public uint state;
            public uint owningPid;

            public uint ProcessId
            {
                get 
                { 
                    return owningPid; 
                }
            }

            public long LocalScopeId
            {
                get 
                {
                    return localScopeId; 
                }
            }

            public IPAddress LocalAddress
            {
                get 
                { 
                    return new IPAddress(localAddr, LocalScopeId); 
                }
            }

            public ushort LocalPort
            {
                get 
                { 
                    return BitConverter.ToUInt16(localPort.Take(2).Reverse().ToArray(), 0); 
                }
            }

            public long RemoteScopeId
            {
                get 
                { 
                    return remoteScopeId; 
                }
            }

            public IPAddress RemoteAddress
            {
                get 
                { 
                    return new IPAddress(remoteAddr, RemoteScopeId); 
                }
            }

            public ushort RemotePort
            {
                get 
                { 
                    return BitConverter.ToUInt16(remotePort.Take(2).Reverse().ToArray(), 0); 
                }
            }

            public MIB_TCP_STATE State
            {
                get 
                { 
                    return (MIB_TCP_STATE)state; 
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPTABLE_OWNER_PID
        {
            public uint dwNumEntries;

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 1)]
            public MIB_TCPROW_OWNER_PID[] table;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] localPort;
            public uint remoteAddr;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] remotePort;
            public uint owningPid;

            public uint ProcessId
            {
                get 
                { 
                    return owningPid; 
                }
            }

            public IPAddress LocalAddress
            {
                get 
                { 
                    return new IPAddress(localAddr); 
                }
            }

            public ushort LocalPort
            {
                get
                {
                    return BitConverter.ToUInt16(new byte[2] { localPort[1], localPort[0] }, 0);
                }
            }

            public IPAddress RemoteAddress
            {
                get 
                { 
                    return new IPAddress(remoteAddr); 
                }
            }

            public ushort RemotePort
            {
                get
                {
                    return BitConverter.ToUInt16(new byte[2] { remotePort[1], remotePort[0] }, 0);
                }
            }

            public MIB_TCP_STATE State
            {
                get 
                { 
                    return (MIB_TCP_STATE)state; 
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            private const int MAX_PATH = 260;
            internal uint dwSize;
            internal uint cntUsage;
            internal uint th32ProcessID;
            internal IntPtr th32DefaultHeapID;
            internal uint th32ModuleID;
            internal uint cntThreads;
            internal uint th32ParentProcessID;
            internal int pcPriClassBase;
            internal uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            internal string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PSS_THREAD_INFORMATION
        {
            /// <summary>
            /// <para>The count of threads in the snapshot.</para>
            /// </summary>
            internal uint ThreadsCaptured;

            /// <summary>
            /// <para>The length of the <c>CONTEXT</c> record captured, in bytes.</para>
            /// </summary>
            internal uint ContextLength;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PSS_PROCESS_INFORMATION
        {
            /// <summary>
            /// <para>The exit code of the process. If the process has not exited, this is set to <c>STILL_ACTIVE</c> (259).</para>
            /// </summary>
            internal uint ExitStatus;

            /// <summary>
            /// <para>The address to the process environment block (PEB). Reserved for use by the operating system.</para>
            /// </summary>
            internal IntPtr PebBaseAddress;

            /// <summary>
            /// <para>The affinity mask of the process.</para>
            /// </summary>
            internal UIntPtr AffinityMask;

            /// <summary>
            /// <para>The base priority level of the process.</para>
            /// </summary>
            internal int BasePriority;

            /// <summary>
            /// <para>The process ID.</para>
            /// </summary>
            internal uint ProcessId;

            /// <summary>
            /// <para>The parent process ID.</para>
            /// </summary>
            internal uint ParentProcessId;

            /// <summary>
            /// <para>Flags about the process. For more information, see PSS_PROCESS_FLAGS.</para>
            /// </summary>
            internal PSS_PROCESS_FLAGS Flags;

            /// <summary>
            /// <para>The time the process was created. For more information, see FILETIME.</para>
            /// </summary>
            internal FILETIME CreateTime;

            /// <summary>
            /// <para>If the process exited, the time of the exit. For more information, see FILETIME.</para>
            /// </summary>
            internal FILETIME ExitTime;

            /// <summary>
            /// <para>The amount of time the process spent executing in kernel-mode. For more information, see FILETIME.</para>
            /// </summary>
            internal FILETIME KernelTime;

            /// <summary>
            /// <para>The amount of time the process spent executing in user-mode. For more information, see FILETIME.</para>
            /// </summary>
            internal FILETIME UserTime;

            /// <summary>
            /// <para>The priority class.</para>
            /// </summary>
            internal uint PriorityClass;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr PeakVirtualSize;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr VirtualSize;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal uint PageFaultCount;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr PeakWorkingSetSize;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr WorkingSetSize;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr QuotaPeakPagedPoolUsage;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr QuotaPagedPoolUsage;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr QuotaPeakNonPagedPoolUsage;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr QuotaNonPagedPoolUsage;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr PagefileUsage;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr PeakPagefileUsage;

            /// <summary>
            /// <para>A memory usage counter. See the GetProcessMemoryInfo function for more information.</para>
            /// </summary>
            internal UIntPtr PrivateUsage;

            /// <summary>
            /// <para>Reserved for use by the operating system.</para>
            /// </summary>
            internal uint ExecuteFlags;

            /// <summary>
            /// <para>The full path to the process executable. If the path exceeds the allocated buffer size, it is truncated.</para>
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            internal string ImageFileName;
        }

        // For PSCaptureSnapshot/PSQuerySnapshot \\
        // Credit (MIT): https://github.com/dahall/Vanara/blob/master/PInvoke/Kernel32/ProcessSnapshot.cs, author: https://github.com/dahall
        [Flags]
        private enum PSS_CAPTURE_FLAGS : uint
        {
            /// <summary>Capture nothing.</summary>
            PSS_CAPTURE_NONE = 0x00000000,

            /// <summary>
            /// Capture a snapshot of all cloneable pages in the process. The clone includes all MEM_PRIVATE regions, as well as all sections
            /// (MEM_MAPPED and MEM_IMAGE) that are shareable. All Win32 sections created via CreateFileMapping are shareable.
            /// </summary>
            PSS_CAPTURE_VA_CLONE = 0x00000001,

            /// <summary>(Do not use.)</summary>
            PSS_CAPTURE_RESERVED_00000002 = 0x00000002,

            /// <summary>Capture the handle table (handle values only).</summary>
            PSS_CAPTURE_HANDLES = 0x00000004,

            /// <summary>Capture name information for each handle.</summary>
            PSS_CAPTURE_HANDLE_NAME_INFORMATION = 0x00000008,

            /// <summary>Capture basic handle information such as HandleCount, PointerCount, GrantedAccess, etc.</summary>
            PSS_CAPTURE_HANDLE_BASIC_INFORMATION = 0x00000010,

            /// <summary>Capture type-specific information for supported object types: Process, Thread, Event, Mutant, Section.</summary>
            PSS_CAPTURE_HANDLE_TYPE_SPECIFIC_INFORMATION = 0x00000020,

            /// <summary>Capture the handle tracing table.</summary>
            PSS_CAPTURE_HANDLE_TRACE = 0x00000040,

            /// <summary>Capture thread information (IDs only).</summary>
            PSS_CAPTURE_THREADS = 0x00000080,

            /// <summary>Capture the context for each thread.</summary>
            PSS_CAPTURE_THREAD_CONTEXT = 0x00000100,

            /// <summary>Capture extended context for each thread (e.g. CONTEXT_XSTATE).</summary>
            PSS_CAPTURE_THREAD_CONTEXT_EXTENDED = 0x00000200,

            /// <summary>(Do not use.)</summary>
            PSS_CAPTURE_RESERVED_00000400 = 0x00000400,

            /// <summary>
            /// Capture a snapshot of the virtual address space. The VA space is captured as an array of MEMORY_BASIC_INFORMATION structures.
            /// This flag does not capture the contents of the pages.
            /// </summary>
            PSS_CAPTURE_VA_SPACE = 0x00000800,

            /// <summary>
            /// For MEM_IMAGE and MEM_MAPPED regions, dumps the path to the file backing the sections (identical to what GetMappedFileName
            /// returns). For MEM_IMAGE regions, also dumps: The PROCESS_VM_READ access right is required on the process handle.
            /// </summary>
            PSS_CAPTURE_VA_SPACE_SECTION_INFORMATION = 0x00001000,

            /// <summary/>
            PSS_CAPTURE_IPT_TRACE = 0x00002000,

            /// <summary>
            /// The breakaway is optional. If the clone process fails to create as a breakaway, then it is created still inside the job. This
            /// flag must be specified in combination with either PSS_CREATE_FORCE_BREAKAWAY and/or PSS_CREATE_BREAKAWAY.
            /// </summary>
            PSS_CREATE_BREAKAWAY_OPTIONAL = 0x04000000,

            /// <summary>The clone is broken away from the parent process' job. This is equivalent to CreateProcess flag CREATE_BREAKAWAY_FROM_JOB.</summary>
            PSS_CREATE_BREAKAWAY = 0x08000000,

            /// <summary>The clone is forcefully broken away the parent process's job. This is only allowed for Tcb-privileged callers.</summary>
            PSS_CREATE_FORCE_BREAKAWAY = 0x10000000,

            /// <summary>
            /// The facility should not use the process heap for any persistent or transient allocations. The use of the heap may be
            /// undesirable in certain contexts such as creation of snapshots in the exception reporting path (where the heap may be corrupted).
            /// </summary>
            PSS_CREATE_USE_VM_ALLOCATIONS = 0x20000000,

            /// <summary>
            /// Measure performance of the facility. Performance counters can be retrieved via PssQuerySnapshot with the
            /// PSS_QUERY_PERFORMANCE_COUNTERS information class of PSS_QUERY_INFORMATION_CLASS.
            /// </summary>
            PSS_CREATE_MEASURE_PERFORMANCE = 0x40000000,

            /// <summary>
            /// The virtual address (VA) clone process does not hold a reference to the underlying image. This will cause functions such as
            /// QueryFullProcessImageName to fail on the VA clone process.
            /// </summary>
            PSS_CREATE_RELEASE_SECTION = 0x80000000
        }

        private enum PSS_QUERY_INFORMATION_CLASS
        {
            /// <summary>Returns a PSS_PROCESS_INFORMATION structure, with information about the original process.</summary>
            PSS_QUERY_PROCESS_INFORMATION,

            /// <summary>Returns a PSS_VA_CLONE_INFORMATION structure, with a handle to the VA clone.</summary>
            PSS_QUERY_VA_CLONE_INFORMATION,

            /// <summary>Returns a PSS_AUXILIARY_PAGES_INFORMATION structure, which contains the count of auxiliary pages captured.</summary>
            PSS_QUERY_AUXILIARY_PAGES_INFORMATION,

            /// <summary>Returns a PSS_VA_SPACE_INFORMATION structure, which contains the count of regions captured.</summary>
            PSS_QUERY_VA_SPACE_INFORMATION,

            /// <summary>Returns a PSS_HANDLE_INFORMATION structure, which contains the count of handles captured.</summary>
            PSS_QUERY_HANDLE_INFORMATION,

            /// <summary>Returns a PSS_THREAD_INFORMATION structure, which contains the count of threads captured.</summary>
            PSS_QUERY_THREAD_INFORMATION,

            /// <summary>
            /// Returns a PSS_HANDLE_TRACE_INFORMATION structure, which contains a handle to the handle trace section, and its size.
            /// </summary>
            PSS_QUERY_HANDLE_TRACE_INFORMATION,

            /// <summary>Returns a PSS_PERFORMANCE_COUNTERS structure, which contains various performance counters.</summary>
            PSS_QUERY_PERFORMANCE_COUNTERS,
        }

        [Flags]
        private enum PSS_THREAD_FLAGS
        {
            /// <summary>No flag.</summary>
            PSS_THREAD_FLAGS_NONE = 0x0000,

            /// <summary>The thread terminated.</summary>
            PSS_THREAD_FLAGS_TERMINATED = 0x0001
        }

        [Flags]
        private enum PSS_PROCESS_FLAGS
        {
            /// <summary>No flag.</summary>
            PSS_PROCESS_FLAGS_NONE = 0x00000000,

            /// <summary>The process is protected.</summary>
            PSS_PROCESS_FLAGS_PROTECTED = 0x00000001,

            /// <summary>The process is a 32-bit process running on a 64-bit native OS.</summary>
            PSS_PROCESS_FLAGS_WOW64 = 0x00000002,

            /// <summary>Undefined.</summary>
            PSS_PROCESS_FLAGS_RESERVED_03 = 0x00000004,

            /// <summary>Undefined.</summary>
            PSS_PROCESS_FLAGS_RESERVED_04 = 0x00000008,

            /// <summary>
            /// The process is frozen; for example, a debugger is attached and broken into the process or a Store process is suspended by a
            /// lifetime management service.
            /// </summary>
            PSS_PROCESS_FLAGS_FROZEN = 0x00000010
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PSS_THREAD_ENTRY
        {
            /// <summary>
            /// <para>The exit code of the process. If the process has not exited, this is set to <c>STILL_ACTIVE</c> (259).</para>
            /// </summary>
            public uint ExitStatus;

            /// <summary>
            /// <para>The address of the thread environment block (TEB). Reserved for use by the operating system.</para>
            /// </summary>
            public IntPtr TebBaseAddress;

            /// <summary>
            /// <para>The process ID.</para>
            /// </summary>
            public uint ProcessId;

            /// <summary>
            /// <para>The thread ID.</para>
            /// </summary>
            public uint ThreadId;

            /// <summary>
            /// <para>The affinity mask of the process.</para>
            /// </summary>
            public UIntPtr AffinityMask;

            /// <summary>
            /// <para>The thread’s dynamic priority level.</para>
            /// </summary>
            public int Priority;

            /// <summary>
            /// <para>The base priority level of the process.</para>
            /// </summary>
            public int BasePriority;

            /// <summary>
            /// <para>Reserved for use by the operating system.</para>
            /// </summary>
            public IntPtr LastSyscallFirstArgument;

            /// <summary>
            /// <para>Reserved for use by the operating system.</para>
            /// </summary>
            public ushort LastSyscallNumber;

            /// <summary>
            /// <para>The time the thread was created. For more information, see FILETIME.</para>
            /// </summary>
            public FILETIME CreateTime;

            /// <summary>
            /// <para>If the thread exited, the time of the exit. For more information, see FILETIME.</para>
            /// </summary>
            public FILETIME ExitTime;

            /// <summary>
            /// <para>The amount of time the thread spent executing in kernel mode. For more information, see FILETIME.</para>
            /// </summary>
            public FILETIME KernelTime;

            /// <summary>
            /// <para>The amount of time the thread spent executing in user mode. For more information, see FILETIME.</para>
            /// </summary>
            public FILETIME UserTime;

            /// <summary>
            /// <para>A pointer to the thread procedure for thread.</para>
            /// </summary>
            public IntPtr Win32StartAddress;

            /// <summary>
            /// <para>The capture time of this thread. For more information, see FILETIME.</para>
            /// </summary>
            public FILETIME CaptureTime;

            /// <summary>
            /// <para>Flags about the thread. For more information, see PSS_THREAD_FLAGS.</para>
            /// </summary>
            public PSS_THREAD_FLAGS Flags;

            /// <summary>
            /// <para>The count of times the thread suspended.</para>
            /// </summary>
            public ushort SuspendCount;

            /// <summary>
            /// <para>The size of ContextRecord, in bytes.</para>
            /// </summary>
            public ushort SizeOfContextRecord;

            /// <summary>
            /// <para>
            /// A pointer to the context record if thread context information was captured. The pointer is valid for the lifetime of the walk
            /// marker passed to PssWalkSnapshot.
            /// </para>
            /// </summary>
            public IntPtr ContextRecord;         // valid for life time of walk marker
        }

        private enum PSS_WALK_INFORMATION_CLASS
        {
            /// <summary>
            /// Returns a PSS_AUXILIARY_PAGE_ENTRY structure, which contains the address, page attributes and contents of an auxiliary copied page.
            /// </summary>
            PSS_WALK_AUXILIARY_PAGES,

            /// <summary>
            /// Returns a PSS_VA_SPACE_ENTRY structure, which contains the MEMORY_BASIC_INFORMATION structure for every distinct VA region.
            /// </summary>
            PSS_WALK_VA_SPACE,

            /// <summary>
            /// Returns a PSS_HANDLE_ENTRY structure, with information specifying the handle value, its type name, object name (if captured),
            /// basic information (if captured), and type-specific information (if captured).
            /// </summary>
            PSS_WALK_HANDLES,

            /// <summary>
            /// Returns a PSS_THREAD_ENTRY structure, with basic information about the thread, as well as its termination state, suspend
            /// count and Win32 start address.
            /// </summary>
            PSS_WALK_THREADS,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            /// <summary>The number of read operations performed.</summary>
            public ulong ReadOperationCount;

            /// <summary>The number of write operations performed.</summary>
            public ulong WriteOperationCount;

            /// <summary>The number of I/O operations performed, other than read and write operations.</summary>
            public ulong OtherOperationCount;

            /// <summary>The number of bytes read.</summary>
            public ulong ReadTransferCount;

            /// <summary>The number of bytes written.</summary>
            public ulong WriteTransferCount;

            /// <summary>The number of bytes transferred during operations other than read and write operations.</summary>
            public ulong OtherTransferCount;
        }

        // NtQuerySystemInformation - Process \\

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_PROCESS_INFORMATION
        {
            internal uint NextEntryOffset;
            internal uint NumberOfThreads;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            internal readonly byte[] Reserved1;
            internal UNICODE_STRING ImageName;
            internal int BasePriority;
            internal UIntPtr UniqueProcessId;
            internal readonly UIntPtr Reserved2;
            internal uint HandleCount;
            internal uint SessionId;
            private readonly UIntPtr Reserved3;
            internal UIntPtr PeakVirtualSize;  // SIZE_T
            internal UIntPtr VirtualSize;
            private readonly uint Reserved4;
            internal UIntPtr PeakWorkingSetSize;  // SIZE_T
            internal UIntPtr WorkingSetSize;  // SIZE_T
            private readonly UIntPtr Reserved5;
            internal UIntPtr QuotaPagedPoolUsage;  // SIZE_T
            private readonly UIntPtr Reserved6;
            internal UIntPtr QuotaNonPagedPoolUsage;  // SIZE_T
            internal UIntPtr PagefileUsage;  // SIZE_T
            internal UIntPtr PeakPagefileUsage;  // SIZE_T
            internal UIntPtr PrivatePageCount;  // SIZE_T
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            private readonly long[] Reserved7;
        }

        // Method Imports \\

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumProcesses([In, Out, MarshalAs(UnmanagedType.LPArray)] uint[] lpidProcess, uint cb, out uint lpcbNeeded);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessMemoryInfo(SafeProcessHandle hProcess, [Out] out PROCESS_MEMORY_COUNTERS_EX counters, [In] uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessHandleCount(SafeProcessHandle hProcess, out uint pdwHandleCount);

        // Process dump support.
        [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MiniDumpWriteDump(SafeProcessHandle hProcess, uint processId, SafeHandle hFile, MINIDUMP_TYPE dumpType, IntPtr expParam, IntPtr userStreamParam, IntPtr callbackParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint processAccess, bool bInheritHandle,uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle ProcessHandle, out FILETIME CreationTime, out FILETIME ExitTime, out FILETIME KernelTime, out FILETIME UserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern uint GetModuleBaseName(SafeProcessHandle hProcess, [Optional] IntPtr hModule, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpBaseName, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int PssCaptureSnapshot(SafeProcessHandle ProcessHandle, PSS_CAPTURE_FLAGS CaptureFlags, uint ContextFlags, ref IntPtr SnapshotHandle);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int PssQuerySnapshot(IntPtr SnapshotHandle, PSS_QUERY_INFORMATION_CLASS InformationClass, IntPtr Buffer, uint BufferLength);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int PssFreeSnapshot(IntPtr ProcessHandle, IntPtr SnapshotHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int PssWalkSnapshot(IntPtr SnapshotHandle, PSS_WALK_INFORMATION_CLASS InformationClass, IntPtr WalkMarkerHandle, IntPtr Buffer, uint BufferLength);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int PssWalkMarkerCreate([Optional] IntPtr Allocator, ref IntPtr WalkMarkerHandle);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int PssWalkMarkerFree(IntPtr WalkMarkerHandle);

        [DllImport("psapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(ref PerformanceInformation pi, uint cb);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob([In] SafeProcessHandle ProcessHandle, [In, Optional] IntPtr JobHandle, [MarshalAs(UnmanagedType.Bool)] out bool Result);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, uint ulAf, TCP_TABLE_CLASS TableClass, uint Reserved = 0);

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetProcessIoCounters([In] SafeProcessHandle ProcessHandle, out IO_COUNTERS lpIoCounters);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, uint SystemInformationLength, out uint ReturnLength);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeObjectHandle CreateToolhelp32Snapshot([In] uint dwFlags, [In] uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HeapFree(IntPtr hHeap, uint dwFlags, IntPtr lpMem);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First([In] SafeObjectHandle hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next([In] SafeObjectHandle hSnapshot, ref PROCESSENTRY32 lppe);

        // Impls/Helpers \\

        /// <summary>
        /// Gets the number of execution threads started by the process with supplied pid.
        /// </summary>
        /// <param name="pid">The id of the process (pid).</param>
        /// <returns>The number of execution threads started by the process.</returns>
        /// <exception cref="Win32Exception">A Win32 Error Code will be present in the exception Message.</exception>
        public static int GetProcessThreadCount(int pid)
        {
            int activeThreads = 0;
            IntPtr snap = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            SafeProcessHandle hProc = GetSafeProcessHandle(pid);

            try
            {
                if (hProc.IsInvalid)
                {
                    return 0;
                }

                int err = PssCaptureSnapshot(hProc, PSS_CAPTURE_FLAGS.PSS_CAPTURE_THREADS, 0, ref snap);

                if (err != ERROR_SUCCESS)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                int size = Marshal.SizeOf(typeof(PSS_THREAD_INFORMATION));
                buffer = Marshal.AllocHGlobal(size);
                err = PssQuerySnapshot(snap, PSS_QUERY_INFORMATION_CLASS.PSS_QUERY_THREAD_INFORMATION, buffer, (uint)size);

                if (err != ERROR_SUCCESS)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                PSS_THREAD_INFORMATION threadInfo = (PSS_THREAD_INFORMATION)Marshal.PtrToStructure(buffer, typeof(PSS_THREAD_INFORMATION));

                if (threadInfo.ThreadsCaptured > 0)
                {
                    foreach (var entry in PssWalkSnapshot<PSS_THREAD_ENTRY>(snap, PSS_WALK_INFORMATION_CLASS.PSS_WALK_THREADS))
                    {
                        if (entry.ExitStatus != STILL_ACTIVE || entry.Flags.HasFlag(PSS_THREAD_FLAGS.PSS_THREAD_FLAGS_TERMINATED))
                        {
                            continue;
                        }
                        activeThreads++;
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                // Immediately die.
                Environment.FailFast($"OOM. Taking down FO:{Environment.NewLine}{Environment.StackTrace}");
            }
            catch (ArgumentException)
            {

            }
            catch (SEHException seh)
            {
                logger.LogWarning($"GetProcessThreadCount: Failed with SEH exception {seh.ErrorCode}: {seh.Message}");
                return 0;
            }
            catch (Win32Exception we)
            {
                logger.LogWarning($"GetProcessThreadCount: Failed with Win32 exception {we.NativeErrorCode}: {we.Message}");
                return 0;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (snap != IntPtr.Zero)
                {
                    if (PssFreeSnapshot(GetCurrentProcess(), snap) != ERROR_SUCCESS)
                    {
                        logger.LogWarning($"Failed to free process snapshot with Win32 error code {Marshal.GetLastWin32Error()}");
                    }
                }

                hProc?.Dispose();
                hProc = null;
            }

            return activeThreads;
        }

        /// <summary>
        /// Creates a native snapshot of all processes currently running on the system.
        /// </summary>
        /// <returns>SafeObjectHandle to the snapshot.</returns>
        public static SafeObjectHandle CreateProcessSnapshot()
        {
            return CreateToolhelp32Snapshot((uint)CreateToolhelp32SnapshotFlags.TH32CS_SNAPPROCESS, 0);
        }

        /// <summary>
        /// Takes a snapshot of Process object with specified pid.
        /// </summary>
        /// <param name="pid">The process id of the target process.</param>
        /// <returns>A PSS_PROCESS_INFORMATION structure containing current information about the process. An empty structure means this function failed.</returns>
        /// <exception cref="Win32Exception">This is thrown if PssCaptureSnapshot fails.</exception>
        private static PSS_PROCESS_INFORMATION PssGetProcessSnapshot(uint pid)
        {
            PSS_PROCESS_INFORMATION procInfo = new();
            IntPtr snapShot = IntPtr.Zero;
            IntPtr procInfoBuffer = IntPtr.Zero;
            SafeProcessHandle hProc = null;

            try
            {
                hProc = OpenProcess((uint)ProcessAccessFlags.All, false, pid);

                if (hProc == null || hProc.IsInvalid)
                {
                    return procInfo;
                }

                if (PssCaptureSnapshot(hProc, PSS_CAPTURE_FLAGS.PSS_CAPTURE_NONE, 0, ref snapShot) != ERROR_SUCCESS)
                {
                    throw new Win32Exception(
                       $"PssGetProcessSnapshot({pid}) Failed with Win32 error code {Marshal.GetLastWin32Error()}");
                }

                int size = Marshal.SizeOf(typeof(PSS_PROCESS_INFORMATION));
                procInfoBuffer = Marshal.AllocHGlobal(size);

                if (PssQuerySnapshot(snapShot, PSS_QUERY_INFORMATION_CLASS.PSS_QUERY_PROCESS_INFORMATION, procInfoBuffer, (uint)size) != ERROR_SUCCESS)
                {
                    throw new Win32Exception(
                       $"PssQuerySnapshot({pid}) Failed with Win32 error code {Marshal.GetLastWin32Error()}");
                }

                procInfo = (PSS_PROCESS_INFORMATION)Marshal.PtrToStructure(procInfoBuffer, typeof(PSS_PROCESS_INFORMATION));
            }
            catch (ArgumentException)
            {
                return procInfo;
            }
            catch (OutOfMemoryException)
            {
                // Immediately die.
                Environment.FailFast($"OOM. Taking down FO:{Environment.NewLine}{Environment.StackTrace}");
            }
            finally
            {
                Marshal.FreeHGlobal(procInfoBuffer);
                IntPtr currentHProc = GetCurrentProcess();
                _ = PssFreeSnapshot(currentHProc, snapShot);
                hProc.Dispose();
                hProc = null;
            }

            return procInfo;
        }

        private static Dictionary<int, List<(string childProcName, int childProcId, DateTime childProcStartTime)>> descendantsDictionary = new();

        public static bool RefreshSFUserChildProcessDataCache()
        {
            ClearSFUserChildProcessDataCache();
            NtSetSFUserServiceDescendantCache();
            return descendantsDictionary.Any();
        }

        private static bool NtSetSFUserServiceDescendantCache()
        {
            descendantsDictionary = new Dictionary<int, List<(string childProcName, int childProcId, DateTime childProcStartTime)>>();
            List<SYSTEM_PROCESS_INFORMATION> procInfoList = NtGetFilteredSystemProcessInfo();
            
            if (procInfoList == null)
            {
                return false;
            }

            for (int i = 0; i < procInfoList.Count; ++i)
            {
                SYSTEM_PROCESS_INFORMATION procInfo = procInfoList[i];
                
                if (procInfo.UniqueProcessId != UIntPtr.Zero)
                {
                    uint pid = procInfo.UniqueProcessId.ToUInt32();

                    // Has a parent process.
                    if (procInfo.Reserved2 != UIntPtr.Zero)
                    {
                        uint parentPid = procInfo.Reserved2.ToUInt32();
                        string childProcName = Path.GetFileNameWithoutExtension(procInfo.ImageName.Buffer);

                        try
                        {
                            if (!descendantsDictionary.ContainsKey((int)parentPid))
                            {
                                List<(string childProcName, int childProcId, DateTime childProcStartTime)> descendants = new()
                            {
                                (childProcName, (int)pid, GetProcessStartTime((int)pid))
                            };

                                _ = descendantsDictionary.TryAdd((int)parentPid, descendants);
                            }
                            else
                            {
                                descendantsDictionary[(int)parentPid].Add((childProcName, (int)pid, GetProcessStartTime((int)pid)));
                            }
                        }
                        catch (ArgumentException)
                        {

                        }
                        catch (Win32Exception)
                        {
                            // process no longer around or not allowed to access its information..
                        }
                    }
                }
            }

            return true;
        }

        public static void ClearSFUserChildProcessDataCache()
        {
            try
            {
                descendantsDictionary?.Clear();
                descendantsDictionary = null;
            }
            catch (ArgumentException)
            {

            }
        }

        /// <summary>
        /// Gets the child processes, if any, belonging to the process with supplied pid.
        /// </summary>
        /// <param name="parentPid">The process ID (pid) of target parent process.</param>
        /// <param name="parentProcName">Name of the process associated to specified pid when this call is made.</param>
        /// <param name="handleToSnapshot">Handle to process snapshot (created using NativeMethods.CreateToolhelp32Snapshot).</param>
        /// <returns>A List of tuple (string procName, int procId, DateTime processStartTime) representing each child process.</returns>
        /// <exception cref="Win32Exception">A Win32 Error Code will be present in the exception Message.</exception>
        public static List<(string procName, int procId, DateTime processStartTime)> GetChildProcesses(int parentPid, string parentProcName, SafeObjectHandle handleToSnapshot = null)
        {
            if (parentPid < 1 || string.IsNullOrWhiteSpace(parentProcName))
            {
                return null;
            }

            if (descendantsDictionary != null && descendantsDictionary.Any())
            {
                if (descendantsDictionary.ContainsKey(parentPid))
                {
                    return descendantsDictionary[parentPid];
                }
                else
                {
                    // parent has no children.
                    return null;
                }
            }

            // Else, take a snapshot (so, something failed with the more performant approach or the required initialization function was not called first) \\

            bool isLocalSnapshot = false;

            try
            {
                if (handleToSnapshot == null || handleToSnapshot.IsInvalid || handleToSnapshot.IsClosed)
                {
                    isLocalSnapshot = true;
                    handleToSnapshot = CreateToolhelp32Snapshot((uint)CreateToolhelp32SnapshotFlags.TH32CS_SNAPPROCESS, 0);

                    if (handleToSnapshot.IsInvalid)
                    {
                        logger.LogWarning(
                            $"GetChildProcesses({parentPid}): Failed to process snapshot at CreateToolhelp32Snapshot " +
                            $"with Win32 error code {Marshal.GetLastWin32Error()}");

                        return null;
                    }
                }

                PROCESSENTRY32 procEntry = new()
                {
                    dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32))
                };

                if (!Process32First(handleToSnapshot, ref procEntry))
                {
                    logger.LogWarning($"GetChildProcesses({parentPid}): Failed to process snapshot at Process32First " +
                                      $"with Win32 error code {Marshal.GetLastWin32Error()}");
                    
                    return null;
                }

                List<(string procName, int procId, DateTime processStartTime)> childProcs = new();

                do
                {
                    try
                    {
                        // If the detected pid is not a child of the supplied parent pid, then ignore.
                        if (parentPid != procEntry.th32ParentProcessID)
                        {
                            continue;
                        }

                        // Filter out the procs we know are not the droids we're looking for just by name or pid.
                        if (procEntry.th32ProcessID == 0 || FindInStringArray(ignoreProcessList, procEntry.szExeFile)
                            || FindInStringArray(ignoreFabricSystemServicesList, procEntry.szExeFile))
                        {
                            continue;
                        }

                        string parentSnapProcName = GetProcessNameFromId((int)procEntry.th32ParentProcessID);

                        if (string.IsNullOrWhiteSpace(parentSnapProcName))
                        {
                            continue;
                        }

                        if (string.CompareOrdinal(parentProcName, parentSnapProcName) == 0)
                        {
                            childProcs.Add((procEntry.szExeFile.Replace(".exe", ""), (int)procEntry.th32ProcessID, GetProcessStartTime((int)procEntry.th32ProcessID)));
                        }
                    }
                    catch (ArgumentException)
                    {

                    }
                    catch (Win32Exception)
                    {
                        // From GetProcessNameFromId.
                    }

                } while (Process32Next(handleToSnapshot, ref procEntry));

                return childProcs;
            }
            finally
            {
                if (isLocalSnapshot && !handleToSnapshot.IsInvalid)
                {
                    handleToSnapshot?.Dispose();
                    handleToSnapshot = null;
                }
            }
        }

        // Credit: https://github.com/dahall/Vanara/blob/5b22a156f0ba1301b48229f30b6ff4758f60a4ee/PInvoke/Kernel32/PsApi.cs#L258
        private static uint[] EnumProcesses()
        {
            uint rsz = 1024;
            uint sz;
            uint[] ids;

            do
            {
                sz = rsz * 2;
                ids = new uint[sz / sizeof(uint)];

                if (!EnumProcesses(ids, sz, out rsz))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

            } while (sz == rsz);

            try
            {
                List<uint> list = ids.ToList();
                list.RemoveAll(x => x == 0);
                ids = list.ToArray();
                list.Clear();
                list = null;
            }
            catch (ArgumentException)
            {

            }

            return ids;
        }

        /// <summary>
        /// Gets the Windows process name, without extension, for the specified process identifier.
        /// </summary>
        /// <param name="pid">The process id.</param>
        /// <returns>Process name string, if successful. Else, null.</returns>
        /// <exception cref="Win32Exception">A Win32Exception exception will be thrown if this specified process id is not found or if it is non-accessible due to its access control level.</exception>
        public static string NtGetProcessNameFromId(uint pid)
        {
            try
            {
               return NtGetProcessNameFromIdInternal(pid);
            }
            catch (Win32Exception e)
            {
                if (e.NativeErrorCode is 5 or 6)
                {
                    throw;
                }
            }

            return null;
        }

        /// <summary>
        /// Get the process name for the specified process identifier.
        /// </summary>
        /// <param name="pid">The process id.</param>
        /// <returns>Process name string, if successful. Else, null.</returns>
        /// <exception cref="Win32Exception">A Win32Exception exception will be thrown if this specified process id is not found or if it is non-accessible due to its access control level.</exception>
        public static string GetProcessNameFromId(int pid)
        {
            try
            {
                string s = GetProcessNameFromIdInternal((uint)pid);

                if (s?.Length == 0)
                {
                    return null;
                }

                return s.Replace(".exe", "");
            }
            catch (ArgumentException)
            {

            }
            catch (Win32Exception e)
            {
                if (e.NativeErrorCode is 5 or 6)
                {
                    throw;
                }
            }

            return null;
        }

        private static string GetProcessNameFromIdInternal(uint pid)
        {
            SafeProcessHandle hProc = null;
            StringBuilder sbProcName = new(1024);

            try
            {
                hProc = GetSafeProcessHandle((int)pid);

                if (!hProc.IsInvalid)
                {
                    // Get the name of the process.
                    // If GetModuleBaseName succeeds, the return value specifies the length of the string copied to the buffer, in characters.
                    // If GetModuleBaseName fails, the return value is 0.
                    if (GetModuleBaseName(hProc, IntPtr.Zero, sbProcName, (uint)sbProcName.Capacity) == 0)
                    {
                        return string.Empty;
                    }
                }

                return sbProcName.ToString();
            }
            finally
            {
                sbProcName.Clear();
                sbProcName = null;
                hProc.Dispose();
                hProc = null;
            }
        }

        /// <summary>
        /// Gets the process id for the specified process name. **Note that this is only useful if there is one process of the specified name**.
        /// </summary>
        /// <param name="procName">The name of the process.</param>
        /// <returns>Process id as uint. If this fails for any reason, it will return 0.</returns>
        public static int GetProcessIdFromName(string procName)
        {
            try
            {
                uint[] ids = EnumProcesses();

                for (int i = 0; i < ids.Length; ++i)
                {
                    uint pid = ids[i];

                    if (pid < 5)
                    {
                        continue;
                    }

                    string name;

                    try
                    {
                        name = GetProcessNameFromIdInternal(pid);
                    }
                    catch (Win32Exception)
                    {
                        // FO can't access specified (restricted) process.
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(name) || FindInStringArray(ignoreProcessList, name))
                    {
                        continue;
                    }

                    name = name.Replace(".exe", string.Empty);

                    if (name != procName)
                    {
                        continue;
                    }

                    return (int)pid;
                }
            }
            catch (Win32Exception we)
            {
                // From EnumProcesses
                logger.LogWarning($"Failure getting process information: {we.Message}");
            }

            return 0;
        }

        /// <summary>
        /// Gets the process identifiers for all running processes of the specified process name.
        /// </summary>
        /// <param name="processName"></param>
        /// <returns></returns>
        public static int[] NtGetProcessIdFromName(string processName)
        {
            List<int> pids = new();
            var procInfo = NtGetSysProcInfo();

            for (int i = 0; i < procInfo.Length; ++i)
            {
                try
                {
                    if (string.CompareOrdinal(Path.GetFileNameWithoutExtension(procInfo[i].ImageName.Buffer), processName) != 0)
                    {
                        continue;
                    }

                    pids.Add((int)procInfo[i].UniqueProcessId.ToUInt32());
                }
                catch (Exception e) when (e is ArgumentException or Win32Exception)
                {

                }
            }
            
            return pids.ToArray();
        }

        /// <summary>
        /// Gets the start time of the process with the specified identifier.
        /// </summary>
        /// <param name="procId">The id of the process.</param>
        /// <returns>The start time of the process.</returns>
        /// <exception cref="Win32Exception">A Win32Exception exception will be thrown if this specified process id is not found or if it is non-accessible due to its access control level.</exception>
        public static DateTime GetProcessStartTime(int procId)
        {
            SafeProcessHandle procHandle = null;

            try
            {
                procHandle = GetSafeProcessHandle(procId);

                if (procHandle.IsInvalid || procHandle.IsClosed)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (!GetProcessTimes(procHandle, out FILETIME ftCreation, out _, out _, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    ulong ufiletime = unchecked((((ulong)(uint)ftCreation.dwHighDateTime) << 32) | (uint)ftCreation.dwLowDateTime);
                    var startTime = DateTime.FromFileTimeUtc((long)ufiletime);
                    return startTime;
                }
                catch (ArgumentException)
                {

                }

                return DateTime.MinValue;
            }
            finally
            {
                procHandle?.Dispose();
                procHandle = null;
            }
        }

        /// <summary>
        /// Gets the exit time of the process with the specified identifier.
        /// </summary>
        /// <param name="procId">The id of the process.</param>
        /// <returns>The exit time of the process.</returns>
        /// <exception cref="Win32Exception">A Win32Exception exception will be thrown if this specified process id is not found or if it is non-accessible due to its access control level.</exception>
        public static DateTime GetProcessExitTime(int procId)
        {
            SafeProcessHandle procHandle = null;

            try
            {
                procHandle = GetSafeProcessHandle(procId);

                if (procHandle.IsInvalid || procHandle.IsClosed)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (!GetProcessTimes(procHandle, out FILETIME ftCreation, out FILETIME ftExit, out _, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    ulong uExitfiletime = unchecked((((ulong)(uint)ftExit.dwHighDateTime) << 32) | (uint)ftExit.dwLowDateTime);

                    // Has not exited.
                    if (uExitfiletime == 0)
                    {
                        return DateTime.MinValue;
                    }

                    return DateTime.FromFileTimeUtc((long)uExitfiletime);
                }
                catch (ArgumentException)
                {

                }

                return DateTime.MinValue;
            }
            finally
            {
                procHandle?.Dispose();
                procHandle = null;
            }
        }

        /// <summary>
        /// Returns a SafeProcessHandle for a process with the specificed process id.
        /// </summary>
        /// <param name="id">Process id.</param>
        /// <returns>SafeProcessHandle instance.</returns>
        public static SafeProcessHandle GetSafeProcessHandle(int id, ProcessAccessFlags flags = ProcessAccessFlags.All)
        {
            return OpenProcess((uint)flags, false, (uint)id);
        }

        internal static MEMORYSTATUSEX GetSystemMemoryInfo()
        {
            MEMORYSTATUSEX memory = new();

            if (!GlobalMemoryStatusEx(memory))
            {
                throw new Win32Exception($"NativeMethods.GetSystemMemoryInfo failed with Win32 error code {Marshal.GetLastWin32Error()}");
            }

            return memory;
        }

        /// <summary>
        /// Gets System performance information.
        /// </summary>
        /// <param name="pi">Instance of <see cref="PerformanceInformation"/> structure to populate</param>
        /// <returns>true if the function call was successful, false otherwise. Check <see cref="Marshal.GetLastWin32Error"/>
        /// for additional error information.</returns>
        internal static bool GetSytemPerformanceInfo(ref PerformanceInformation pi)
        {
            pi.cb = (uint)Marshal.SizeOf(typeof(PerformanceInformation));
            
            if (!GetPerformanceInfo(ref pi, pi.cb))
            {
                throw new Win32Exception($"GetPerformanceInfo failed with {Marshal.GetLastWin32Error()}");
            }

            return true;
        }

        /// <summary>
        /// Gets the child processes, if any, belonging to the process with supplied pid.
        /// </summary>
        /// <param name="parentPid">The process ID of parent process (SF service host process).</param>
        /// <param name="currentProcIds">An array containing all pids of processes running at the time it was created.</param>
        /// <returns>A List of tuple (string procName, int procId) representing each child process belonging to the specified parent process (with parentPid).</returns>
        internal static List<(string, uint)> PssGetServiceProcessDescendants(uint parentPid, ref uint[] currentProcIds)
        {
            if (currentProcIds == null || parentPid < 0)
            {
                return null;
            }

            List<(string, uint)> ret = new();

            for (int i = 0; i < currentProcIds.Length; ++i)
            {
                try
                {
                    var snapshot = PssGetProcessSnapshot(currentProcIds[i]);

                    if (string.IsNullOrWhiteSpace(snapshot.ImageFileName))
                    {
                        continue;
                    }

                    string procName = Path.GetFileName(snapshot.ImageFileName);

                    // We don't care about SF system service procs.
                    if (FindInStringArray(ignoreFabricSystemServicesList, procName))
                    {
                        continue;
                    }

                    // Rename as file extension is not expected by caller.
                    procName = procName.Replace(".exe", string.Empty);

                    // The process is a child of the supplied process with the given pid (parentPid). Add it to the list.
                    if (parentPid == snapshot.ParentProcessId)
                    {
                        ret.Add((procName, snapshot.ProcessId));
                    }
                }
                catch (Exception e) when (e is ArgumentException or Win32Exception)
                {

                }
            }

            return ret;
        }

        // Networking \\
        // Credit: http://pinvoke.net/default.aspx/iphlpapi/GetExtendedTcpTable.html

        /// <summary>
        /// Gets a list of TCPv4 connection info tuples for use in determining TCP ports in use per process or machine-wide.
        /// Note that this does not consider BOUND state connections. If you need that information, then use the netstat-based API in OSProviderInfo platform impls.
        /// </summary>
        /// <returns>List of (ushort LocalPort, int OwningProcessId, MIB_TCP_STATE State) tuples.</returns>
        public static List<(ushort LocalPort, int OwningProcessId, MIB_TCP_STATE State)> GetAllTcpConnections()
        {
            return InternalGetTcpConnections();
        }

        /// <summary>
        /// Gets a list of TCPv6 connection info tuples for use in determining TCP ports in use per process or machine-wide.
        /// Note that this does not consider BOUND state connections. If you need that information, then use the netstat-based API in OSProviderInfo platform impls.
        /// </summary>
        /// <returns>List of (ushort LocalPort, int OwningProcessId, MIB_TCP_STATE State) tuples.</returns>
        public static List<(ushort LocalPort, int OwningProcessId, MIB_TCP_STATE State)> GetAllTcp6Connections()
        {
            return InternalGetTcp6Connections();
        }

        // Process disk IO \\

        /// <summary>
        /// Gets the number of read operations performed by the process with specified process ID.
        /// </summary>
        /// <param name="processId">The ID of the target process.</param>
        /// <returns>The number of read operations performed by the process as a long value.</returns>
        /// <exception cref="Win32Exception"></exception>
        public static long GetProcessIOReadOperationCount(uint processId)
        {
            long reads = 0;

            using SafeProcessHandle safeProcessHandle = GetSafeProcessHandle((int)processId) ?? throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!GetProcessIoCounters(safeProcessHandle, out IO_COUNTERS info))
            {
                int ret = Marshal.GetLastWin32Error();

                if (ret != 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            else
            {
                reads = (long)info.ReadOperationCount;
            }

            return reads;
        }

        /// <summary>
        /// Gets the number of write operations performed by the process with specified process ID.
        /// </summary>
        /// <param name="processId">The ID of the target process.</param>
        /// <returns>The number of write operations performed by the process as a long value.</returns>
        /// <exception cref="Win32Exception"></exception>
        public static long GetProcessIOWriteOperationCount(uint processId)
        {
            long writes = 0;
            using SafeProcessHandle safeProcessHandle = GetSafeProcessHandle((int)processId) ?? throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!GetProcessIoCounters(safeProcessHandle, out IO_COUNTERS info))
            {
                int ret = Marshal.GetLastWin32Error();

                if (ret != 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            else
            {
                writes = (long)info.WriteOperationCount;
            }

            return writes;
        }

        /// <summary>
        /// Gets the number of bytes read by the target process with specified process ID.
        /// </summary>
        /// <param name="processId">The ID of the target process.</param>
        /// <returns>The number of bytes read as a long value.</returns>
        /// <exception cref="Win32Exception"></exception>
        public static long GetProcessIOReadTransferCount(uint processId)
        {
            long reads = 0;

            using SafeProcessHandle safeProcessHandle = GetSafeProcessHandle((int)processId) ?? throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!GetProcessIoCounters(safeProcessHandle, out IO_COUNTERS info))
            {
                int ret = Marshal.GetLastWin32Error();

                if (ret != 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            else
            {
                reads = (long)info.ReadTransferCount;
            }

            return reads;
        }

        /// <summary>
        /// Gets the number of bytes written by the target process with specified process ID.
        /// </summary>
        /// <param name="processId">The ID of the target process.</param>
        /// <returns>The number of bytes written as a long value.</returns>
        /// <exception cref="Win32Exception"></exception>
        public static long GetProcessIOWriteTransferCount(uint processId)
        {
            long writes = 0;
            using SafeProcessHandle safeProcessHandle = GetSafeProcessHandle((int)processId) ?? throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!GetProcessIoCounters(safeProcessHandle, out IO_COUNTERS info))
            {
                int ret = Marshal.GetLastWin32Error();

                if (ret != 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            else
            {
                writes = (long)info.WriteTransferCount;
            }

            return writes;
        }

        private static List<SYSTEM_PROCESS_INFORMATION> GetSFServiceProcessInfo(bool includeFabricSystemServices = false)
        {
            var procInfo = NtGetFilteredSystemProcessInfo(includeFabricSystemServices);
            List<SYSTEM_PROCESS_INFORMATION> ret = new();
            uint parentPid = 0;

            for (int i = 0; i < procInfo.Count; ++i)
            {
                if (Path.GetFileName(procInfo[i].ImageName.Buffer) == "Fabric.exe")
                {
                    parentPid = procInfo[i].Reserved2.ToUInt32();
                    break;
                }
            }

            if (parentPid == 0)
            {
                return ret;
            }

            for (int i = 0; i < procInfo.Count; ++i)
            {
                if (procInfo[i].Reserved2.ToUInt32() == parentPid)
                {
                    ret.Add(procInfo[i]);
                }
            }

            return ret;
        }

        /// <summary>
        /// Gets a filtered list of process information objeccts that will includes all, but not limited to, executing SF user services and their descendants.
        /// </summary>
        /// <returns>An array of uint values that are process identifiers of all currently running SF user service host processes (and descendants) on the system. 
        /// If the function fails, then it will return null.</returns>
        private static List<SYSTEM_PROCESS_INFORMATION> NtGetFilteredSystemProcessInfo(bool includeFabricSystemServices = false)
        {
            SYSTEM_PROCESS_INFORMATION[] procInfo = NtGetSysProcInfo();

            if (procInfo == null)
            {
                return null;
            }

            List<SYSTEM_PROCESS_INFORMATION> procInfoList = new();

            for (int i = 0; i < procInfo.Length; ++i)
            {
                try
                {
                    if (procInfo[i].UniqueProcessId == UIntPtr.Zero)
                    {
                        continue;
                    }

                    uint pid = procInfo[i].UniqueProcessId.ToUInt32();

                    using (SafeProcessHandle safeProcessHandle = GetSafeProcessHandle((int)pid))
                    {
                        // Failure in OpenProcess, ignore.
                        if (safeProcessHandle.IsInvalid)
                        {
                            continue;
                        }

                        // What happens if child is spawned with breakaway from Job Object? This is highly unlikely for SF user services..
                        // All SF user service host processes (and their descendants) are owned by JOs, therefore if the process is not part of a JO, then ignore it.
                        if (!IsProcessInJob(safeProcessHandle, IntPtr.Zero, out bool isInJob) || !isInJob)
                        {
                            continue;
                        }
                    }

                    string procName = Path.GetFileName(procInfo[i].ImageName.Buffer);

                    if (FindInStringArray(ignoreProcessList, procName))
                    {
                        continue;
                    }

                    if (!includeFabricSystemServices && FindInStringArray(ignoreFabricSystemServicesList, procName))
                    {
                        continue;
                    }

                    procInfoList.Add(procInfo[i]);
                }
                catch (ArgumentException)
                {

                }
            }

            return procInfoList;
        }

        private static List<(ushort LocalPort, int OwningProcessId, MIB_TCP_STATE State)> InternalGetTcpConnections()
        {
            MIB_TCPROW_OWNER_PID[] tableRows;
            uint buffSize = 0;
            var dwNumEntriesField = typeof(MIB_TCPTABLE_OWNER_PID).GetField("dwNumEntries");

            // Determine how much memory to allocate.
            _ = GetExtendedTcpTable(IntPtr.Zero, ref buffSize, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);
            IntPtr tcpTablePtr = Marshal.AllocHGlobal((int)buffSize);

            try
            {
                uint ret = GetExtendedTcpTable(tcpTablePtr, ref buffSize, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);

                if (ret != ERROR_SUCCESS)
                {
                    logger.LogWarning($"NativeMethods.InternalGetTcpConnections: Failed to get TCPv4 connections with Win32 error {Marshal.GetLastWin32Error()}");
                    return null;
                }

                MIB_TCPTABLE_OWNER_PID table = (MIB_TCPTABLE_OWNER_PID)Marshal.PtrToStructure(tcpTablePtr, typeof(MIB_TCPTABLE_OWNER_PID));
                int rowStructSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                uint numEntries = (uint)dwNumEntriesField.GetValue(table);
                tableRows = new MIB_TCPROW_OWNER_PID[numEntries];
                IntPtr rowPtr = (IntPtr)((long)tcpTablePtr + 4);

                for (int i = 0; i < numEntries; ++i)
                {
                    MIB_TCPROW_OWNER_PID tcpRow = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCPROW_OWNER_PID));
                    tableRows[i] = tcpRow;
                    rowPtr = (IntPtr)((long)rowPtr + rowStructSize); // next entry
                }

                if (tableRows != null)
                {
                    var values = new List<(ushort LocalPort, int OwningProcessId, MIB_TCP_STATE State)>();

                    foreach (var row in tableRows)
                    {
                        values.Add((row.LocalPort, (int)row.owningPid, row.State));
                    }

                    return values;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }

            return null;
        }

        private static List<(ushort LocalPort, int OwningProcessId, MIB_TCP_STATE State)> InternalGetTcp6Connections()
        {
            MIB_TCP6ROW_OWNER_PID[] tableRows;
            uint buffSize = 0;
            var dwNumEntriesField = typeof(MIB_TCP6TABLE_OWNER_PID).GetField("dwNumEntries");

            // Determine how much memory to allocate.
            _ = GetExtendedTcpTable(IntPtr.Zero, ref buffSize, true, AF_INET6, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);
            IntPtr tcpTablePtr = Marshal.AllocHGlobal((int)buffSize);

            try
            {
                uint ret = GetExtendedTcpTable(tcpTablePtr, ref buffSize, true, AF_INET6, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);

                if (ret != ERROR_SUCCESS)
                {
                    logger.LogWarning($"NativeMethods.InternalGetTcp6Connections: Failed to get TCPv6 connections with Win32 error {Marshal.GetLastWin32Error()}");
                    return null;
                }

                MIB_TCP6TABLE_OWNER_PID table = (MIB_TCP6TABLE_OWNER_PID)Marshal.PtrToStructure(tcpTablePtr, typeof(MIB_TCP6TABLE_OWNER_PID));
                int rowStructSize = Marshal.SizeOf(typeof(MIB_TCP6ROW_OWNER_PID));
                uint numEntries = (uint)dwNumEntriesField.GetValue(table);
                tableRows = new MIB_TCP6ROW_OWNER_PID[numEntries];
                IntPtr rowPtr = (IntPtr)((long)tcpTablePtr + 4);

                for (int i = 0; i < numEntries; ++i)
                {
                    MIB_TCP6ROW_OWNER_PID tcpRow = (MIB_TCP6ROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCP6ROW_OWNER_PID));
                    tableRows[i] = tcpRow;
                    rowPtr = (IntPtr)((long)rowPtr + rowStructSize); // next entry
                }

                if (tableRows != null)
                {
                    var values = new List<(ushort LocalPort, int OwningProcessId, MIB_TCP_STATE State)>();

                    foreach (var row in tableRows)
                    {
                        values.Add((row.LocalPort, (int)row.owningPid, row.State));
                    }

                    return values;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }

            return null;
        }

        private static SYSTEM_PROCESS_INFORMATION[] NtGetSysProcInfo()
        {
            const int MAX_TRIES = 5;
            ArrayList arrProcInfo = new();
            uint size = 1024;
            int tried = 0;
            IntPtr procInfoBuffer = IntPtr.Zero;

            try
            {
                procInfoBuffer = Marshal.AllocHGlobal(1024);

                // Figure out the actual memory size we need. So, set actualSize given current related system state (number of processes currently executing).
                int status = NtQuerySystemInformation(SystemProcessInformation, procInfoBuffer, size, out uint actualSize);

                while (status != 0 && tried <= MAX_TRIES)
                {
                    Marshal.FreeHGlobal(procInfoBuffer);

                    // Pad the buffer to have a better chance of getting the right size after 1 iteration.
                    size += actualSize;
                    procInfoBuffer = Marshal.AllocHGlobal((int)size);
                    status = NtQuerySystemInformation(SystemProcessInformation, procInfoBuffer, size, out actualSize);
                    tried++;
                }

                if (tried == MAX_TRIES && status != 0)
                {
                    return null;
                }

                long offset = 0;
                SYSTEM_PROCESS_INFORMATION sysProcInfo = (SYSTEM_PROCESS_INFORMATION)Marshal.PtrToStructure(procInfoBuffer, typeof(SYSTEM_PROCESS_INFORMATION));
                arrProcInfo.Add(sysProcInfo);

                while (sysProcInfo.NextEntryOffset > 0)
                {
                    offset += sysProcInfo.NextEntryOffset;
                    IntPtr tempPtr = new(procInfoBuffer.ToInt64() + offset);
                    sysProcInfo = (SYSTEM_PROCESS_INFORMATION)Marshal.PtrToStructure(tempPtr, typeof(SYSTEM_PROCESS_INFORMATION));
                    arrProcInfo.Add(sysProcInfo);
                }

                var arr = new SYSTEM_PROCESS_INFORMATION[arrProcInfo.Count];
                arrProcInfo.CopyTo(arr);
                return arr;
            }
            catch (OutOfMemoryException)
            {
                // Immediately die.
                Environment.FailFast($"OOM. Taking down FO:{Environment.NewLine}{Environment.StackTrace}");
            }
            catch (Exception e) when (e is ArgumentException or MissingMethodException or Win32Exception)
            {
                
            }
            finally
            {
                if (procInfoBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(procInfoBuffer);
                }
            }

            return null;
        }

        private static string NtGetProcessNameFromIdInternal(uint pid)
        {
            try 
            {
                var procInfo = NtGetSysProcInfo();

                for (int i = 0; i < procInfo.Length; ++i) 
                { 
                    if (procInfo[i].UniqueProcessId.ToUInt32() == pid)
                    {
                        return Path.GetFileNameWithoutExtension(procInfo[i].ImageName.Buffer);
                    }
                }
            }
            catch (ArgumentException)
            {

            }

            return null;
        }

        // Credit (MIT): https://github.com/dahall/Vanara/blob/master/PInvoke/Kernel32/ProcessSnapshot.cs, author: https://github.com/dahall
        private static IEnumerable<T> PssWalkSnapshot<T>(IntPtr SnapshotHandle, PSS_WALK_INFORMATION_CLASS InformationClass) where T : struct
        {
            IntPtr hWalk = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;

            try
            {
                int ret = PssWalkMarkerCreate(IntPtr.Zero, ref hWalk);

                if (ret != ERROR_SUCCESS)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                int size = Marshal.SizeOf(typeof(T));
                buffer = Marshal.AllocHGlobal(size);

                do
                {
                    var err = PssWalkSnapshot(SnapshotHandle, InformationClass, hWalk, buffer, (uint)size);

                    if (err == ERROR_NO_MORE_ITEMS)
                    {
                        break;
                    }
                    else if (err == ERROR_SUCCESS)
                    {
                        yield return Marshal.PtrToStructure<T>(buffer);
                    }
                    else
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                } while (true);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (hWalk != IntPtr.Zero)
                {
                    if (PssWalkMarkerFree(hWalk) != ERROR_SUCCESS)
                    {
                        logger.LogWarning($"Failed to free walk marker with Win32 error {Marshal.GetLastWin32Error()}");
                    }
                }
            }
        }

        private static bool FindInStringArray(string[] arr, string s)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                // 0 means the two strings are equal.
                if (string.CompareOrdinal(s, arr[i]) != 0)
                { 
                    continue;
                }

                return true;
            }

            return false;
        }
        
        // Cleanup \\

        private static bool TryReleaseHandle(IntPtr handle)
        {
            try
            {
                if (handle != IntPtr.Zero)
                {
                    // Rare.
                    if (!CloseHandle(handle))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    handle = IntPtr.Zero;
                    return true;
                }
            }
            catch(SEHException seh)
            {
                logger.LogWarning($"ReleaseHandle: Failed to release handle with SEH exception {seh.ErrorCode}: {seh.Message}");
            }
            catch (Win32Exception win32)
            {
                logger.LogWarning($"ReleaseHandle: Failed to release handle with Win32 error {win32.NativeErrorCode}: {win32.Message}");
            }
            
            return false;
        }

        // Safe object handle.
        public sealed class SafeObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeObjectHandle() : base(ownsHandle: true)
            {

            }

            protected override bool ReleaseHandle()
            {
                return TryReleaseHandle(handle);
            }
        }
    }
}