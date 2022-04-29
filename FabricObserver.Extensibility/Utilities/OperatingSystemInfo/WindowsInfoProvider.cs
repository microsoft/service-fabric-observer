// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Concurrent;
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
    public class WindowsInfoProvider : OSInfoProvider
    {
        private const string TcpProtocol = "tcp";
        private (int, int) windowsDynamicPortRange = (-1, -1);
        private readonly ConcurrentDictionary<int, string> netstatOutput;
        private const int netstatOutputMaxCacheTimeSeconds = 15;
        private DateTime LastCacheUpdate = DateTime.MinValue;
        private readonly object _lock =  new object();

        public WindowsInfoProvider()
        {
            windowsDynamicPortRange = TupleGetDynamicPortRange();
            netstatOutput = new ConcurrentDictionary<int, string>();
        }

        public override (int LowPort, int HighPort) TupleGetDynamicPortRange()
        {
            if (windowsDynamicPortRange != (-1, -1))
            {
                return windowsDynamicPortRange;
            }
            
            using (var process = new Process())
            {
                try
                {
                    string error = string.Empty, output = string.Empty;

                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netsh int ipv4 show dynamicportrange {TcpProtocol} | find /i \"port\"",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    process.ErrorDataReceived += (sender, e) => { error += e.Data; };
                    process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { output += e.Data + Environment.NewLine; } };
                    process.StartInfo = ps;

                    if (!process.Start())
                    {
                        return (-1, -1);
                    }

                    // Start async reads.
                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();
                    process.WaitForExit();

                    Match match = Regex.Match(
                                        output,
                                        @"Start Port\s+:\s+(?<startPort>\d+).+?Number of Ports\s+:\s+(?<numberOfPorts>\d+)",
                                        RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    string startPort = match.Groups["startPort"].Value;
                    string portCount = match.Groups["numberOfPorts"].Value;
                    int exitStatus = process.ExitCode;

                    if (exitStatus != 0)
                    {
                        Logger.LogWarning(
                            "TupleGetDynamicPortRange: netsh failure. " +
                            $"Unable to determine dynamic port range (will return (-1, -1)):{Environment.NewLine}{error}");

                        return (-1, -1);
                    }

                    if (int.TryParse(startPort, out int lowPortRange) && int.TryParse(portCount, out int count))
                    {
                        int highPortRange = lowPortRange + count;

                        return (lowPortRange, highPortRange);
                    }
                }
                catch (Exception e) when (
                                 e is ArgumentException ||
                                 e is IOException ||
                                 e is InvalidOperationException ||
                                 e is RegexMatchTimeoutException ||
                                 e is Win32Exception)
                {
                    Logger.LogWarning($"Handled Exception in TupleGetDynamicPortRange (will return (-1, -1)):{Environment.NewLine}{e}");
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
                count = Retry.Do(() => GetTcpPortCount(processId, ephemeral: true), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                Logger.LogWarning($"Failed all retries (3) for GetActiveEphemeralPortCount (will return -1):{Environment.NewLine}{ae}");
                count = -1;
            }

            return count;
        }

        public override double GetActiveEphemeralPortCountPercentage(int processId = -1, ServiceContext context = null)
        {
            double usedPct = 0.0;
            int count = GetActiveEphemeralPortCount(processId, context);

            // Something went wrong.
            if (count <= 0)
            {
                return usedPct;
            }

            (int LowPort, int HighPort) = TupleGetDynamicPortRange();
            int totalEphemeralPorts = HighPort - LowPort;

            if (totalEphemeralPorts > 0)
            {
                usedPct = (double) (count* 100) / totalEphemeralPorts;
            }

            return usedPct;
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
                count = Retry.Do(() => GetTcpPortCount(processId, ephemeral: false), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                Logger.LogWarning($"Failed all retries (3) for GetActivePortCount (will return -1):{Environment.NewLine}{ae}");
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
                win32OsInfo = new ManagementObjectSearcher(
                                    "SELECT Caption,Version,Status,OSLanguage,NumberOfProcesses,FreePhysicalMemory,FreeVirtualMemory," +
                                    "TotalVirtualMemorySize,TotalVisibleMemorySize,InstallDate,LastBootUpTime FROM Win32_OperatingSystem");

                results = win32OsInfo.Get();

                using (ManagementObjectCollection.ManagementObjectEnumerator enumerator = results.GetEnumerator())
                {
                    while (enumerator.MoveNext())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ManagementObject mObj = (ManagementObject)enumerator.Current;

                        try
                        {
                            object captionObj = mObj.Properties["Caption"].Value;
                            object versionObj = mObj.Properties["Version"].Value;
                            object statusObj = mObj.Properties["Status"].Value;
                            object osLanguageObj = mObj.Properties["OSLanguage"].Value;
                            object numProcsObj = mObj.Properties["NumberOfProcesses"].Value;
                            object freePhysicalObj = mObj.Properties["FreePhysicalMemory"].Value;
                            object freeVirtualTotalObj = mObj.Properties["FreeVirtualMemory"].Value;
                            object totalVirtualObj = mObj.Properties["TotalVirtualMemorySize"].Value;
                            object totalVisibleObj = mObj.Properties["TotalVisibleMemorySize"].Value;
                            object installDateObj = mObj.Properties["InstallDate"].Value;
                            object lastBootDateObj = mObj.Properties["LastBootUpTime"].Value;

                            osInfo.Name = captionObj?.ToString();

                            if (int.TryParse(numProcsObj?.ToString(), out int numProcesses))
                            {
                                osInfo.NumberOfProcesses = numProcesses;
                            }
                            else
                            {
                                osInfo.NumberOfProcesses = -1;
                            }

                            osInfo.Status = statusObj?.ToString();
                            osInfo.Language = osLanguageObj?.ToString();
                            osInfo.Version = versionObj?.ToString();
                            osInfo.InstallDate = ManagementDateTimeConverter.ToDateTime(installDateObj?.ToString()).ToUniversalTime().ToString("o");
                            osInfo.LastBootUpTime = ManagementDateTimeConverter.ToDateTime(lastBootDateObj?.ToString()).ToUniversalTime().ToString("o");
                            osInfo.FreePhysicalMemoryKB = ulong.TryParse(freePhysicalObj?.ToString(), out ulong freePhysical) ? freePhysical : 0;
                            osInfo.FreeVirtualMemoryKB = ulong.TryParse(freeVirtualTotalObj?.ToString(), out ulong freeVirtual) ? freeVirtual : 0;
                            osInfo.TotalVirtualMemorySizeKB = ulong.TryParse(totalVirtualObj?.ToString(), out ulong totalVirtual) ? totalVirtual : 0;
                            osInfo.TotalVisibleMemorySizeKB = ulong.TryParse(totalVisibleObj?.ToString(), out ulong totalVisible) ? totalVisible : 0;  
                        }
                        catch (ManagementException me)
                        {
                            Logger.LogInfo($"Handled ManagementException in GetOSInfoAsync retrieval:{Environment.NewLine}{me}");
                        }
                        catch (Exception e)
                        {
                            Logger.LogInfo($"Bug? => Exception in GetOSInfoAsync:{Environment.NewLine}{e}");
                        }
                        finally
                        {
                            mObj?.Dispose();
                            mObj = null;
                        }
                    }
                }
            }
            finally
            {
                results?.Dispose();
                results = null;
                win32OsInfo?.Dispose();
                win32OsInfo = null;
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

        private int GetTcpPortCount(int processId = -1, bool ephemeral = false)
        {
            if (DateTime.UtcNow.Subtract(LastCacheUpdate) > TimeSpan.FromSeconds(netstatOutputMaxCacheTimeSeconds))
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow.Subtract(LastCacheUpdate) > TimeSpan.FromSeconds(netstatOutputMaxCacheTimeSeconds))
                    {
                        RefreshNetstatData();
                        windowsDynamicPortRange = TupleGetDynamicPortRange();
                    }
                }
            }

            var tempLocalPortData = new List<(int Pid, int Port)>();
            string findStrProc = string.Empty;
            string error = string.Empty;
            (int lowPortRange, int highPortRange) = (-1, -1);

            if (ephemeral)
            {
                (lowPortRange, highPortRange) = windowsDynamicPortRange;
            }
     
            foreach (string portRow in netstatOutput.Values)
            {
                if (string.IsNullOrWhiteSpace(portRow))
                {
                    continue;
                }

                (int localPort, int pid) = TupleGetLocalPortPidPairFromNetStatString(portRow);

                if (localPort == -1 || pid == -1)
                {
                    continue;
                }

                if (processId > 0)
                {
                    if (processId != pid)
                    {
                        continue;
                    }

                    // Only add unique pid (if supplied in call) and local port data to list.
                    if (tempLocalPortData.Any(t => t.Pid == pid && t.Port == localPort))
                    {
                        continue;
                    }
                }
                else
                {
                    if (tempLocalPortData.Any(t => t.Port == localPort))
                    {
                        continue;
                    }
                }

                // Ephemeral ports query?
                if (ephemeral && (localPort < lowPortRange || localPort > highPortRange))
                {
                    continue;
                }

                tempLocalPortData.Add((pid, localPort));
            }

            int count = tempLocalPortData.Count;
            tempLocalPortData.Clear();
            tempLocalPortData = null;

            return count;
        }

        private void RefreshNetstatData()
        {
            Process process = null;

            try
            {
                netstatOutput.Clear();

                string error = null;
                process = new Process();
                var ps = new ProcessStartInfo
                {
                    Arguments = $"/c netstat -qno -p {TcpProtocol}",
                    FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                int i = 0;

                // Capture any error information from netstat.
                process.ErrorDataReceived += (sender, e) => { error += e.Data; };

                // Fill the dictionary with netstat output lines.
                process.OutputDataReceived += (sender, outputLine) => { ++i; if (!string.IsNullOrWhiteSpace(outputLine.Data)) { netstatOutput.TryAdd(i, outputLine.Data.Trim()); } };
                process.StartInfo = ps;

                if (!process.Start())
                {
                    Logger.LogWarning($"Unable to start process: {ps.Arguments}");
                    return;
                }

                // Start asynchronous read operations.
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                process.WaitForExit();
                int exitStatus = process.ExitCode;

                if (exitStatus == 0)
                {
                    LastCacheUpdate = DateTime.UtcNow;
                    return;
                }

                // There was an error associated with the non-zero exit code.
                string msg = $"RefreshNetstatData: netstat -qno -p {TcpProtocol} exited with {exitStatus}: {error}";
                Logger.LogWarning(msg);

                // Handled by Retry.Do.
                throw new RetryableException(msg);
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is NotSupportedException || ex is SystemException)
            {
                Logger.LogWarning($"Unable to get netstat information:{Environment.NewLine}{ex}");
            }
            finally
            {
                process?.Dispose();
                process = null;
            }
        }

        /// <summary>
        /// Gets local port number and associated process ID from netstat standard output line.
        /// </summary>
        /// <param name="netstatOutputLine">Single line (row) of text from netstat output.</param>
        /// <returns>Integer Tuple: (port, pid)</returns>
        private static (int, int) TupleGetLocalPortPidPairFromNetStatString(string netstatOutputLine)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(netstatOutputLine))
                {
                    return (-1, -1);
                }

                string[] stats = netstatOutputLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (stats.Length != 5 || !int.TryParse(stats[4], out int pid))
                {
                    return (-1, -1);
                }

                string localIpAndPort = stats[1];

                if (string.IsNullOrWhiteSpace(localIpAndPort) || !localIpAndPort.Contains(":"))
                {
                    return (-1, -1);
                }

                // We *only* care about the local IP.
                string localPort = localIpAndPort.Split(':')[1];

                if (!int.TryParse(localPort, out int port))
                {
                    return (-1, -1);
                }

                return (port, pid);
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException)
            {
                return (-1, -1);
            }
        }

        public override (long TotalMemoryGb, long MemoryInUseMb, double PercentInUse) TupleGetSystemMemoryInfo()
        {
            try
            {
                NativeMethods.MEMORYSTATUSEX memoryInfo = NativeMethods.GetSystemMemoryInfo();
                ulong totalMemoryBytes = memoryInfo.ullTotalPhys;
                ulong availableMemoryBytes = memoryInfo.ullAvailPhys;
                ulong inUse = totalMemoryBytes - availableMemoryBytes;
                float used = (float)inUse / totalMemoryBytes;
                float usedPct = used * 100;

                return ((long)totalMemoryBytes / 1024 / 1024 / 1024, (long)inUse / 1024 / 1024, usedPct);
            }
            catch (Win32Exception we)
            {
                Logger.LogWarning($"TupleGetMemoryInfo: Failure (native) computing memory data:{Environment.NewLine}{we}");
            }

            return (0, 0, 0);
        }
    }
}
