// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public abstract class OSInfoProvider
    {
        private static OSInfoProvider instance;
        private static readonly object instanceLock = new();
        private static readonly object loggerLock = new();
        private static Logger logger = null;

        public static OSInfoProvider Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (instanceLock)
                    {
                        if (instance == null)
                        {
                            if (OperatingSystem.IsWindows())
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
                if (logger == null)
                {
                    lock (loggerLock)
                    {
                        logger ??= new Logger("OSInfoProvider");
                        logger.EnableVerboseLogging = true;
                        logger.EnableETWLogging = true;
                    }
                }

                return logger;
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
        /// Gets current count of active TCP ports.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of tcp ports in use by the process.</param>
        /// <param name="configPath">Optional (this is used by Linux callers only - see LinuxInfoProvider.cs): 
        /// If supplied, will use the path to find the Linux Capabilities binary to run this command.</param>
        /// <returns>Current number of TCP ports in use as integer value.</returns>
        public abstract int GetActiveTcpPortCount(int processId = 0, string configPath = null);

        /// <summary>
        /// Gets count of current active TCP ports in the dynamic range.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of tcp ports in use by the process.</param>
        /// <param name="configPath">Optional (this is used by Linux callers only - see LinuxInfoProvider.cs): 
        /// If supplied, will use the path to find the Linux Capabilities binary to run this command.</param>
        /// <returns>Current number of TCP ports in use as integer value.</returns>
        public abstract int GetActiveEphemeralPortCount(int processId = 0, string configPath = null);

        /// <summary>
        /// Gets current dynamic port range information.
        /// </summary>
        /// <returns>Tuple (int LowPort, int HighPort, int NumberOfPorts) containing current port range (low to high) and total number of ports in the dynamic range.</returns>
        public abstract (int LowPort, int HighPort, int NumberOfPorts) TupleGetDynamicPortRange();

        /// <summary>
        /// Provides details about the operating system state.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An OSInfo object containing various OS properties. See OSInfo.cs.</returns>
        public abstract Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Returns the Maximum number of Linux File Handles configured in the OS. Note: This is not implemented for Windows.
        /// </summary>
        /// <returns>int value representing the maximum number of file handles/fds configured on host OS at the time of the call. For Windows, this always returns -1.</returns>
        public abstract int GetMaximumConfiguredFileHandlesCount();

        /// <summary>
        /// Returns the current Total number of allocated Linux File Handles. Note: This is not implemented for Windows.
        /// </summary>
        /// <returns>int value representing total number of allocated file handles/fds on host OS. For Windows, this always returns -1.</returns>
        public abstract int GetTotalAllocatedFileHandlesCount();

        /// <summary>
        /// Gets the percentage (of total in range) of current ephemeral ports currently in use on the machine or by process of supplied pid.
        /// </summary>
        /// <param name="processId">Id of process.</param>
        /// <param name="configPath">Configuration Settings path. This is required by the Linux impl. Ignored for Windows.</param>
        /// <returns>Percentage of ephemeral TCP ports in use as a double.</returns>
        public abstract double GetActiveEphemeralPortCountPercentage(int processId = 0, string configPath = null);

        /// <summary>
        /// Gets total number of current BOUND state TCP ports in the dynamic range (ephemeral ports).
        /// </summary>
        /// <param name="processId">Optional: Process identifier.</param>
        /// <returns>Count of current BOUND state ephemeral TCP ports as an integer.</returns>
        public abstract int GetBoundStateEphemeralPortCount(int processId = 0);

        /// <summary>
        /// Gets total number of current BOUND state TCP ports.
        /// </summary>
        /// <param name="processId">Optional: Process identifier.</param>
        /// <returns>Count of current BOUND state TCP ports as an integer.</returns>
        public abstract int GetBoundStatePortCount(int processId = 0);
    }
}
