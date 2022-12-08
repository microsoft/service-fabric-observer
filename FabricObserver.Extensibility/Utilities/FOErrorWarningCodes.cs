// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Error and Warning Codes related to machine resource usage metrics at the machine and service levels. 
    /// FabricHealer understands these codes.
    /// </summary>
    public sealed class FOErrorWarningCodes
    {
        /// <summary>
        /// Ok HealthState
        /// </summary>
        public const string Ok = "FO000";
        /// <summary>
        /// FO001   Percentage of total CPU usage has exceeded configured Error threshold for an app service process.
        /// </summary>
        public const string AppErrorCpuPercent = "FO001";
        /// <summary>
        /// FO002	Percentage of total CPU usage has exceeded configured Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningCpuPercent = "FO002";
        /// <summary>
        /// FO003	Percentage of total CPU usage has exceeded configured Error threshold on a machine.
        /// </summary>
        public const string NodeErrorCpuPercent = "FO003";
        /// <summary>
        /// FO004	Percentage of total CPU usage has exceeded configured Warning threshold on a machine.
        /// </summary>
        public const string NodeWarningCpuPercent = "FO004";
        /// <summary>
        /// FO005	Error: Certificate expiration has occured.
        /// </summary>
        public const string ErrorCertificateExpiration = "FO005";
        /// <summary>
        /// FO006	Warning: Certificate expiration is imminent.
        /// </summary>
        public const string WarningCertificateExpiration = "FO006";
        /// <summary>
        /// FO007	Disk usage percentage has exceeded configured Error threshold on a machine.
        /// </summary>
        public const string NodeErrorDiskSpacePercent = "FO007";
        /// <summary>
        /// FO008	Disk usage space (MB) has exceeded configured Error threshold on a machine.
        /// </summary>
        public const string NodeErrorDiskSpaceMB = "FO008";
        /// <summary>
        /// FO009	Disk usage percentage has exceeded configured Warning threshold on a machine.
        /// </summary>
        public const string NodeWarningDiskSpacePercent = "FO009";
        /// <summary>
        /// FO010	Disk usage space (MB) has exceeded configured Warning threshold on a machine.
        /// </summary>
        public const string NodeWarningDiskSpaceMB = "FO010";
        /// <summary>
        /// FO011	Avergage disk queue length has exceeded configured Error threshold.
        /// </summary>
        public const string NodeErrorDiskAverageQueueLength = "FO011";
        /// <summary>
        /// FO012	Average disk queue length has exceeded configured Warning threshold.
        /// </summary>
        public const string NodeWarningDiskAverageQueueLength = "FO012";
        /// <summary>
        /// FO013	Percentage of total physical memory usage has exceeded configured Error threshold for an app service process.
        /// </summary>
        public const string AppErrorMemoryPercent = "FO013";
        /// <summary>
        /// FO014	Percentage of total physical memory usage has exceeded configured Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningMemoryPercent = "FO014";
        /// <summary>
        /// FO015	Committed memory (MB) has exceeded configured Error threshold for an app service process.
        /// </summary>
        public const string AppErrorMemoryMB = "FO015";
        /// <summary>
        /// FO016	Committed memory (MB) has exceeded configured Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningMemoryMB = "FO016";
        /// <summary>
        /// FO017	Percentage of total physical memory usage has exceeded configured Error threshold on a machine.
        /// </summary>
        public const string NodeErrorMemoryPercent = "FO017";
        /// <summary>
        /// FO018	Percentage of total physical memory usage has exceeded configured Warning threshold on a machine.
        /// </summary>
        public const string NodeWarningMemoryPercent = "FO018";
        /// <summary>
        /// FO019	Total Committed memory (MB) has exceeded configured Error threshold on a machine.
        /// </summary>
        public const string NodeErrorMemoryMB = "FO019";
        /// <summary>
        /// FO020	Total Committed memory (MB) has exceeded configured Warning threshold on a machine.
        /// </summary>
        public const string NodeWarningMemoryMB = "FO020";
        /// <summary>
        /// FO021	Error: Configured endpoint detected as unreachable.
        /// </summary>
        public const string AppErrorNetworkEndpointUnreachable = "FO021";
        /// <summary>
        /// FO022	Warning: Configured endpoint detected as unreachable.
        /// </summary>
        public const string AppWarningNetworkEndpointUnreachable = "FO022";
        /// <summary>
        /// FO023	Number of active TCP ports at or exceeding configured Error threshold for an app service process.
        /// </summary>
        public const string AppErrorTooManyActiveTcpPorts = "FO023";
        /// <summary>
        /// FO024	Number of active TCP ports at or exceeding configured Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningTooManyActiveTcpPorts = "FO024";
        /// <summary>
        /// FO025	Number of active TCP ports at or exceeding configured Error threshold on a machine.
        /// </summary>
        public const string NodeErrorTooManyActiveTcpPorts = "FO025";
        /// <summary>
        /// FO026	Number of active TCP ports at or exceeding configured Warning threshold on a machine.
        /// </summary>
        public const string NodeWarningTooManyActiveTcpPorts = "FO026";
        /// <summary>
        /// FO027	Number of enabled Firewall Rules at or exceeding configured Error threshold on a machine.
        /// </summary>
        public const string ErrorTooManyFirewallRules = "FO027";
        /// <summary>
        /// FO028	Number of enabled Firewall Rules at or exceeding configured Warning threshold on a machine.
        /// </summary>
        public const string WarningTooManyFirewallRules = "FO028";
        /// <summary>
        /// FO029	Number of active Ephemeral TCP ports (ports in the dynamic range) at or exceeding configured Error threshold for an app service process.
        /// </summary>
        public const string AppErrorTooManyActiveEphemeralPorts = "FO029";
        /// <summary>
        /// FO030	Number of active Ephemeral TCP ports (ports in the dynamic range) at or exceeding configured Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningTooManyActiveEphemeralPorts = "FO030";
        /// <summary>
        /// FO031	Number of active Ephemeral TCP ports (ports in the Windows dynamic port range) at or exceeding configured Error threshold for a machine.
        /// </summary>
        public const string NodeErrorTooManyActiveEphemeralPorts = "FO031";
        /// <summary>
        /// FO032	Number of active Ephemeral TCP ports (ports in the Windows dynamic port range) at or exceeding configured Warning threshold for a machine.
        /// </summary>
        public const string NodeWarningTooManyActiveEphemeralPorts = "FO032";
        /// <summary>
        /// FO033	Number of allocated File Handles is at or exceeding configured Error threshold for an app service process.
        /// </summary>
        public const string AppErrorTooManyOpenFileHandles = "FO033";
        /// <summary>
        /// FO034	Number of allocated File Handles is at or exceeding configured Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningTooManyOpenFileHandles = "FO034";
        /// <summary>
        /// FO035	Percentage of Maximum number of File Descriptors in use is at or exceeding configured Error threshold on a Linux machine.
        /// </summary>
        public const string NodeErrorTotalOpenFileHandlesPercent = "FO035";
        /// <summary>
        /// FO036	Percentage of Maximum number of File Descriptors in use is at or exceeding configured Warning threshold on a Linux machine.
        /// </summary>
        public const string NodeWarningTotalOpenFileHandlesPercent = "FO036";
        /// <summary>
        /// FO037	Number of allocated File Handles is at or exceeding configured Error threshold on a Linux a machine.
        /// </summary>
        public const string NodeErrorTooManyOpenFileHandles = "FO037";
        /// <summary>
        /// FO038	Number of allocated File Handles is at or exceeding configured Warning threshold on a Linux a machine.
        /// </summary>
        public const string NodeWarningTooManyOpenFileHandles = "FO038";
        /// <summary>
        /// FO039	Number of threads at or exceeding configured Error threshold for an app service process.
        /// </summary>
        public const string AppErrorTooManyThreads = "FO039";
        /// <summary>
        /// FO040	Number of threads at or exceeding configured Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningTooManyThreads = "FO040";
        /// <summary>
        /// FO041	Percentage of Maximum number of KVS LVIDs in use is at or exceeding internal Warning threshold (75%) for an app service process.
        /// The related threshold is non-configurable and Windows-only.
        /// </summary>
        public const string AppWarningKvsLvidsPercentUsed = "FO041";
        /// <summary>
        /// FO042	Folder size (MB) has exceeded configured Error threshold
        /// </summary>
        public const string NodeErrorFolderSizeMB = "FO042";
        /// <summary>
        /// FO043	Folder size (MB) has exceeded configured Warning threshold
        /// </summary>
        public const string NodeWarningFolderSizeMB = "FO043";
        /// <summary>
        /// FO044	Percentage of active Ephemeral TCP ports in use is at or exceeding configured Error threshold for an app service process.
        /// </summary>
        public const string AppErrorActiveEphemeralPortsPercent = "FO044";
        /// <summary>
        /// FO045	Percentage of active Ephemeral TCP ports in use is at or exceeding configured Error threshold for an app service process.
        /// </summary>
        public const string AppWarningActiveEphemeralPortsPercent = "FO045";
        /// <summary>
        /// FO046	Percentage of active Ephemeral TCP ports in use is at or exceeding configured Error threshold for a machine.
        /// </summary>
        public const string NodeErrorActiveEphemeralPortsPercent = "FO046";
        /// <summary>
        /// FO047   Percentage of active Ephemeral TCP ports in use is at or exceeding configured Error threshold for a machine.
        /// </summary>
        public const string NodeWarningActiveEphemeralPortsPercent = "FO047";
        /// <summary>
        /// FO048  Private Bytes usage (Commit) MB is at or exceeding Error threshold for an app service process.
        /// </summary>
        public const string AppErrorPrivateBytesMb = "FO048";
        /// <summary>
        /// FO049  Private Bytes usage (Commit) MB is at or exceeding Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningPrivateBytesMb = "FO049";
        /// <summary>
        /// FO050  Private Bytes usage (Commit) Percentage is at or exceeding Error threshold for an app service process.
        /// </summary>
        public const string AppErrorPrivateBytesPercent = "FO050";
        /// <summary>
        /// FO051  Private Bytes usage (Commit) Percentage is at or exceeding Warning threshold for an app service process.
        /// </summary>
        public const string AppWarningPrivateBytesPercent = "FO051";
        /// <summary>
        /// FO052  At or exceeding configured percentage of Memory Resource Governance limit for a service code package.
        /// </summary>
        public const string AppWarningRGMemoryLimitPercent = "FO052";

        /// <summary>
        /// AppErrorCodesDictionary dictionary.
        /// </summary>
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
                { AppErrorActiveEphemeralPortsPercent, "AppErrorActiveEphemeralPortsPercent" },
                { AppWarningActiveEphemeralPortsPercent, "AppWarningActiveEphemeralPortsPercent" },
                { AppErrorTooManyOpenFileHandles, "AppErrorTooManyOpenFileHandles" },
                { AppWarningTooManyOpenFileHandles, "AppWarningTooManyOpenFileHandles" },
                { AppErrorTooManyThreads, "AppErrorTooManyThreads" },
                { AppWarningTooManyThreads, "AppWarningTooManyThreads" },
                { AppWarningKvsLvidsPercentUsed, "AppWarningKvsLvidsPercentUsed"},
                { AppErrorPrivateBytesMb, "AppErrorPrivateBytesMb"},
                { AppWarningPrivateBytesMb, "AppWarningPrivateBytesMb"},
                { AppErrorPrivateBytesPercent, "AppErrorPrivateBytesPercent" },
                { AppWarningPrivateBytesPercent, "AppWarningPrivateBytesPercent" },
                { AppWarningRGMemoryLimitPercent, "AppWarningRGMemoryLimitPercent" }
            };

        /// <summary>
        /// NodeErrorCodesDictionary dictionary.
        /// </summary>
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
                { NodeErrorFolderSizeMB, "NodeErrorFolderSizeMB" },
                { NodeWarningFolderSizeMB, "NodeWarningFolderSizeMB" },
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
                { NodeErrorActiveEphemeralPortsPercent, "NodeErrorActiveEphemeralPortsPercent" },
                { NodeWarningActiveEphemeralPortsPercent, "NodeWarningActiveEphemeralPortsPercent" },
                { NodeErrorTotalOpenFileHandlesPercent, "NodeErrorTotalOpenFileHandlesPercent" },
                { NodeWarningTotalOpenFileHandlesPercent, "NodeWarningTotalOpenFileHandlesPercent" },
                { NodeErrorTooManyOpenFileHandles, "NodeErrorTooManyOpenFileHandles" },
                { NodeWarningTooManyOpenFileHandles, "NodeWarningTooManyOpenFileHandles" }
            };


        /// <summary>
        /// This function takes a SupportedErrorCode code (key) and returns the name of the error or warning (value).
        /// </summary>
        /// <param name="code">The SupportedErrorWarningCodes code to use as a lookup key.</param>
        /// <returns>The name of the error or warning code.</returns>
        public static string GetCodeNameFromErrorCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return null;
            }

            if (AppErrorCodesDictionary.Any(k => k.Key == code))
            {
                return AppErrorCodesDictionary.First(k => k.Key == code).Value;
            }

            if (NodeErrorCodesDictionary.Any(k => k.Key == code))
            {
                return NodeErrorCodesDictionary.First(k => k.Key == code).Value;
            }

            return null;
        }
    }
}
