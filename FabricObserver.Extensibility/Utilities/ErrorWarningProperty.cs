// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    public sealed class ErrorWarningProperty
    {
        // CPU/Memory
        public const string CpuTime = "CPU Time (Percent)";
        public const string MemoryConsumptionMb = "Memory Usage (MB)";
        public const string MemoryConsumptionPercentage = "Memory Usage (Percent)";

        // Certificates
        public const string CertificateExpiration = "Certificate Expiration";

        // Disk.
        public const string DiskAverageQueueLength = "Average Disk Queue Length";
        public const string DiskSpaceUsagePercentage = "Disk Space Usage (Percent)";
        public const string DiskSpaceUsageMb = "Disk Space Usage (MB)";
        public const string DiskSpaceAvailableMb = "Disk Space Available (MB)";
        public const string DiskSpaceTotalMb = "Disk Space Total (MB)";
        public const string FolderSizeMB = "Folder Size (MB)";

        // Network
        public const string InternetConnectionFailure = "Outbound Internet Connection Failure";
        public const string ActiveFirewallRules = "Active Firewall Rules";
        public const string ActiveTcpPorts = "Active TCP Ports";
        public const string ActiveEphemeralPorts = "Active Ephemeral Ports";
        public const string ActiveEphemeralPortsPercentage = "Active Ephemeral Ports (Percent)";
        public const string TotalEphemeralPorts = "Total Ephemeral Ports";

        // File Handles
        public const string AllocatedFileHandles = "Allocated File Handles";
        public const string AllocatedFileHandlesPct = "Allocated File Handles (Percent)";

        // Threads
        public const string ThreadCount = "Thread Count";

        // Child procs
        public const string ChildProcessCount = "Child Process Count";

        // KVS LVIDs
        public const string KvsLvidsPercent = "LVID Usage (Percent)";
    }
}
