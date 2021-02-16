// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    // FabricObserver Error/Warning/Ok Codes.
    public sealed class FOErrorWarningCodes
    {
        // Ok
        public const string Ok = "FO000";

        // CPU
        public const string AppErrorCpuPercent = "FO001";
        public const string AppWarningCpuPercent = "FO002";
        public const string NodeErrorCpuPercent = "FO003";
        public const string NodeWarningCpuPercent = "FO004";

        // Certificate
        public const string ErrorCertificateExpiration = "FO005";
        public const string WarningCertificateExpiration = "FO006";

        // Disk
        public const string NodeErrorDiskSpacePercent = "FO007";
        public const string NodeErrorDiskSpaceMB = "FO008";
        public const string NodeWarningDiskSpacePercent = "FO009";
        public const string NodeWarningDiskSpaceMB = "FO010";
        public const string NodeErrorDiskAverageQueueLength = "FO011";
        public const string NodeWarningDiskAverageQueueLength = "FO012";

        // Memory
        public const string AppErrorMemoryPercent = "FO013";
        public const string AppWarningMemoryPercent = "FO014";
        public const string AppErrorMemoryMB = "FO015";
        public const string AppWarningMemoryMB = "FO016"; 
        public const string NodeErrorMemoryPercent = "FO017";
        public const string NodeWarningMemoryPercent = "FO018";
        public const string NodeErrorMemoryMB = "FO019";
        public const string NodeWarningMemoryMB = "FO020";

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

        // Process owned File Handles / File Descriptors - Linux (File Descriptors) and Windows (File Handles)
        public const string AppErrorTooManyOpenFileHandles = "FO033";
        public const string AppWarningTooManyOpenFileHandles = "FO034";

        // System-wide open File Handles / File Descriptors - Linux only.
        public const string NodeErrorTotalOpenFileHandlesPercent = "FO035";
        public const string NodeWarningTotalOpenFileHandlesPercent = "FO036";

        public static Dictionary<string, string> AppErrorCodesDictionary
        {
            get;
        } = new Dictionary<string, string>
        {
            { Ok, "Ok" },
            { AppErrorCpuPercent, "AppErrorCpuPercent" },
            { AppWarningCpuPercent, "AppWarningCpuPercent" },
            { AppErrorMemoryPercent, "AppErrorMemoryPercent" },
            { AppWarningMemoryPercent, "AppWarningMemoryPercent" },
            { AppErrorMemoryMB, "AppErrorMemoryMB" },
            { AppWarningMemoryMB, "AppWarningMemoryMB" },
            { AppErrorNetworkEndpointUnreachable, "AppErrorNetworkEndpointUnreachable" },
            { AppWarningNetworkEndpointUnreachable, "AppWarningNetworkEndpointUnreachable" },
            { AppErrorTooManyActiveTcpPorts, "AppErrorTooManyActiveTcpPorts" },
            { AppWarningTooManyActiveTcpPorts, "AppWarningTooManyActiveTcpPorts" },
            { AppErrorTooManyActiveEphemeralPorts, "AppErrorTooManyActiveEphemeralPorts" },
            { AppWarningTooManyActiveEphemeralPorts, "AppWarningTooManyActiveEphemeralPorts" },
            { AppErrorTooManyOpenFileHandles, "AppErrorTooManyOpenFileHandles" },
            { AppWarningTooManyOpenFileHandles, "AppWarningTooManyOpenFileHandles" },
        };

        public static Dictionary<string, string> NodeErrorCodesDictionary
        {
            get;
        } = new Dictionary<string, string>
        {
            { Ok, "Ok" },
            { NodeErrorCpuPercent, "NodeErrorCpuPercent" },
            { NodeWarningCpuPercent, "NodeWarningCpuPercent" },
            { ErrorCertificateExpiration, "ErrorCertificateExpiration" },
            { WarningCertificateExpiration, "WarningCertificateExpiration" },
            { NodeErrorDiskSpacePercent, "NodeErrorDiskSpacePercent" },
            { NodeErrorDiskSpaceMB, "NodeErrorDiskSpaceMB" },
            { NodeWarningDiskSpacePercent, "NodeWarningDiskSpacePercent" },
            { NodeWarningDiskSpaceMB, "NodeWarningDiskSpaceMB" },
            { NodeErrorDiskAverageQueueLength, "NodeErrorDiskAverageQueueLength" },
            { NodeWarningDiskAverageQueueLength, "NodeWarningDiskAverageQueueLength" },
            { NodeErrorMemoryPercent, "NodeErrorMemoryPercent" },
            { NodeWarningMemoryPercent, "NodeWarningMemoryPercent" },
            { NodeErrorMemoryMB, "NodeErrorMemoryMB" },
            { NodeWarningMemoryMB, "NodeWarningMemoryMB" },
            { NodeErrorTooManyActiveTcpPorts, "NodeErrorTooManyActiveTcpPorts" },
            { NodeWarningTooManyActiveTcpPorts, "NodeWarningTooManyActiveTcpPorts" },
            { ErrorTooManyFirewallRules, "NodeErrorTooManyFirewallRules" },
            { WarningTooManyFirewallRules, "NodeWarningTooManyFirewallRules" },
            { NodeErrorTooManyActiveEphemeralPorts, "NodeErrorTooManyActiveEphemeralPorts" },
            { NodeWarningTooManyActiveEphemeralPorts, "NodeWarningTooManyActiveEphemeralPorts" },
            { NodeErrorTotalOpenFileHandlesPercent, "NodeErrorTotalOpenFileHandlesPercent" },
            { NodeWarningTotalOpenFileHandlesPercent, "NodeWarningTotalOpenFileHandlesPercent" },
        };

        public static string GetErrorWarningNameFromFOCode(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            if (AppErrorCodesDictionary.Any(k => k.Key == id))
            {
                return AppErrorCodesDictionary.First(k => k.Key == id).Value;
            }

            if (NodeErrorCodesDictionary.Any(k => k.Key == id))
            {
                return NodeErrorCodesDictionary.First(k => k.Key == id).Value;
            }

            return null;
        }
    }
}
