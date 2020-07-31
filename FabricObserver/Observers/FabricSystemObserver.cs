// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Fabric.Health;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    // ***FabricSystemObserver is disabled by default.***
    // This observer monitors all Fabric system service processes across various resource usage metrics.
    // It will signal Warnings or Errors based on settings supplied in Settings.xml.
    // If the FabricObserverWebApi service is deployed: The output (a local file) is created for and used by the API service (http://localhost:5000/api/ObserverManager).
    // SF Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    // You should not enable this observer unless you have spent some time analyzing how your services impact SF system services (like Fabric.exe)
    // If Fabric.exe is running at 70% CPU due to your service code, and this is normal for your workloads, then do not warn at this threshold.
    // As with all observers, you should first understand what are the happy (normal) states across resource usage before you set thresholds for the unhappy states.
    public class FabricSystemObserver : ObserverBase
    {
        private readonly List<string> processWatchList;
        private Stopwatch stopwatch;
        private bool disposed;

        // Health Report data container - For use in analysis to deterWarne health state.
        private List<FabricResourceUsageData<int>> allCpuData;
        private List<FabricResourceUsageData<float>> allMemData;

        // Windows only. (EventLog).
        private List<EventRecord> evtRecordList;
        private bool monitorWinEventLog;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricSystemObserver"/> class.
        /// </summary>
        public FabricSystemObserver()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                this.processWatchList = new List<string>
                {
                    "Fabric",
                    "FabricCAS.dll",
                    "FabricDCA.dll",
                    "FabricDnsService",
                    "FabricFAS.dll",
                    "FabricGateway.exe",
                    "FabricHost",
                    "FabricIS.dll",
                    "FabricRM",
                    "FabricUS",
                };
            }
            else
            {
                // Windows
                this.processWatchList = new List<string>
                {
                    "Fabric",
                    "FabricApplicationGateway",
                    "FabricCAS",
                    "FabricDCA",
                    "FabricDnsService",
                    "FabricFAS",
                    "FabricGateway",
                    "FabricHost",
                    "FabricIS",
                    "FabricRM",
                    "FabricUS",
                };
            }
        }

        public int CpuErrorUsageThresholdPct { get; set; }

        public int MemErrorUsageThresholdMb { get; set; }

        public int TotalActivePortCount { get; set; }

        public int TotalActiveEphemeralPortCount { get; set; }

        public int PortCountWarning { get; set; }

        public int PortCountError { get; set; }

        public int CpuWarnUsageThresholdPct { get; set; }

        public int MemWarnUsageThresholdMb { get; set; }

        public string ErrorOrWarningKind { get; set; } = null;

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            this.Token = token;

            if (this.Token.IsCancellationRequested)
            {
                return;
            }

            this.Initialize();

            try
            {
                foreach (var procName in this.processWatchList)
                {
                    this.Token.ThrowIfCancellationRequested();
                    string dotnet = string.Empty;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && procName.EndsWith(".dll"))
                    {
                        dotnet = "dotnet ";
                    }

                    this.GetProcessInfo($"{dotnet}{procName}");
                }
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    this.WriteToLogWithLevel(
                        this.ObserverName,
                        "Unhandled exception in ObserveAsync. Failed to observe CPU and Memory usage of " + string.Join(",", this.processWatchList) + ": " + e,
                        LogLevel.Error);
                }

                throw;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && ObserverManager.ObserverWebAppDeployed
                && this.monitorWinEventLog)
            {
                this.ReadServiceFabricWindowsEventLog();
            }

            // Set TTL.
            this.stopwatch.Stop();
            this.RunDuration = this.stopwatch.Elapsed;
            this.stopwatch.Reset();

            await this.ReportAsync(token).ConfigureAwait(true);

            // No need to keep these objects in memory across healthy iterations.
            // Clear out/null list objects.
            this.allCpuData = null;
            this.allMemData = null;
            this.LastRunDateTime = DateTime.Now;
        }

        /// <inheritdoc/>
        public override Task ReportAsync(CancellationToken token)
        {
            this.Token.ThrowIfCancellationRequested();
            var timeToLiveWarning = this.SetHealthReportTimeToLive();
            var portInformationReport = new HealthReport
            {
                Observer = this.ObserverName,
                NodeName = this.NodeName,
                HealthMessage = $"Number of ports in use by Fabric services: {this.TotalActivePortCount}\n" +
                                $"Number of ephemeral ports in use by Fabric services: {this.TotalActiveEphemeralPortCount}\n" +
                                (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ?
                                    $"Fabric mem use: {this.allMemData.Where(x => x.Id == "Fabric")?.FirstOrDefault()?.AverageDataValue}\n" +
                                    $"FabricGateway mem use: {this.allMemData.Where(x => x.Id == "FabricGateway.exe")?.FirstOrDefault()?.AverageDataValue}\n" +
                                    $"FabricHost mem use: {this.allMemData.Where(x => x.Id == "FabricHost")?.FirstOrDefault()?.AverageDataValue}\n" : string.Empty),

                State = HealthState.Ok,
                HealthReportTimeToLive = timeToLiveWarning,
            };

            // TODO: Report on port count based on thresholds PortCountWarning/Error.
            this.HealthReporter.ReportHealthToServiceFabric(portInformationReport);

            // DEBUG
            this.WriteToLogWithLevel(
                this.ObserverName,
                $"Number of ports in use by Fabric services: {this.TotalActivePortCount}\nNumber of ephemeral ports in use by Fabric services: {this.TotalActiveEphemeralPortCount}\n",
                LogLevel.Information);

            // Reset ports counters.
            this.TotalActivePortCount = 0;
            this.TotalActiveEphemeralPortCount = 0;

            // CPU
            this.ProcessResourceDataList(
                this.allCpuData,
                this.CpuErrorUsageThresholdPct,
                this.CpuWarnUsageThresholdPct);

            // Memory
            this.ProcessResourceDataList(
                this.allMemData,
                this.MemErrorUsageThresholdMb,
                this.MemWarnUsageThresholdMb);

            // Windows Event Log
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && ObserverManager.ObserverWebAppDeployed
                && this.monitorWinEventLog)
            {
                // SF Eventlog Errors?
                // Write this out to a new file, for use by the web front end log viewer.
                // Format = HTML.
                int count = this.evtRecordList.Count();
                var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "EventVwrErrors.txt");

                // Remove existing file.
                if (File.Exists(logPath))
                {
                    try
                    {
                        File.Delete(logPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                if (count >= 10)
                {
                    var sb = new StringBuilder();

                    _ = sb.AppendLine("<br/><div><strong>" +
                                  "<a href='javascript:toggle(\"evtContainer\")'>" +
                                  "<div id=\"plus\" style=\"display: inline; font-size: 25px;\">+</div> " + count +
                                  " Error Events in ServiceFabric and System</a> " +
                                  "Event logs</strong>.<br/></div>");

                    _ = sb.AppendLine("<div id='evtContainer' style=\"display: none;\">");

                    foreach (var evt in this.evtRecordList.Distinct())
                    {
                        token.ThrowIfCancellationRequested();

                        try
                        {
                            // Access event properties:
                            _ = sb.AppendLine("<div>" + evt.LogName + "</div>");
                            _ = sb.AppendLine("<div>" + evt.LevelDisplayName + "</div>");
                            if (evt.TimeCreated.HasValue)
                            {
                                _ = sb.AppendLine("<div>" + evt.TimeCreated.Value.ToShortDateString() + "</div>");
                            }

                            foreach (var prop in evt.Properties)
                            {
                                if (prop.Value != null && Convert.ToString(prop.Value).Length > 0)
                                {
                                    _ = sb.AppendLine("<div>" + prop.Value + "</div>");
                                }
                            }
                        }
                        catch (EventLogException)
                        {
                        }
                    }

                    _ = sb.AppendLine("</div>");

                    _ = this.ObserverLogger.TryWriteLogFile(logPath, sb.ToString());
                    _ = sb.Clear();
                }

                // Clean up.
                if (count > 0)
                {
                    this.evtRecordList.Clear();
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// ReadServiceFabricWindowsEventLog().
        /// </summary>
        public void ReadServiceFabricWindowsEventLog()
        {
            string sfOperationalLogSource = "Microsoft-ServiceFabric/Operational";
            string sfAdminLogSource = "Microsoft-ServiceFabric/Admin";
            string systemLogSource = "System";
            string sfLeaseAdminLogSource = "Microsoft-ServiceFabric-Lease/Admin";
            string sfLeaseOperationalLogSource = "Microsoft-ServiceFabric-Lease/Operational";

            var range2Days = DateTime.UtcNow.AddDays(-1);
            var format = range2Days.ToString(
                         "yyyy-MM-ddTHH:mm:ss.fffffff00K",
                         CultureInfo.InvariantCulture);
            var datexQuery = string.Format(
                             "*[System/TimeCreated/@SystemTime >='{0}']",
                             format);

            // Critical and Errors only.
            string xQuery = "*[System/Level <= 2] and " + datexQuery;

            // SF Admin Event Store.
            var evtLogQuery = new EventLogQuery(sfAdminLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }

            // SF Operational Event Store.
            evtLogQuery = new EventLogQuery(sfOperationalLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }

            // SF Lease Admin Event Store.
            evtLogQuery = new EventLogQuery(sfLeaseAdminLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }

            // SF Lease Operational Event Store.
            evtLogQuery = new EventLogQuery(sfLeaseOperationalLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }

            // System Event Store.
            evtLogQuery = new EventLogQuery(systemLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent();
                     eventInstance != null;
                     eventInstance = evtLogReader.ReadEvent())
                {
                    this.Token.ThrowIfCancellationRequested();
                    this.evtRecordList.Add(eventInstance);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }

            if (disposing)
            {
                this.disposed = true;
            }
        }

        private static Process[] GetDotnetProcessesByFirstArgument(string argument)
        {
            List<Process> result = new List<Process>();
            Process[] processes = Process.GetProcessesByName("dotnet");
            for (int i = 0; i < processes.Length; ++i)
            {
                Process p = processes[i];
                try
                {
                    string cmdline = File.ReadAllText($"/proc/{p.Id}/cmdline");
                    string[] parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 1 && string.Equals(argument, parts[1], StringComparison.Ordinal))
                    {
                        result.Add(p);
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // It is possible that the process already exited.
                }
            }

            return result.ToArray();
        }

        private void Initialize()
        {
            if (this.stopwatch == null)
            {
                this.stopwatch = new Stopwatch();
            }

            this.Token.ThrowIfCancellationRequested();

            this.stopwatch.Start();

            this.SetThresholdsFromConfiguration();

            if (this.allMemData == null)
            {
                this.allMemData = new List<FabricResourceUsageData<float>>(this.processWatchList.Count);

                foreach (var proc in this.processWatchList)
                {
                    this.allMemData.Add(
                        new FabricResourceUsageData<float>(
                            ErrorWarningProperty.TotalMemoryConsumptionMb,
                            proc,
                            this.DataCapacity,
                            this.UseCircularBuffer));
                }
            }

            if (this.allCpuData == null)
            {
                this.allCpuData = new List<FabricResourceUsageData<int>>(this.processWatchList.Count);

                foreach (var proc in this.processWatchList)
                {
                    this.allCpuData.Add(
                        new FabricResourceUsageData<int>(
                            ErrorWarningProperty.TotalMemoryConsumptionMb,
                            proc,
                            this.DataCapacity,
                            this.UseCircularBuffer));
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && this.monitorWinEventLog)
            {
                this.evtRecordList = new List<EventRecord>();
            }
        }

        private void SetThresholdsFromConfiguration()
        {
            /* Error thresholds */

            this.Token.ThrowIfCancellationRequested();

            var cpuError = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorCpu);

            if (!string.IsNullOrEmpty(cpuError))
            {
                _ = int.TryParse(cpuError, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverErrorCpu}.");
                }

                this.CpuErrorUsageThresholdPct = threshold;
            }

            var memError = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorMemory);

            if (!string.IsNullOrEmpty(memError))
            {
                _ = int.TryParse(memError, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverErrorMemory}.");
                }

                this.MemErrorUsageThresholdMb = threshold;
            }

            var percentErrorUnhealthyNodes = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.FabricSystemObserverErrorPercentUnhealthyNodes);

            /* Warning thresholds */

            this.Token.ThrowIfCancellationRequested();

            var cpuWarn = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnCpu);

            if (!string.IsNullOrEmpty(cpuWarn))
            {
                _ = int.TryParse(cpuWarn, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverWarnCpu}.");
                }

                this.CpuWarnUsageThresholdPct = threshold;
            }

            var memWarn = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnMemory);

            if (!string.IsNullOrEmpty(memWarn))
            {
                _ = int.TryParse(memWarn, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} MB is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverWarnMemory}.");
                }

                this.MemWarnUsageThresholdMb = threshold;
            }

            var percentWarnUnhealthyNodes = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.FabricSystemObserverWarnPercentUnhealthyNodes);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            // Monitor Windows event log for SF and System Error/Critical events?
            // This can be noisy. Use wisely.
            var watchEvtLog = this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.FabricSystemObserverMonitorWindowsEventLog);

            if (!string.IsNullOrEmpty(watchEvtLog) && bool.TryParse(watchEvtLog, out bool watchEl))
            {
                this.monitorWinEventLog = watchEl;
            }
        }

        private void GetProcessInfo(string procName)
        {
            // This is to support differences between Linux and Windows dotnet process naming pattern.
            // Default value is what Windows expects for proc name. In linux, the procname is an argument (typically) dotnet command.
            string dotnetArg = procName;
            Process[] processes = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && procName.Contains("dotnet "))
            {
                dotnetArg = $"{procName.Replace("dotnet ", string.Empty)}";
                processes = GetDotnetProcessesByFirstArgument(dotnetArg);
            }
            else
            {
                processes = Process.GetProcessesByName(procName);
            }

            if (processes.Length == 0)
            {
                return;
            }

            Stopwatch timer = new Stopwatch();

            foreach (var process in processes)
            {
                try
                {
                    this.Token.ThrowIfCancellationRequested();

                    // ports in use by Fabric services.
                    this.TotalActivePortCount += OperatingSystemInfoProvider.Instance.GetActivePortCount(process.Id);
                    this.TotalActiveEphemeralPortCount += OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount(process.Id);

                    TimeSpan duration = TimeSpan.FromSeconds(15);

                    if (this.MonitorDuration > TimeSpan.MinValue)
                    {
                        duration = this.MonitorDuration;
                    }

                    // Warm up the counters.
                    CpuUsage cpuUsage = new CpuUsage();
                    _ = cpuUsage.GetCpuUsageProcess(process);
                    _ = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(process.Id);

                    timer.Start();

                    while (!process.HasExited && timer.Elapsed <= duration)
                    {
                        this.Token.ThrowIfCancellationRequested();

                        try
                        {
                            // CPU Time for service process.
                            int cpu = (int)cpuUsage.GetCpuUsageProcess(process);
                            this.allCpuData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(cpu);

                            // Private Working Set for service process.
                            float mem = ProcessInfoProvider.Instance.GetProcessPrivateWorkingSetInMB(process.Id);
                            this.allMemData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(mem);

                            Thread.Sleep(250);
                        }
                        catch (Exception e)
                        {
                            this.WriteToLogWithLevel(
                                this.ObserverName,
                                $"Can't observe {process} details:{Environment.NewLine}{e}",
                                LogLevel.Warning);

                            throw;
                        }
                    }
                }
                catch (Win32Exception)
                {
                    // This will always be the case if FabricObserver.exe is not running as Admin or LocalSystem.
                    // It's OK. Just means that the elevated process (like FabricHost.exe) won't be observed.
                    this.WriteToLogWithLevel(
                        this.ObserverName,
                        $"Can't observe {process.ProcessName} due to it's privilege level. FabricObserver must be running as System or Admin for this specific task.",
                        LogLevel.Information);

                    break;
                }
                finally
                {
                    process?.Dispose();
                }

                timer.Stop();
                timer.Reset();
            }
        }

        private void ProcessResourceDataList<T>(
            IReadOnlyCollection<FabricResourceUsageData<T>> data,
            T thresholdError,
            T thresholdWarning)
                where T : struct
        {
            foreach (var dataItem in data)
            {
                this.Token.ThrowIfCancellationRequested();

                if (dataItem.Data.Count == 0 || Convert.ToDouble(dataItem.AverageDataValue) < 0)
                {
                    continue;
                }

                if (this.CsvFileLogger.EnableCsvLogging)
                {
                    var fileName = "FabricSystemServices_" + this.NodeName;
                    var propertyName = data.First().Property;

                    // Log average data value to long-running store (CSV).
                    string dataLogMonitorType = propertyName;

                    switch (propertyName)
                    {
                        case ErrorWarningProperty.TotalMemoryConsumptionMb:
                            dataLogMonitorType = "Working Set %";
                            break;

                        case ErrorWarningProperty.TotalCpuTime:
                            dataLogMonitorType = "% CPU Time";
                            break;
                    }

                    this.CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Average", Math.Round(Convert.ToDouble(dataItem.AverageDataValue), 2));
                    this.CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Peak", Math.Round(Convert.ToDouble(dataItem.MaxDataValue)));
                }

                this.ProcessResourceDataReportHealth(
                    dataItem,
                    thresholdError,
                    thresholdWarning,
                    this.SetHealthReportTimeToLive(),
                    HealthReportType.Application);
            }
        }
    }
}
