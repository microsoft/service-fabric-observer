// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public abstract class OSInfoProvider
    {
        private static OSInfoProvider instance;
        private static readonly object _instanceLock = new object();
        private static readonly object _loggerLock = new object();
        private static Logger _logger = null;

        public static OSInfoProvider Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (instance == null)
                        {
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                instance = new WindowsInfoProvider();
                            }
                            else
                            {
                                instance = new LinuxInfoProvider();
                            }
                        }
                    }
                }

                return instance;
            }
        }

        protected static Logger OSInfoLogger
        {
            get
            {
                if (_logger == null)
                {
                    lock (_loggerLock)
                    {
                        if (_logger == null)
                        {
                            _logger = new Logger("OSInfo");
                        }
                    }
                }

                return _logger;
            }
        }

        /// <summary>
        /// Gets OS physical memory information.
        /// </summary>
        /// <returns></returns>
        public abstract (long TotalMemoryGb, long MemoryInUseMb, double PercentInUse) TupleGetSystemPhysicalMemoryInfo();

        /// <summary>
        /// Gets OS virtual memory information. Note, this is not yet implemented for Linux. Linux calls will just get TupleGetSystemPhysicalMemoryInfo() result for now.
        /// </summary>
        /// <returns>Tuple of total available to commit in gigabytes and currenty committed in megabytes.</returns>
        public abstract (long TotalCommitGb, long CommittedInUseMb) TupleGetSystemCommittedMemoryInfo();

        /// <summary>
        /// Compute count of active TCP ports.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of tcp ports in use by the process.</param>
        /// <param name="configPath">Optional (this is used by Linux callers only - see LinuxInfoProvider.cs): 
        /// If supplied, will use the path to find the Linux Capabilities binary to run this command.</param>
        /// <returns>Number of active TCP ports in use as integer value.</returns>
        public abstract int GetActiveTcpPortCount(int processId = -1, string configPath = null);

        /// <summary>
        /// Compute count of active TCP ports in the dynamic range.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of tcp ports in use by the process.</param>
        /// <param name="configPath">Optional (this is used by Linux callers only - see LinuxInfoProvider.cs): 
        /// If supplied, will use the path to find the Linux Capabilities binary to run this command.</param>
        /// <returns>Number of active TCP ports in use as integer value.</returns>
        public abstract int GetActiveEphemeralPortCount(int processId = -1, string configPath = null);

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public abstract (int LowPort, int HighPort, int NumberOfPorts) TupleGetDynamicPortRange();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Returns the Maximum number of Linux File Handles configured in the OS. Note: This is not implemented for Windows.
        /// </summary>
        /// <returns>int value representing the maximum number of file handles/fds configured on host OS at the time of the call. For Windows, this always returns -1.</returns>
        public abstract int GetMaximumConfiguredFileHandlesCount();

        /// <summary>
        /// Returns the Total number of allocated Linux File Handles. Note: This is not implemented for Windows.
        /// </summary>
        /// <returns>int value representing total number of allocated file handles/fds on host OS. For Windows, this always returns -1.</returns>
        public abstract int GetTotalAllocatedFileHandlesCount();

        /// <summary>
        /// Gets the percentage (of total in range) of ephemeral ports currently in use on the machine or by process of supplied pid.
        /// </summary>
        /// <param name="processId">Id of process.</param>
        /// <param name="configPath">Configuration Settings path. This is required by the Linux impl. Ignored for Windows.</param>
        /// <returns>Percentage of ephemeral ports in use as a double.</returns>
        public abstract double GetActiveEphemeralPortCountPercentage(int processId = -1, string configPath = null);
    }
}
