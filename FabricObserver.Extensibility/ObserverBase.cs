// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Concurrent;
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
using FabricObserver.Utilities.ServiceFabric;
using Microsoft.Win32.SafeHandles;
using ConfigSettings = FabricObserver.Observers.Utilities.ConfigSettings;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    public abstract class ObserverBase : IDisposable
    {
        private const int TtlAddMinutes = 5;
        private bool disposed;
        private ConcurrentDictionary<string, (int DumpCount, DateTime LastDumpDate)> ServiceDumpCountDictionary;
        private readonly object lockObj = new();
        public volatile bool HasActiveFabricErrorOrWarning;
        public volatile int CurrentErrorCount;
        public volatile int CurrentWarningCount;

        public static StatelessServiceContext FabricServiceContext
        {
            get; private set;
        }

        public static bool IsWindows => OperatingSystem.IsWindows();

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

        public Uri ApplicationName
        {
            get; set;
        }

        public Uri ServiceName
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

        public ConfigurationPackage ConfigPackage
        {
            get; set;
        }

        public CodePackage CodePackage
        {
            get; set;
        }

        public ObserverHealthReporter HealthReporter
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
            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.IsObserverTelemetryEnabled = value;
                }
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
            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.IsObserverTelemetryEnabled = value;
                }
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

        public ConcurrentQueue<string> ServiceNames
        {
            get; set;
        } = new ConcurrentQueue<string>();

        public string ServiceNamesLogPath
        {
            get; set;
        }

        public int MonitoredServiceProcessCount
        {
            get; set;
        }

        public int MonitoredAppCount
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

        public TimeSpan MonitorSleepDuration
        {
            get => ConfigurationSettings?.MonitorSleepDuration ?? TimeSpan.MinValue;
            set
            {
                if (ConfigurationSettings != null)
                {
                    ConfigurationSettings.MonitorSleepDuration = value;
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

        public bool IsEtwProviderEnabled
        {
            get; set;
        }

        public static FabricClient FabricClientInstance => FabricClientUtilities.FabricClientSingleton;

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
        /// <param name="fabricClient">Pass null for this parameter. It only exists here to preserve the existing interface definition of IFabricObserverStartup, which is used by FO plugins. 
        /// There is actually no longer a need to pass in a FabricClient instance as this is now a singleton instance managed by 
        /// FabricObserver.Extensibility.FabricClientUtilities and is protected against premature disposal.</param>
        /// <param name="serviceContext">The ServiceContext instance for FO, which is a Stateless singleton (1 partition) service that runs on all nodes in an SF cluster. FO will inject this instance when the application starts.</param>
        protected ObserverBase(FabricClient fabricClient, StatelessServiceContext serviceContext)
        {
            ObserverName = GetType().Name;
            ConfigurationSectionName = ObserverName + "Configuration";
            ApplicationName = new Uri(serviceContext.CodePackageActivationContext.ApplicationName);
            NodeName = serviceContext.NodeContext.NodeName;
            NodeType = serviceContext.NodeContext.NodeType;
            ServiceName = serviceContext.ServiceName;
            ConfigPackage = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            CodePackage = serviceContext.CodePackageActivationContext.GetCodePackageObject("Code");
            FabricServiceContext = serviceContext;

            SetObserverStaticConfiguration();

            if (ObserverName == ObserverConstants.AppObserverName)
            {
                ServiceNames.Enqueue(ServiceName.OriginalString);
            }

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

            ConfigurationSettings = new ConfigSettings(ConfigPackage.Settings, ConfigurationSectionName);
            ObserverLogger.EnableVerboseLogging = ConfigurationSettings.EnableVerboseLogging;
            HealthReporter = new ObserverHealthReporter(ObserverLogger);
            IsObserverWebApiAppDeployed = 
                bool.TryParse(
                    GetSettingParameterValue(
                        ObserverConstants.ObserverManagerConfigurationSectionName,
                        ObserverConstants.ObserverWebApiEnabled), out bool obsWeb) && obsWeb && IsObserverWebApiAppInstalled();

            ServiceNamesLogPath = Path.Combine(ObserverLogger.LogFolderBasePath, ObserverName, "ServiceNames", "Services.txt");
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

                if (ConfigPackage.Settings.Sections.All(sec => sec.Name != sectionName))
                {
                    return null;
                }

                if (ConfigPackage.Settings.Sections[sectionName].Parameters.All(param => param.Name != parameterName))
                {
                    return null;
                }

                string setting = ConfigPackage.Settings.Sections[sectionName].Parameters[parameterName]?.Value;

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
            GC.SuppressFinalize(this);
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
            if (!IsWindows)
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
                    _ = Directory.CreateDirectory(DumpsPath);
                }
                catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    ObserverLogger.LogWarning($"Can't create dump directory for path {DumpsPath}. Will not generate dmp file for {procName}. " +
                                              $"Error info:{Environment.NewLine}{e}");
                    return false;
                }
            }

            if (!EnsureProcess(procName, processId))
            {
                ObserverLogger.LogWarning($"Exiting DumpWindowsServiceProcess as target process {processId} is longer running.");
                return false;
            }

            ServiceDumpCountDictionary ??= new ConcurrentDictionary<string, (int DumpCount, DateTime LastDump)>();

            StringBuilder sb = new(metric);
            string metricName = sb.Replace("Total", string.Empty)
                                  .Replace("(MB)", string.Empty)
                                  .Replace("(Percent)", string.Empty)
                                  .Replace("Active", string.Empty)
                                  .Replace("Allocated", string.Empty)
                                  .Replace("Length", string.Empty)
                                  .Replace("Usage", string.Empty)
                                  .Replace("Time", string.Empty)
                                  .Replace("TCP", string.Empty)
                                  .Replace(" ", string.Empty).ToString();
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
                    Logger.TryCleanFolder(DumpsPath, $"{dumpKey}*.dmp", TimeSpan.FromDays(1));
                    return false;
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {

            }

            if (!ServiceDumpCountDictionary.ContainsKey(dumpKey))
            {
                _ = ServiceDumpCountDictionary.TryAdd(dumpKey, (0, DateTime.UtcNow));
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

            var miniDumpType = DumpType switch
            {
                DumpType.Full => NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemory |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithTokenInformation,

                DumpType.MiniPlus => NativeMethods.MINIDUMP_TYPE.MiniDumpWithPrivateReadWriteMemory |
                                     NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
                                     NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
                                     NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules |
                                     NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
                                     NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
                                     NativeMethods.MINIDUMP_TYPE.MiniDumpWithTokenInformation,

                DumpType.Mini => NativeMethods.MINIDUMP_TYPE.MiniDumpNormal |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
                                 NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo,

                _ => throw new NotSupportedException($"Unsupported dump type."),
            };

            string dumpFilePath = null;

            try
            {
                if (dumpFileName == string.Empty)
                {
                    dumpFileName = procName;
                }

                if (!EnsureProcess(procName, processId))
                {
                    ObserverLogger.LogWarning($"Exiting DumpWindowsServiceProcess as target process {processId} is longer running.");
                    return false;
                }

                using (SafeProcessHandle processHandle = NativeMethods.GetSafeProcessHandle(processId))
                {
                    if (processHandle.IsInvalid || processHandle.IsClosed)
                    {
                        throw new Win32Exception($"Failed getting handle to process {processId} with Win32 error {Marshal.GetLastWin32Error()}");
                    }

                    dumpFileName += $"_{DateTime.Now:ddMMyyyyHHmmssFFF}.dmp";

                        // Check disk space availability before writing dump file.
                        string driveName = DumpsPath.Substring(0, 2);

                        if (DiskUsage.GetCurrentDiskSpaceUsedPercent(driveName) > 90)
                        {
                            ObserverLogger.LogWarning("Not enough disk space available for dump file creation.");
                            return false;
                        }

                        dumpFilePath = Path.Combine(DumpsPath, dumpFileName);

                    lock (lockObj)
                    {
                        using (FileStream file = File.Create(dumpFilePath))
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

                            if (!string.IsNullOrWhiteSpace(dumpKey))
                            {
                                if (ServiceDumpCountDictionary.ContainsKey(dumpKey))
                                {
                                    ServiceDumpCountDictionary[dumpKey] = (ServiceDumpCountDictionary[dumpKey].DumpCount + 1, DateTime.UtcNow);
                                }
                                else
                                {
                                    _ = ServiceDumpCountDictionary.TryAdd(dumpKey, (1, DateTime.UtcNow));
                                }
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception e) when (
                    e is ArgumentException or
                    InvalidOperationException or
                    IOException or
                    PlatformNotSupportedException or
                    UnauthorizedAccessException or
                    Win32Exception)
            {
                ObserverLogger.LogWarning(
                    $"Failure generating Windows process dump file {dumpFileName} with error:{Environment.NewLine}{e}");

                // This would mean a partial file may have been created (like the process went away during dump capture). Delete it.
                if (File.Exists(dumpFilePath))
                {
                    try
                    {
                        Retry.Do(() => File.Delete(Path.Combine(DumpsPath, dumpFileName)), TimeSpan.FromSeconds(1), Token);
                    }
                    catch (AggregateException)
                    {
                        // Couldn't delete file.
                        // Retry.Do throws AggregateException containing list of exceptions caught. In this case, we don't really care.
                    }
                }
            }
           
            return false;
        }

        /// <summary>
        /// This function *only* processes *numeric* data held in (FabricResourceUsageData (FRUD) instances and generates Application, Service, and Node level Health Reports depending on supplied Error and Warning thresholds. 
        /// </summary>
        /// <typeparam name="T">Generic: This represents the numeric type of data this function will operate on.</typeparam>
        /// <param name="data">FabricResourceUsageData (FRUD) instance.</param>
        /// <param name="thresholdError">Error threshold (numeric)</param>
        /// <param name="thresholdWarning">Warning threshold (numeric)</param>
        /// <param name="healthReportTtl">Health report Time to Live (TimeSpan)</param>
        /// <param name="entityType">Service Fabric Entity type. Note, only Application and Node types are supported by this function.</param>
        /// <param name="processName">For service reporting, you must provide a process name for the target service instance. Except for ContainerObserver, where this does not apply.</param>
        /// <param name="replicaOrInstance">Replica or Instance information contained in a type.</param>
        /// <param name="dumpOnError">Whether or not to dump process if Error threshold has been reached.</param>
        /// <param name="processId">The id of the service process (AppObserver, FabricSystemObserver supply these, for example).</param>
        public void ProcessResourceDataReportHealth<T>(
                        FabricResourceUsageData<T> data,
                        T thresholdError,
                        T thresholdWarning,
                        TimeSpan healthReportTtl,
                        EntityType entityType,
                        string processName = null,
                        ReplicaOrInstanceMonitoringInfo replicaOrInstance = null,
                        bool dumpOnError = false,
                        bool dumpOnWarning = false,
                        int processId = 0,
                        int childProcCount = 0) where T : struct
        {
            if (data == null)
            {
                ObserverLogger.LogWarning("ProcessResourceDataReportHealth: data is null.");
                return;
            }

            string thresholdName = "Warning";
            bool warningOrError = false;
            string id = null, appType = null, appTypeVersion = null, serviceTypeName = null, serviceTypeVersion = null;
            int procId = processId;
            T threshold = thresholdWarning;
            HealthState healthState = HealthState.Ok;
            Uri appName = null;
            Uri serviceName = null;
            TelemetryDataBase telemetryData = null;

            if (entityType is EntityType.Application or EntityType.Service)
            {
                if (replicaOrInstance != null)
                {
                    appName = replicaOrInstance.ApplicationName;
                    appType = replicaOrInstance.ApplicationTypeName;
                    appTypeVersion = replicaOrInstance.ApplicationTypeVersion;
                    serviceName = replicaOrInstance.ServiceName;
                    serviceTypeName = replicaOrInstance.ServiceTypeName;
                    serviceTypeVersion = replicaOrInstance.ServiceTypeVersion;
                    procId = (int)replicaOrInstance.HostProcessId;
                   
                    // This doesn't apply to ContainerObserver.
                    if (ObserverName != ObserverConstants.ContainerObserverName)
                    {
                        if (string.IsNullOrWhiteSpace(processName))
                        {
                            ObserverLogger.LogWarning("ProcessResourceDataReportHealth: Process name is required for service level reporting. Exiting.");
                            data.ClearData();
                            return;
                        }

                        if (procId != processId)
                        {
                            ObserverLogger.LogWarning($"ProcessResourceDataReportHealth: Process with id {processId} is no longer running. Exiting.");
                            data.ClearData();
                            return;
                        }

                        id = $"{NodeName}_{processName}_{data.Property.Replace(" ", string.Empty)}";

                        // The health event description will be a serialized instance of telemetryData,
                        // so it should be completely constructed (filled with data) regardless
                        // of user telemetry settings.
                        telemetryData = new ServiceTelemetryData()
                        {
                            ApplicationName = appName.OriginalString,
                            ApplicationType = appType,
                            ApplicationTypeVersion = appTypeVersion,
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = entityType,
                            Metric = data.Property,
                            NodeName = NodeName,
                            NodeType = NodeType,
                            ObserverName = ObserverName,
                            PartitionId = replicaOrInstance.PartitionId.ToString(),
                            ProcessId = procId,
                            ProcessName = processName,
                            ProcessStartTime = replicaOrInstance.HostProcessStartTime.ToString("o"),
                            Property = id,
                            ReplicaId = replicaOrInstance.ReplicaOrInstanceId,
                            ReplicaRole = replicaOrInstance.ReplicaRole.ToString(),
                            ReplicaStatus = replicaOrInstance.ReplicaStatus.ToString(),
                            RGMemoryEnabled = replicaOrInstance.RGMemoryEnabled,
                            RGAppliedMemoryLimitMb = replicaOrInstance.RGAppliedMemoryLimitMb,
                            RGCpuEnabled = replicaOrInstance.RGCpuEnabled,
                            RGAppliedCpuLimitCores = replicaOrInstance.RGAppliedCpuLimitCores,
                            ServiceKind = replicaOrInstance.ServiceKind.ToString(),
                            ServiceName = serviceName.OriginalString,
                            ServiceTypeName = serviceTypeName,
                            ServiceTypeVersion = serviceTypeVersion,
                            ServicePackageActivationMode = replicaOrInstance.ServicePackageActivationMode.ToString(),
                            Source = ObserverConstants.FabricObserverName,
                            Value = data.AverageDataValue
                        };
                    }
                    else
                    {
                        string name = replicaOrInstance.ServiceName.OriginalString;
                        int index = name.LastIndexOf('/') + 1;
                        string containerIdService = name[index..];
                        id = $"{NodeName}_{containerIdService}_{data.Property.Replace(" ", string.Empty)}";

                        telemetryData = new ContainerTelemetryData()
                        {
                            ApplicationName = appName.OriginalString,
                            ContainerId = replicaOrInstance.ContainerId.ToString(),
                            ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                            EntityType = entityType,
                            Metric = data.Property,
                            NodeName = NodeName,
                            NodeType = NodeType,
                            ObserverName = ObserverName,
                            PartitionId = replicaOrInstance.PartitionId.ToString(),
                            Property = id,
                            ReplicaId = replicaOrInstance.ReplicaOrInstanceId,
                            ReplicaRole = replicaOrInstance.ReplicaRole.ToString(),
                            ReplicaStatus = replicaOrInstance.ReplicaStatus.ToString(),
                            ServiceKind = replicaOrInstance.ServiceKind.ToString(),
                            ServiceName = serviceName.OriginalString,
                            ServicePackageActivationMode = replicaOrInstance.ServicePackageActivationMode.ToString(),
                            Source = ObserverConstants.FabricObserverName,
                            Value = data.AverageDataValue
                        };
                    }
                }
                else // System service report from FabricSystemObserver.
                {
                    if (string.IsNullOrWhiteSpace(processName))
                    {
                        ObserverLogger.LogWarning("ProcessDataReportHealth: processName is missing.");
                        data.ClearData();
                        return;
                    }

                    if (processId == 0)
                    {
                        ObserverLogger.LogWarning("ProcessDataReportHealth: processId is invalid.");
                        data.ClearData();
                        return;
                    }

                    appName = new Uri(ObserverConstants.SystemAppName);

                    id = $"{NodeName}_{processName}_{data.Property.Replace(" ", string.Empty)}";

                    // The health event description will be a serialized instance of telemetryData,
                    // so it should be completely constructed (filled with data) regardless
                    // of user telemetry settings.
                    telemetryData = new SystemServiceTelemetryData()
                    {
                        ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                        EntityType = entityType,
                        Metric = data.Property,
                        NodeName = NodeName,
                        NodeType = NodeType,
                        ObserverName = ObserverName,
                        ProcessId = procId,
                        ProcessName = processName,
                        ProcessStartTime = GetProcessStartTime(procId).ToString("o"),
                        Property = id,
                        Source = ObserverConstants.FabricObserverName,
                        Value = data.AverageDataValue
                    };
                }

                // Telemetry - This is informational, per reading telemetry, healthstate is irrelevant here. If the process has children, then don't emit this raw data since it will already
                // be contained in the ChildProcessTelemetry data instances and AppObserver will have already emitted it.
                // Enable this for your observer if you want to send data to ApplicationInsights or LogAnalytics for each resource usage observation it makes per specified metric.
                if (IsTelemetryEnabled && replicaOrInstance?.ChildProcesses == null)
                {
                     _ = TelemetryClient?.ReportMetricAsync(telemetryData, Token);
                }

                // ETW - This is informational, per reading EventSource tracing, healthstate is irrelevant here. If the process has children, then don't emit this raw data since it will already
                // be contained in the ChildProcessTelemetry data instances and AppObserver will have already emitted it.
                // Enable this for your observer if you want to log etw (which can then be read by some agent that will send it to some endpoint)
                // for each resource usage observation it makes per specified metric.
                if (IsEtwEnabled && replicaOrInstance?.ChildProcesses == null)
                {
                    ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                }
            }
            else
            {
                id = data.Id;

                if (ObserverName == ObserverConstants.NodeObserverName)
                {
                    telemetryData = new NodeTelemetryData()
                    {
                        ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                        EntityType = entityType,
                        NodeName = NodeName,
                        NodeType = NodeType,
                        ObserverName = ObserverName,
                        Property = $"{NodeName}_{id}",
                        Metric = data.Property,
                        Source = ObserverConstants.FabricObserverName,
                        Value = data.AverageDataValue
                    };
                }
                else if (ObserverName == ObserverConstants.DiskObserverName)
                {
                    telemetryData = new DiskTelemetryData()
                    {
                        ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                        EntityType = entityType,
                        DriveName = IsWindows ? id[..2] : string.Empty,
                        FolderName = data.Property == ErrorWarningProperty.FolderSizeMB ? id : null,
                        NodeName = NodeName,
                        NodeType = NodeType,
                        ObserverName = ObserverName,
                        Property = id,
                        Metric = data.Property,
                        Source = ObserverConstants.FabricObserverName,
                        Value = data.AverageDataValue
                    };
                }

                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportMetricAsync(telemetryData, Token);
                }

                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                }
            }

            // Health Error
            if (data.IsUnhealthy(thresholdError))
            {
                thresholdName = "Error";
                threshold = thresholdError;
                warningOrError = true;
                healthState = HealthState.Error;

                // User service process dump - Error. This is Windows-only today.
                if (replicaOrInstance != null && dumpOnError && IsWindows)
                {
                    if (!string.IsNullOrWhiteSpace(DumpsPath))
                    {
                        ServiceDumpCountDictionary ??= new ConcurrentDictionary<string, (int DumpCount, DateTime LastDump)>();

                        try
                        {
                            if (data.Id.Contains($":{processName}{procId}"))
                            {
                                // DumpWindowsServiceProcess does not log success, so doing that here.
                                if (DumpWindowsServiceProcess(procId, processName, data.Property))
                                {
                                    ObserverLogger.LogInfo(
                                     $"Successfully dumped {processName}({procId}) which exceeded Error threshold for {data.Property}.");
                                }
                            }
                        }
                        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
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

                // User service process dump - Warning. This is Windows-only today.
                if (replicaOrInstance != null && dumpOnWarning && IsWindows)
                {
                    if (!string.IsNullOrWhiteSpace(DumpsPath))
                    {
                        ServiceDumpCountDictionary ??= new ConcurrentDictionary<string, (int DumpCount, DateTime LastDump)>();

                        try
                        {
                            if (data.Id.Contains($":{processName}{procId}"))
                            {
                                // DumpWindowsServiceProcess does not log success, so doing that here.
                                if (DumpWindowsServiceProcess(procId, processName, data.Property))
                                {
                                    ObserverLogger.LogInfo(
                                     $"Successfully dumped {processName}({procId}) which exceeded Warning threshold for {data.Property}.");
                                }
                            }
                        }
                        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
                        {
                            ObserverLogger.LogWarning($"Unable to generate dmp file:{Environment.NewLine}{e}");
                        }
                    }
                }
            }

            if (warningOrError)
            {
                string errorWarningCode = null;

                // Ephemeral port sugar for event description.
                string dynamicRange = string.Empty;
                string totalPorts = string.Empty;
                int Low = 0, High = 0, Total = 0;

                if (data.Property.Contains("Ephemeral"))
                {
                    (Low, High, Total) = OSInfoProvider.Instance.TupleGetDynamicPortRange();
                    dynamicRange = $" (dynamic range: {Low}-{High})";

                    if (data.Property.Contains("Percent"))
                    {
                        if (Total > 0)
                        {
                            int count = (int)(data.AverageDataValue / 100 * Total);
                            totalPorts = $" ({count}/{Total})";
                        }
                    }
                }

                switch (data.Property)
                {
                    case ErrorWarningProperty.CpuTime when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorCpuPercent : FOErrorWarningCodes.AppWarningCpuPercent;
                        break;

                    case ErrorWarningProperty.CpuTime when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorCpuPercent : FOErrorWarningCodes.NodeWarningCpuPercent;
                        break;

                    case ErrorWarningProperty.DiskSpaceUsagePercentage when entityType == EntityType.Disk:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorDiskSpacePercent : FOErrorWarningCodes.NodeWarningDiskSpacePercent;
                        break;

                    case ErrorWarningProperty.DiskSpaceUsageMb when entityType == EntityType.Disk:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorDiskSpaceMB : FOErrorWarningCodes.NodeWarningDiskSpaceMB;
                        break;

                    case ErrorWarningProperty.FolderSizeMB when entityType == EntityType.Disk:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorFolderSizeMB : FOErrorWarningCodes.NodeWarningFolderSizeMB;
                        break;

                    case ErrorWarningProperty.DiskAverageQueueLength when entityType == EntityType.Disk:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorDiskAverageQueueLength : FOErrorWarningCodes.NodeWarningDiskAverageQueueLength;
                        break;

                    case ErrorWarningProperty.MemoryConsumptionMb when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorMemoryMB : FOErrorWarningCodes.AppWarningMemoryMB;
                        break;

                    case ErrorWarningProperty.MemoryConsumptionMb when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorMemoryMB : FOErrorWarningCodes.NodeWarningMemoryMB;
                        break;

                    case ErrorWarningProperty.MemoryConsumptionPercentage when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorMemoryPercent : FOErrorWarningCodes.AppWarningMemoryPercent;
                        break;

                    case ErrorWarningProperty.MemoryConsumptionPercentage when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorMemoryPercent : FOErrorWarningCodes.NodeWarningMemoryPercent;
                        break;

                    case ErrorWarningProperty.ActiveFirewallRules when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.ErrorTooManyFirewallRules : FOErrorWarningCodes.WarningTooManyFirewallRules;
                        break;

                    case ErrorWarningProperty.ActiveTcpPorts when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyActiveTcpPorts : FOErrorWarningCodes.AppWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.ActiveTcpPorts when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTooManyActiveTcpPorts : FOErrorWarningCodes.NodeWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.ActiveEphemeralPorts when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyActiveEphemeralPorts : FOErrorWarningCodes.AppWarningTooManyActiveEphemeralPorts;
                        break;

                    case ErrorWarningProperty.ActiveEphemeralPorts when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTooManyActiveEphemeralPorts : FOErrorWarningCodes.NodeWarningTooManyActiveEphemeralPorts;
                        break;

                    case ErrorWarningProperty.ActiveEphemeralPortsPercentage when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorActiveEphemeralPortsPercent : FOErrorWarningCodes.AppWarningActiveEphemeralPortsPercent;
                        break;

                    case ErrorWarningProperty.ActiveEphemeralPortsPercentage when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorActiveEphemeralPortsPercent : FOErrorWarningCodes.NodeWarningActiveEphemeralPortsPercent;
                        break;
                        
                    // Support for legacy Handles property name.
                    case ErrorWarningProperty.AllocatedFileHandles when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyOpenHandles : FOErrorWarningCodes.AppWarningTooManyOpenHandles;
                        break;

                    case ErrorWarningProperty.HandleCount when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyOpenHandles : FOErrorWarningCodes.AppWarningTooManyOpenHandles;
                        break;

                    case ErrorWarningProperty.ThreadCount when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyThreads : FOErrorWarningCodes.AppWarningTooManyThreads;
                        break;

                    // Internal monitor for Windows KVS LVID consumption. Only Warning state is supported. This is a non-configurable monitor.
                    case ErrorWarningProperty.KvsLvidsPercent when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = FOErrorWarningCodes.AppWarningKvsLvidsPercentUsed;
                        break;

                    case ErrorWarningProperty.AllocatedFileHandles when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTooManyOpenHandles : FOErrorWarningCodes.NodeWarningTooManyOpenHandles;
                        break;

                    // Support for legacy Handles (Percent) property name.
                    case ErrorWarningProperty.AllocatedFileHandlesPct when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTotalOpenHandlesPercent : FOErrorWarningCodes.NodeWarningTotalOpenHandlesPercent;
                        break;

                    case ErrorWarningProperty.HandleCountPercentage when entityType is EntityType.Machine or EntityType.Node:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTotalOpenHandlesPercent : FOErrorWarningCodes.NodeWarningTotalOpenHandlesPercent;
                        break;

                    // Process Private Bytes (MB).
                    case ErrorWarningProperty.PrivateBytesMb when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorPrivateBytesMb : FOErrorWarningCodes.AppWarningPrivateBytesMb;
                        break;

                    // Process Private Bytes (Persent).
                    case ErrorWarningProperty.PrivateBytesPercent when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorPrivateBytesPercent : FOErrorWarningCodes.AppWarningPrivateBytesPercent;
                        break;

                    // RG Memory Limit Percent. Only Warning is supported.
                    case ErrorWarningProperty.RGMemoryUsagePercent when entityType is EntityType.Application or EntityType.Service:
                        errorWarningCode = FOErrorWarningCodes.AppWarningRGMemoryLimitPercent;
                        break;

                    // RG CPU Limit Percent. Only Warning is supported.
                    case ErrorWarningProperty.RGCpuUsagePercent when entityType == EntityType.Application || entityType == EntityType.Service:
                        errorWarningCode = FOErrorWarningCodes.AppWarningRGCpuLimitPercent;
                        break;
                }

                var healthMessage = new StringBuilder();
                string childProcMsg = string.Empty;
                string rgInfo = string.Empty;
                string drive = string.Empty;

                if (replicaOrInstance != null && childProcCount > 0)
                {
                    childProcMsg = $" Note that {serviceName.OriginalString} has spawned one or more child processes ({childProcCount}). " +
                                   $"Their cumulative impact on {processName}'s resource usage has been applied.";
                }

                if (replicaOrInstance != null && data.Property == ErrorWarningProperty.RGMemoryUsagePercent)
                {
                    rgInfo = $" of {replicaOrInstance.RGAppliedMemoryLimitMb}MB";
                }

                if (replicaOrInstance != null && data.Property == ErrorWarningProperty.RGCpuUsagePercent)
                {
                    rgInfo = $" of {replicaOrInstance.RGAppliedCpuLimitCores} cores";
                }

                string metric = data.Property;

                if (entityType == EntityType.Disk)
                {
                    drive = $" on drive {id}";
                }

                _ = healthMessage.Append($"{metric}{dynamicRange} has exceeded the specified {thresholdName} threshold ({threshold}{data.Units}{rgInfo}){drive}");
                _ = healthMessage.Append($" - {metric}: {data.AverageDataValue}{data.Units}{rgInfo}{totalPorts}"); 
                
                if (childProcMsg != string.Empty)
                {
                    _ = healthMessage.Append(childProcMsg);
                }

                telemetryData.Code = errorWarningCode;
                telemetryData.HealthState = healthState;
                telemetryData.Description = healthMessage.ToString();
                telemetryData.Source = $"{ObserverName}({errorWarningCode})";
                telemetryData.Property = entityType == EntityType.Disk ? $"{id} {data.Property}" : id;

                if (telemetryData.ObserverName == ObserverConstants.NodeObserverName)
                {
                    telemetryData.Property = $"{NodeName}_{id}";
                }

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(telemetryData, Token);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                }

                // This is used for managing health reports (clearing reports on start and on graceful close).
                if (serviceName != null && !ServiceNames.Any(a => a == serviceName.OriginalString))
                {
                    // Volatile state. This is used to clear health reports during a config upgrade or graceful close.
                    ServiceNames.Enqueue(serviceName.OriginalString);
                }

                var healthReport = new HealthReport
                {
                    AppName = appName,
                    Code = errorWarningCode,
                    EmitLogEvent = EnableVerboseLogging || IsObserverWebApiAppDeployed,
                    HealthData = telemetryData,
                    HealthMessage = healthMessage.ToString(),
                    HealthReportTimeToLive = healthReportTtl,
                    EntityType = entityType,
                    ServiceName = serviceName,
                    State = healthState,
                    NodeName = NodeName,
                    Observer = ObserverName,
                    Property = telemetryData.Property,
                    ResourceUsageDataProperty = data.Property,
                    SourceId = $"{ObserverName}({errorWarningCode})"
                };

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Set internal health state info on data instance.
                data.ActiveErrorOrWarning = true;
                data.ActiveErrorOrWarningCode = errorWarningCode;

                // This means this observer created a Warning or Error SF Health Report
                HasActiveFabricErrorOrWarning = true;

                // Update internal counters.
                if (healthState == HealthState.Warning)
                {
                    CurrentWarningCount++;
                }
                else if (healthState == HealthState.Error)
                {
                    CurrentErrorCount++;
                }
                
                // Clean up sb.
                _ = healthMessage.Clear();
                healthMessage = null;
            }
            else
            {
                if (data.ActiveErrorOrWarning)
                {
                    telemetryData.Code = FOErrorWarningCodes.Ok;
                    telemetryData.HealthState = HealthState.Ok;
                    telemetryData.Property = entityType == EntityType.Disk ? $"{id} {data.Property}" : id;
                    telemetryData.Description = $"{data.Property} is now within normal/expected range.";
                    telemetryData.Metric = data.Property;
                    telemetryData.Source = $"{ObserverName}({data.ActiveErrorOrWarningCode})";
                    telemetryData.Value = data.AverageDataValue;

                    if (telemetryData.ObserverName == ObserverConstants.NodeObserverName)
                    {
                        telemetryData.Property = $"{NodeName}_{id}";
                    }

                    // Telemetry
                    if (IsTelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportHealthAsync(telemetryData, Token);
                    }

                    // ETW.
                    if (IsEtwEnabled)
                    {
                        ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                    }

                    var healthReport = new HealthReport
                    {
                        AppName = appName,
                        ServiceName = serviceName,
                        Code = data.ActiveErrorOrWarningCode,
                        EmitLogEvent = EnableVerboseLogging || IsObserverWebApiAppDeployed,
                        HealthData = telemetryData,
                        HealthMessage = $"{data.Property} is now within normal/expected range.",
                        HealthReportTimeToLive = default,
                        EntityType = entityType,
                        State = HealthState.Ok,
                        NodeName = NodeName,
                        Observer = ObserverName,
                        Property = telemetryData.Property,
                        ResourceUsageDataProperty = data.Property,
                        SourceId = $"{ObserverName}({data.ActiveErrorOrWarningCode})"
                    };

                    // Emit an Ok Health Report to clear Fabric Health warning.
                    HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // Reset health FRUD/obs health data.
                    data.ActiveErrorOrWarning = false;
                    data.ActiveErrorOrWarningCode = FOErrorWarningCodes.Ok;
                    HasActiveFabricErrorOrWarning = false;
                }
            }
        }

        private static HealthState GetHealthStateFromErrorCode(string activeErrorOrWarningCode)
        {
            if (string.IsNullOrWhiteSpace(activeErrorOrWarningCode))
            {
                return HealthState.Unknown;
            }

            if (activeErrorOrWarningCode == FOErrorWarningCodes.Ok)
            {
                return HealthState.Ok;
            }

            if (FOErrorWarningCodes.GetCodeNameFromErrorCode(activeErrorOrWarningCode) == null)
            {
                return HealthState.Unknown;
            }

            if (FOErrorWarningCodes.GetCodeNameFromErrorCode(activeErrorOrWarningCode).Contains("warning", StringComparison.OrdinalIgnoreCase)) 
            {
                return HealthState.Warning;
            }

            if (FOErrorWarningCodes.GetCodeNameFromErrorCode(activeErrorOrWarningCode).Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return HealthState.Error;
            }

            return HealthState.Unknown;
        }

        /// <summary>
        /// Ensures the supplied process id is still mapped to the supplied process name.
        /// </summary>
        /// <param name="procName">The process name.</param>
        /// <param name="procId">The process id.</param>
        /// <returns>True if the pid is mapped to the process of supplied name. False otherwise.</returns>
        private static bool EnsureProcess(string procName, int procId)
        {
            if (string.IsNullOrWhiteSpace(procName) || procId == 0)
            {
                return false;
            }

            if (!IsWindows)
            {
                try
                {
                    using (Process proc = Process.GetProcessById(procId))
                    {
                        return proc.ProcessName == procName;
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    return false;
                }
            }

            return NativeMethods.GetProcessNameFromId(procId) == procName;
        }

        public DateTime GetProcessStartTime(int processId)
        {
            try
            {
                if (IsWindows)
                {
                    return NativeMethods.GetProcessStartTime(processId);
                }
                else
                {
                    using Process p = Process.GetProcessById(processId);
                    return p.StartTime;
                }
            }
            catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException)
            {
                ObserverLogger.LogInfo($"Unable to get process start time: {e.Message}. This means process {processId} is no longer running or FO can't access it due to access rights.");
            }

            return DateTime.MinValue;
        }

        /// <summary>
        /// Computes TTL for an observer's health reports based on how long it takes an observer to run, time differential between runs,
        /// observer loop sleep time, plus a little more time to guarantee that a health report will remain active until the next time the observer runs.
        /// Note that if you set a RunInterval on an observer, that will be reflected here and the Warning will last for that amount of time at least.
        /// </summary>
        /// <returns>TimeSpan that contains the TTL value.</returns>
        public TimeSpan GetHealthReportTTL()
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

        // Non-App parameters settings (set in Settings.xml only).
        private void SetObserverStaticConfiguration()
        {
            // Archive file lifetime - ObserverLogger files.
            if (int.TryParse(
                GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.MaxArchivedLogFileLifetimeDaysParameter), out int maxFileArchiveLifetime))
            {
                MaxLogArchiveFileLifetimeDays = maxFileArchiveLifetime;
            }

            // ETW
            if (bool.TryParse(
                GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.EnableETWProvider), out bool etwProviderEnabled))
            {
                IsEtwProviderEnabled = etwProviderEnabled;
            }
        }

        protected void SetTelemetryConfiguration()
        {
            // Telemetry.
            if (bool.TryParse(
                GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.TelemetryEnabled), out bool telemEnabled))
            {
                IsTelemetryProviderEnabled = telemEnabled;
            }

            if (!IsTelemetryProviderEnabled)
            {
                return;
            }

            string telemetryProviderType =
                GetSettingParameterValue(
                    ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.TelemetryProviderType);

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
                        GetSettingParameterValue(
                            ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.LogAnalyticsLogTypeParameter);

                    string logAnalyticsSharedKey =
                        GetSettingParameterValue(
                            ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.LogAnalyticsSharedKeyParameter);

                    string logAnalyticsWorkspaceId =
                        GetSettingParameterValue(
                            ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.LogAnalyticsWorkspaceIdParameter);

                    if (string.IsNullOrWhiteSpace(logAnalyticsWorkspaceId) || string.IsNullOrWhiteSpace(logAnalyticsSharedKey))
                    {
                        IsTelemetryProviderEnabled = false;
                        return;
                    }

                    TelemetryClient = new LogAnalyticsTelemetry(logAnalyticsWorkspaceId, logAnalyticsSharedKey, logAnalyticsLogType);
                    break;

                case TelemetryProviderType.AzureApplicationInsights:
                        
                    string aiConnString = GetSettingParameterValue(
                        ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.AppInsightsConnectionString);

                    if (string.IsNullOrWhiteSpace(aiConnString))
                    {
                        IsTelemetryProviderEnabled = false;
                        return;
                    }

                    TelemetryClient = new AppInsightsTelemetry(aiConnString);
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
            if (int.TryParse(GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.MaxArchivedCsvFileLifetimeDaysParameter), out int maxCsvFileArchiveLifetime))
            {
                MaxCsvArchiveFileLifetimeDays = maxCsvFileArchiveLifetime;
            }

            // Csv file write format - CsvLogger only.
            if (Enum.TryParse(GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.CsvFileWriteFormatParameter), ignoreCase: true, out CsvFileWriteFormat csvWriteFormat))
            {
                CsvWriteFormat = csvWriteFormat;
            }

            CsvFileLogger = new DataTableFileLogger
            {
                FileWriteFormat = CsvWriteFormat,
                MaxArchiveCsvFileLifetimeDays = MaxCsvArchiveFileLifetimeDays
            };

            string dataLogPath = GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName, ObserverConstants.DataLogPathParameter);

            CsvFileLogger.BaseDataLogFolderPath = !string.IsNullOrWhiteSpace(dataLogPath) ? Path.Combine(dataLogPath, ObserverName) : Path.Combine(Environment.CurrentDirectory, "fabric_observer_csvdata", ObserverName);
        }

        private static bool IsObserverWebApiAppInstalled()
        {
            try
            {
                var deployedObsWebApps =
                    FabricClientInstance.QueryManager.GetApplicationListAsync(new Uri("fabric:/FabricObserverWebApi")).GetAwaiter().GetResult();

                return deployedObsWebApps?.Count > 0;
            }
            catch (Exception e) when (e is FabricException or TimeoutException)
            {

            }

            return false;
        }
    }
}