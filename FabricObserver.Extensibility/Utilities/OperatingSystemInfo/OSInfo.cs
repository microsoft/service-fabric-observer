// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    public struct OSInfo
    {
        // General Info
        public string Name;
        public string Version;
        public string Status;
        public string Language;
        public int NumberOfProcesses;
        public string InstallDate;
        public string LastBootUpTime;

        /* Mem */

        // Note: AvailableMemoryKB is only useful for Linux (it is not set for Windows..). For Windows, FreePhysicalMemory suffices.
        // For Linux, with swap, AvailableMemoryKB is the droid we're looking for.
        public ulong AvailableMemoryKB;
        public ulong FreePhysicalMemoryKB;
        public ulong FreeVirtualMemoryKB;
        public ulong TotalVirtualMemorySizeKB;
        public ulong TotalVisibleMemorySizeKB;
    }
}
