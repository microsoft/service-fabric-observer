// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
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

        public abstract float GetProcessWorkingSetMb(int processId, bool getPrivateWorkingSet = false);

        public abstract float GetProcessAllocatedHandles(int processId, StatelessServiceContext context);

        public abstract List<(string ProcName, int Pid)> GetChildProcessInfo(int processId);

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
