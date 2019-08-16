// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Fabric;
using System.Fabric.Health;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using FabricObserver.Utilities;
using System.Threading;
using System.IO;
using System.Diagnostics;
using FabricObserver.Model;

// TODO: pull stats from docker stats for each container... 
// Use this info for populating Mem, CPU data files...
namespace FabricObserver
{
    public class ContainerObserver : ObserverBase
    {
        private TimeSpan _timeToLiveWarning = TimeSpan.FromMinutes(5);
 
        public override DateTime LastRunDateTime { get; set; } = DateTime.MinValue;
        //private string ContainerId { get; set; } = "0a5c9afda03a";
        //private string ContainerName { get; set; }
        private readonly string PSGetContainerVHDXInfoCmd = @"Get-VHD -Path ";
        private readonly string DockerWindowsContainerDataPath = null;
        private List<string> _containerFolderNames;
        private string _nodeName = null, _dataPackagePath = null;
        //private bool _initialized = false;
        private List<FabricResourceUsageData<int>> _containersDiskSpaceUsageData;
        private readonly List<Model.ApplicationInfo> _targetList = new List<Model.ApplicationInfo>();
        private bool _probeAllContainersRunningOnNode = false;
        private CancellationToken _token;

        public ContainerObserver(StatelessServiceContext fabricServiceContext,
                                 FabricClient fabricClient) : base(ObserverConstants.ContainerObserverName,
                                                                   fabricServiceContext,
                                                                   fabricClient)
        {
            // e.g., C:\ProgramData\Docker\windowsfilter
            // Get this information from running docker inspect... data object...
            DockerWindowsContainerDataPath = Environment.CurrentDirectory.Substring(0, 2) + @"\ProgramData\Docker\windowsfilter\";
            this._containerFolderNames = new List<string>();
            this._nodeName = fabricServiceContext.NodeContext.NodeName;
            this._dataPackagePath = fabricServiceContext.CodePackageActivationContext.GetDataPackageObject("Observers.Data").Path;
            this._containersDiskSpaceUsageData = new List<FabricResourceUsageData<int>>();
        }

        // docker exec 0a5c9afda03a cmd.exe /C dir | find "bytes free"

        public override async Task ObserveAsync(CancellationToken token)
        {
            if (!Initialize())
            {
                return;
            }

            this._token = token;
            /*
            // probe into container resources by using docker exec to pass console commands to running container...
            // this requires container id and is much lighter in weight than a Docker managed API (which doesn't work for this) or PowerShell...
            // however, Powershell is more useful given its Get-VHD cmdlet... So, commenting out the code below... -CT
            string containerFreeBytes = LaunchWindowsProcessWithOutputString("cmd.exe", "/C docker exec " + this.ContainerId + 
                                                                             " cmd.exe /C dir | find /i \"bytes free\"");
            
            if (string.IsNullOrEmpty(containerFreeBytes))
            {
                return;
            }

            containerFreeBytes = containerFreeBytes.Trim().Substring(9).Replace("bytes free", "").TrimEnd('\r', '\n');

            long freeBytes = 0L;

            if (!long.TryParse(containerFreeBytes.Replace(",", ""), out freeBytes))
            {
                Logger.LogError("Can't parse free bytes string. Aborting...");
                return;
            }
            */
            await GetAllWindowsContainersDiskInfoPSAsync(token).ConfigureAwait(true);

            // Set TTL...
            if (LastRunDateTime == DateTime.MinValue) // First run...
            {
                this._timeToLiveWarning = (this._timeToLiveWarning +
                                           TimeSpan.FromSeconds(ObserverManager.ExecutionFrequency));
            }
            else
            {
                this._timeToLiveWarning = (DateTime.Now.Subtract(LastRunDateTime) +
                                           TimeSpan.FromSeconds(ObserverManager.ExecutionFrequency));
            }

            await ReportAsync(token).ConfigureAwait(true);

            LastRunDateTime = DateTime.Now;
        }

        private bool Initialize()
        {
            ConfigSettings.Initialize(FabricRuntime.GetActivationContext()
                                      .GetConfigurationPackageObject(ConfigSettings.ConfigPackageName).Settings, 
                                      ConfigSettings.ContainerObserverConfiguration, "ContainerObserverDataFileName");

            var ContainerObserverDataFileName = Path.Combine(this._dataPackagePath, ConfigSettings.ContainerObserverDataFileName);

            if (!File.Exists(ContainerObserverDataFileName))
            {
                WriteToLogWithLevel(ObserverName,
                                    $"Will not watch resource consumption as no configuration parameters have been supplied... " +
                                    $"| {this._nodeName}",
                                    LogLevel.Information);
                return false;
            }

            if (this._targetList.Count < 1)
            {
                using (Stream stream = new FileStream(ContainerObserverDataFileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > 40
                        && JsonHelper.IsJson<List<Model.ApplicationInfo>>(File.ReadAllText(ContainerObserverDataFileName)))
                    {
                        this._targetList.AddRange(JsonHelper.ReadFromJsonStream<Model.ApplicationInfo[]>(stream));
                    }
                }
            }

            // Are any of the config-supplied containers deployed?...
            if (this._targetList.Count < 1)
            {
                WriteToLogWithLevel(ObserverName,
                                    $"Will not observe resource consumption for containers as no configuration parameters have been supplied... " +
                                    $"| {this._nodeName}",
                                    LogLevel.Information);

                return false;
            }
            else if (this._targetList.Count == 1)
            {
                if (this._targetList[0].Target.ToLower() == "all")
                {
                    this._probeAllContainersRunningOnNode = true;
                    // TODO: Create a max count or populate a list of RUNNING containers to evaluate...
                }
            }

            return true;
        }

        private async Task GetAllWindowsContainersDiskInfoPSAsync(CancellationToken token)
        {
            if (!Directory.Exists(this.DockerWindowsContainerDataPath))
            {
                return;
            }

            token.ThrowIfCancellationRequested();

            await Task.Factory.StartNew(() =>
            {
                PowerShell PS = null;
                Runspace RS = null;

                try
                {
                    RS = RunspaceFactory.CreateRunspace();
                    RS.Open();
                    PS = PowerShell.Create();
                    PS.Runspace = RS;

                    token.ThrowIfCancellationRequested();
                    
                    var d = new DirectoryInfo(this.DockerWindowsContainerDataPath);

                    foreach (var folder in d.GetDirectories())
                    {
                        token.ThrowIfCancellationRequested();

                        // There could SEVERAL folders here, probably not a good idea to evaluate all...
                        // Best to determine which containers are actually running first...
                        if (!_probeAllContainersRunningOnNode && !this._targetList.Any(n => n.Target == folder.Name))
                        {
                            continue;
                        }

                        foreach (var file in folder.GetFiles())
                        {
                            token.ThrowIfCancellationRequested();

                            if (file.Extension == ".vhdx")
                            { 
                                PS.AddScript(PSGetContainerVHDXInfoCmd + file.FullName);
                                var results = PS.Invoke();
                                ulong diskSizeMB = (ulong)results.FirstOrDefault(prop => prop.Properties["Size"] != null).Properties["Size"].Value / 1024 /1024;
                                ulong currentDiskUsedMB = (ulong)results.FirstOrDefault(prop => prop.Properties["FileSize"] != null).Properties["FileSize"].Value / 1024 / 1024;
                                ulong freeMB = diskSizeMB - currentDiskUsedMB;
                                int usedPct = (int)((double)(diskSizeMB - freeMB) / diskSizeMB) * 100;
                                var data = new FabricResourceUsageData<int>(folder.Name);
                                data.Data.Add(usedPct);
                                this._containersDiskSpaceUsageData.Add(data);
                            }
                        }  
                    }
                }
                catch (Exception e)
                {
                    if (e is TaskCanceledException || e is OperationCanceledException)
                    {
                        WriteToLogWithLevel(ObserverName, "GetComputerInfoAsync task aborted...", LogLevel.Information);
                    }
                    else
                    {

                        HealthReporter.ReportServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                           ObserverName,
                                                           HealthState.Error,
                                                           $"Unhandled exception processing container resource usage information: {e.Message}: \n {e.StackTrace}");
                        throw;
                    }
                }
                finally
                {
                    RS?.Close();
                    RS?.Dispose();
                    PS?.Stop();
                    PS?.Dispose();
                    RS = null;
                    PS = null;
                }
            }).ConfigureAwait(true);
        }

        public override async Task ReportAsync(CancellationToken token)
        {

            await Task.Factory.StartNew(() =>
            {
                foreach (var targetContainer in this._targetList)
                {
                    token.ThrowIfCancellationRequested();

                    ProcessDataReportHealth(this._containersDiskSpaceUsageData, "Disk Space", targetContainer.CpuErrorLimitPct);
                    ProcessDataReportHealth(this._containersDiskSpaceUsageData, "Disk Space", targetContainer.CpuWarningLimitPct);
                }
            }, token).ConfigureAwait(true); ;
        }

        private void ProcessDataReportHealth<T>(List<FabricResourceUsageData<T>> usageData, string propertyName, T threshold)
        {
            int warnings = 0;

            foreach (var data in usageData)
            {
                this._token.ThrowIfCancellationRequested();

                // Log average data value to long-running store (CSV)...
                string containerName = data.Name;
                var fileName = "ContainerVhdx_" + containerName + "_" + NodeName;
                string stat = "Average";

                if (propertyName.Contains("Disk Space"))
                {
                    stat = "% Used";
                }

                DataLogger.LogData(fileName, NodeName, propertyName, stat, Math.Round(data.AverageDataValue, 2));
                /*
                if (!propertyName.Contains("Disk Space"))
                {
                    DataLogger.LogData(fileName, FabricServiceContext.NodeContext.NodeName, propertyName, "Peak", Math.Round(Convert.ToDouble(data.MaxDataValue), 2));
                }
                */
                if (data.IsUnhealthy(threshold))
                {
                    string message = "Exceeding threshold for ";

                    if (propertyName.Contains("Disk Space"))
                    {
                        message += "Disk Usage " + data.AverageDataValue + "%";
                    }
                    /*
                    else if (propertyName.Contains("Reads"))
                    {
                        message += "Avg. Disk sec/Read " + Math.Round(data.AverageDataValue, 4) + "ms";
                    }
                    else if (propertyName.Contains("Writes"))
                    {
                        message += "Avg. Disk sec/Write " + Math.Round(data.AverageDataValue, 4) + "ms";
                    }
                    */
                    // Emit a Health Report/Log Warning since there is close to no disk space available...
                    HealthReporter.ReportHealthToFabric(ObserverName,
                                                        NodeName,
                                                        containerName + ": " + message,
                                                        HealthState.Warning,
                                                        true,
                                                        this._timeToLiveWarning);
                    warnings++;
                    data.ActiveErrorOrWarning = true;
                    HasActiveFabricErrorOrWarning = true;
                }
                else
                {
                    if (data.ActiveErrorOrWarning)
                    {
                        // Emit an Ok Health Report to clear Fabric Health warning...
                        HealthReporter.ReportHealthToFabric(ObserverName,
                                                            NodeName,
                                                            data.Name + ": Usage below Warning threshold...",
                                                            HealthState.Ok);
                        // Reset health states...
                        data.ActiveErrorOrWarning = false;

                        // This means this observer created a Warning or Error SF Health Report 
                        HasActiveFabricErrorOrWarning = true;
                    }
                }

                // No need to keep data in memory...
                data.Data.Clear();
                data.Data.TrimExcess();
            }

            // empty the list...
            if (warnings == 0)
            {
                usageData.Clear();
                usageData.TrimExcess();
            }
        }
    }
}
