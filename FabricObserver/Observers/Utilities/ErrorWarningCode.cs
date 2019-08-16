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
        public const string ErrorDiskAverageQueueLength = "FO021";
        public const string WarningDiskAverageQueueLength = "FO022";

        // Memory
        public const string ErrorMemoryCommitted = "FO009";
        public const string WarningMemoryCommitted = "FO0010";

        // Networking
        public const string ErrorNetworkEndpointUnreachable = "FO0011";
        public const string WarningNetworkEndpointUnreachable = "FO0012";
        public const string ErrorTooManyActivePorts = "FO0013";
        public const string WarningTooManyActivePorts = "FO0014";
        public const string ErrorTooManyFirewallRules = "FO0015";
        public const string WarningTooManyFirewallRules = "FO0016";
        public const string ErrorNetworkBytesSent = "FO0017";
        public const string WarningNetworkBytesSent = "FO0018";
        public const string ErrorNetworkBytesReceived = "FO0019";
        public const string WarningNetworkBytesReceived = "FO0020";

        // Unknown
        public const string Unknown = "FO00";
    }
}
