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
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.TelemetryLib;
using ConfigSettings = FabricObserver.Observers.Utilities.ConfigSettings;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    public abstract class ObserverBase : IObserver
    {
        private const int TtlAddMinutes = 5;
        private bool disposed;
        private Dictionary<string, (int DumpCount, DateTime LastDumpDate)> ServiceDumpCountDictionary;
        private readonly object lockObj = new object();

        // Process dump settings. Only AppObserver and Windows is supported. \\
        public string DumpsPath
        {
            get; set;
        }

        public int MaxDumps
        {
            get; set;
        }

        public TimeSpan MaxDumpsTimeWindow
        {
            get; set;
        } = TimeSpan.FromHours(4);

        public DumpType DumpType
        {
            get; set;
        } = DumpType.MiniPlus;

        // End AO procsess dump settings. \\

        public string ObserverName
        {
            get; set;
        }

        public bool IsObserverWebApiAppDeployed
        {
            get; set;
        }

        public string NodeName
        {
            get; set;
        }

        public string NodeType
        {
            get; set;
        }

        public ObserverHealthReporter HealthReporter
        {
            get;
        }

        public StatelessServiceContext FabricServiceContext
        {
            get;
        }

        public DateTime LastRunDateTime
        {
            get; set;
        }

        public TimeSpan RunDuration
        {
            get; set;
        }

        public CancellationToken Token
        {
            get; set;
        }

        public bool IsEnabled 
        { 
            get => ConfigurationSettings == null || ConfigurationSettings.IsEnabled;
            
            // default is observer enabled.
            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.IsEnabled = value;
                }
            }
        }

        public bool IsTelemetryEnabled
        {
            get
            {
                if (ConfigurationSettings != null)
                {
                    return IsTelemetryProviderEnabled && ConfigurationSettings.IsObserverTelemetryEnabled;
                }

                return false;
            }
        }

        public bool IsEtwEnabled
        {
            get
            {
                if (ConfigurationSettings != null)
                {
                    return ObserverLogger.EnableETWLogging && ConfigurationSettings.IsObserverEtwEnabled;
                }

                return false;
            }
        }

        public bool IsUnhealthy
        {
            get; set;
        }

        public ConfigSettings ConfigurationSettings
        {
            get; set;
        }

        public bool EnableVerboseLogging
        {
            get => ConfigurationSettings != null && ConfigurationSettings.EnableVerboseLogging;

            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.EnableVerboseLogging = value;
                }
            }
        }

        public bool EnableCsvLogging
        {
            get
            {
                if (ConfigurationSettings == null)
                {
                    return false;
                }

                if (CsvFileLogger == null && ConfigurationSettings.EnableCsvLogging)
                {
                    InitializeCsvLogger();
                }

                return ConfigurationSettings.EnableCsvLogging;
            }

            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.EnableCsvLogging = value;
                }
            }
        }

        /// <summary>
        /// The maximum number of days an archived observer log file will be stored. After this time, it will be deleted from disk.
        /// </summary>
        public int MaxLogArchiveFileLifetimeDays
        {
            get; set;
        }

        /// <summary>
        /// The maximum number of days a csv file produced by CsvLogger will be stored. After this time, it will be deleted from disk.
        /// </summary>
        public int MaxCsvArchiveFileLifetimeDays
        {
            get; set;
        }

        public Logger ObserverLogger
        {
            get; set;
        }

        public DataTableFileLogger CsvFileLogger
        {
            get; set;
        }

        // Each derived Observer can set this to maintain health status across iterations.
        public bool HasActiveFabricErrorOrWarning
        {
            get; set;
        }

        public ConcurrentQueue<string> AppNames
        {
            get; set;
        } = new ConcurrentQueue<string>();

        public int MonitoredServiceProcessCount
        {
            get; set;
        }

        public int MonitoredAppCount
        {
            get; set;
        }

        public int CurrentErrorCount
        {
            get; set;
        }

        public int CurrentWarningCount
        {
            get; set;
        }

        public TimeSpan RunInterval
        {
            get => ConfigurationSettings?.RunInterval ?? TimeSpan.MinValue;

            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.RunInterval = value;
                }
            }
        }

        public TimeSpan AsyncClusterOperationTimeoutSeconds
        {
            get; set;
        } = TimeSpan.FromSeconds(60);

        public int DataCapacity
        {
            get => ConfigurationSettings?.DataCapacity ?? 10;

            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.DataCapacity = value;
                }
            }
        }

        public bool UseCircularBuffer
        {
            get => ConfigurationSettings != null && ConfigurationSettings.UseCircularBuffer;

            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.UseCircularBuffer = value;
                }
            }
        }

        public TimeSpan MonitorDuration
        {
            get => ConfigurationSettings?.MonitorDuration ?? TimeSpan.MinValue;
            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.MonitorDuration = value;
                }
            }
        }

        protected bool IsTelemetryProviderEnabled
        {
            get; set;
        }

        protected ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        protected bool IsEtwProviderEnabled
        {
            get; set;
        }

        protected FabricClient FabricClientInstance
        {
            get; set;
        }

        protected string ConfigurationSectionName
        {
            get;
        }

        public CsvFileWriteFormat CsvWriteFormat 
        {
            get; set; 
        }

        /// <summary>
        /// Base type constructor for all observers (both built-in and plugin impls).
        /// </summary>
        /// <param name="fabricClient">FO employs exactly one instance of a FabricClient object. This protects against memory abuse. FO will inject the instance when the application starts.</param>
        /// <param name="statelessServiceContext">The ServiceContext instance for FO, which is a Stateless singleton (1 partition) service that runs on all nodes in an SF cluster. FO will inject this instance when the application starts.</param>
        protected ObserverBase(FabricClient fabricClient, StatelessServiceContext statelessServiceContext)
        {
            ObserverName = GetType().Name;
            ConfigurationSectionName = ObserverName + "Configuration";
            FabricClientInstance = fabricClient;
            FabricServiceContext = statelessServiceContext;
            NodeName = FabricServiceContext.NodeContext.NodeName;
            NodeType = FabricServiceContext.NodeContext.NodeType;
            SetObserverConfiguration();

            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.ObserverLogPathParameter);
            
            if (!string.IsNullOrWhiteSpace(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "fabric_observer_logs");
                logFolderBasePath = logFolderBase;
            }

            ObserverLogger = new Logger(ObserverName, logFolderBasePath, MaxLogArchiveFileLifetimeDays)
            {
                EnableETWLogging = IsEtwProviderEnabled
            };

            ConfigurationSettings = new ConfigSettings(
                    FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config")?.Settings,
                    ConfigurationSectionName);

            ObserverLogger.EnableVerboseLogging = ConfigurationSettings.EnableVerboseLogging;
            HealthReporter = new ObserverHealthReporter(ObserverLogger, fabricClient);

            // This is so EnableVerboseLogging can (and should) be set to false and the ObserverWebApi service will still function.
            // If you renamed the application, then you must set the ObserverWebApiEnabled parameter to true in ObserverManagerConfiguration section 
            // in Settings.xml. Note the || in the conditional check below.
            IsObserverWebApiAppDeployed = 
                bool.TryParse(
                    GetSettingParameterValue(
                        ObserverConstants.ObserverManagerConfigurationSectionName,
                        ObserverConstants.ObserverWebApiEnabled), out bool obsWeb) && obsWeb && IsObserverWebApiAppInstalled();
        }

        /// <summary>
        /// This abstract method must be implemented by deriving type. This is called by ObserverManager on any observer instance that is enabled and ready to run.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A Task.</returns>
        public abstract Task ObserveAsync(CancellationToken token);

        /// <summary>
        /// This abstract method must be implemented by deriving type. This is called by an observer's ObserveAsync method when it is time to report on observations.
        /// It exists as its own abstract method to force a simple pattern in observer implementations: Initiate/orchestrate observations in one method. Do related health reporting in another.
        /// This is a simple design choice that is enforced.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A Task.</returns>
        public abstract Task ReportAsync(CancellationToken token);

        /// <summary>
        /// Gets a parameter value from the specified section.
        /// </summary>
        /// <param name="sectionName">Name of the section.</param>
        /// <param name="parameterName">Name of the parameter.</param>
        /// <param name="defaultValue">Default value.</param>
        /// <returns>parameter value.</returns>
        public string GetSettingParameterValue(string sectionName, string parameterName, string defaultValue = null)
        {
            if (string.IsNullOrWhiteSpace(sectionName) || string.IsNullOrWhiteSpace(parameterName))
            {
                return null;
            }

            try
            {
                ConfigurationPackage serviceConfiguration = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

                if (serviceConfiguration == null)
                {
                    return null;
                }

                if (serviceConfiguration.Settings.Sections.All(sec => sec.Name != sectionName))
                {
                    return null;
                }

                if (serviceConfiguration.Settings.Sections[sectionName].Parameters.All(param => param.Name != parameterName))
                {
                    return null;
                }

                string setting = serviceConfiguration.Settings.Sections[sectionName].Parameters[parameterName]?.Value;

                if (string.IsNullOrWhiteSpace(setting) && defaultValue != null)
                {
                    return defaultValue;
                }

                return setting;
            }
            catch (ArgumentException)
            {

            }

            return null;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        // Windows process dmp creator.\\

        /// <summary>
        /// This function will create Windows process dumps in supplied location if there is enough disk space available.
        /// Only AppObserver is supported today since it will generate memory dumps for the service processes it monitors when an Error threshold has been breached.
        /// In the future, this may be applied to FabricSystemObserver as well, thus this code is located in ObserverBase..
        /// This function runs if you set dumpProcessOnError to true in AppObserver.config.json AND enable process dumps in AppObserver configuration in ApplicationManifest.xml.
        /// </summary>
        /// <param name="processId">Process id of the target process to dump.</param>
        /// <param name="procName">Process name.</param>
        /// <param name="metric">The name of the metric threshold that was breached, leading to dump.</param>
        /// <returns>true or false if the operation succeeded.</returns>
        public bool DumpWindowsServiceProcess(int processId, string procName, string metric)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }

            // Must provide a process name.. & do not try and dump yourself..
            if (string.IsNullOrEmpty(procName) || procName == ObserverConstants.FabricObserverName)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(DumpsPath))
            {
                return false;
            }

            if (!Directory.Exists(DumpsPath))
            {
                try
                {
                    Directory.CreateDirectory(DumpsPath);
                }
                catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
                {
                    ObserverLogger.LogWarning($"Can't create dump directory for path {DumpsPath}. Will not generate dmp file for {procName}. " +
                                              $"Error info:{Environment.NewLine}{e}");
                    return false;
                }
            }

            if (ServiceDumpCountDictionary == null)
            {
                ServiceDumpCountDictionary = new Dictionary<string, (int DumpCount, DateTime LastDump)>(5);
            }

            StringBuilder sb = new StringBuilder(metric);
            string metricName = sb.Replace(" ", string.Empty)
                                  .Replace("Total", string.Empty)
                                  .Replace("MB", string.Empty)
                                  .Replace("%", string.Empty)
                                  .Replace("Active", string.Empty)
                                  .Replace("Allocated", string.Empty)
                                  .Replace("Length", string.Empty)
                                  .Replace("Consumption", string.Empty)
                                  .Replace("Time", string.Empty)
                                  .Replace("TCP", string.Empty).ToString();
            sb.Clear();
            string dumpKey = $"{procName}_{metricName}";
            string dumpFileName = $"{dumpKey}_{NodeName}";

            try
            {
                if (Directory.Exists(DumpsPath) && Directory.GetFiles(DumpsPath, $"{dumpKey}*.dmp", SearchOption.AllDirectories).Length >= MaxDumps)
                {
                    ObserverLogger.LogWarning($"Reached maximum number({MaxDumps}) of {dumpKey} dmp files stored on local disk. Will not create dmp file. " +
                                              $"If enabled, please make sure that AzureStorageObserver is configured correctly. " +
                                              $"Will attempt to delete old (>= 1 day) local files now.");

                    // Clean out old dmp files, if any. Generally, there will only be some dmp files remaining on disk if customer has not configured
                    // AzureStorageObserver correctly or some error occurred during some stage of the upload process.
                    ObserverLogger.TryCleanFolder(DumpsPath, $"{dumpKey}*.dmp", TimeSpan.FromDays(1));
                    return false;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {

            }

            if (!ServiceDumpCountDictionary.ContainsKey(dumpKey))
            {
                ServiceDumpCountDictionary.Add(dumpKey, (0, DateTime.UtcNow));
            }
            else if (DateTime.UtcNow.Subtract(ServiceDumpCountDictionary[dumpKey].LastDumpDate) >= MaxDumpsTimeWindow)
            {
                ServiceDumpCountDictionary[dumpKey] = (0, DateTime.UtcNow);
            }
            else if (ServiceDumpCountDictionary[dumpKey].DumpCount >= MaxDumps)
            {
                ObserverLogger.LogWarning($"Reached maximum number of process dumps({MaxDumps}) for key {dumpKey} " +
                                          $"within {MaxDumpsTimeWindow.TotalHours} hour period. Will not create dmp file.");
                return false;
            }

            NativeMethods.MINIDUMP_TYPE miniDumpType;

            switch (DumpType)
            {
                case DumpType.Full:
                    miniDumpType = NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemory |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules;
                    break;

                case DumpType.MiniPlus:
                    miniDumpType = NativeMethods.MINIDUMP_TYPE.MiniDumpWithPrivateReadWriteMemory |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules;
                    break;

                case DumpType.Mini:
                    miniDumpType = NativeMethods.MINIDUMP_TYPE.MiniDumpWithIndirectlyReferencedMemory |
                                   NativeMethods.MINIDUMP_TYPE.MiniDumpScanMemory;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(DumpType), DumpType, null);
            }

            string dumpFilePath = null;

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (dumpFileName == string.Empty)
                    {
                        dumpFileName = process.ProcessName;
                    }

                    IntPtr processHandle = process.Handle;
                    dumpFileName += $"_{DateTime.Now:ddMMyyyyHHmmssFFF}.dmp";

                    // Check disk space availability before writing dump file.
                    string driveName = DumpsPath.Substring(0, 2);

                    if (DiskUsage.GetCurrentDiskSpaceUsedPercent(driveName) > 90)
                    {
                        ObserverLogger.LogWarning("Not enough disk space available for dump file creation.");
                        return false;
                    }

                    dumpFilePath = Path.Combine(DumpsPath, dumpFileName);

                    using (FileStream file = File.Create(dumpFilePath))
                    {
                        lock (lockObj)
                        {
                            if (!NativeMethods.MiniDumpWriteDump(
                                                processHandle,
                                                (uint)processId,
                                                file.SafeFileHandle,
                                                miniDumpType,
                                                IntPtr.Zero,
                                                IntPtr.Zero,
                                                IntPtr.Zero))
                            {
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            }

                            if (!string.IsNullOrWhiteSpace(metric))
                            {
                                ServiceDumpCountDictionary[dumpKey] = (ServiceDumpCountDictionary[dumpKey].DumpCount + 1, DateTime.UtcNow);
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception e) when (
                    e is ArgumentException ||
                    e is InvalidOperationException ||
                    e is IOException ||
                    e is PlatformNotSupportedException ||
                    e is UnauthorizedAccessException ||
                    e is Win32Exception)
            {
                ObserverLogger.LogWarning(
                    $"Failure generating Windows process dump file {dumpFileName} with error:{Environment.NewLine}{e}");

                if (File.Exists(dumpFilePath))
                {
                    // This means a partial file may have been created (like the process went away during dump capture). Delete it.
                    try
                    {
                        Retry.Do(() => File.Delete(Path.Combine(DumpsPath, dumpFileName)), TimeSpan.FromSeconds(1), Token);
                    }
                    catch(AggregateException)
                    {
                        // Couldn't delete file.
                        // Retry.Do throws AggregateException containing list of exceptions caught. In this case, we don't really care..
                    }
                }
            }
           
            return false;
        }

        /// <summary>
        /// This function *only* processes *numeric* data held in (FabricResourceUsageData (FRUD) instances and generates Application or Node level Health Reports depending on supplied Error and Warning thresholds. 
        /// </summary>
        /// <typeparam name="T">Generic: This represents the numeric type of data this function will operate on.</typeparam>
        /// <param name="data">FabricResourceUsageData (FRUD) instance.</param>
        /// <param name="thresholdError">Error threshold (numeric)</param>
        /// <param name="thresholdWarning">Warning threshold (numeric)</param>
        /// <param name="healthReportTtl">Health report Time to Live (TimeSpan)</param>
        /// <param name="EntityType">HealthReport type. Note, only Application and Node health report types are supported by this function.</param>
        /// <param name="replicaOrInstance">Replica or Instance information contained in a type.</param>
        /// <param name="dumpOnError">Whether or not to dump process if Error threshold has been reached.</param>
        public void ProcessResourceDataReportHealth<T>(
                           FabricResourceUsageData<T> data,
                           T thresholdError,
                           T thresholdWarning,
                           TimeSpan healthReportTtl,
                           EntityType EntityType = EntityType.Node,
                           ReplicaOrInstanceMonitoringInfo replicaOrInstance = null,
                           bool dumpOnError = false) where T : struct
        {
            if (data == null)
            {
                return;
            }

            if (EntityType != EntityType.Application && EntityType != EntityType.Node)
            {
                ObserverLogger.LogWarning($"ProcessResourceDataReportHealth: Unsupported HealthReport type -> {Enum.GetName(typeof(EntityType), EntityType)}");
                return;
            }

            string thresholdName = "Warning";
            bool warningOrError = false;
            string name = string.Empty, id, drive = string.Empty;
            int procId = 0;
            T threshold = thresholdWarning;
            HealthState healthState = HealthState.Ok;
            Uri appName = null;
            Uri serviceName = null;
            TelemetryData telemetryData;

            if (EntityType == EntityType.Application)
            {
                if (replicaOrInstance != null)
                {
                    // Create a unique id which will be used for health Warnings and OKs (clears).
                    appName = replicaOrInstance.ApplicationName;
                    serviceName = replicaOrInstance.ServiceName;
                    name = serviceName.OriginalString.Replace($"{appName.OriginalString}/", string.Empty);
                    procId = (int)replicaOrInstance.HostProcessId;
                }
                else // System service report from FabricSystemObserver.
                {
                    appName = new Uri(ObserverConstants.SystemAppName);
                    name = data.Id;

                    try
                    {
                        procId = (int)Process.GetProcessesByName(name).First()?.Id;
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is PlatformNotSupportedException || e is Win32Exception)
                    {

                    }
                }

                id = $"{NodeName}_{name}_{data.Property.Replace(" ", string.Empty)}";

                // The health event description will be a serialized instance of telemetryData,
                // so it should be completely constructed (filled with data) regardless
                // of user telemetry settings.
                telemetryData = new TelemetryData()
                {
                    ApplicationName = appName?.OriginalString ?? string.Empty,
                    ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                    EntityType = EntityType.Application,
                    NodeName = NodeName,
                    NodeType = FabricServiceContext.NodeContext.NodeType,
                    ObserverName = ObserverName,
                    Metric = data.Property,
                    Value = data.AverageDataValue,
                    PartitionId = replicaOrInstance != null ? replicaOrInstance.PartitionId : default,
                    ProcessId = procId,
                    ReplicaId = replicaOrInstance != null ? replicaOrInstance.ReplicaOrInstanceId : 0,
                    ServiceName = serviceName?.OriginalString ?? string.Empty,
                    SystemServiceProcessName = appName?.OriginalString == ObserverConstants.SystemAppName ? name : string.Empty,
                    Source = ObserverName
                };

                // If the source issue is from FSO, then set the SystemServiceProcessName on TD instance.
                if (appName != null && appName.OriginalString == ObserverConstants.SystemAppName)
                {
                    telemetryData.SystemServiceProcessName = name;
                }

                // Container
                if (!string.IsNullOrWhiteSpace(replicaOrInstance?.ContainerId))
                {
                    telemetryData.ContainerId = replicaOrInstance.ContainerId;
                }

                // Telemetry - This is informational, per reading telemetry, healthstate is irrelevant here. If the process has children, then don't emit this raw data since it will already
                // be contained in the ChildProcessTelemetry data instances and AppObserver will have already emitted it.
                // Enable this for your observer if you want to send data to ApplicationInsights or LogAnalytics for each resource usage observation it makes per specified metric.
                if (IsTelemetryEnabled && replicaOrInstance?.ChildProcesses == null)
                {
                     _ = TelemetryClient?.ReportMetricAsync(telemetryData, Token).ConfigureAwait(false);
                }

                // ETW - This is informational, per reading EventSource tracing, healthstate is irrelevant here. If the process has children, then don't emit this raw data since it will already
                // be contained in the ChildProcessTelemetry data instances and AppObserver will have already emitted it.
                // Enable this for your observer if you want to log etw (which can then be read by some agent that will send it to some endpoint)
                // for each resource usage observation it makes per specified metric.
                if (IsEtwEnabled && replicaOrInstance?.ChildProcesses == null)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            ApplicationName = appName?.OriginalString ?? string.Empty,
                            ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = Enum.GetName(typeof(EntityType), EntityType.Application),
                            NodeName,
                            NodeType = FabricServiceContext.NodeContext.NodeType,
                            ObserverName,
                            Metric = data.Property,
                            Value = data.AverageDataValue,
                            PartitionId = replicaOrInstance?.PartitionId != null ? replicaOrInstance?.PartitionId.ToString() : string.Empty,
                            ProcessId = procId,
                            ReplicaId = replicaOrInstance?.ReplicaOrInstanceId != null ? replicaOrInstance.ReplicaOrInstanceId : 0,
                            ServiceName = serviceName?.OriginalString ?? string.Empty,
                            Source = ObserverName,
                            SystemServiceProcessName = appName?.OriginalString == ObserverConstants.SystemAppName ? name : string.Empty
                        });
                }
            }
            else
            {
                drive = string.Empty;
                id = data.Id;

                if (ObserverName == ObserverConstants.DiskObserverName)
                {
                    drive = $"{id}: ";

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && data.Property != ErrorWarningProperty.FolderSizeMB)
                    {
                        drive = $"{id.Remove(1, 2)}: ";
                    }
                }

                // Report raw data.
                telemetryData = new TelemetryData()
                {
                    ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                    EntityType = EntityType.Node,
                    NodeName = NodeName,
                    NodeType = FabricServiceContext.NodeContext.NodeType,
                    ObserverName = ObserverName,
                    Metric = $"{drive}{data.Property}",
                    Source = ObserverName,
                    Value = data.AverageDataValue
                };

                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportMetricAsync(telemetryData, Token).ConfigureAwait(false);
                }

                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            ClusterInformation.ClusterInfoTuple.ClusterId,
                            NodeName,
                            FabricServiceContext.NodeContext.NodeType,
                            ObserverName,
                            Metric = $"{drive}{data.Property}",
                            Source = ObserverName,
                            Value = data.AverageDataValue
                        });
                }
            }

            // Health Error
            if (data.IsUnhealthy(thresholdError))
            {
                thresholdName = "Error";
                threshold = thresholdError;
                warningOrError = true;
                healthState = HealthState.Error;

                // FO emits a health report each time it detects an Error threshold breach for some metric for some supported entity (target).
                // Don't increment this internal counter if the target is already in error for the same metric.
                if (!data.ActiveErrorOrWarning)
                {
                    CurrentErrorCount++;
                }

                // **Windows-only**. This is used by AppObserver, but makes sense to be
                // part of the base class for future use, like for plugins that manage service processes.
                if (replicaOrInstance != null && dumpOnError && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!string.IsNullOrWhiteSpace(DumpsPath))
                    {
                        if (ServiceDumpCountDictionary == null)
                        {
                            ServiceDumpCountDictionary = new Dictionary<string, (int DumpCount, DateTime LastDump)>(5);
                        }

                        try
                        {
                            int pid = (int)replicaOrInstance.HostProcessId;

                            using (var proc = Process.GetProcessById(pid))
                            {
                                string procName = proc?.ProcessName;

                                lock (lockObj)
                                {
                                    _ = DumpWindowsServiceProcess(pid, procName, data.Property);
                                }
                            }
                        }
                        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                        {
                            ObserverLogger.LogWarning($"Unable to generate dmp file:{Environment.NewLine}{e}");
                        }
                    }
                }
            }

            // Health Warning
            if (!warningOrError && data.IsUnhealthy(thresholdWarning))
            {
                warningOrError = true;
                healthState = HealthState.Warning;

                // FO emits a health report each time it detects a Warning threshold breach for some metric for some supported entity (target).
                // Don't increment this internal counter if the target is already in warning for the same metric.
                if (!data.ActiveErrorOrWarning)
                {
                    CurrentWarningCount++;
                }
            }

            if (warningOrError)
            {
                string errorWarningCode = null;

                // Ephemeral port sugar for event description.
                string dynamicRange = string.Empty;
                string totalPorts = string.Empty;
                int Low = 0, High = 0;

                if (data.Property.Contains("Ephemeral"))
                {
                    (Low, High) = OSInfoProvider.Instance.TupleGetDynamicPortRange();
                    dynamicRange = $" (dynamic range: {Low}-{High})";

                    if (data.Property.Contains("Percent"))
                    {
                        int total = High - Low;

                        if (total > 0)
                        {
                            int count = (int)(data.AverageDataValue / 100 * total);
                            totalPorts = $" ({count}/{total})";
                        }
                    }
                }

                switch (data.Property)
                {
                    case ErrorWarningProperty.CpuTime when EntityType == EntityType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorCpuPercent : FOErrorWarningCodes.AppWarningCpuPercent;
                        break;

                    case ErrorWarningProperty.CpuTime:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorCpuPercent : FOErrorWarningCodes.NodeWarningCpuPercent;
                        break;

                    case ErrorWarningProperty.DiskSpaceUsagePercentage:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorDiskSpacePercent : FOErrorWarningCodes.NodeWarningDiskSpacePercent;
                        break;

                    case ErrorWarningProperty.DiskSpaceUsageMb:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorDiskSpaceMB : FOErrorWarningCodes.NodeWarningDiskSpaceMB;
                        break;

                    case ErrorWarningProperty.FolderSizeMB:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorFolderSizeMB : FOErrorWarningCodes.NodeWarningFolderSizeMB;
                        break;

                    case ErrorWarningProperty.MemoryConsumptionMb when EntityType == EntityType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorMemoryMB : FOErrorWarningCodes.AppWarningMemoryMB;
                        break;

                    case ErrorWarningProperty.MemoryConsumptionMb:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorMemoryMB : FOErrorWarningCodes.NodeWarningMemoryMB;
                        break;

                    case ErrorWarningProperty.MemoryConsumptionPercentage when replicaOrInstance != null:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorMemoryPercent : FOErrorWarningCodes.AppWarningMemoryPercent;
                        break;

                    case ErrorWarningProperty.MemoryConsumptionPercentage:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorMemoryPercent : FOErrorWarningCodes.NodeWarningMemoryPercent;
                        break;

                    case ErrorWarningProperty.DiskAverageQueueLength:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorDiskAverageQueueLength : FOErrorWarningCodes.NodeWarningDiskAverageQueueLength;
                        break;

                    case ErrorWarningProperty.ActiveFirewallRules:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.ErrorTooManyFirewallRules : FOErrorWarningCodes.WarningTooManyFirewallRules;
                        break;

                    case ErrorWarningProperty.ActiveTcpPorts when EntityType == EntityType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyActiveTcpPorts : FOErrorWarningCodes.AppWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.ActiveTcpPorts:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTooManyActiveTcpPorts : FOErrorWarningCodes.NodeWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.ActiveEphemeralPorts when EntityType == EntityType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyActiveEphemeralPorts : FOErrorWarningCodes.AppWarningTooManyActiveEphemeralPorts;
                        break;

                    case ErrorWarningProperty.ActiveEphemeralPorts:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTooManyActiveEphemeralPorts : FOErrorWarningCodes.NodeWarningTooManyActiveEphemeralPorts;
                        break;

                    case ErrorWarningProperty.ActiveEphemeralPortsPercentage when EntityType == EntityType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorActiveEphemeralPortsPercent : FOErrorWarningCodes.AppWarningActiveEphemeralPortsPercent;
                        break;

                    case ErrorWarningProperty.ActiveEphemeralPortsPercentage:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorActiveEphemeralPortsPercent : FOErrorWarningCodes.NodeWarningActiveEphemeralPortsPercent;
                        break;

                    case ErrorWarningProperty.AllocatedFileHandles when EntityType == EntityType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyOpenFileHandles : FOErrorWarningCodes.AppWarningTooManyOpenFileHandles;
                        break;

                    case ErrorWarningProperty.ThreadCount when EntityType == EntityType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyThreads : FOErrorWarningCodes.AppWarningTooManyThreads;
                        break;

                    // Internal monitor for Windows KVS LVID consumption. Only Warning state is supported. This is a non-configurable monitor.
                    case ErrorWarningProperty.KvsLvidsPercent when EntityType == EntityType.Application:
                        errorWarningCode = FOErrorWarningCodes.AppWarningKvsLvidsPercentUsed;
                        break;

                    case ErrorWarningProperty.AllocatedFileHandles:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTooManyOpenFileHandles : FOErrorWarningCodes.NodeWarningTooManyOpenFileHandles;
                        break;

                    case ErrorWarningProperty.AllocatedFileHandlesPct:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTotalOpenFileHandlesPercent : FOErrorWarningCodes.NodeWarningTotalOpenFileHandlesPercent;
                        break;
                }

                var healthMessage = new StringBuilder();
                string childProcMsg = string.Empty;

                if (replicaOrInstance != null && replicaOrInstance.ChildProcesses != null)
                {
                    childProcMsg = $" Note that {serviceName.OriginalString} has spawned one or more child processes ({replicaOrInstance.ChildProcesses.Count}). " +
                                   $"Their cumulative impact on {name}'s resource usage has been applied.";
                }

                _ = healthMessage.Append($"{drive}{data.Property}{dynamicRange} has exceeded the specified {thresholdName} limit ({threshold}{data.Units})");
                _ = healthMessage.Append($" - {data.Property}: {data.AverageDataValue}{data.Units}{totalPorts}");
                
                if (childProcMsg != string.Empty)
                {
                    _ = healthMessage.Append(childProcMsg);
                }

                // The health event description will be a serialized instance of telemetryData,
                // so it should be completely constructed (filled with data) regardless
                // of user telemetry settings.
                telemetryData.ApplicationName = appName?.OriginalString ?? string.Empty;
                telemetryData.Code = errorWarningCode;

                if (replicaOrInstance != null && !string.IsNullOrWhiteSpace(replicaOrInstance.ContainerId))
                {
                    telemetryData.ContainerId = replicaOrInstance.ContainerId;
                }

                telemetryData.HealthState = healthState;
                telemetryData.Description = healthMessage.ToString();
                telemetryData.Source = $"{ObserverName}({errorWarningCode})";
                telemetryData.Property = id;

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(telemetryData, Token).ConfigureAwait(false);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            ApplicationName = appName?.OriginalString ?? string.Empty,
                            ClusterInformation.ClusterInfoTuple.ClusterId,
                            Code = errorWarningCode,
                            ContainerId = replicaOrInstance?.ContainerId != null ? replicaOrInstance.ContainerId : string.Empty,
                            EntityType = !string.IsNullOrWhiteSpace(appName?.OriginalString) ? "Application" : "Node",
                            HealthEventProperty = id,
                            HealthState = Enum.GetName(typeof(HealthState), healthState),
                            Description = healthMessage.ToString(),
                            Metric = $"{drive}{data.Property}",
                            NodeName,
                            NodeType = FabricServiceContext.NodeContext.NodeType,
                            ObserverName,
                            PartitionId = replicaOrInstance?.PartitionId != null ? replicaOrInstance.PartitionId.ToString() : string.Empty,
                            ProcessId = procId,
                            ReplicaId = replicaOrInstance?.ReplicaOrInstanceId != null ? replicaOrInstance.ReplicaOrInstanceId : 0,
                            ServiceName = serviceName?.OriginalString ?? string.Empty,
                            Source = $"{ObserverName}({errorWarningCode})",
                            SystemServiceProcessName = appName?.OriginalString == ObserverConstants.SystemAppName ? name : string.Empty,
                            Value = data.AverageDataValue
                        });
                }

                var healthReport = new HealthReport
                {
                    AppName = appName,
                    Code = errorWarningCode,
                    EmitLogEvent = EnableVerboseLogging || IsObserverWebApiAppDeployed,
                    HealthData = telemetryData,
                    HealthMessage = healthMessage.ToString(),
                    HealthReportTimeToLive = healthReportTtl,
                    ReportType = EntityType,
                    State = healthState,
                    NodeName = NodeName,
                    Observer = ObserverName,
                    Property = id,
                    ResourceUsageDataProperty = data.Property,
                    SourceId = $"{ObserverName}({errorWarningCode})"
                };

                if (appName != null && AppNames.All(a => a != appName.OriginalString))
                {
                    AppNames.Enqueue(appName.OriginalString);
                }

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Set internal health state info on data instance.
                data.ActiveErrorOrWarning = true;
                data.ActiveErrorOrWarningCode = errorWarningCode;

                // This means this observer created a Warning or Error SF Health Report
                HasActiveFabricErrorOrWarning = true;

                // Clean up sb.
                _ = healthMessage.Clear();
                healthMessage = null;
            }
            else
            {
                if (data.ActiveErrorOrWarning)
                {
                    // The health event description will be a serialized instance of telemetryData,
                    // so it should be completely constructed (filled with data) regardless
                    // of user telemetry settings.
                    telemetryData.ApplicationName = appName?.OriginalString ?? string.Empty;
                    telemetryData.Code = FOErrorWarningCodes.Ok;

                    if (replicaOrInstance != null && !string.IsNullOrWhiteSpace(replicaOrInstance.ContainerId))
                    {
                        telemetryData.ContainerId = replicaOrInstance.ContainerId;
                    }

                    telemetryData.HealthState = HealthState.Ok;
                    telemetryData.Property = id;
                    telemetryData.Description = $"{data.Property} is now within normal/expected range.";
                    telemetryData.Metric = data.Property;
                    telemetryData.Source = $"{ObserverName}({data.ActiveErrorOrWarningCode})";
                    telemetryData.Value = data.AverageDataValue;

                    // Telemetry
                    if (IsTelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportHealthAsync(telemetryData, Token);
                    }

                    // ETW.
                    if (IsEtwEnabled)
                    {
                        ObserverLogger.LogEtw(
                            ObserverConstants.FabricObserverETWEventName,
                            new
                            {
                                ApplicationName = appName != null ? appName.OriginalString : string.Empty,
                                ClusterInformation.ClusterInfoTuple.ClusterId,
                                Code = data.ActiveErrorOrWarningCode,
                                ContainerId = replicaOrInstance?.ContainerId != null ? replicaOrInstance.ContainerId : string.Empty,
                                HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                                HealthEventProperty = id,
                                Description = $"{data.Property} is now within normal/expected range.",
                                Metric = data.Property,
                                NodeName,
                                NodeType = FabricServiceContext.NodeContext.NodeType,
                                ObserverName,
                                PartitionId = replicaOrInstance?.PartitionId != null ? replicaOrInstance.PartitionId.ToString() : string.Empty,
                                ProcessId = procId,
                                ReplicaId = replicaOrInstance?.ReplicaOrInstanceId != null ? replicaOrInstance.ReplicaOrInstanceId : 0,
                                ServiceName = name ?? string.Empty,
                                Source = $"{ObserverName}({data.ActiveErrorOrWarningCode})",
                                SystemServiceProcessName = appName?.OriginalString == ObserverConstants.SystemAppName ? name : string.Empty,
                                Value = data.AverageDataValue
                            });
                    }

                    var healthReport = new HealthReport
                    {
                        AppName = appName,
                        Code = data.ActiveErrorOrWarningCode,
                        EmitLogEvent = EnableVerboseLogging || IsObserverWebApiAppDeployed,
                        HealthData = telemetryData,
                        HealthMessage = $"{data.Property} is now within normal/expected range.",
                        HealthReportTimeToLive = default,
                        ReportType = EntityType,
                        State = HealthState.Ok,
                        NodeName = NodeName,
                        Observer = ObserverName,
                        Property = id,
                        ResourceUsageDataProperty = data.Property,
                        SourceId = $"{ObserverName}({data.ActiveErrorOrWarningCode})"
                    };

                    // Emit an Ok Health Report to clear Fabric Health warning.
                    HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // Reset health states.
                    data.ActiveErrorOrWarning = false;
                    data.ActiveErrorOrWarningCode = FOErrorWarningCodes.Ok;
                    HasActiveFabricErrorOrWarning = false;
                }
            }

            data.ClearData();
        }

        /// <summary>
        /// Computes TTL for an observer's health reports based on how long it takes an observer to run, time differential between runs,
        /// observer loop sleep time, plus a little more time to guarantee that a health report will remain active until the next time the observer runs.
        /// Note that if you set a RunInterval on an observer, that will be reflected here and the Warning will last for that amount of time at least.
        /// </summary>
        /// <returns>TimeSpan that contains the TTL value.</returns>
        public TimeSpan GetHealthReportTimeToLive()
        {
            _ = int.TryParse(
                    GetSettingParameterValue(
                        ObserverConstants.ObserverManagerConfigurationSectionName,
                        ObserverConstants.ObserverLoopSleepTimeSeconds),
                    out int obsSleepTime);

            // First run.
            if (LastRunDateTime == DateTime.MinValue)
            {
                return TimeSpan.FromSeconds(obsSleepTime)
                        .Add(TimeSpan.FromMinutes(TtlAddMinutes))
                        .Add(RunInterval > TimeSpan.MinValue ? RunInterval : TimeSpan.Zero);
            }

            return DateTime.Now.Subtract(LastRunDateTime)
                    .Add(TimeSpan.FromSeconds(RunDuration > TimeSpan.MinValue ? RunDuration.TotalSeconds : 0))
                    .Add(TimeSpan.FromSeconds(obsSleepTime));
        }

        // This is here so each Observer doesn't have to implement IDisposable.
        // Just override in observer impls that do implement IDisposable.
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                disposed = true;
            }
        }

        private void SetObserverConfiguration()
        {
            // Archive file lifetime - ObserverLogger files.
            if (int.TryParse(GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.MaxArchivedLogFileLifetimeDays), out int maxFileArchiveLifetime))
            {
                MaxLogArchiveFileLifetimeDays = maxFileArchiveLifetime;
            }

            // ETW
            if (bool.TryParse(GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.EnableETWProvider), out bool etwProviderEnabled))
            {
                IsEtwProviderEnabled = etwProviderEnabled;
            }

            // Telemetry.
            if (bool.TryParse(GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.TelemetryEnabled), out bool telemEnabled))
            {
                IsTelemetryProviderEnabled = telemEnabled;
            }

            if (!IsTelemetryProviderEnabled)
            {
                return;
            }

            string telemetryProviderType = GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.TelemetryProviderType);

            if (string.IsNullOrWhiteSpace(telemetryProviderType))
            {
                IsTelemetryProviderEnabled = false;

                return;
            }

            if (!Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
            {
                IsTelemetryProviderEnabled = false;

                return;
            }

            switch (telemetryProvider)
            {
                case TelemetryProviderType.AzureLogAnalytics:
                        
                    string logAnalyticsLogType =
                        GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.LogAnalyticsLogTypeParameter);

                    string logAnalyticsSharedKey =
                        GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.LogAnalyticsSharedKeyParameter);

                    string logAnalyticsWorkspaceId =
                        GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.LogAnalyticsWorkspaceIdParameter);

                    if (string.IsNullOrWhiteSpace(logAnalyticsWorkspaceId) || string.IsNullOrWhiteSpace(logAnalyticsSharedKey))
                    {
                        IsTelemetryProviderEnabled = false;
                        return;
                    }

                    TelemetryClient = new LogAnalyticsTelemetry(
                        logAnalyticsWorkspaceId,
                        logAnalyticsSharedKey,
                        logAnalyticsLogType,
                        FabricClientInstance,
                        new CancellationToken());

                    break;

                case TelemetryProviderType.AzureApplicationInsights:
                        
                    string aiKey = GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.AiKey);

                    if (string.IsNullOrWhiteSpace(aiKey))
                    {
                        IsTelemetryProviderEnabled = false;
                        return;
                    }

                    TelemetryClient = new AppInsightsTelemetry(aiKey);
                    break;

                default:

                    IsTelemetryProviderEnabled = false;
                    break;
            }
        }

        private void InitializeCsvLogger()
        {
            if (CsvFileLogger != null)
            {
                return;
            }

            // Archive file lifetime - CsvLogger files.
            if (int.TryParse(GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.MaxArchivedCsvFileLifetimeDays), out int maxCsvFileArchiveLifetime))
            {
                MaxCsvArchiveFileLifetimeDays = maxCsvFileArchiveLifetime;
            }

            // Csv file write format - CsvLogger only.
            if (Enum.TryParse(GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.CsvFileWriteFormat), ignoreCase: true, out CsvFileWriteFormat csvWriteFormat))
            {
                CsvWriteFormat = csvWriteFormat;
            }

            CsvFileLogger = new DataTableFileLogger
            {
                FileWriteFormat = CsvWriteFormat,
                MaxArchiveCsvFileLifetimeDays = MaxCsvArchiveFileLifetimeDays
            };

            string dataLogPath = GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.DataLogPathParameter);

            CsvFileLogger.BaseDataLogFolderPath = !string.IsNullOrWhiteSpace(dataLogPath) ? Path.Combine(dataLogPath, ObserverName) : Path.Combine(Environment.CurrentDirectory, "fabric_observer_csvdata", ObserverName);
        }

        private bool IsObserverWebApiAppInstalled()
        {
            try
            {
                var deployedObsWebApps = FabricClientInstance.QueryManager.GetApplicationListAsync(new Uri("fabric:/FabricObserverWebApi")).GetAwaiter().GetResult();
                return deployedObsWebApps?.Count > 0;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {

            }

            return false;
        }
    }
}