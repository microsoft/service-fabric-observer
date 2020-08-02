using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    internal class LinuxProcessInfoProvider : ProcessInfoProvider
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
