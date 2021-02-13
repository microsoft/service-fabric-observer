// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public abstract class OperatingSystemInfoProvider
    {
        private static OperatingSystemInfoProvider instance;
        private static object lockObj = new object();

        protected OperatingSystemInfoProvider()
        {
        }

        public static OperatingSystemInfoProvider Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (lockObj)
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

        protected Logger Logger
        {
            get;
        } = new Logger("OSUtilities");


        public abstract (long TotalMemory, double PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse();

        public abstract int GetActivePortCount(int processId = -1, ServiceContext context = null);

        public abstract int GetActiveEphemeralPortCount(int processId = -1, ServiceContext context = null);

        public abstract (int LowPort, int HighPort) TupleGetDynamicPortRange();

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
    }
}
