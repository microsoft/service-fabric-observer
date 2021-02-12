// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxProcessInfoProvider : ProcessInfoProvider
    {
        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            if (LinuxProcFS.TryParseStatusFile(processId, out ParsedStatus status))
            {
                return (status.VmRSS - status.RsSFile) / 1048576f;
            }
            else
            {
                // Could not read from /proc/[pid]/status - it is possible that process already exited.
                return 0f;
            }
        }

        // TODO: This is long running when processId is -1. Use token cancellation to throw out of this.
        public override async Task<float> GetProcessOpenFileHandlesAsync(int processId, StatelessServiceContext context, CancellationToken token)
        {
            // We need the full path to the currently deployed FO CodePackage, which is where our 
            // proxy binary lives.
            string path = context.CodePackageActivationContext.GetCodePackageObject("Code").Path;
            string arg = processId.ToString();

            // This is a proxy binary that employs Linux Capabilites to run either ls /proc/[pid]/fd (with piped output via wc) or lsof (with piped output via wc) 
            // with elevated privilege (sudo) from a non-privileged process (FabricObserver, which runs as sfappuser).
            // FO runs as sfappsuser (SF default, Linux normal user), which can't run ls. During deployment, a setup script is run (as root user)
            // that adds capabilities to the elevated_proc_fd binary (which internally implements the same set of capabilites), which will *only* run a single command:
            // in this case, either ls (when a real process id is passed in) or lsof (when you pass -1 for processId, which means you want the number *ALL* allocated FDs - which is an expensive operation. Use it wisely..).
            string bin = $"{path}/elevated_proc_fd";

            float result;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                Arguments = arg,
                FileName = bin,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            using (Process process = Process.Start(startInfo))
            {
                var stdOut = process.StandardOutput;
                string output = await stdOut.ReadToEndAsync().ConfigureAwait(false);

                process.WaitForExit();

                result = float.TryParse(output, out float ret) ? ret : -42f;

                if (process.ExitCode != 0)
                {
                    this.Logger.LogWarning($"elevated_proc_fd exited with: {process.ExitCode}");
                    return -1f;
                }
            }

            return result;
        }
    }
}