// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Utilities;

namespace FabricObserver
{
    // This observer monitors logical disk behavior and signals Service Fabric Warning or Error events based on user-supplied thresholds
    // in Settings.xml...
    // The output (a local file) is used by the API service and the HTML frontend (http://localhost:5000/api/ObserverManager).
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    public class DiskObserver : ObserverBase
    {
        // Data storage containers for post run analysis...
        private List<FabricResourceUsageData<float>> diskIOReadsData;
        private List<FabricResourceUsageData<float>> diskIOWritesData;
        private List<FabricResourceUsageData<float>> diskAverageQueueLengthData;
        private List<FabricResourceUsageData<double>> diskSpacePercentageUsageData;
        private List<FabricResourceUsageData<double>> diskSpaceUsageData;
        private List<FabricResourceUsageData<double>> diskSpaceAvailableData;
        private List<FabricResourceUsageData<double>> diskSpaceTotalData;
        private StringBuilder diskInfo = new StringBuilder();
        private TimeSpan monitorDuration = TimeSpan.FromSeconds(5);
        private Stopwatch stopWatch;

        public int DiskSpacePercentErrorThreshold { get; set; }

        public int DiskSpacePercentWarningThreshold { get; set; }

        public int IOReadsErrorThreshold { get; set; }

        public int IOReadsWarningThreshold { get; set; }

        public int IOWritesErrorThreshold { get; set; }

        public int IOWritesWarningThreshold { get; set; }

        public int AverageQueueLengthWarningThreshold { get; set; }

        public int AverageQueueLengthErrorThreshold { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DiskObserver"/> class.
        /// </summary>
        public DiskObserver()
            : base(ObserverConstants.DiskObserverName)
        {
            this.diskIOReadsData = new List<FabricResourceUsageData<float>>();
            this.diskIOWritesData = new List<FabricResourceUsageData<float>>();
            this.diskSpacePercentageUsageData = new List<FabricResourceUsageData<double>>();
            this.diskSpaceUsageData = new List<FabricResourceUsageData<double>>();
            this.diskSpaceAvailableData = new List<FabricResourceUsageData<double>>();
            this.diskSpaceTotalData = new List<FabricResourceUsageData<double>>();
            this.diskAverageQueueLengthData = new List<FabricResourceUsageData<float>>();
            this.stopWatch = new Stopwatch();
        }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example...
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            this.SetErrorWarningThresholds();

            DriveInfo[] allDrives = DriveInfo.GetDrives();
            var diskUsage = new DiskUsage();

            if (ObserverManager.ObserverWebAppDeployed)
            {
                this.diskInfo = new StringBuilder();
            }

            try
            {
                int readyCount = 0;
                foreach (var d in allDrives)
                {
                    token.ThrowIfCancellationRequested();

                    if (d.IsReady)
                    {
                        readyCount++;

                        // This section only needs to run if you have the FabricObserverWebApi app installed...
                        if (ObserverManager.ObserverWebAppDeployed)
                        {
                            this.diskInfo.AppendFormat("\n\nDrive Name: {0}\n", d.Name);

                            // Logging...
                            this.diskInfo.AppendFormat("Drive Type: {0}\n", d.DriveType);
                            this.diskInfo.AppendFormat("  Volume Label   : {0}\n", d.VolumeLabel);
                            this.diskInfo.AppendFormat("  Filesystem     : {0}\n", d.DriveFormat);
                            this.diskInfo.AppendFormat("  Total Disk Size: {0} GB\n", d.TotalSize / 1024 / 1024 / 1024);
                            this.diskInfo.AppendFormat("  Root Directory : {0}\n", d.RootDirectory);
                            this.diskInfo.AppendFormat("  Free User : {0} GB\n", d.AvailableFreeSpace / 1024 / 1024 / 1024);
                            this.diskInfo.AppendFormat("  Free Total: {0} GB\n", d.TotalFreeSpace / 1024 / 1024 / 1024);
                            this.diskInfo.AppendFormat("  % Used    : {0}%\n", diskUsage.GetCurrentDiskSpaceUsedPercent(d.Name));
                        }

                        // Setup monitoring data structures...
                        string id = d.Name.Substring(0, 1);

                        // Since these live across iterations, do not duplicate them in the containing list...
                        if (!this.diskIOReadsData.Any(data => data.Id == id))
                        {
                            this.diskIOReadsData.Add(new FabricResourceUsageData<float>("Disk sec/Read (ms)", id));
                        }

                        if (!this.diskIOWritesData.Any(data => data.Id == id))
                        {
                            this.diskIOWritesData.Add(new FabricResourceUsageData<float>("Disk sec/Write (ms)", id));
                        }

                        // Disk space %...
                        if (!this.diskSpacePercentageUsageData.Any(data => data.Id == id))
                        {
                            this.diskSpacePercentageUsageData.Add(new FabricResourceUsageData<double>("Disk Space Consumption %", id));
                        }

                        if (!this.diskSpaceUsageData.Any(data => data.Id == id))
                        {
                            this.diskSpaceUsageData.Add(new FabricResourceUsageData<double>("Disk Space Consumption MB", id));
                        }

                        if (!this.diskSpaceAvailableData.Any(data => data.Id == id))
                        {
                            this.diskSpaceAvailableData.Add(new FabricResourceUsageData<double>("Disk Space Available MB", id));
                        }

                        if (!this.diskSpaceTotalData.Any(data => data.Id == id))
                        {
                            this.diskSpaceTotalData.Add(new FabricResourceUsageData<double>("Disk Space Total MB", id));
                        }

                        // Current disk queue length...
                        if (!this.diskAverageQueueLengthData.Any(data => data.Id == id))
                        {
                            this.diskAverageQueueLengthData.Add(new FabricResourceUsageData<float>("Average Disk Queue Length", id));
                        }

                        // Generate data over time (_monitorDuration...) for use in ReportAsync health analysis...
                        this.stopWatch?.Start();

                        while (this.stopWatch?.Elapsed <= this.monitorDuration)
                        {
                            token.ThrowIfCancellationRequested();

                            this.diskIOReadsData.FirstOrDefault(
                                                    x => x.Id == id)
                                                    .Data.Add(diskUsage.PerfCounterGetDiskIOInfo(d.Name.Substring(0, 2), "LogicalDisk", "Avg. Disk sec/Read") * 1000);

                            this.diskIOWritesData.FirstOrDefault(
                                                    x => x.Id == id)
                                                    .Data.Add(diskUsage.PerfCounterGetDiskIOInfo(d.Name.Substring(0, 2), "LogicalDisk", "Avg. Disk sec/Write") * 1000);

                            this.diskSpacePercentageUsageData.FirstOrDefault(
                                                     x => x.Id == id)
                                                     .Data.Add(diskUsage.GetCurrentDiskSpaceUsedPercent(id));

                            this.diskSpaceUsageData.FirstOrDefault(
                                                     x => x.Id == id)
                                                     .Data.Add(diskUsage.GetUsedDiskSpace(id, SizeUnit.Megabytes));

                            this.diskSpaceAvailableData.FirstOrDefault(
                                                     x => x.Id == id)
                                                     .Data.Add(diskUsage.GetAvailabeDiskSpace(id, SizeUnit.Megabytes));

                            this.diskSpaceTotalData.FirstOrDefault(
                                                     x => x.Id == id)
                                                     .Data.Add(diskUsage.GetTotalDiskSpace(id, SizeUnit.Megabytes));

                            this.diskAverageQueueLengthData.FirstOrDefault(
                                                             x => x.Id == id)
                                                             .Data.Add(diskUsage.GetAverageDiskQueueLength(d.Name.Substring(0, 2)));

                            Thread.Sleep(250);
                        }

                        // This section only needs to run if you have the FabricObserverWebApi app installed...
                        if (ObserverManager.ObserverWebAppDeployed)
                        {
                            this.diskInfo.AppendFormat(
                                "{0}",
                                this.GetWindowsPerfCounterDetailsText(
                                                            this.diskIOReadsData.FirstOrDefault(
                                                                                            x => x.Id == d.Name.Substring(0, 1)).Data,
                                                            "Avg. Disk sec/Read"));
                            this.diskInfo.AppendFormat(
                                "{0}",
                                this.GetWindowsPerfCounterDetailsText(
                                                            this.diskIOWritesData.FirstOrDefault(
                                                                                            x => x.Id == d.Name.Substring(0, 1)).Data,
                                                            "Avg. Disk sec/Write"));

                            this.diskInfo.AppendFormat(
                                "{0}",
                                this.GetWindowsPerfCounterDetailsText(
                                                            this.diskAverageQueueLengthData.FirstOrDefault(
                                                                                            x => x.Id == d.Name.Substring(0, 1)).Data,
                                                            "Avg. Disk Queue Length"));
                        }

                        this.stopWatch.Stop();
                        this.stopWatch.Reset();
                    }
                }
            }
            finally
            {
                diskUsage.Dispose();
                diskUsage = null;
            }

            await this.ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
        }

        private void SetErrorWarningThresholds()
        {
            try
            {
                if (int.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.DiskObserverConfigurationSectionName,
                    ObserverConstants.DiskObserverDiskSpacePercentError), out int diskUsedError))
                {
                    this.DiskSpacePercentErrorThreshold = diskUsedError;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.DiskObserverConfigurationSectionName,
                    ObserverConstants.DiskObserverDiskSpacePercentWarning), out int diskUsedWarning))
                {
                    this.DiskSpacePercentWarningThreshold = diskUsedWarning;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.DiskObserverConfigurationSectionName,
                    ObserverConstants.DiskObserverIOReadsError), out int diskReadsError))
                {
                    this.IOReadsErrorThreshold = diskReadsError;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.DiskObserverConfigurationSectionName,
                    ObserverConstants.DiskObserverIOReadsWarning), out int diskReadsWarning))
                {
                    this.IOReadsWarningThreshold = diskReadsWarning;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.DiskObserverConfigurationSectionName,
                    ObserverConstants.DiskObserverIOWritesError), out int diskWritesError))
                {
                    this.IOWritesErrorThreshold = diskWritesError;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.DiskObserverConfigurationSectionName,
                    ObserverConstants.DiskObserverIOWritesWarning), out int diskWritesWarning))
                {
                    this.IOWritesWarningThreshold = diskWritesWarning;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.DiskObserverConfigurationSectionName,
                    ObserverConstants.DiskObserverAverageQueueLengthError), out int diskCurrentQueueLengthError))
                {
                    this.AverageQueueLengthErrorThreshold = diskCurrentQueueLengthError;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    ObserverConstants.DiskObserverConfigurationSectionName,
                    ObserverConstants.DiskObserverAverageQueueLengthWarning), out int diskCurrentQueueLengthWarning))
                {
                    this.AverageQueueLengthWarningThreshold = diskCurrentQueueLengthWarning;
                }
            }
            catch (ArgumentNullException)
            {
            }
            catch (FormatException)
            {
            }
        }

        private string GetWindowsPerfCounterDetailsText(List<float> data, string counter)
        {
            if (data == null || data.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            string ret;
            string unit = "ms";

            if (counter.Contains("Queue"))
            {
                unit = string.Empty;
            }

            sb.AppendFormat("  {0}: {1}", counter, Math.Round(data.Average(), 3) + $" {unit}" + Environment.NewLine);

            ret = sb.ToString();
            sb.Clear();

            return ret;
        }

        /// <inheritdoc/>
        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                var timeToLiveWarning = this.SetTimeToLiveWarning(this.monitorDuration.Seconds);

                // Reads...
                foreach (var data in this.diskIOReadsData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        this.IOReadsErrorThreshold,
                        this.IOReadsWarningThreshold,
                        timeToLiveWarning);
                }

                // Writes...
                foreach (var data in this.diskIOWritesData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        this.IOWritesErrorThreshold,
                        this.IOWritesWarningThreshold,
                        timeToLiveWarning);
                }

                // Disk Space Usage % from Settings.xml...
                foreach (var data in this.diskSpacePercentageUsageData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        this.DiskSpacePercentErrorThreshold,
                        this.DiskSpacePercentWarningThreshold,
                        timeToLiveWarning);
                }

                // Disk Space Usage
                foreach (var data in this.diskSpaceUsageData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        0,
                        0,
                        timeToLiveWarning);
                }

                // Disk Space Available
                foreach (var data in this.diskSpaceAvailableData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        0,
                        0,
                        timeToLiveWarning);
                }

                // Disk Space Total
                foreach (var data in this.diskSpaceTotalData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        0,
                        0,
                        timeToLiveWarning);
                }

                // Average disk queue length...
                foreach (var data in this.diskAverageQueueLengthData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        this.AverageQueueLengthErrorThreshold,
                        this.AverageQueueLengthWarningThreshold,
                        timeToLiveWarning);
                }

                token.ThrowIfCancellationRequested();

                // This section only needs to run if you have the FabricObserverWebApi app installed...
                if (ObserverManager.ObserverWebAppDeployed)
                {
                    var diskInfoPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "disks.txt");

                    this.ObserverLogger.TryWriteLogFile(diskInfoPath, this.diskInfo.ToString());

                    this.diskInfo.Clear();
                }

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.OriginalString,
                        this.ObserverName,
                        HealthState.Error,
                        $"Unhandled exception processing Disk information: {e.Message}: \n {e.StackTrace}");
                }

                throw;
            }
        }
    }
}