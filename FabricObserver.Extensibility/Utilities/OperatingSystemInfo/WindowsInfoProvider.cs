// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static FabricObserver.Observers.Utilities.NativeMethods;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsInfoProvider : OSInfoProvider
    {
        private const string TcpProtocol = "tcp";
        private const int portDataMaxCacheTimeSeconds = 45;
        private const int dynamicRangeMaxCacheTimeMinutes = 15;
        private readonly bool useNetstat = true;

        // Win32 Impl (iphlpapi.dll pInvoke call, fills this cache. *Does not include BOUND state records*).
        private readonly List<(ushort LocalPort, uint OwningProcessId, MIB_TCP_STATE State)> win32TcpConnInfo = null;

        // Netstat Impl (launches console, calls netstat -qno -p tcp, parses output, fills this cache).
        private readonly ConcurrentDictionary<int, string> netstatOutput = null;
        private readonly object _lock = new object();
        private (int LowPort, int HighPort, int NumberOfPorts) windowsDynamicPortRange = (-1, -1, 0);
        private DateTime LastDynamicRangeCacheUpdate = DateTime.MinValue;
        private DateTime LastCacheUpdate = DateTime.MinValue;

        public WindowsInfoProvider()
        {
            windowsDynamicPortRange = TupleGetDynamicPortRange();

            if (useNetstat)
            {
                netstatOutput = new ConcurrentDictionary<int, string>();
            }
            else
            {
                win32TcpConnInfo = new List<(ushort LocalPort, uint OwningProcessId, MIB_TCP_STATE State)>();
            }
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
                            OSInfoLogger.LogInfo($"Handled ManagementException in GetOSInfoAsync retrieval:{Environment.NewLine}{me.Message}");
                        }
                        catch (Exception e)
                        {
                            OSInfoLogger.LogWarning($"Exception in GetOSInfoAsync:{Environment.NewLine}{e.Message}");
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
            OSInfoLogger.LogWarning("Called GetMaximumConfiguredFileHandlesCount on Windows. This is unsupported. Returning -1.");
            return -1;
        }

        // Not implemented. No Windows support.
        public override int GetTotalAllocatedFileHandlesCount()
        {
            OSInfoLogger.LogWarning("Called GetTotalAllocatedFileHandlesCount on Windows. This is unsupported. Returning -1.");
            return -1;
        }

        public override (long TotalMemoryGb, long MemoryInUseMb, double PercentInUse) TupleGetSystemPhysicalMemoryInfo()
        {
            try
            {
                NativeMethods.MEMORYSTATUSEX memoryInfo = NativeMethods.GetSystemMemoryInfo();
                ulong totalMemoryBytes = memoryInfo.ullTotalPhys;
                ulong availableMemoryBytes = memoryInfo.ullAvailPhys;
                ulong inUse = totalMemoryBytes - availableMemoryBytes;
                double usedPct = memoryInfo.dwMemoryLoad;

                return ((long)totalMemoryBytes / 1024 / 1024 / 1024, (long)inUse / 1024 / 1024, usedPct);
            }
            catch (Win32Exception we)
            {
                OSInfoLogger.LogWarning($"TupleGetMemoryInfo: Failure (native) computing memory data:{Environment.NewLine}{we.Message}");
            }

            return (0, 0, 0);
        }

        public override (long TotalCommitGb, long CommittedInUseMb) TupleGetSystemCommittedMemoryInfo()
        {
            try
            {
                NativeMethods.PerformanceInformation pi = new NativeMethods.PerformanceInformation();
                
                if (!NativeMethods.GetSytemPerformanceInfo(ref pi))
                {
                    OSInfoLogger.LogWarning($"NativeMethods.GetPerformanceInfo failure: {Marshal.GetLastWin32Error()}");
                    return (0, 0);
                }

                long pageSize = pi.PageSize.ToInt64();
                
                // virtual memory, committed
                long commitLimit = pi.CommitLimit.ToInt64() * pageSize;
                long availableCommit = commitLimit - pi.CommitTotal.ToInt64() * pageSize;
                long committed = commitLimit - availableCommit;

                return (commitLimit / 1024 / 1024 / 1024, committed / 1024 / 1024);
            }
            catch (Win32Exception we)
            {
                OSInfoLogger.LogWarning($"TupleGetSystemVirtualMemoryInfo: Failure (native) computing memory data:{Environment.NewLine}{we.Message}");
            }

            return (0, 0);
        }

        public override (int LowPort, int HighPort, int NumberOfPorts) TupleGetDynamicPortRange()
        {
            if (DateTime.UtcNow.Subtract(LastDynamicRangeCacheUpdate) > TimeSpan.FromMinutes(dynamicRangeMaxCacheTimeMinutes))
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow.Subtract(LastDynamicRangeCacheUpdate) > TimeSpan.FromMinutes(dynamicRangeMaxCacheTimeMinutes))
                    {
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

                                process.ErrorDataReceived += (sender, e) =>
                                {
                                    error += e.Data;
                                };

                                process.OutputDataReceived += (sender, e) =>
                                {
                                    if (!string.IsNullOrWhiteSpace(e.Data))
                                    {
                                        output += e.Data + Environment.NewLine;
                                    }
                                };

                                process.StartInfo = ps;

                                if (!process.Start())
                                {
                                    return (-1, -1, 0);
                                }

                                // Start async reads.
                                process.BeginErrorReadLine();
                                process.BeginOutputReadLine();

                                if (process.WaitForExit(60000))
                                {
                                    Match match = Regex.Match(
                                                    output,
                                                    @"Start Port\s+:\s+(?<startPort>\d+).+?Number of Ports\s+:\s+(?<numberOfPorts>\d+)",
                                                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                                    string startPort = match.Groups["startPort"].Value;
                                    string portCount = match.Groups["numberOfPorts"].Value;
                                    int exitStatus = process.ExitCode;

                                    if (exitStatus != 0)
                                    {
                                        OSInfoLogger.LogWarning(
                                            "TupleGetDynamicPortRange: netsh failure. " +
                                            $"Unable to determine dynamic port range (will return (-1, -1)):{Environment.NewLine}{error}");

                                        return (-1, -1, 0);
                                    }

                                    if (int.TryParse(startPort, out int lowPortRange) && int.TryParse(portCount, out int count))
                                    {
                                        int highPortRange = lowPortRange + count;
                                        LastDynamicRangeCacheUpdate = DateTime.UtcNow;

                                        return (lowPortRange, highPortRange, count);
                                    }
                                }
                                else
                                {
                                    OSInfoLogger.LogWarning("netsh call did not complete in time (60s). Killing process. Unable to determine dynamic range.");
                                    process.Kill();
                                }
                            }
                            catch (Exception e) when (
                                             e is ArgumentException ||
                                             e is IOException ||
                                             e is InvalidOperationException ||
                                             e is RegexMatchTimeoutException ||
                                             e is Win32Exception ||
                                             e is SystemException)
                            {
                                OSInfoLogger.LogWarning($"Handled Exception in TupleGetDynamicPortRange (will return (-1, -1, 0)): {e.Message}");
                            }
                        }
                    }
                }
            }

            return windowsDynamicPortRange;
        }

        public override int GetActiveEphemeralPortCount(int processId = -1, string configPath = null)
        {
            int count = 0;

            try
            {
                if (useNetstat)
                {
                    // This involves creating a process, so apply retries.
                    count = Retry.Do(() => GetTcpPortCountNetstat(processId, ephemeral: true), TimeSpan.FromSeconds(3), CancellationToken.None);
                }
                else
                {
                    count = GetTcpPortCountWin32(processId, ephemeral: true);
                }
            }
            catch (AggregateException ae)
            {
                OSInfoLogger.LogWarning($"Failed all retries (3) for GetActiveEphemeralPortCount (will return -1): {ae.Flatten().Message}");
                count = -1;
            }
            catch (Win32Exception we)
            {
                OSInfoLogger.LogWarning($"Failed GetActiveEphemeralPortCount with Win32 error (will return -1):{Environment.NewLine}{we}");
                count = -1;
            }

            return count;
        }

        public override int GetActiveTcpPortCount(int processId = -1, string configPath = null)
        {
            int count;

            try
            {
                if (useNetstat)
                {
                    count = Retry.Do(() => GetTcpPortCountNetstat(processId, ephemeral: false), TimeSpan.FromSeconds(3), CancellationToken.None);
                }
                else
                {
                    count = GetTcpPortCountWin32(processId, ephemeral: false);
                }
            }
            catch (AggregateException ae)
            {
                OSInfoLogger.LogWarning($"Failed all retries (3) for GetActivePortCount (will return -1):{Environment.NewLine}{ae.Flatten().Message}");
                count = -1;
            }
            catch (Win32Exception we)
            {
                OSInfoLogger.LogWarning($"GetActiveTcpPortCount failed with Win32 error (will return -1):{Environment.NewLine}{we}");
                count = -1;
            }

            return count;
        }

        public override double GetActiveEphemeralPortCountPercentage(int processId = -1, string configPath = null)
        {
            double usedPct = 0.0;
            int count = GetActiveEphemeralPortCount(processId);

            // Something went wrong.
            if (count <= 0)
            {
                return usedPct;
            }

            (_, _, int NumberOfPorts) = TupleGetDynamicPortRange();
            int totalEphemeralPorts = NumberOfPorts;

            if (totalEphemeralPorts > 0)
            {
                usedPct = (double)(count * 100) / totalEphemeralPorts;
            }

            return usedPct;
        }

        private int GetTcpPortCountNetstat(int processId = -1, bool ephemeral = false)
        {
            if (DateTime.UtcNow.Subtract(LastCacheUpdate) > TimeSpan.FromSeconds(portDataMaxCacheTimeSeconds))
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow.Subtract(LastCacheUpdate) > TimeSpan.FromSeconds(portDataMaxCacheTimeSeconds))
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
                (lowPortRange, highPortRange, _) = windowsDynamicPortRange;
            }

            foreach (string portRow in netstatOutput.Values)
            {
                if (string.IsNullOrWhiteSpace(portRow))
                {
                    continue;
                }

                var (LocalPort, OwningProcessId) = TupleGetLocalPortPidPairFromNetStatString(portRow);

                if (LocalPort == -1 || OwningProcessId == -1)
                {
                    continue;
                }

                if (processId > 0)
                {
                    if (processId != OwningProcessId)
                    {
                        continue;
                    }

                    // Only add unique pid (if supplied in call) and local port data to list.
                    if (tempLocalPortData.Any(t => t.Pid == OwningProcessId && t.Port == LocalPort))
                    {
                        continue;
                    }
                }
                else
                {
                    if (tempLocalPortData.Any(t => t.Port == LocalPort))
                    {
                        continue;
                    }
                }

                // Ephemeral ports query?
                if (ephemeral && (LocalPort < lowPortRange || LocalPort > highPortRange))
                {
                    continue;
                }

                tempLocalPortData.Add((OwningProcessId, LocalPort));
            }

            int count = tempLocalPortData.Count;
            tempLocalPortData.Clear();
            tempLocalPortData = null;

            return count;
        }

        private int GetTcpPortCountWin32(int processId = -1, bool ephemeral = false)
        {
            if (DateTime.UtcNow.Subtract(LastCacheUpdate) > TimeSpan.FromSeconds(portDataMaxCacheTimeSeconds))
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow.Subtract(LastCacheUpdate) > TimeSpan.FromSeconds(portDataMaxCacheTimeSeconds))
                    {
                        UpdateWin32TcpConnectionCache();
                        windowsDynamicPortRange = TupleGetDynamicPortRange();
                    }
                }
            }

            var tempLocalPortData = new List<(int Port, uint Pid)>();
            string findStrProc = string.Empty;
            string error = string.Empty;
            (int lowPortRange, int highPortRange) = (-1, -1);

            if (ephemeral)
            {
                (lowPortRange, highPortRange, _) = windowsDynamicPortRange;
            }

            foreach (var (LocalPort, OwningProcessId, State) in win32TcpConnInfo)
            {
                int localPort = LocalPort;
                uint pid = OwningProcessId;

                if (processId > 0)
                {
                    if (processId != pid)
                    {
                        continue;
                    }

                    // Only add unique pid (if supplied in call) and local port data to list.
                    if (tempLocalPortData.Any(t => t.Port == localPort && t.Pid == pid))
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

                tempLocalPortData.Add((localPort, pid));
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

                int iKey = 0;

                // Capture any error information from netstat.
                process.ErrorDataReceived += (sender, e) => { error += e.Data; };

                // Fill the dictionary with netstat output lines.
                process.OutputDataReceived += (sender, outputLine) =>
                {
                    ++iKey;

                    if (outputLine.Data != null && outputLine.Data.Contains(":"))
                    {
                        if (!string.IsNullOrWhiteSpace(outputLine.Data))
                        {
                            netstatOutput.TryAdd(iKey, outputLine.Data.Trim());
                        }
                    }
                };

                process.StartInfo = ps;

                if (!process.Start())
                {
                    OSInfoLogger.LogWarning($"Unable to start process: {ps.Arguments}");
                    return;
                }

                // Start asynchronous read operations.
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                if (process.WaitForExit(120000))
                {
                    int exitStatus = process.ExitCode;

                    if (exitStatus == 0)
                    {
                        LastCacheUpdate = DateTime.UtcNow;
                        return;
                    }

                    // There was an error associated with the non-zero exit code.
                    string msg = $"RefreshNetstatData: netstat -qno -p {TcpProtocol} exited with {exitStatus}: {error}";
                    OSInfoLogger.LogWarning(msg);

                    // Handled by Retry.Do.
                    throw new RetryableException(msg);
                }
                else
                {
                    OSInfoLogger.LogWarning("netstat call did not complete in time (120s). Killing process. Unable to complete connection data cache refresh.");
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is NotSupportedException || ex is SystemException)
            {
                OSInfoLogger.LogWarning($"Unable to get netstat information: {ex.Message}");
            }
            finally
            {
                process?.Dispose();
                process = null;
            }
        }

        private void UpdateWin32TcpConnectionCache()
        {
            win32TcpConnInfo.Clear();
            win32TcpConnInfo.AddRange(GetAllTcpConnections());
            LastCacheUpdate = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets local port number and associated process ID from netstat standard output line.
        /// </summary>
        /// <param name="netstatOutputLine">Single line (row) of text from netstat output.</param>
        /// <returns>Integer Tuple: (port, pid)</returns>
        private static (int LocalPort, int OwningProcessId) TupleGetLocalPortPidPairFromNetStatString(string netstatOutputLine)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(netstatOutputLine))
                {
                    return (-1, -1);
                }

                var tcpPortInfo = new TcpPortInfo(netstatOutputLine);
                return (tcpPortInfo.LocalPort, tcpPortInfo.OwningProcessId);
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException)
            {
                OSInfoLogger.LogWarning($"Failed to parse supplied netstat output row ({netstatOutputLine}): {e.Message}");
                return (-1, -1);
            }
        }
    }
}