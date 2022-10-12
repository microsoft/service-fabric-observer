// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
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

namespace FabricObserver.Observers.Utilities
{
    public class WindowsInfoProvider : OSInfoProvider
    {
        private const string TcpProtocol = "tcp";
        private (int LowPort, int HighPort, int NumberOfPorts) windowsDynamicPortRange = (-1, -1, 0);
        private readonly List<NativeMethods.MIB_TCPROW_OWNER_PID> allTcpConnInfo;
        private const int tcpPortOutputMaxCacheTimeSeconds = 15;
        private const int dynamicRangeMaxCacheTimeMinutes = 15;
        private DateTime LastPortCacheUpdate = DateTime.MinValue;
        private DateTime LastDynamicRangeCacheUpdate = DateTime.MinValue;
        private readonly object _lock = new object();
        
        public WindowsInfoProvider()
        {
            windowsDynamicPortRange = TupleGetDynamicPortRange();
            allTcpConnInfo = new List<NativeMethods.MIB_TCPROW_OWNER_PID>();
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
            return -1;
        }

        // Not implemented. No Windows support.
        public override int GetTotalAllocatedFileHandlesCount()
        {
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
            if (DateTime.UtcNow.Subtract(LastDynamicRangeCacheUpdate) < TimeSpan.FromMinutes(dynamicRangeMaxCacheTimeMinutes))
            {
                return windowsDynamicPortRange;
            }

            lock (_lock)
            {
                if (DateTime.UtcNow.Subtract(LastDynamicRangeCacheUpdate) < TimeSpan.FromMinutes(dynamicRangeMaxCacheTimeMinutes))
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
                                int highPortRange = lowPortRange + count - 1;
                                LastDynamicRangeCacheUpdate = DateTime.UtcNow;

                                return (lowPortRange, highPortRange, count);
                            }
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
                        OSInfoLogger.LogWarning($"Handled Exception in TupleGetDynamicPortRange (will return (-1, -1)):{Environment.NewLine}{e.Message}");
                    }
                }
            }

            return (-1, -1, 0);
        }

        public override int GetActiveEphemeralPortCount(int processId = -1, string configPath = null)
        {
            int count;

            try
            {
                count = Retry.Do(() => GetTcpPortCount(processId, ephemeral: true), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                OSInfoLogger.LogWarning($"Failed all retries (3) for GetActiveEphemeralPortCount (will return -1):{Environment.NewLine}{ae.Flatten().Message}");
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
                usedPct = (double) (count* 100) / totalEphemeralPorts;
            }

            return usedPct;
        }

        public override int GetActiveTcpPortCount(int processId = -1, string configPath = null)
        {
            int count;

            try
            {
                count = Retry.Do(() => GetTcpPortCount(processId, ephemeral: false), TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException ae)
            {
                OSInfoLogger.LogWarning($"Failed all retries (3) for GetActivePortCount (will return -1):{Environment.NewLine}{ae.Flatten().Message}");
                count = -1;
            }

            return count;
        }

        private void UpdateTcpConnectionsCache()
        {
            if (DateTime.UtcNow.Subtract(LastPortCacheUpdate) < TimeSpan.FromSeconds(tcpPortOutputMaxCacheTimeSeconds))
            {
                return;
            }

            lock (_lock)
            {
                if (DateTime.UtcNow.Subtract(LastPortCacheUpdate) < TimeSpan.FromSeconds(tcpPortOutputMaxCacheTimeSeconds))
                {
                    return;
                }

                allTcpConnInfo.Clear();
                allTcpConnInfo.AddRange(NativeMethods.GetAllTCPV4Connections());
                LastPortCacheUpdate = DateTime.UtcNow;

                // Wait and retry in case of some internal failure (which always includes an empty List of mib info - Win32 exception is logged..).
                Thread.Sleep(1000);

                // This would only the case if there was some failure. So, try one more time.
                if (allTcpConnInfo.Count == 0)
                {
                    allTcpConnInfo.AddRange(NativeMethods.GetAllTCPV4Connections());
                    LastPortCacheUpdate = DateTime.UtcNow;
                }
            }
        }

        private int GetTcpPortCount(int processId = -1, bool ephemeral = false)
        {
            UpdateTcpConnectionsCache();

            var tempLocalPortData = new List<(int Port, uint Pid)>();
            string findStrProc = string.Empty;
            string error = string.Empty;
            (int lowPortRange, int highPortRange, int numberOfPorts) = (-1, -1, 0);

            if (ephemeral)
            {
                (lowPortRange, highPortRange, numberOfPorts) = windowsDynamicPortRange;
            }

            foreach (var conn in allTcpConnInfo)
            {
                int localPort = conn.LocalPort;
                uint pid = conn.ProcessId;

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
    }
}
