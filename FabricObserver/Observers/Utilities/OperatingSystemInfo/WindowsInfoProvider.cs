using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    internal class WindowsInfoProvider : OperatingSystemInfoProvider
    {
        internal override (long TotalMemory, int PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse()
        {
            ManagementObjectSearcher win32OsInfo = null;
            ManagementObjectCollection results = null;

            try
            {
                win32OsInfo = new ManagementObjectSearcher("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
                results = win32OsInfo.Get();

                foreach (var prop in results)
                {
                    long visibleTotal = -1;
                    long freePhysical = -1;

                    foreach (var p in prop.Properties)
                    {
                        string n = p.Name;
                        object v = p.Value;

                        if (n.Contains("TotalVisible", StringComparison.OrdinalIgnoreCase))
                        {
                            visibleTotal = Convert.ToInt64(v);
                        }

                        if (n.Contains("FreePhysical", StringComparison.OrdinalIgnoreCase))
                        {
                            freePhysical = Convert.ToInt64(v);
                        }
                    }

                    if (visibleTotal <= -1 || freePhysical <= -1)
                    {
                        continue;
                    }

                    double used = ((double)(visibleTotal - freePhysical)) / visibleTotal;
                    int usedPct = (int)(used * 100);

                    return (visibleTotal / 1024 / 1024, usedPct);
                }
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (ManagementException)
            {
            }
            finally
            {
                win32OsInfo?.Dispose();
                results?.Dispose();
            }

            return (-1L, -1);
        }

        internal override int GetActiveEphemeralPortCount(int processId = -1)
        {
            try
            {
                string protoParam = "tcp";

                int count = 0;

                using (var p = new Process())
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"-ano -p {protoParam}",
                        FileName = "netstat.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    p.StartInfo = ps;
                    _ = p.Start();
                    var stdOutput = p.StandardOutput;

                    (int lowPortRange, int highPortRange) = this.TupleGetDynamicPortRange();

                    string portRow;

                    ReadOnlySpan<char> processIdSpan = (processId > -1 ? processId.ToString() : string.Empty).AsSpan();

                    while ((portRow = stdOutput.ReadLine()) != null)
                    {
                        if (!portRow.Contains(protoParam, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (processId > -1)
                        {
                            int lastSpaceIndex = portRow.LastIndexOf(' ');

                            if (portRow.AsSpan(lastSpaceIndex + 1).CompareTo(processIdSpan, StringComparison.Ordinal) != 0)
                            {
                                continue;
                            }
                        }

                        int port = GetPortNumberFromConsoleOutputRow(portRow);

                        if (port >= lowPortRange && port <= highPortRange)
                        {
                            ++count;
                        }
                    }

                    int exitStatus = p.ExitCode;
                    stdOutput.Close();

                    if (exitStatus != 0)
                    {
                        return -1;
                    }

                    // Compute count of active ports in dynamic range.
                    return count;
                }
            }
            catch (Exception e) when
            (e is ArgumentException
             || e is InvalidOperationException
             || e is Win32Exception)
            {
            }

            return -1;
        }

        internal override (int LowPort, int HighPort) TupleGetDynamicPortRange()
        {
            using (var p = new Process())
            {
                string protoParam = "tcp";

                try
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netsh int ipv4 show dynamicportrange {protoParam} | find /i \"port\"",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                    };

                    p.StartInfo = ps;
                    _ = p.Start();

                    var stdOutput = p.StandardOutput;
                    string output = stdOutput.ReadToEnd();
                    Match match = Regex.Match(
                        output,
                        @"Start Port\s+:\s+(?<startPort>\d+).+?Number of Ports\s+:\s+(?<numberOfPorts>\d+)",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    string startPort = match.Groups["startPort"].Value;
                    string portCount = match.Groups["numberOfPorts"].Value;
                    string exitStatus = p.ExitCode.ToString();
                    stdOutput.Close();

                    if (exitStatus != "0")
                    {
                        return (-1, -1);
                    }

                    int lowPortRange = int.Parse(startPort);
                    int highPortRange = lowPortRange + int.Parse(portCount);

                    return (lowPortRange, highPortRange);
                }
                catch (Exception e) when
                 (e is IOException
                 || e is InvalidOperationException
                 || e is Win32Exception)
                {
                }

                return (-1, -1);
            }
        }

        internal override int GetActivePortCount(int processId = -1)
        {
            const string Protocol = "tcp";

            try
            {
                string protoParam = string.Empty;

                protoParam = "-p " + Protocol;

                var findStrProc = $"| find /i \"{Protocol}\"";

                if (processId > 0)
                {
                    findStrProc = $"| find \"{processId}\"";
                }

                using (var p = new Process())
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netstat -ano {protoParam} {findStrProc} /c",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                    };

                    p.StartInfo = ps;
                    _ = p.Start();

                    var stdOutput = p.StandardOutput;
                    string output = stdOutput.ReadToEnd().Trim('\n', '\r');
                    string exitStatus = p.ExitCode.ToString();
                    stdOutput.Close();

                    if (exitStatus != "0")
                    {
                        return -1;
                    }

                    return int.TryParse(output, out int ret) ? ret : 0;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            return -1;
        }

        internal override Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken)
        {
            ManagementObjectSearcher win32OsInfo = null;
            ManagementObjectCollection results = null;

            OSInfo osInfo = default(OSInfo);

            try
            {
                win32OsInfo = new ManagementObjectSearcher("SELECT Caption,Version,Status,OSLanguage,NumberOfProcesses,FreePhysicalMemory,FreeVirtualMemory,TotalVirtualMemorySize,TotalVisibleMemorySize,InstallDate,LastBootUpTime FROM Win32_OperatingSystem");
                results = win32OsInfo.Get();

                foreach (var prop in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var p in prop.Properties)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string name = p.Name;
                        string value = p.Value.ToString();

                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                        {
                            continue;
                        }

                        switch (name.ToLowerInvariant())
                        {
                            case "caption":
                                osInfo.Name = value;
                                break;
                            case "numberofprocesses":
                                if (int.TryParse(value, out int numProcesses))
                                {
                                    osInfo.NumberOfProcesses = numProcesses;
                                }
                                else
                                {
                                    osInfo.NumberOfProcesses = -1;
                                }

                                break;
                            case "status":
                                osInfo.Status = value;
                                break;
                            case "oslanguage":
                                osInfo.Language = value;
                                break;
                            case "version":
                                osInfo.Version = value;
                                break;
                            case "installdate":
                                osInfo.InstallDate = ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().ToString("o");
                                break;
                            case "lastbootuptime":
                                osInfo.LastBootUpTime = ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().ToString("o");
                                break;
                            case "freephysicalmemory":
                                osInfo.FreePhysicalMemoryKB = ulong.Parse(value);
                                break;
                            case "freevirtualmemory":
                                osInfo.FreeVirtualMemoryKB = ulong.Parse(value);
                                break;
                            case "totalvirtualmemorysize":
                                osInfo.TotalVirtualMemorySizeKB = ulong.Parse(value);
                                break;
                            case "totalvisiblememorysize":
                                osInfo.TotalVisibleMemorySizeKB = ulong.Parse(value);
                                break;
                        }
                    }
                }
            }
            catch (ManagementException)
            {
            }
            finally
            {
                results?.Dispose();
                win32OsInfo?.Dispose();
            }

            return Task.FromResult(osInfo);
        }

        private static int GetPortNumberFromConsoleOutputRow(string row)
        {
            int colonIndex = row.IndexOf(':');

            if (colonIndex >= 0)
            {
                int spaceIndex = row.IndexOf(' ', startIndex: colonIndex + 1);

                if (spaceIndex >= 0)
                {
                    return int.Parse(row.AsSpan(colonIndex + 1, spaceIndex - colonIndex - 1));
                }
            }

            return -1;
        }
    }
}
