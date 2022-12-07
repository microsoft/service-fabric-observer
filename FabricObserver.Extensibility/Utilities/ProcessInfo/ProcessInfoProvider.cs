// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

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
            set
            {
                instance = value;
            }
        }

        protected Logger Logger 
        { 
            get; 
        } = new Logger("Utilities");

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
                using (Process p = Process.GetProcessById(processId))
                {
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
