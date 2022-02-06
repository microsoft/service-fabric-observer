// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;

namespace FabricObserver.Observers
{
    // DiskObserver monitors logical disk states (space consumption, queue length and folder sizes) and creates Service Fabric
    // Warning or Error Node-level health reports based on settings in ApplicationManifest.xml.
    public class DiskObserver : ObserverBase
    {
        // Data storage containers for post run analysis.
        private List<FabricResourceUsageData<float>> DiskAverageQueueLengthData;
        private List<FabricResourceUsageData<double>> DiskSpaceUsagePercentageData;
        private List<FabricResourceUsageData<double>> DiskSpaceAvailableMbData;
        private List<FabricResourceUsageData<double>> DiskSpaceTotalMbData;
        private List<FabricResourceUsageData<double>> FolderSizeDataMb;
        private readonly Stopwatch stopWatch;
        private readonly bool isWindows;
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

        /* For folder size monitoring */

        public List<(string FolderName, double Threshold, bool IsWarningTheshold)> FolderSizeConfigData
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DiskObserver"/> class.
        /// </summary>
        public DiskObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
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
            FolderSizeDataMb ??= new List<FabricResourceUsageData<double>>();

            if (isWindows)
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

                    string id = d.Name;

                    // Since these live across iterations, do not duplicate them in the containing list.
                    // Disk space %.
                    if (DiskSpaceUsagePercentageData.All(data => data.Id != id) && (DiskSpacePercentErrorThreshold > 0 || DiskSpacePercentWarningThreshold > 0))
                    {
                        DiskSpaceUsagePercentageData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceUsagePercentage, id, 1));
                    }

                    // Current disk queue length. Windows only.
                    if (isWindows && DiskAverageQueueLengthData.All(data => data.Id != id) && (AverageQueueLengthErrorThreshold > 0 || AverageQueueLengthWarningThreshold > 0))
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
                    // Also, this feature is not supported for Linux yet.
                    if (isWindows)
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
                    if (!IsObserverWebApiAppDeployed || !isWindows)
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
                
                // Process Folder size data.
                CheckFolderSizeUsage();
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogError($"Unhandled exception in ObserveAsync:{Environment.NewLine}{e}"); 
                
                // Fix the bug..
                throw;
            }

            await ReportAsync(token).ConfigureAwait(false);

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

        private void ProcessFolderSizeConfig()
        {
            string folderPathErrorThresholdPairs = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FolderSizePathsErrorThresholdsMb);
            string folderPathWarningThresholdPairs = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FolderSizePathsWarningThresholdsMb);
            AddFolderSizeConfigData(folderPathErrorThresholdPairs, isWarningThreshold: false);
            AddFolderSizeConfigData(folderPathWarningThresholdPairs, isWarningThreshold: true);
        }

        private void AddFolderSizeConfigData(string folderSizeConfig, bool isWarningThreshold)
        {
            if (!string.IsNullOrWhiteSpace(folderSizeConfig))
            {
                // "[path, threshold] [path1, threshold1] ..."
                string[] configData = folderSizeConfig.Split("]", StringSplitOptions.RemoveEmptyEntries);
                foreach (string data in configData)
                {
                    if (!string.IsNullOrWhiteSpace(data))
                    {
                        string[] pairs = data.Split(",");

                        try
                        {
                            string path = pairs[0].Remove(0, 1);

                            if (!double.TryParse(pairs[1], out double threshold))
                            {
                                continue;
                            }

                            FolderSizeConfigData.Add((path, threshold, isWarningThreshold));
                        }
                        catch (ArgumentException)
                        {

                        }
                    }
                }
            }
        }

        private void CheckFolderSizeUsage()
        {
            foreach (var (FolderPath, Threshold, _) in FolderSizeConfigData)
            {
                if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath) || Threshold < 1.0)
                {
                    continue;
                }

                try
                {
                    string id = FolderPath.Replace(Path.DirectorySeparatorChar.ToString(), string.Empty);

                    if (!FolderSizeDataMb.Any(x => x.Id == id))
                    {
                        FolderSizeDataMb.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.FolderSizeMB, id, 1));
                    }

                    double size = GetFolderSize(FolderPath, SizeUnit.Megabytes);
                    FolderSizeDataMb.Find(x => x.Id == id)?.AddData(size);
                }
                catch (ArgumentException)
                {

                }
            }
        }

        private double GetFolderSize(string path, SizeUnit unit)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                ObserverLogger.LogWarning($"GetFolderSize: Invalid value supplied for {nameof(path)} parameter. Supplied value = {path}");
                return 0.0;
            }

            try
            {
                var dir = new DirectoryInfo(path);
                double folderSizeInBytes = Convert.ToDouble(dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length));

                if (unit == SizeUnit.Gigabytes)
                {
                    return folderSizeInBytes / 1024 / 1024 / 1024;
                }

                return folderSizeInBytes / 1024 / 1024;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is SecurityException)
            {
                ObserverLogger.LogWarning($"Failure computing folder size for {path}:{Environment.NewLine}{e}");
                return 0.0;
            }
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

                // Folder size.
                for (int i = 0; i < FolderSizeDataMb.Count; ++i)
                {
                    token.ThrowIfCancellationRequested();

                    var data = FolderSizeDataMb[i];

                    ProcessResourceDataReportHealth(
                        data,
                        FolderSizeConfigData.Find(x => !x.IsWarningTheshold && data.Id == x.FolderName.Replace(Path.DirectorySeparatorChar.ToString(), string.Empty)).Threshold,
                        FolderSizeConfigData.Find(x => x.IsWarningTheshold && data.Id == x.FolderName.Replace(Path.DirectorySeparatorChar.ToString(), string.Empty)).Threshold,
                        timeToLiveWarning);
                }

                // User-supplied Average disk queue length thresholds from ApplicationManifest.xml. Windows only.
                if (isWindows)
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
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
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
                if (int.TryParse(GetSettingParameterValue(
                                    ConfigurationSectionName,
                                    ObserverConstants.DiskObserverDiskSpacePercentError), out int diskUsedError))
                {
                    DiskSpacePercentErrorThreshold = diskUsedError;
                }

                Token.ThrowIfCancellationRequested();

                if (int.TryParse(GetSettingParameterValue(
                                    ConfigurationSectionName,
                                    ObserverConstants.DiskObserverDiskSpacePercentWarning), out int diskUsedWarning))
                {
                    DiskSpacePercentWarningThreshold = diskUsedWarning;
                }

                Token.ThrowIfCancellationRequested();

                if (int.TryParse(GetSettingParameterValue(
                                    ConfigurationSectionName,
                                    ObserverConstants.DiskObserverAverageQueueLengthError), out int diskCurrentQueueLengthError))
                {
                    AverageQueueLengthErrorThreshold = diskCurrentQueueLengthError;
                }

                Token.ThrowIfCancellationRequested();

                if (int.TryParse(GetSettingParameterValue(
                                    ConfigurationSectionName,
                                    ObserverConstants.DiskObserverAverageQueueLengthWarning), out int diskCurrentQueueLengthWarning))
                {
                    AverageQueueLengthWarningThreshold = diskCurrentQueueLengthWarning;
                }

                // Folder size monitoring.
                FolderSizeConfigData ??= new List<(string FolderName, double Threshold, bool IsWarningTheshold)>();
                ProcessFolderSizeConfig();
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