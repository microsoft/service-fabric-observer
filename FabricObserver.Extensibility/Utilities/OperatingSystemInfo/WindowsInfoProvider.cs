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

                    p.WaitForExit();

                    string startPort = match.Groups["startPort"].Value;
                    string portCount = match.Groups["numberOfPorts"].Value;
                    int exitStatus = p.ExitCode;
                    stdOutput.Close();

                    if (exitStatus != 0)
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
                count = Retry.Do(() => GetEphemeralPortCount(processId), TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                Logger.LogWarning($"Retry failed for GetActiveEphemeralPortCount:{Environment.NewLine}{ae}");
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
                count = Retry.Do(() => GetTcpPortCount(processId), TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                Logger.LogWarning($"Retry failed for GetActivePortCount:{Environment.NewLine}{ae}");
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
            List<(int Pid, int Port)> tempLocalPortData = new List<(int Pid, int Port)>();
            string findStrProc = string.Empty;
            string error = string.Empty;

            if (processId > 0)
            {
                findStrProc = $" | find \"{processId}\"";
            }

            using (var p = new Process())
            {
                var ps = new ProcessStartInfo
                {
                    Arguments = $"/c netstat -qno -p {TcpProtocol}{findStrProc}",
                    FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                // Capture any error information from netstat.
                p.ErrorDataReceived += (sender, e) => { error += e.Data; };
                p.StartInfo = ps;
                _ = p.Start();
                var stdOutput = p.StandardOutput;

                // Start asynchronous read operation on error stream.  
                p.BeginErrorReadLine();

                (int lowPortRange, int highPortRange) = TupleGetDynamicPortRange();
                string portRow;
                while ((portRow = stdOutput.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(portRow))
                    {
                        continue;
                    }

                    int localPort = GetLocalPortFromNetstatOutputLine(portRow);

                    if (localPort == -1)
                    {
                        continue;
                    }

                    // Only add unique pid and port data to list. This would filter out cases where BOUND and ESTABLISHED states have the same Pid and Port, which
                    // would artificially increase the count of ports that FO computes.
                    if (processId > 0)
                    {
                        /* A pid could be a subset of a port number, so make sure that we only match pid. */

                        List<string> stats = portRow.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries).ToList();

                        if (stats.Count != 5 || !int.TryParse(stats[4], out int pidPart))
                        {
                            continue;
                        }

                        if (processId != pidPart)
                        {
                            continue;
                        }

                        if (tempLocalPortData.Any(t => t.Pid == processId && t.Port == localPort))
                        {
                            continue;
                        }

                        if (localPort >= lowPortRange && localPort <= highPortRange)
                        {
                            tempLocalPortData.Add((processId, localPort));
                        }
                    }
                    else
                    {
                        if (tempLocalPortData.Any(t => t.Port == localPort))
                        {
                            continue;
                        }

                        if (localPort >= lowPortRange && localPort <= highPortRange)
                        {
                            tempLocalPortData.Add((processId, localPort));
                        }
                    }
                }

                p.WaitForExit();

                int exitStatus = p.ExitCode;
                int count = tempLocalPortData.Count;
                tempLocalPortData.Clear();
                stdOutput.Close();

                if (exitStatus == 0)
                {
                    return count;
                }

                string msg = $"netstat failure: ({exitStatus}): {error}";
                Logger.LogWarning(msg);

                // this will be handled by Retry.Do().
                throw new Exception(msg);
            }
        }

        private int GetTcpPortCount(int processId = -1)
        {
            string protoParam = "-p " + TcpProtocol;
            string findStrProc = string.Empty;
            string error = string.Empty;
            List<(int Pid, int Port)> tempLocalPortData = new List<(int Pid, int Port)>();

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
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                // Capture any error information from netstat.
                p.ErrorDataReceived += (sender, e) => { error += e.Data; };
                p.StartInfo = ps;
                _ = p.Start();
                var stdOutput = p.StandardOutput;

                // Start asynchronous read operation on error stream.  
                p.BeginErrorReadLine();

                string portRow;
                while ((portRow = stdOutput.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(portRow))
                    {
                        continue;
                    }

                    int localPort = GetLocalPortFromNetstatOutputLine(portRow);

                    if (localPort == -1)
                    {
                        continue;
                    }

                    // Only add unique pid (if supplied in call) and local port data to list.
                    if (processId > 0)
                    {
                        /* A pid could be a subset of a port number, so make sure that we only match pid. */

                        List<string> stats = portRow.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

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
                        if (tempLocalPortData.All(t => t.Port != localPort))
                        {
                            tempLocalPortData.Add((processId, localPort));
                        }
                    }
                }

                p.WaitForExit();

                int exitStatus = p.ExitCode;
                int count = tempLocalPortData.Count;
                tempLocalPortData.Clear();
                stdOutput.Close();

                if (exitStatus == 0)
                {
                    return count;
                }

                string msg = $"netstat failure: ({exitStatus}): {error}";
                Logger.LogWarning(msg);

                // this will be handled by Retry.Do().
                throw new Exception(msg);
            }
        }

        /// <summary>
        /// Gets local port number from netstat standard output line.
        /// </summary>
        /// <param name="outputLine">Single line (row) of text from netstat output.</param>
        /// <returns>Local port number</returns>
        private static int GetLocalPortFromNetstatOutputLine(string outputLine)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputLine))
                {
                    return -1;
                }

                List<string> stats = outputLine.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries).ToList();

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
            catch (Exception e) when (e is ArgumentException || e is FormatException)
            {
                return -1;
            }
        }
    }
}
