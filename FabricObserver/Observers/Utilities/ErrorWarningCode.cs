// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Utilities
{
    public sealed class ErrorWarningCode
    {
        // CPU...
        public const string ErrorCpuTime = "FO001";
        public const string WarningCpuTime = "FO002";

        // Disk
        public const string ErrorDiskSpace = "FO003";
        public const string WarningDiskSpace = "FO004";
        public const string ErrorDiskIoReads = "FO005";
        public const string WarningDiskIoReads = "FO006";
        public const string ErrorDiskIoWrites = "FO007";
        public const string WarningDiskIoWrites = "FO008";
        public const string ErrorDiskAverageQueueLength = "FO009";
        public const string WarningDiskAverageQueueLength = "FO010";

        // Memory
        public const string ErrorMemoryCommitted = "FO011";
        public const string WarningMemoryCommitted = "FO012";
        public const string ErrorMemoryPercentUsed = "FO013";
        public const string WarningMemoryPercentUsed = "FO014";

        // Networking
        public const string ErrorNetworkEndpointUnreachable = "FO015";
        public const string WarningNetworkEndpointUnreachable = "FO016";
        public const string ErrorTooManyActivePorts = "FO017";
        public const string WarningTooManyActivePorts = "FO018";
        public const string ErrorTooManyFirewallRules = "FO019";
        public const string WarningTooManyFirewallRules = "FO020";

        // Unknown
        public const string Unknown = "FO000";
    }
}
