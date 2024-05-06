// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver.Observers.Utilities
{
    public abstract class CpuUtilizationProvider
    {
        private static CpuUtilizationProvider instance;
        private static readonly object instanceLock = new();
        private static readonly object loggerLock = new();
        private static Logger logger = null;

        public static CpuUtilizationProvider Instance
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
                                instance = new WindowsCpuUtilizationProvider();
                            }
                            else if (OperatingSystem.IsLinux())
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

        protected static Logger CpuInfoLogger
        {
            get
            {
                if (logger == null)
                {
                    lock (loggerLock)
                    {
                        logger ??= new Logger("CpuInfoProvider");
                        logger.EnableVerboseLogging = true;
                        logger.EnableETWLogging = true;
                    }
                }

                return logger;
            }
        }

        /// <summary>
        /// Gets processor time percentage across all cores.
        /// </summary>
        /// <returns></returns>
        public abstract float GetProcessorTimePercentage();

        /// <summary>
        /// Free resources owned by this instance.
        /// </summary>
        public abstract void Dispose();
    }
}
