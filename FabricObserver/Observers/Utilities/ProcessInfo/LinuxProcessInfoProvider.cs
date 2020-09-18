// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    public class LinuxProcessInfoProvider : ProcessInfoProvider
    {
        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            if (LinuxProcFS.TryParseStatuSFile(processId, out ParsedStatus status))
            {
               return (status.VmRSS - status.RsSFile) / 1048576f;
            }
            else
            {
                // Could not read from /proc/[pid]/status - it is possible that process already exited.
                return 0f;
            }
        }
    }
}
