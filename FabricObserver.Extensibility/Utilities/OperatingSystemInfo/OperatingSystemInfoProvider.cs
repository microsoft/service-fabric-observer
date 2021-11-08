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
    public abstract class OSInfoProvider
    {
        private static OSInfoProvider instance;
        private static readonly object lockObj = new object();

        public static OSInfoProvider Instance
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

        //this is in Bytes (B) not bits (b) even though it says Gb and Mb instead of GB and MB
        //why is TotalMemoryGB a long instead of a float? Floors the value so upto <1Gb is lost
        public abstract (long TotalMemoryGb, long MemoryInUseMb, double PercentInUse) TupleGetMemoryInfo();

        public abstract int GetActiveTcpPortCount(int processId = -1, ServiceContext context = null);

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
