using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    internal class LinuxInfoProvider : OperatingSystemInfoProvider
    {
        internal override (long TotalMemory, int PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse()
        {
            long totalMemory = -1;

            using (StreamReader sr = new StreamReader("/proc/meminfo", encoding: Encoding.ASCII))
            {
                string line;

                while ((line = sr.ReadLine()) != null)
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        // totalMemory is in KB. This is always first line.
                        totalMemory = ReadInt64(line, "MemTotal:".Length + 1);
                    }
                    else if (line.StartsWith("MemFree:"))
                    {
                        // freeMem is in KB. Usually second line.
                        long freeMem = ReadInt64(line, "MemFree:".Length + 1);

                        // Divide by 1048576 to convert total memory
                        // from KB to GB.
                        return (totalMemory / 1048576, (int)(((double)(totalMemory - freeMem)) / totalMemory * 100));
                    }
                }
            }

            return (-1L, -1);
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

            osInfo.Version = File.ReadAllText("/proc/version");

            osInfo.Language = string.Empty;
            osInfo.Status = "OK";
            osInfo.NumberOfProcesses = Process.GetProcesses().Length;

            using (StreamReader sr = new StreamReader("/proc/meminfo", encoding: Encoding.UTF8))
            {
                string line;
                ulong vmallocUsed = 0UL;

                while ((line = sr.ReadLine()) != null)
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        // totalMemory is in KB. This is always first line.
                        osInfo.TotalVisibleMemorySizeKB = (ulong)ReadInt64(line, "MemTotal:".Length + 1);
                    }
                    else if (line.StartsWith("MemFree:"))
                    {
                        // freeMem is in KB. Usually second line.
                        osInfo.FreePhysicalMemoryKB = (ulong)ReadInt64(line, "MemFree:".Length + 1);
                    }
                    else if (line.StartsWith("VmallocTotal:"))
                    {
                        osInfo.TotalVirtualMemorySizeKB = (ulong)ReadInt64(line, "VmallocTotal:".Length + 1);
                    }
                    else if (line.StartsWith("VmallocUsed:"))
                    {
                        vmallocUsed = (ulong)ReadInt64(line, "VmallocUsed:".Length + 1);
                    }
                }

                osInfo.FreeVirtualMemoryKB = osInfo.TotalVirtualMemorySizeKB - vmallocUsed;
            }

            // Doc: https://access.redhat.com/documentation/en-us/red_hat_enterprise_linux/6/html/deployment_guide/s2-proc-uptime
            string text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync("/proc/uptime"));

            // /proc/uptime contains to 2 decimal numbers. The first value represents the total number of seconds the system has been up.
            // The second value is the sum of how much time each core has spent idle, in seconds.
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            osInfo.LastBootUpTime = DateTime.UtcNow.AddSeconds(-double.Parse(parts[0])).ToString("o");

            try
            {
                osInfo.InstallDate = new DirectoryInfo("/var/log/installer").CreationTimeUtc.ToString("o");
            }
            catch
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

        private static long ReadInt64(string line, int startIndex)
        {
            long result = 0;

            while (line[startIndex] == ' ')
            {
                ++startIndex;
            }

            int len = line.Length;

            while (startIndex < len)
            {
                char c = line[startIndex];

                int d = c - '0';

                if (d >= 0 && d <= 9)
                {
                    result = checked((result * 10L) + (long)d);
                    ++startIndex;
                }
                else
                {
                    break;
                }
            }

            return result;
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
