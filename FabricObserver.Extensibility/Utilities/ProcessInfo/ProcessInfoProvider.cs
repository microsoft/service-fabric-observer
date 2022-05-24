// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FabricObserver.Observers.Utilities
{
    public abstract class ProcessInfoProvider : IProcessInfoProvider
    {
        private static IProcessInfoProvider instance;
        private static readonly object lockObj = new object();

        public static IProcessInfoProvider Instance
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
        }

        protected Logger Logger 
        { 
            get; 
        } = new Logger("Utilities");

        public abstract float GetProcessWorkingSetMb(int processId, string procName = null, bool getPrivateWorkingSet = false);

        public abstract List<(string ProcName, int Pid)> GetChildProcessInfo(int parentPid, IntPtr handleToSnapshot);

        public abstract float GetProcessAllocatedHandles(int processId, string configPath = null);

        public abstract double GetProcessKvsLvidsUsagePercentage(string procName, int procId = -1);

        /// <summary>
        /// Gets the number of execution threads owned by the process of supplied process id.
        /// </summary>
        /// <param name="processId">Id of the process.</param>
        /// <param name="processName">Name of the process. This is used to ensure the id supplied is still applied to the process of the supplied named.</param>
        /// <returns>Number of threads owned by specified process.</returns>
        public static int GetProcessThreadCount(int processId, string processName)
        {
            try
            {
                using (Process p = Process.GetProcessById(processId))
                {
                    if (p.ProcessName != processName)
                    {
                        return 0;
                    }

                    p.Refresh();
                    return p.Threads.Count;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is SystemException)
            {
                return 0;
            }
        }
    }
}
