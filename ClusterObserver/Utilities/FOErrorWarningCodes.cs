// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;

namespace FabricClusterObserver.Utilities
{
    // FabricObserver Error/Warning/Ok Codes.
    public sealed class FOErrorWarningCodes
    {
        // CPU
        public const string AppErrorCpuTime = "FO001";
        public const string AppWarningCpuTime = "FO002";
        public const string NodeErrorCpuTime = "FO003";
        public const string NodeWarningCpuTime = "FO004";

        // Certificate
        public const string ErrorCertificateExpiration = "FO005";
        public const string WarningCertificateExpiration = "FO006";

        // Disk
        public const string NodeErrorDiskSpacePercentUsed = "FO007";
        public const string NodeErrorDiskSpaceMB = "FO008";
        public const string NodeWarningDiskSpacePercentUsed = "FO009";
        public const string NodeWarningDiskSpaceMB = "FO010";
        public const string AppErrorDiskIoReads = "FO011";
        public const string AppWarningDiskIoReads = "FO012";
        public const string AppErrorDiskIoWrites = "FO013";
        public const string AppWarningDiskIoWrites = "FO014";
        public const string NodeErrorDiskIoReads = "FO015";
        public const string NodeWarningDiskIoReads = "FO016";
        public const string NodeErrorDiskIoWrites = "FO017";
        public const string NodeWarningDiskIoWrites = "FO018";
        public const string NodeErrorDiskAverageQueueLength = "FO019";
        public const string NodeWarningDiskAverageQueueLength = "FO020";

        // Memory
        public const string AppErrorMemoryCommitted = "FO021";
        public const string AppWarningMemoryCommitted = "FO022";
        public const string AppErrorMemoryPercentUsed = "FO023";
        public const string AppWarningMemoryPercentUsed = "FO024";
        public const string AppErrorMemoryCommittedMB = "FO025";
        public const string AppWarningMemoryCommittedMB = "FO026";
        public const string NodeErrorMemoryCommitted = "FO027";
        public const string NodeWarningMemoryCommitted = "FO028";
        public const string NodeErrorMemoryPercentUsed = "FO029";
        public const string NodeWarningMemoryPercentUsed = "FO030";
        public const string NodeErrorMemoryCommittedMB = "FO031";
        public const string NodeWarningMemoryCommittedMB = "FO032";

        // Networking
        public const string AppErrorNetworkEndpointUnreachable = "FO033";
        public const string AppWarningNetworkEndpointUnreachable = "FO034";
        public const string AppErrorTooManyActiveTcpPorts = "FO035";
        public const string AppWarningTooManyActiveTcpPorts = "FO036";
        public const string NodeErrorTooManyActiveTcpPorts = "FO037";
        public const string NodeWarningTooManyActiveTcpPorts = "FO038";
        public const string ErrorTooManyFirewallRules = "FO039";
        public const string WarningTooManyFirewallRules = "FO040";
        public const string AppErrorTooManyActiveEphemeralPorts = "FO041";
        public const string AppWarningTooManyActiveEphemeralPorts = "FO042";
        public const string NodeErrorTooManyActiveEphemeralPorts = "FO043";
        public const string NodeWarningTooManyActiveEphemeralPorts = "FO044";

        // Ok
        public const string Ok = "FO000";

        public static Dictionary<string, string> AppErrorCodesDictionary { get; private set; } = new Dictionary<string, string>
        {
            { Ok, "Ok" },
            { AppErrorCpuTime, "AppErrorCpuTime" },
            { AppWarningCpuTime, "AppWarningCpuTime" },
            { AppErrorDiskIoReads, "AppErrorDiskIoReads" },
            { AppWarningDiskIoReads, "AppWarningDiskIoReads" },
            { AppErrorDiskIoWrites, "AppErrorDiskIoWrites" },
            { AppWarningDiskIoWrites, "AppWarningDiskIoWrites" },
            { AppErrorMemoryCommitted, "AppErrorMemoryCommitted" },
            { AppWarningMemoryCommitted, "AppWarningMemoryCommitted" },
            { AppErrorMemoryPercentUsed, "AppErrorMemoryPercentUsed" },
            { AppWarningMemoryPercentUsed, "AppWarningMemoryPercentUsed" },
            { AppErrorMemoryCommittedMB, "AppErrorMemoryCommittedMB" },
            { AppWarningMemoryCommittedMB, "AppWarningMemoryCommittedMB" },
            { AppErrorNetworkEndpointUnreachable, "AppErrorNetworkEndpointUnreachable" },
            { AppWarningNetworkEndpointUnreachable, "AppWarningNetworkEndpointUnreachable" },
            { AppErrorTooManyActiveTcpPorts, "AppErrorTooManyActiveTcpPorts" },
            { AppWarningTooManyActiveTcpPorts, "AppWarningTooManyActiveTcpPorts" },
            { AppErrorTooManyActiveEphemeralPorts, "AppErrorTooManyActiveEphemeralPorts" },
            { AppWarningTooManyActiveEphemeralPorts, "AppWarningTooManyActiveEphemeralPorts" },
        };

        public static Dictionary<string, string> NodeErrorCodesDictionary { get; private set; } = new Dictionary<string, string>
        {
            { Ok, "Ok" },
            { NodeErrorCpuTime, "NodeErrorCpuTime" },
            { NodeWarningCpuTime, "NodeWarningCpuTime" },
            { ErrorCertificateExpiration, "ErrorCertificateExpiration" },
            { WarningCertificateExpiration, "WarningCertificateExpiration" },
            { NodeErrorDiskSpacePercentUsed, "NodeErrorDiskSpacePercentUsed" },
            { NodeErrorDiskSpaceMB, "NodeErrorDiskSpaceMB" },
            { NodeWarningDiskSpacePercentUsed, "NodeWarningDiskSpacePercentUsed" },
            { NodeWarningDiskSpaceMB, "NodeWarningDiskSpaceMB" },
            { NodeErrorDiskIoReads, "NodeErrorDiskIoReads" },
            { NodeWarningDiskIoReads, "NodeWarningDiskIoReads" },
            { NodeErrorDiskIoWrites, "NodeErrorDiskIoWrites" },
            { NodeWarningDiskIoWrites, "NodeWarningDiskIoWrites" },
            { NodeErrorDiskAverageQueueLength, "NodeErrorDiskAverageQueueLength" },
            { NodeWarningDiskAverageQueueLength, "NodeWarningDiskAverageQueueLength" },
            { NodeErrorMemoryCommitted, "NodeErrorMemoryCommitted" },
            { NodeWarningMemoryCommitted, "NodeWarningMemoryCommitted" },
            { NodeErrorMemoryPercentUsed, "NodeErrorMemoryPercentUsed" },
            { NodeWarningMemoryPercentUsed, "NodeWarningMemoryPercentUsed" },
            { NodeErrorMemoryCommittedMB, "NodeErrorMemoryCommittedMB" },
            { NodeWarningMemoryCommittedMB, "NodeWarningMemoryCommittedMB" },
            { NodeErrorTooManyActiveTcpPorts, "NodeErrorTooManyActiveTcpPorts" },
            { NodeWarningTooManyActiveTcpPorts, "NodeWarningTooManyActiveTcpPorts" },
            { ErrorTooManyFirewallRules, "NodeErrorTooManyFirewallRules" },
            { WarningTooManyFirewallRules, "NodeWarningTooManyFirewallRules" },
            { NodeErrorTooManyActiveEphemeralPorts, "NodeErrorTooManyActiveEphemeralPorts" },
            { NodeWarningTooManyActiveEphemeralPorts, "NodeWarningTooManyActiveEphemeralPorts" },
        };
    }
}
