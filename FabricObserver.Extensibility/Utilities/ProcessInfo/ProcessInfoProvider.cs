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
    public abstract class ProcessInfoProvider : IProcessInfoProvider, IDisposable
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

        public void Dispose()
        {
            Dispose(disposing: true);
            instance = null;
        }

        protected Logger Logger 
        { 
            get; 
        } = new Logger("Utilities");

        public abstract float GetProcessPrivateWorkingSetInMB(int processId);

        public abstract float GetProcessAllocatedHandles(int processId, StatelessServiceContext context);

        public abstract List<Process> GetChildProcesses(Process process);

        protected abstract void Dispose(bool disposing);
    }
}
