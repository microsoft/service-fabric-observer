// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Utilities
{
    public sealed class ErrorWarningProperty
    {
        // CPU/Memory
        public const string TotalCpuTime = "Total CPU Time";
        public const string TotalMemoryConsumptionMB = "Memory Consumption MB";
        public const string TotalMemoryConsumptionPct = "Memory Consumption %";

        // Disk...
        public const string DiskAverageQueueLength = "Average Disk Queue Length";
        public const string DiskSpaceUsagePercentage = "Disk Space Consumption %";
        public const string DiskSpaceUsageMB = "Disk Space Consumption MB";
        public const string DiskSpaceAvailableMB = "Disk Space Available MB";
        public const string DiskSpaceTotalMB = "Disk Space Total MB";

        // Network
        public const string InternetConnectionFailure = "Outbound Internet Connection Failure";
        public const string TotalActiveFirewallRules = "Total Active Firewall Rules";
        public const string TotalActivePorts = "Total Active Ports";
        public const string TotalEphemeralPorts = "Total Ephemeral Active Ports";
    }
}
