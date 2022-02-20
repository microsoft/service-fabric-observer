// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace FabricObserver.Observers.Utilities
{
    public abstract class CpuUtilizationProvider
    {
        public abstract float GetProcessorTimePercentage();
        private static CpuUtilizationProvider instance;
        private static readonly object lockObj = new object();

        public static CpuUtilizationProvider Instance
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
                                instance = new WindowsCpuUtilizationProvider();
                            }
                            else
                            {
                                instance = new LinuxCpuUtilizationProvider();
                            }
                        }
                    }
                }

                return instance;
            }
            set
            {
                instance = value;
            }
        }

        public abstract void Dispose();
    }
}
