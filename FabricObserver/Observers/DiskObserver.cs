// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;

namespace FabricObserver.Observers
{
    // This observer monitors logical disk behavior and signals Service Fabric Warning or Error events based on user-supplied thresholds
    // in Settings.xml.
    // The output (a local file) is used by the API service and the HTML frontend (http://localhost:5000/api/ObserverManager).
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    public class DiskObserver : ObserverBase
    {
        // Data storage containers for post run analysis.
        private List<FabricResourceUsageData<float>> DiskAverageQueueLengthData;
        private List<FabricResourceUsageData<double>> DiskSpaceUsagePercentageData;
        private List<FabricResourceUsageData<double>> DiskSpaceAvailableMbData;
        private List<FabricResourceUsageData<double>> DiskSpaceTotalMbData;
        private readonly Stopwatch stopWatch;
        private StringBuilder diskInfo = new StringBuilder();

        public int DiskSpacePercentErrorThreshold
        {
            get; set;
        }

        public int DiskSpacePercentWarningThreshold
        {
            get; set;
        }

        public int AverageQueueLengthWarningThreshold
        {
            get; set;
        }

        public int AverageQueueLengthErrorThreshold
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DiskObserver"/> class.
        /// </summary>
        public DiskObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                DiskAverageQueueLengthData = new List<FabricResourceUsageData<float>>();
            }

            stopWatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            Token = token;
            stopWatch.Start();

            SetErrorWarningThresholds();
            DriveInfo[] allDrives = DriveInfo.GetDrives();
            int driveCount = allDrives.Length;

            if (IsObserverWebApiAppDeployed)
            {
                diskInfo = new StringBuilder();
            }

            DiskSpaceUsagePercentageData ??= new List<FabricResourceUsageData<double>>(driveCount);
            DiskSpaceAvailableMbData ??= new List<FabricResourceUsageData<double>>(driveCount);
            DiskSpaceTotalMbData ??= new List<FabricResourceUsageData<double>>(driveCount);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                DiskAverageQueueLengthData ??= new List<FabricResourceUsageData<float>>(driveCount);
            }

            try 
            {
                foreach (var d in allDrives)
                {
                    token.ThrowIfCancellationRequested();

                    if (!DiskUsage.ShouldCheckDrive(d))
                    {
                        continue;
                    }

                    // This section only needs to run if you have the FabricObserverWebApi app installed.
                    if (IsObserverWebApiAppDeployed)
                    {
                        _ = diskInfo.AppendFormat("\n\nDrive Name: {0}\n", d.Name);

                        // Logging.
                        _ = diskInfo.AppendFormat("Drive Type: {0}\n", d.DriveType);
                        _ = diskInfo.AppendFormat("  Volume Label   : {0}\n", d.VolumeLabel);
                        _ = diskInfo.AppendFormat("  Filesystem     : {0}\n", d.DriveFormat);
                        _ = diskInfo.AppendFormat("  Total Disk Size: {0} GB\n", d.TotalSize / 1024 / 1024 / 1024);
                        _ = diskInfo.AppendFormat("  Root Directory : {0}\n", d.RootDirectory);
                        _ = diskInfo.AppendFormat("  Free User : {0} GB\n", d.AvailableFreeSpace / 1024 / 1024 / 1024);
                        _ = diskInfo.AppendFormat("  Free Total: {0} GB\n", d.TotalFreeSpace / 1024 / 1024 / 1024);
                        _ = diskInfo.AppendFormat("  % Used    : {0}%\n", DiskUsage.GetCurrentDiskSpaceUsedPercent(d.Name));
                    }

                    // Setup monitoring data structures.
                    string id = d.Name;

                    // Since these live across iterations, do not duplicate them in the containing list.
                    // Disk space %.
                    if (DiskSpaceUsagePercentageData.All(data => data.Id != id) && (DiskSpacePercentErrorThreshold > 0 || DiskSpacePercentWarningThreshold > 0))
                    {
                        DiskSpaceUsagePercentageData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceUsagePercentage, id, 1));
                    }

                    // Current disk queue length. Windows only.
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && DiskAverageQueueLengthData.All(data => data.Id != id) && (AverageQueueLengthErrorThreshold > 0 || AverageQueueLengthWarningThreshold > 0))
                    {
                        DiskAverageQueueLengthData.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.DiskAverageQueueLength, id, 1));
                    }

                    // This data is just used for Telemetry today.
                    if (DiskSpaceAvailableMbData.All(data => data.Id != id))
                    {
                        DiskSpaceAvailableMbData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceAvailableMb, id, 1));
                    }

                    // This data is just used for Telemetry today.
                    if (DiskSpaceTotalMbData.All(data => data.Id != id))
                    {
                        DiskSpaceTotalMbData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceTotalMb, id, 1));
                    }

                    // It is important to check if code is running on Windows, since d.Name.Substring(0, 2) will fail on Linux for / (root) mount point.
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        if (AverageQueueLengthErrorThreshold > 0 || AverageQueueLengthWarningThreshold > 0)
                        {
                            // Warm up counter.
                            _ = DiskUsage.GetAverageDiskQueueLength(d.Name[..2]);
                            await Task.Delay(250);
                            DiskAverageQueueLengthData.Find(x => x.Id == id)?.AddData(DiskUsage.GetAverageDiskQueueLength(d.Name[..2]));
                        }
                    }

                    if (DiskSpacePercentErrorThreshold > 0 || DiskSpacePercentWarningThreshold > 0)
                    {
                        DiskSpaceUsagePercentageData.Find(x => x.Id == id)?.AddData(DiskUsage.GetCurrentDiskSpaceUsedPercent(id));
                    }

                    DiskSpaceAvailableMbData.Find(x => x.Id == id)?.AddData(DiskUsage.GetAvailableDiskSpace(id, SizeUnit.Megabytes));
                    DiskSpaceTotalMbData.Find(x => x.Id == id)?.AddData(DiskUsage.GetTotalDiskSpace(id, SizeUnit.Megabytes));

                    // This section only needs to run if you have the FabricObserverWebApi app installed.
                    if (!IsObserverWebApiAppDeployed || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        continue;
                    }

                    token.ThrowIfCancellationRequested();

                    _ = diskInfo.AppendFormat(
                        "{0}", 
                        GetWindowsPerfCounterDetailsText(DiskAverageQueueLengthData.FirstOrDefault(
                                                            x => d.Name.Length > 0 && x.Id == d.Name[..1])?.Data,
                                                                 "Avg. Disk Queue Length"));
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                ObserverLogger.LogError($"Unhandled exception in ObserveAsync:{Environment.NewLine}{e}"); 
                
                // Fix the bug..
                throw;
            }

            await ReportAsync(token).ConfigureAwait(true);

            // The time it took to run this observer.
            stopWatch.Stop();
            RunDuration = stopWatch.Elapsed;

            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration: {RunDuration}");
            }

            stopWatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                var timeToLiveWarning = GetHealthReportTimeToLive();

                // User-supplied Disk Space Usage % thresholds from ApplicationManifest.xml.
                for (int i = 0; i < DiskSpaceUsagePercentageData.Count; ++i)
                {
                    token.ThrowIfCancellationRequested();
                    var data = DiskSpaceUsagePercentageData[i];

                    ProcessResourceDataReportHealth(
                                        data,
                                        DiskSpacePercentErrorThreshold,
                                        DiskSpacePercentWarningThreshold,
                                        timeToLiveWarning);
                }

                // User-supplied Average disk queue length thresholds from ApplicationManifest.xml. Windows only.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    for (int i = 0; i <  DiskAverageQueueLengthData.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();
                        var data = DiskAverageQueueLengthData[i];

                        ProcessResourceDataReportHealth(
                                            data,
                                            AverageQueueLengthErrorThreshold,
                                            AverageQueueLengthWarningThreshold,
                                            timeToLiveWarning);
                    }
                }

                /* For ETW Only - These calls will just produce ETW (note the thresholds). See ObserverBase.ProcessDataReportHealth 
                                  in FabricObserver.Extensibility project. */
                if (IsEtwEnabled)
                {
                    // Disk Space Available
                    for (int i = 0; i < DiskSpaceAvailableMbData.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();
                        var data = DiskSpaceAvailableMbData[i];
                        ProcessResourceDataReportHealth(data, 0, 0, timeToLiveWarning);
                    }

                    // Disk Space Total
                    for (int i = 0; i < DiskSpaceTotalMbData.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();
                        var data = DiskSpaceTotalMbData[i];
                        ProcessResourceDataReportHealth(data, 0, 0, timeToLiveWarning);
                    }
                }

                token.ThrowIfCancellationRequested();

                // This section only needs to run if you have the FabricObserverWebApi app installed.
                if (!IsObserverWebApiAppDeployed)
                {
                    return Task.CompletedTask;
                }

                var diskInfoPath = Path.Combine(ObserverLogger.LogFolderBasePath, "disks.txt");
                _ = ObserverLogger.TryWriteLogFile(diskInfoPath, diskInfo.ToString());
                _ = diskInfo.Clear();
            }
            catch (Exception e)
            {
                // ObserverManager handles these.
                if (e is OperationCanceledException || e is TaskCanceledException || e is FabricException)
                {
                    throw;
                }

                ObserverLogger.LogWarning($"Unhandled exception in ReportAsync:{Environment.NewLine}{e}");

                // Fix the bug..
                throw;
            }

            return Task.CompletedTask;
        }

        private void SetErrorWarningThresholds()
        {
            Token.ThrowIfCancellationRequested();

            try
            {
                if (int.TryParse(
                            GetSettingParameterValue(
                            ConfigurationSectionName,
                            ObserverConstants.DiskObserverDiskSpacePercentError), out int diskUsedError))
                {
                    DiskSpacePercentErrorThreshold = diskUsedError;
                }

                Token.ThrowIfCancellationRequested();

                if (int.TryParse(
                            GetSettingParameterValue(
                            ConfigurationSectionName,
                            ObserverConstants.DiskObserverDiskSpacePercentWarning), out int diskUsedWarning))
                {
                    DiskSpacePercentWarningThreshold = diskUsedWarning;
                }

                Token.ThrowIfCancellationRequested();

                if (int.TryParse(
                            GetSettingParameterValue(
                            ConfigurationSectionName,
                            ObserverConstants.DiskObserverAverageQueueLengthError), out int diskCurrentQueueLengthError))
                {
                    AverageQueueLengthErrorThreshold = diskCurrentQueueLengthError;
                }

                Token.ThrowIfCancellationRequested();

                if (int.TryParse(
                            GetSettingParameterValue(
                                ConfigurationSectionName,
                                ObserverConstants.DiskObserverAverageQueueLengthWarning), out int diskCurrentQueueLengthWarning))
                {
                    AverageQueueLengthWarningThreshold = diskCurrentQueueLengthWarning;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException)
            {

            }
            catch (Exception e)
            {
                // ObserverManager handles these.
                if (e is OperationCanceledException || e is TaskCanceledException || e is FabricException)
                {
                    throw;
                }

                ObserverLogger.LogWarning($"Unhandled exception in SetErrorWarningThresholds:{Environment.NewLine}{e}");
                // Fix the bug...
                throw;
            }
        }

        private string GetWindowsPerfCounterDetailsText(IEnumerable<float> data, string counter)
        {
            if (data == null || data.Count() == 0)
            {
                return null;
            }

            Token.ThrowIfCancellationRequested();

            var sb = new StringBuilder();
            string ret;
            string unit = "ms";

            if (counter.Contains("Queue"))
            {
                unit = string.Empty;
            }

            _ = sb.AppendFormat("  {0}: {1}", counter, Math.Round(data.Average(), 3) + $" {unit}" + Environment.NewLine);

            ret = sb.ToString();
            _ = sb.Clear();

            return ret;
        }
    }
}