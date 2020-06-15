using System;
using System.Diagnostics;
using System.IO;
using System.Text;

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
    }
}
