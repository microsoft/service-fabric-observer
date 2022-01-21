// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxInfoProvider : OSInfoProvider
    {
        public override (long TotalMemoryGb, long MemoryInUseMb, double PercentInUse) TupleGetSystemMemoryInfo()
        {
            Dictionary<string, ulong> memInfo = LinuxProcFS.ReadMemInfo();

            long totalMemory = (long)memInfo[MemInfoConstants.MemTotal];
            long freeMem = (long)memInfo[MemInfoConstants.MemFree];
            long availableMem = (long)memInfo[MemInfoConstants.MemAvailable];

            // Divide by 1048576 to convert total memory from KB to GB.
            long totalMem = totalMemory / 1048576;
            double pctUsed = ((double)(totalMemory - availableMem - freeMem)) / totalMemory * 100;
            long memUsed = (totalMemory - availableMem - freeMem) / 1024;

            return (totalMem, memUsed, Math.Round(pctUsed, 2));
        }

        public override int GetActiveTcpPortCount(int processId = -1, ServiceContext context = null)
        {
            int count = GetPortCount(processId, predicate: line => true, context);
            return count;
        }

        public override int GetActiveEphemeralPortCount(int processId = -1, ServiceContext context = null)
        {
            (int lowPort, int highPort) = TupleGetDynamicPortRange();

            int count = GetPortCount(processId, line =>
            {
                int port = GetPortFromNetstatOutput(line);
                return port >= lowPort && port <= highPort;
            }, context);

            return count;
        }

        public override (int LowPort, int HighPort) TupleGetDynamicPortRange()
        {
            string text = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range");
            int tabIndex = text.IndexOf('\t');
            return (LowPort: int.Parse(text.Substring(0, tabIndex)), HighPort: int.Parse(text.Substring(tabIndex + 1)));
        }

        public override async Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken)
        {
            OSInfo osInfo = default;
            (int exitCode, List<string> outputLines) = await ExecuteProcessAsync("lsb_release", "-d");

            if (exitCode == 0 && outputLines.Count == 1)
            {
                /*
                ** Example:
                ** Description:\tUbuntu 18.04.2 LTS
                */
                osInfo.Name = outputLines[0].Split(new[] { ':' }, 2)[1].Trim();
            }

            osInfo.Version = File.ReadAllText("/proc/version");

            osInfo.Language = string.Empty;
            osInfo.Status = "OK";
            osInfo.NumberOfProcesses = Process.GetProcesses().Length;

            // Source code: https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/fs/proc/meminfo.c
            Dictionary<string, ulong> memInfo = LinuxProcFS.ReadMemInfo();
            osInfo.TotalVisibleMemorySizeKB = memInfo[MemInfoConstants.MemTotal];
            osInfo.FreePhysicalMemoryKB = memInfo[MemInfoConstants.MemFree];
            osInfo.AvailableMemoryKB = memInfo[MemInfoConstants.MemAvailable];

            // On Windows, TotalVirtualMemorySize = TotalVisibleMemorySize + SizeStoredInPagingFiles.
            // SizeStoredInPagingFiles - Total number of kilobytes that can be stored in the operating system paging files—0 (zero)
            // indicates that there are no paging files. Be aware that this number does not represent the actual physical size of the paging file on disk.
            // https://docs.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem
            osInfo.TotalVirtualMemorySizeKB = osInfo.TotalVisibleMemorySizeKB + memInfo[MemInfoConstants.SwapTotal];

            osInfo.FreeVirtualMemoryKB = osInfo.FreePhysicalMemoryKB + memInfo[MemInfoConstants.SwapFree];

            (float uptime, _) = LinuxProcFS.ReadUptime();

            osInfo.LastBootUpTime = DateTime.UtcNow.AddSeconds(-uptime).ToString("o");

            try
            {
                osInfo.InstallDate = new DirectoryInfo("/var/log/installer").CreationTimeUtc.ToString("o");
            }
            catch (IOException)
            {
                osInfo.InstallDate = "N/A";
            }

            return osInfo;
        }

        private static int GetPortCount(int processId, Predicate<string> predicate, ServiceContext context = null)
        {
            string processIdStr = processId == -1 ? string.Empty : " " + processId + "/";

            /*
            ** -t - tcp
            ** -p - display PID/Program name for sockets
            ** -n - don't resolve names
            ** -a - display all sockets (default: connected)
            */
            string arg = "-tna";
            string bin = "netstat";

            if (processId > -1)
            {
                if (context == null)
                {
                    return -1;
                }

                // We need the full path to the currently deployed FO CodePackage, which is were our 
                // proxy binary lives, which is used for elevated netstat call.
                string path = context.CodePackageActivationContext.GetCodePackageObject("Code").Path;
                arg = string.Empty;

                // This is a proxy binary that uses Capabilities to run netstat -tpna with elevated privilege.
                // FO runs as sfappsuser (SF default, Linux normal user), which can't run netstat -tpna. 
                // During deployment, a setup script is run (as root user)
                // that adds capabilities to elevated_netstat program, which will *only* run (execv) "netstat -tpna".
                bin = $"{path}/elevated_netstat";
            }

            var startInfo = new ProcessStartInfo
            {
                Arguments = arg,
                FileName = bin,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false
            };

            int count = 0;
            using (Process process = Process.Start(startInfo))
            {
                string line;
                while (process != null && (line = process.StandardOutput.ReadLine()) != null)
                {
                    if (!line.StartsWith("tcp ", StringComparison.OrdinalIgnoreCase))
                    {
                        // skip headers
                        continue;
                    }

                    if (processId != -1 && !line.Contains(processIdStr))
                    {
                        continue;
                    }

                    if (!predicate(line))
                    {
                        continue;
                    }

                    ++count;
                }

                process?.WaitForExit();

                if (process != null && process.ExitCode != 0)
                {
                    return -1;
                }
            }

            return count;
        }

        private static int GetPortFromNetstatOutput(string line)
        {
            /*
            ** Example:
            ** tcp        0      0 0.0.0.0:19080           0.0.0.0:*               LISTEN      13422/FabricGateway
            */

            int colonIndex = line.IndexOf(':');

            if (colonIndex < 0)
            {
                return -1;
            }

            int spaceIndex = line.IndexOf(' ', startIndex: colonIndex + 1);

            if (spaceIndex >= 0)
            {
                return int.Parse(line.Substring(colonIndex + 1, spaceIndex - colonIndex - 1));
            }

            return -1;
        }

        /// <summary>
        /// Returns the Maximum configured number of File Handles/FDs in Linux OS instance.
        /// </summary>
        /// <returns>int value representing configured maximum number of file handles/fds in Linux OS instance.</returns>
        public override int GetMaximumConfiguredFileHandlesCount()
        {
            // sysctl fs.file-max - Maximum number of file handles the Linux kernel will allocate. This is a configurable setting on Linux.
            string cmdResult = "sysctl fs.file-max | awk '{ print $3 }'".Bash();

            // sysctl fs.file-max result will be in this format:
            // fs.file-max = 1616177

            if (string.IsNullOrEmpty(cmdResult))
            {
                return -1;
            }

            if (int.TryParse(cmdResult.Trim(), out int maxHandles))
            {
                return maxHandles;
            }

            return -1;
        }

        /// <summary>
        /// Returns the total number of allocated (in use) Linux File Handles/FDs.
        /// </summary>
        /// <returns>integer value representing the total number of allocated (in use) Linux FileHandles/FDs.</returns>
        public override int GetTotalAllocatedFileHandlesCount()
        {
            // sysctl fs.file-nr result will be in this format:
            // fs.file-nr = 30112      0       1616177
            string cmdResult = "sysctl fs.file-nr | awk '{ print $3 }'".Bash();

            if (string.IsNullOrEmpty(cmdResult))
            {
                return -1;
            }

            if (int.TryParse(cmdResult.Trim(), out int totalFDs))
            {
                return totalFDs;
            }

            return -1;
        }

        private async Task<(int ExitCode, List<string> Output)> ExecuteProcessAsync(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                Arguments = arguments,
                FileName = fileName,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false
            };

            List<string> output = new List<string>();

            using (Process process = Process.Start(startInfo))
            {
                string line;
                while (process != null && (line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    output.Add(line);
                }

                process.WaitForExit();

                return (process.ExitCode, output);
            }
        }
    }

    // https://loune.net/2017/06/running-shell-bash-commands-in-net-core/
    public static class LinuxShellHelper
    {
        /// <summary>
        /// This string extension will run a supplied linux bash command and return the console output.
        /// </summary>
        /// <param name="cmd">The bash command to run.</param>
        /// <returns>The console output of the command.</returns>
        public static string Bash(this string cmd)
        {
            var escapedArgs = cmd.Replace("\"", "\\\"");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{escapedArgs}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string result = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return result;
        }
    }
}
