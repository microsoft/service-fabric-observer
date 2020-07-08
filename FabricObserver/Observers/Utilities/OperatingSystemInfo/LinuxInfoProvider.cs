using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    internal class LinuxInfoProvider : OperatingSystemInfoProvider
    {
        internal override (long TotalMemory, int PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse()
        {
            Dictionary<string, ulong> memInfo = LinuxProcFS.ReadMemInfo();

            long totalMemory = (long)memInfo[MemInfoConstants.MemTotal];
            long freeMem = (long)memInfo[MemInfoConstants.MemFree];
            long availableMem = (long)memInfo[MemInfoConstants.MemAvailable];

            // Divide by 1048576 to convert total memory from KB to GB.
            return (totalMemory / 1048576, (int)(((double)(totalMemory - availableMem - freeMem)) / totalMemory * 100));
        }

        internal override int GetActivePortCount(int processId = -1)
        {
            int count = GetPortCount(processId, predicate: (line) => true);
            return count;
        }

        internal override int GetActiveEphemeralPortCount(int processId = -1)
        {
            (int lowPort, int highPort) = this.TupleGetDynamicPortRange();

            int count = GetPortCount(processId, (line) =>
                        {
                            int port = GetPortFromNetstatOutput(line);
                            return port >= lowPort && port <= highPort;
                        });

            return count;
        }

        internal override (int LowPort, int HighPort) TupleGetDynamicPortRange()
        {
            string text = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range");
            int tabIndex = text.IndexOf('\t');
            return (LowPort: int.Parse(text.AsSpan(0, tabIndex)), HighPort: int.Parse(text.AsSpan(tabIndex + 1)));
        }

        internal override async Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken)
        {
            OSInfo osInfo = default(OSInfo);
            (int exitCode, List<string> outputLines) = await this.ExecuteProcessAsync("lsb_release", "-d");

            if (exitCode == 0 && outputLines.Count == 1)
            {
                /*
                ** Example:
                ** Description:\tUbuntu 18.04.2 LTS
                */
                osInfo.Name = outputLines[0].Split(':', count: 2)[1].Trim();
            }

            osInfo.Version = await File.ReadAllTextAsync("/proc/version");

            osInfo.Language = string.Empty;
            osInfo.Status = "OK";
            osInfo.NumberOfProcesses = Process.GetProcesses().Length;

            // Source code: https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/fs/proc/meminfo.c
            Dictionary<string, ulong> memInfo = LinuxProcFS.ReadMemInfo();
            osInfo.TotalVisibleMemorySizeKB = memInfo[MemInfoConstants.MemTotal];
            osInfo.FreePhysicalMemoryKB = memInfo[MemInfoConstants.MemFree];
            osInfo.AvailableMemory = memInfo[MemInfoConstants.MemAvailable];

            // On Windows, TotalVirtualMemorySize = TotalVisibleMemorySize + SizeStoredInPagingFiles.
            // SizeStoredInPagingFiles - Total number of kilobytes that can be stored in the operating system paging files—0 (zero)
            // indicates that there are no paging files. Be aware that this number does not represent the actual physical size of the paging file on disk.
            // https://docs.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem
            osInfo.TotalVirtualMemorySizeKB = osInfo.TotalVisibleMemorySizeKB + memInfo[MemInfoConstants.SwapTotal];

            osInfo.FreeVirtualMemoryKB = osInfo.FreePhysicalMemoryKB + memInfo[MemInfoConstants.SwapFree];

            (float uptime, float idleTime) = await LinuxProcFS.ReadUptimeAsync();

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

        private static int GetPortCount(int processId, Predicate<string> predicate)
        {
            string processIdStr = processId == -1 ? string.Empty : " " + processId.ToString() + "/";

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                /*
                ** -t - tcp
                ** -p - display PID/Program name for sockets
                ** -n - don't resolve names
                ** -a - display all sockets (default: connected)
                */
                Arguments = processId == -1 ? "-tna" : "-tpna",
                FileName = "netstat",
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
            };

            int count = 0;

            using (Process process = Process.Start(startInfo))
            {
                string line;

                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (!line.StartsWith("tcp ", StringComparison.Ordinal))
                    {
                        // skip headers
                        continue;
                    }

                    if (processId != -1 && !line.Contains(processIdStr, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!predicate(line))
                    {
                        continue;
                    }

                    ++count;
                }

                process.WaitForExit();

                if (process.ExitCode != 0)
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

            if (colonIndex >= 0)
            {
                int spaceIndex = line.IndexOf(' ', startIndex: colonIndex + 1);

                if (spaceIndex >= 0)
                {
                    return int.Parse(line.AsSpan(colonIndex + 1, spaceIndex - colonIndex - 1));
                }
            }

            return -1;
        }

        private async Task<(int ExitCode, List<string> Output)> ExecuteProcessAsync(string fileName, string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                Arguments = arguments,
                FileName = fileName,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
            };

            List<string> output = new List<string>();

            using (Process process = Process.Start(startInfo))
            {
                string line;

                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    output.Add(line);
                }

                process.WaitForExit();

                return (process.ExitCode, output);
            }
        }
    }
}
