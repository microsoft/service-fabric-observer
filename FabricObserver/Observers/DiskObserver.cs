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
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers
{
    // DiskObserver monitors logical disk states (space consumption, queue length and folder sizes) and creates Service Fabric
    // Warning or Error Node-level health reports based on settings in ApplicationManifest.xml.
    public sealed class DiskObserver : ObserverBase
    {
        // Data storage containers for post run analysis.
        private List<FabricResourceUsageData<float>> DiskAverageQueueLengthData;
        private List<FabricResourceUsageData<double>> DiskSpaceUsagePercentageData;
        private List<FabricResourceUsageData<double>> DiskSpaceAvailableMbData;
        private List<FabricResourceUsageData<double>> DiskSpaceTotalMbData;
        private List<FabricResourceUsageData<double>> FolderSizeDataMb;
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

        /* For folder size monitoring */

        public bool FolderSizeMonitoringEnabled
        {
            get; set;
        }

        public Dictionary<string, double> FolderSizeConfigDataError
        {
            get; set;
        }

        public Dictionary<string, double> FolderSizeConfigDataWarning
        {
            get; set;
        }

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public DiskObserver(StatelessServiceContext context) : base(null, context)
        {
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

            if (IsWindows)
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

                    if (IsWindows)
                    {
                        try
                        {
                            id = d.Name.Remove(2, 1);
                        }
                        catch (ArgumentException)
                        {

                        }
                    }

                    // Since these live across iterations, do not duplicate them in the containing list.
                    // Disk space %.
                    if (DiskSpaceUsagePercentageData.All(data => data.Id != id) && (DiskSpacePercentErrorThreshold > 0 || DiskSpacePercentWarningThreshold > 0))
                    {
                        DiskSpaceUsagePercentageData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceUsagePercentage, id, 1));
                    }

                    // Current disk queue length. Windows only.
                    if (IsWindows && DiskAverageQueueLengthData.All(data => data.Id != id) && (AverageQueueLengthErrorThreshold > 0 || AverageQueueLengthWarningThreshold > 0))
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
                    if (IsWindows && (AverageQueueLengthErrorThreshold > 0 || AverageQueueLengthWarningThreshold > 0))
                    {
                        DiskAverageQueueLengthData.Find(x => x.Id == id)?.AddData(DiskUsage.GetAverageDiskQueueLength(d.Name[..2]));
                    }

                    if (DiskSpacePercentErrorThreshold > 0 || DiskSpacePercentWarningThreshold > 0)
                    {
                        DiskSpaceUsagePercentageData.Find(x => x.Id == id)?.AddData(DiskUsage.GetCurrentDiskSpaceUsedPercent(id));
                    }

                    DiskSpaceAvailableMbData.Find(x => x.Id == id)?.AddData(DiskUsage.GetAvailableDiskSpace(id, SizeUnit.Megabytes));
                    DiskSpaceTotalMbData.Find(x => x.Id == id)?.AddData(DiskUsage.GetTotalDiskSpace(id, SizeUnit.Megabytes));

                    // This section only needs to run if you have the FabricObserverWebApi app installed.
                    if (!IsObserverWebApiAppDeployed || !IsWindows)
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

                /* Process Folder size data. */

                if (FolderSizeMonitoringEnabled)
                {
                    if (FolderSizeConfigDataWarning?.Count > 0)
                    {
                        CheckFolderSizeUsage(FolderSizeConfigDataWarning);
                    }

                    if (FolderSizeConfigDataError?.Count > 0)
                    {
                        CheckFolderSizeUsage(FolderSizeConfigDataError);
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogError($"Unhandled exception in ObserveAsync:{Environment.NewLine}{e}"); 
                
                // Fix the bug..
                throw;
            }

            await ReportAsync(token);

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
            string FolderPathsErrorThresholdPairs = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.DiskObserverFolderPathsErrorThresholdsMb);
            
            if (!string.IsNullOrWhiteSpace(FolderPathsErrorThresholdPairs))
            {
                FolderSizeConfigDataError ??= new Dictionary<string, double>();
                AddFolderSizeConfigData(FolderPathsErrorThresholdPairs, false);
            }

            string FolderPathsWarningThresholdPairs = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.DiskObserverFolderPathsWarningThresholdsMb);
            
            if (!string.IsNullOrWhiteSpace(FolderPathsWarningThresholdPairs))
            {
                FolderSizeConfigDataWarning ??= new Dictionary<string, double>();
                AddFolderSizeConfigData(FolderPathsWarningThresholdPairs, true);
            }
        }

        private void AddFolderSizeConfigData(string folderSizeConfig, bool isWarningThreshold)
        {
            // No config settings supplied.
            if (string.IsNullOrWhiteSpace(folderSizeConfig))
            {
                return;
            }

            if (!TryGetFolderSizeConfigSettingData(folderSizeConfig, out string[] configData))
            {
                return;
            }

            foreach (string data in configData)
            {
                string[] pairs = data.Split(",");

                if (pairs.Length != 2)
                {
                    continue;
                }

                string path = pairs[0];

                try
                {
                    // Contains env variable(s)?
                    if (path.Contains('%'))
                    {
                        if (Regex.Match(path, @"^%[a-zA-Z0-9_]+%").Success)
                        {
                            path = Environment.ExpandEnvironmentVariables(pairs[0]);
                        }
                    }

                    if (!Directory.Exists(path))
                    {
                        continue;
                    }

                    if (!double.TryParse(pairs[1], out double threshold))
                    {
                        continue;
                    }

                    if (isWarningThreshold)
                    {
                        if (FolderSizeConfigDataWarning != null)
                        {
                            if (!FolderSizeConfigDataWarning.ContainsKey(path))
                            {
                                FolderSizeConfigDataWarning.Add(path, threshold);
                            }
                            else if (FolderSizeConfigDataWarning[path] != threshold) // App Parameter upgrade?
                            {
                                FolderSizeConfigDataWarning[path] = threshold;  
                            }
                        }
                    }
                    else if (FolderSizeConfigDataError != null)
                    {
                        if (!FolderSizeConfigDataError.ContainsKey(path))
                        {
                            FolderSizeConfigDataError.Add(path, threshold);
                        }
                        else if (FolderSizeConfigDataError[path] != threshold) // App Parameter upgrade?
                        {
                            FolderSizeConfigDataError[path] = threshold;
                        }
                    }
                }
                catch (ArgumentException)
                {
                    
                }
            }
        }

        private bool TryGetFolderSizeConfigSettingData(string folderSizeConfig, out string[] configData)
        {
            if (string.IsNullOrWhiteSpace(folderSizeConfig))
            {
                configData = null;
                return false;
            }

            try
            {
                string[] data = folderSizeConfig.Split('|', StringSplitOptions.RemoveEmptyEntries);
                
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = data[i].Trim();
                }

                if (data.Length > 0)
                {
                    configData = data.Where(x => x.Contains(',')).ToArray();

                    if (configData.Length < data.Length)
                    {
                        ObserverLogger.LogWarning("TryGetFolderSizeConfigSettingData: Some path/threshold pairs were missing ',' between items. Ignoring.");
                    }

                    return true;
                }
                
                string message = $"Invalid format for Folder paths/thresholds setting. Supplied {folderSizeConfig}. Expected 'path, threshold'. " +
                                 $"If supplying multiple path/threshold pairs, each pair must be separated by a | character.";

                var healthReport = new Utilities.HealthReport
                {
                    EmitLogEvent = true,
                    HealthMessage = message,
                    HealthReportTimeToLive = GetHealthReportTimeToLive(),
                    Property = $"InvalidConfigFormat({folderSizeConfig})",
                    EntityType = EntityType.Node,
                    State = ObserverManager.ObserverFailureHealthStateLevel,
                    NodeName = NodeName,
                    Observer = ObserverConstants.DiskObserverName,
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "InvalidConfigFormat",
                            ObserverManager.ObserverFailureHealthStateLevel,
                            message,
                            ObserverName,
                            Token);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Property = "InvalidConfigFormat",
                            Level = ObserverManager.ObserverFailureHealthStateLevel,
                            Message = message,
                            NodeName,
                            ObserverName
                        });
                }
            }
            catch (ArgumentException)
            {

            }

            configData = null;
            return false;
        }

        private void CheckFolderSizeUsage(IDictionary<string, double> data)
        {
            foreach (var item in data)
            {
                string path = item.Key;

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                // Contains env variable(s)?
                if (path.Contains('%'))
                {
                    if (Regex.Match(path, @"^%[a-zA-Z0-9_]+%").Success)
                    {
                        path = Environment.ExpandEnvironmentVariables(item.Key);
                    }
                }

                try
                {
                    if (!Directory.Exists(path) || item.Value <= 0)
                    {
                        continue;
                    }

                    if (!FolderSizeDataMb.Any(x => x.Id == item.Key))
                    {
                        FolderSizeDataMb.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.FolderSizeMB, item.Key, 1));
                    }

                    double size = GetFolderSize(path, SizeUnit.Megabytes);
                    FolderSizeDataMb.Find(x => x.Id == item.Key)?.AddData(size);
                }
                catch (ArgumentException ae)
                {
                    ObserverLogger.LogWarning($"Handled Exception in CheckFolderSizeUsage:{Environment.NewLine}{ae}");
                }
            }
        }

        private double GetFolderSize(string path, SizeUnit unit)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                ObserverLogger.LogWarning($"GetFolderSize: Invalid value supplied for {nameof(path)} parameter. Supplied value = '{path}'");
                return 0.0;
            }

            if (!Directory.Exists(path))
            {
                ObserverLogger.LogWarning($"GetFolderSize: '{path}' does not exist.");
                return 0.0;
            }

            try
            {
                var dir = new DirectoryInfo(path);
                double folderSizeInBytes = 
                        Convert.ToDouble(
                            dir.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true })
                               .Sum(fi => fi.Length));

                if (unit == SizeUnit.Gigabytes)
                {
                    return folderSizeInBytes / 1024 / 1024 / 1024;
                }

                // MB.
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
                        timeToLiveWarning,
                        EntityType.Disk);
                }

                // Folder size.
                for (int i = 0; i < FolderSizeDataMb.Count; ++i)
                {
                    token.ThrowIfCancellationRequested();

                    var data = FolderSizeDataMb[i];
                    double errorThreshold = 0.0;
                    double warningThreshold = 0.0;

                    if (FolderSizeConfigDataError?.Count > 0 && FolderSizeConfigDataError.ContainsKey(data.Id))
                    {
                        errorThreshold = FolderSizeConfigDataError[data.Id];
                    }

                    if (FolderSizeConfigDataWarning?.Count > 0 && FolderSizeConfigDataWarning.ContainsKey(data.Id))
                    {
                        warningThreshold = FolderSizeConfigDataWarning[data.Id];
                    }

                    ProcessResourceDataReportHealth(
                        data,
                        errorThreshold,
                        warningThreshold,
                        timeToLiveWarning,
                        EntityType.Disk);
                }

                // User-supplied Average disk queue length thresholds from ApplicationManifest.xml. Windows only.
                if (IsWindows)
                {
                    for (int i = 0; i <  DiskAverageQueueLengthData.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();
                        var data = DiskAverageQueueLengthData[i];

                        ProcessResourceDataReportHealth(
                            data,
                            AverageQueueLengthErrorThreshold,
                            AverageQueueLengthWarningThreshold,
                            timeToLiveWarning,
                            EntityType.Disk);
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
                        ProcessResourceDataReportHealth(data, 0, 0, timeToLiveWarning, EntityType.Disk);
                    }

                    // Disk Space Total
                    for (int i = 0; i < DiskSpaceTotalMbData.Count; ++i)
                    {
                        token.ThrowIfCancellationRequested();
                        var data = DiskSpaceTotalMbData[i];
                        ProcessResourceDataReportHealth(data, 0, 0, timeToLiveWarning, EntityType.Disk);
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
                if (bool.TryParse(GetSettingParameterValue(
                            ConfigurationSectionName,
                            ObserverConstants.DiskObserverEnableFolderSizeMonitoring), out bool enableFolderMonitoring))
                {
                    FolderSizeMonitoringEnabled = enableFolderMonitoring;
                    
                    if (enableFolderMonitoring)
                    {
                        ProcessFolderSizeConfig();
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
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