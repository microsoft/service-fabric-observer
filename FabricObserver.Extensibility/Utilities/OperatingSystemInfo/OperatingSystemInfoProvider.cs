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

        public abstract (long TotalMemory, int PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse();

        public abstract int GetActivePortCount(int processId = -1, ServiceContext context = null);

        public abstract int GetActiveEphemeralPortCount(int processId = -1, ServiceContext context = null);

        public abstract (int LowPort, int HighPort) TupleGetDynamicPortRange();

        public abstract Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken);
    }
}
