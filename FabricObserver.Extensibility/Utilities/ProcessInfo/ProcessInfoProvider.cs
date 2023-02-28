// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FabricObserver.Observers.Utilities
{
    public abstract class ProcessInfoProvider : IProcessInfoProvider
    {
        private static IProcessInfoProvider instance;
        private static readonly object instanceLock = new();
        private static readonly object loggerLock = new();
        private static Logger logger;

        public static IProcessInfoProvider Instance
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
                                instance = new WindowsProcessInfoProvider();
                            }
                            else
                            {
                                instance = new LinuxProcessInfoProvider();
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

        internal static Logger ProcessInfoLogger
        {
            get
            {
                if (logger == null)
                {
                    lock (loggerLock)
                    {
                        logger ??= new Logger("ProcessInfoProvider");
                    }
                }

                return logger;
            }
        }

        public abstract float GetProcessWorkingSetMb(int processId, string procName, CancellationToken token, bool getPrivateWorkingSet = false);

        public abstract float GetProcessPrivateBytesMb(int processId);

        public abstract List<(string ProcName, int Pid)> GetChildProcessInfo(int parentPid, NativeMethods.SafeObjectHandle handleToSnapshot = null);

        public abstract float GetProcessAllocatedHandles(int processId, string configPath = null);

        public abstract double GetProcessKvsLvidsUsagePercentage(string procName, CancellationToken token, int procId = -1);

        /// <summary>
        /// Gets the number of execution threads owned by the process of supplied process id.
        /// </summary>
        /// <param name="processId">Id of the process.</param>
        /// <returns>Number of threads owned by specified process.</returns>
        public static int GetProcessThreadCount(int processId)
        {
            try
            {
                using Process p = Process.GetProcessById(processId);
                p.Refresh();
                return p.Threads.Count;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is SystemException)
            {
                return 0;
            }
        }
    }
}
