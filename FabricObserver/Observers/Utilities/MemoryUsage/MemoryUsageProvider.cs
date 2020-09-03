// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace FabricObserver.Observers.Utilities
{
    internal abstract class MemoryUsageProvider
    {
        private static MemoryUsageProvider instance;
        private static object lockObj = new object();

        internal static MemoryUsageProvider Instance
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
                                instance = new WindowsMemoryUsageProvider();
                            }
                            else
                            {
                                instance = new LinuxMemoryUsageProvider();
                            }
                        }
                    }
                }

                return instance;
            }
        }

        internal abstract ulong GetCommittedBytes();
    }
}
