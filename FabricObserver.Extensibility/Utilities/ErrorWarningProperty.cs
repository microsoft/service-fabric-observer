// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Supported resource usage metric names that FabricObserver uses in Service Fabric Health Reports and Telemetry/ETW events.
    /// </summary>
    public sealed class ErrorWarningProperty
    {
        /// <summary>
        /// CPU Time (Percent)
        /// </summary>
        public const string CpuTime = "CPU Time (Percent)";
        
        /// <summary>
        /// Memory Usage (MB)
        /// </summary>
        public const string MemoryConsumptionMb = "Memory Usage (MB)";
        
        /// <summary>
        /// Memory Usage (Percent)
        /// </summary>
        public const string MemoryConsumptionPercentage = "Memory Usage (Percent)";

        /// <summary>
        /// Certificate Expiration
        /// </summary>
        public const string CertificateExpiration = "Certificate Expiration";

        /// <summary>
        /// Average Disk Queue Length
        /// </summary>
        public const string DiskAverageQueueLength = "Average Disk Queue Length";
        /// <summary>
        /// Disk Space Usage (Percent)
        /// </summary>
        public const string DiskSpaceUsagePercentage = "Disk Space Usage (Percent)";
        /// <summary>
        /// Disk Space Usage (MB)
        /// </summary>
        public const string DiskSpaceUsageMb = "Disk Space Usage (MB)";
        /// <summary>
        /// Disk Space Available (MB)
        /// </summary>
        public const string DiskSpaceAvailableMb = "Disk Space Available (MB)";
        /// <summary>
        /// Disk Space Total (MB)
        /// </summary>
        public const string DiskSpaceTotalMb = "Disk Space Total (MB)";
        /// <summary>
        /// Folder Size (MB)
        /// </summary>
        public const string FolderSizeMB = "Folder Size (MB)";

        /// <summary>
        /// Outbound Internet Connection Failur
        /// </summary>
        public const string InternetConnectionFailure = "Outbound Internet Connection Failure";
        /// <summary>
        /// Active Firewall Rules
        /// </summary>
        public const string ActiveFirewallRules = "Active Firewall Rules";
        /// <summary>
        /// Active TCP Ports
        /// </summary>
        public const string ActiveTcpPorts = "Active TCP Ports";
        /// <summary>
        /// Active Ephemeral Ports
        /// </summary>
        public const string ActiveEphemeralPorts = "Active Ephemeral Ports";
        /// <summary>
        /// Active Ephemeral Ports (Percent)
        /// </summary>
        public const string ActiveEphemeralPortsPercentage = "Active Ephemeral Ports (Percent)";
        /// <summary>
        /// Total Ephemeral Ports. This is used by NodeObserver only.
        /// </summary>
        public const string TotalEphemeralPorts = "Total Ephemeral Ports";

        /// <summary>
        /// Allocated File Handle
        /// </summary>
        public const string AllocatedFileHandles = "Allocated File Handles";
        /// <summary>
        /// Allocated File Handles (Percent)
        /// </summary>
        public const string AllocatedFileHandlesPct = "Allocated File Handles (Percent)";

        /// <summary>
        /// Thread Count
        /// </summary>
        public const string ThreadCount = "Thread Count";

        /// <summary>
        /// Child Process Count
        /// </summary>
        public const string ChildProcessCount = "Child Process Count";

        /// <summary>
        /// LVID Usage (Percent)
        /// </summary>
        public const string KvsLvidsPercent = "LVID Usage (Percent)";

        /// <summary>
        /// Process Private Bytes (MB)
        /// </summary>
        public const string PrivateBytesMb = "Private Bytes (MB)";

        /// <summary>
        /// Process Private Bytes (MB)
        /// </summary>
        public const string PrivateBytesPercent = "Private Bytes (Percent)";

        /// <summary>
        /// Process RG Memory (Percent)
        /// </summary>
        public const string RGMemoryUsagePercent = "RG Memory Usage (Percent)";
    }
}
