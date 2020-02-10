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
        // Ok
        public const string Ok = "FO000";

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
        public const string NodeErrorDiskAverageQueueLength = "FO011";
        public const string NodeWarningDiskAverageQueueLength = "FO012";

        // Memory
        public const string AppErrorMemoryPercentUsed = "FO013";
        public const string AppWarningMemoryPercentUsed = "FO014";
        public const string AppErrorMemoryCommittedMB = "FO015";
        public const string AppWarningMemoryCommittedMB = "FO016";
        public const string NodeErrorMemoryPercentUsed = "FO017";
        public const string NodeWarningMemoryPercentUsed = "FO018";
        public const string NodeErrorMemoryCommittedMB = "FO019";
        public const string NodeWarningMemoryCommittedMB = "FO020";

        // Networking
        public const string AppErrorNetworkEndpointUnreachable = "FO021";
        public const string AppWarningNetworkEndpointUnreachable = "FO022";
        public const string AppErrorTooManyActiveTcpPorts = "FO023";
        public const string AppWarningTooManyActiveTcpPorts = "FO024";
        public const string NodeErrorTooManyActiveTcpPorts = "FO025";
        public const string NodeWarningTooManyActiveTcpPorts = "FO026";
        public const string ErrorTooManyFirewallRules = "FO027";
        public const string WarningTooManyFirewallRules = "FO028";
        public const string AppErrorTooManyActiveEphemeralPorts = "FO029";
        public const string AppWarningTooManyActiveEphemeralPorts = "FO030";
        public const string NodeErrorTooManyActiveEphemeralPorts = "FO031";
        public const string NodeWarningTooManyActiveEphemeralPorts = "FO032";

        public static Dictionary<string, string> AppErrorCodesDictionary { get; private set; } = new Dictionary<string, string>
        {
            { Ok, "Ok" },
            { AppErrorCpuTime, "AppErrorCpuTime" },
            { AppWarningCpuTime, "AppWarningCpuTime" },
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
            { NodeErrorDiskAverageQueueLength, "NodeErrorDiskAverageQueueLength" },
            { NodeWarningDiskAverageQueueLength, "NodeWarningDiskAverageQueueLength" },
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
