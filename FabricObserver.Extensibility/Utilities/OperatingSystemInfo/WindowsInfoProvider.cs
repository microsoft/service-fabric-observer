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
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is ManagementException)
            {
                Logger.LogWarning($"Handled failure in TupleGetTotalPhysicalMemorySizeAndPercentInUse:{Environment.NewLine}{e}");
            }
            finally
            {
                win32OsInfo?.Dispose();
                results?.Dispose();
            }

            return (-1L, -1);
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
                        e is ArgumentException
                        || e is IOException
                        || e is InvalidOperationException
                        || e is RegexMatchTimeoutException
                        || e is Win32Exception)
                {
                }
            }

            return (-1, -1);
        }

        /// <summary>
        /// Compute count of active TCP ports in dynamic range.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of ephemeral ports in use by the process.</param>
        /// <param name="context">Optional (this is used by Linux callers only - see LinuxInfoProvider.cs): 
        /// If supplied, will use the ServiceContext to find the Linux Capabilities binary to run this command.</param>
        /// <returns>number of active Epehemeral TCP ports as int value</returns>
        public override int GetActiveEphemeralPortCount(int processId = -1, ServiceContext context = null)
        {
            int count;

            try
            {
                count = Retry.Do(() => GetEphemeralPortCount(processId), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                Logger.LogWarning($"Retry failed for GetActiveEphemeralPortCount:{Environment.NewLine}{ae.InnerException}");
                count = -1;
            }

            return count;
        }

        /// <summary>
        /// Compute count of active TCP ports.
        /// </summary>
        /// <param name="processId">Optional: If supplied, then return the number of tcp ports in use by the process.</param>
        /// <param name="context">Optional (this is used by Linux callers only - see LinuxInfoProvider.cs): 
        /// If supplied, will use the ServiceContext to find the Linux Capabilities binary to run this command.</param>
        /// <returns>number of active TCP ports as int value</returns>
        public override int GetActiveTcpPortCount(int processId = -1, ServiceContext context = null)
        {
            int count;

            try
            {
                count = Retry.Do(() => GetTcpPortCount(processId), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                Logger.LogWarning($"Retry failed for GetActivePortCount:{Environment.NewLine}{ae.InnerException}");
                count = -1;
            }

            return count;
        }

        public override Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken)
        {
            ManagementObjectSearcher win32OsInfo = null;
            ManagementObjectCollection results = null;

            OSInfo osInfo = default;

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

        private int GetEphemeralPortCount(int processId = -1)
        {
            try
            {
                List<(int Pid, int Port)> tempLocalPortData = new List<(int Pid, int Port)>();
                string s = string.Empty;
                int count = -1;

                if (processId > 0)
                {
                    s = $" | find \"{processId}\"";
                }

                using (var p = new Process())
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netstat -qno -p {TcpProtocol}{s}",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
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
                    while ((portRow = stdOutput.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(portRow))
                        {
                            continue;
                        }

                        int port = GetLocalPortFromConsoleOutputRow(portRow);

                        // Only add unique pid and port data to list. This would filter out cases where BOUND and ESTABLISHED states have the same Pid and Port, which
                        // would artificially increase the count of ports that FO computes.
                        if (processId > 0)
                        {
                            List<string> stats = portRow.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                            if (stats.Count != 5 || !int.TryParse(stats[4], out int pidPart))
                            {
                                continue;
                            }

                            if (processId != pidPart)
                            {
                                continue;
                            }

                            if (!tempLocalPortData.Any(t => t.Pid == processId && t.Port == port))
                            {
                                if (port >= lowPortRange && port <= highPortRange)
                                {
                                    tempLocalPortData.Add((processId, port));
                                }
                            }
                        }
                        else
                        {
                            if (!tempLocalPortData.Any(t => t.Port == port))
                            {
                                if (port >= lowPortRange && port <= highPortRange)
                                {
                                    tempLocalPortData.Add((processId, port));
                                }
                            }
                        }
                    }

                    p.WaitForExit();
                    int exitStatus = p.ExitCode;
                    stdOutput.Close();
                    count = tempLocalPortData.Count;
                    tempLocalPortData.Clear();

                    if (exitStatus != 0)
                    {
                        string msg = $"netstat failure: {exitStatus}";
                        Logger.LogWarning(msg);

                        // this will be handled by Retry.Do().
                        throw new Exception(msg);
                    }

                    return count;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                Logger.LogWarning($"Handled Exception in GetEphemeralPortCount:{Environment.NewLine}{e}");
                
                // This will be handled by Retry.Do().
                throw;
            }
        }

        private int GetTcpPortCount(int processId = -1)
        {
            try
            {
                string protoParam = "-p " + TcpProtocol;
                string findStrProc = string.Empty;
                List<(int Pid, int Port)> tempLocalPortData = new List<(int Pid, int Port)>();
                int output;

                if (processId > 0)
                {
                    findStrProc = $" | find \"{processId}\"";
                }

                using (var p = new Process())
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netstat -qno {protoParam}{findStrProc}",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                    };

                    p.StartInfo = ps;
                    _ = p.Start();
                    var stdOutput = p.StandardOutput;

                    string portRow;
                    while ((portRow = stdOutput.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(portRow))
                        {
                            continue;
                        }

                        int localPort = GetLocalPortFromConsoleOutputRow(portRow);

                        // Only add unique pid (if supplied in call) and local port data to list.
                        if (processId > 0)
                        {
                            List<string> stats = portRow.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                            if (stats.Count != 5 || !int.TryParse(stats[4], out int pidPart))
                            {
                                continue;
                            }

                            if (processId != pidPart)
                            {
                                continue;
                            }

                            if (!tempLocalPortData.Any(t => t.Pid == processId && t.Port == localPort))
                            {
                                tempLocalPortData.Add((processId, localPort));
                            }
                        }
                        else
                        {
                            if (!tempLocalPortData.Any(t => t.Port == localPort))
                            {
                                tempLocalPortData.Add((processId, localPort));
                            }
                        }
                    }

                    output = tempLocalPortData.Count;
                    p.WaitForExit();
                    int exitStatus = p.ExitCode;
                    stdOutput.Close();
                    tempLocalPortData.Clear();

                    if (exitStatus != 0)
                    {
                        string msg = $"netstat failure: {exitStatus}";
                        Logger.LogWarning(msg);

                        // this will be handled by Retry.Do().
                        throw new Exception(msg);
                    }
                    
                    return output;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                Logger.LogWarning($"Handled Exception in GetTcpPortCount:{Environment.NewLine}{e}");

                // This will be handled by Retry.Do().
                throw;
            }
        }

        private int GetLocalPortFromConsoleOutputRow(string portRow)
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
    }
}
