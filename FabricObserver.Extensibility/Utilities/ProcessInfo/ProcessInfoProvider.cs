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
    public abstract class ProcessInfoProvider : IProcessInfoProvider
    {
        private static IProcessInfoProvider instance;
        private static readonly object lockObj = new object();

        protected ProcessInfoProvider()
        {
        }

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

        public abstract float GetProcessPrivateWorkingSetInMB(int processId);

        public abstract float GetProcessAllocatedHandles(int processId, StatelessServiceContext context);
    }
}
