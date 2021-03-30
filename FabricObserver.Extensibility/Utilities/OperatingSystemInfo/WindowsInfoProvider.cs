// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsInfoProvider : OperatingSystemInfoProvider
    {
        private const string TcpProtocol = "tcp";

        public override (long TotalMemory, double PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse()
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

                        if (n.Contains("TotalVisible"))
                        {
                            visibleTotal = Convert.ToInt64(v);
                        }

                        if (n.Contains("FreePhysical"))
                        {
                            freePhysical = Convert.ToInt64(v);
                        }
                    }

                    if (visibleTotal <= -1 || freePhysical <= -1)
                    {
                        continue;
                    }

                    double used = ((double)(visibleTotal - freePhysical)) / visibleTotal;
                    double usedPct = used * 100;

                    return (visibleTotal / 1024 / 1024, Math.Round(usedPct, 2));
                }
            }
            catch (Exception e) when (
                e is FormatException
                || e is InvalidCastException
                || e is ManagementException)
            {
            }
            finally
            {
                win32OsInfo?.Dispose();
                results?.Dispose();
            }

            return (-1L, -1);
        }

        /// <summary>
        /// Compute count of active ports in dynamic range.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of ephemeral ports in use by the process.</param>
        /// <param name="context">Optional (this is used by Linux callers only): If supplied, will use the ServiceContext to find the Linux Capabilities binary to run this command.</param>
        /// <returns></returns>
        public override int GetActiveEphemeralPortCount(int processId = -1, ServiceContext context = null)
        {
            try
            {
                List<(int Pid, int Port)> tempPortData = new List<(int Pid, int Port)>();

                using (var p = new Process())
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"-ano -p {TcpProtocol}",
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

                    (int lowPortRange, int highPortRange) = TupleGetDynamicPortRange();

                    string portRow;

                    var processIdString = processId > -1 ? processId.ToString() : string.Empty;

                    while ((portRow = stdOutput.ReadLine()) != null)
                    {
                        if (!portRow.ToLower().Contains(TcpProtocol))
                        {
                            continue;
                        }

                        if (processId > -1)
                        {
                            int lastSpaceIndex = portRow.LastIndexOf(' ');

                            // Don't process different PIDs if processId is supplied.
                            if (portRow.Substring(lastSpaceIndex + 1).CompareTo(processIdString) != 0)
                            {
                                continue;
                            }
                        }

                        int port = GetLocalPortAndStateFromConsoleOutputRow(portRow);

                        // Only add unique pid and port data to list. This would filter out cases where BOUND and ESTABLISHED states have the same Pid and Port, which
                        // would artifically increase the count of ports that FO computes.
                        if (!tempPortData.Any(t => t.Pid > 0 && t.Pid == processId && t.Port == port))
                        {
                            // We only care about active ports in dynamic range.
                            if (port >= lowPortRange && port <= highPortRange)
                            {
                                tempPortData.Add((processId, port));
                            }
                        }
                    }

                    int exitStatus = p.ExitCode;
                    stdOutput.Close();

                    if (exitStatus != 0)
                    {
                        return -1;
                    }
                }

                return tempPortData.Count;
            }
            catch (Exception e) when (
                e is ArgumentException
                || e is InvalidOperationException
                || e is Win32Exception)
            {
            }

            return -1;
        }

        public override (int LowPort, int HighPort) TupleGetDynamicPortRange()
        {
            using (var p = new Process())
            {
                try
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netsh int ipv4 show dynamicportrange {TcpProtocol} | find /i \"port\"",
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
                catch (Exception e) when (
                    e is IOException
                    || e is InvalidOperationException
                    || e is Win32Exception)
                {
                }
            }

            return (-1, -1);
        }

        public override int GetActivePortCount(int processId = -1, ServiceContext context = null)
        {
            try
            {
                string protoParam = "-p " + TcpProtocol;
                string findStrProc = $"| find /i \"{TcpProtocol}\"";

                if (processId > 0)
                {
                    findStrProc = $"| find \"{processId}\" | find /i \"established\"";
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
            catch (Exception e) when (
                e is ArgumentException
                || e is InvalidOperationException
                || e is Win32Exception)
            {
            }

            return -1;
        }

        public override Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken)
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
                                osInfo.AvailableMemoryKB = ulong.Parse(value);
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

        private int GetLocalPortAndStateFromConsoleOutputRow(string portRow)
        {
            if (string.IsNullOrWhiteSpace(portRow))
            {
                return -1;
            }

            List<string> stats = portRow.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            if (stats.Count == 0)
            {
                return -1;
            }

            string localIpAndPort = stats[1];

            if (!localIpAndPort.Contains(":"))
            {
                return -1;
            }

            // We *only* care about the local IP.
            string localPort = localIpAndPort.Split(':')[1];

            return int.Parse(localPort);
        }

        // Not implemented. No Windows support.
        public override int GetMaximumConfiguredFileHandlesCount()
        {
            return -1;
        }

        // Not implemented. No Windows support.
        public override int GetTotalAllocatedFileHandlesCount()
        {
            return -1;
        }
    }
}
