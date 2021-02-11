// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Fabric;

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

        public override float GetProcessOpenFileHandles(int processId, StatelessServiceContext context)
        {
            if (context == null || processId < 0)
            {
                return -1;
            }

            // We need the full path to the currently deployed FO CodePackage, which is were our 
            // proxy binary lives, which is used for elevated netstat call.
            string path = context.CodePackageActivationContext.GetCodePackageObject("Code").Path;
            string arg = processId.ToString();

            // This is a proxy binary that uses Capabilites to run netstat -tpna with elevated privilege.
            // FO runs as sfappsuser (SF default, Linux normal user), which can't run netstat -tpna. 
            // During deployment, a setup script is run (as root user)
            // that adds capabilities to elevated_netstat program, which will *only* run (execv) "netstat -tpna".
            string bin = $"{path}/elevated_proc_fd";

            float result;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                Arguments = arg,
                FileName = bin,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
            };

            using (Process process = Process.Start(startInfo))
            {
                result = !string.IsNullOrEmpty(process.StandardOutput.ReadToEnd()) ? float.Parse(process.StandardOutput.ReadToEnd()) : -1f;

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    return -1f;
                }
            }

            return result;
        }
    }
}