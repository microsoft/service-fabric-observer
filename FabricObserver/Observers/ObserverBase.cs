// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
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
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    public abstract class ObserverBase : IObserver
    {
        private const int TtlAddMinutes = 5;
        private readonly int maxDumps = 5;
        private readonly Dictionary<string, int> serviceDumpCountDictionary = new Dictionary<string, int>();
        private string SFLogRoot;
        private string dumpsPath;

        public string ObserverName
        {
            get; set;
        }

        public string NodeName
        {
            get; set;
        }

        public string NodeType
        {
            get; private set;
        }

        public ObserverHealthReporter HealthReporter
        {
            get;
        }

        public ServiceContext FabricServiceContext
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
            get; set; 
        } = true;

        public bool IsObserverTelemetryEnabled
        {
            get; set;
        }

        public bool IsUnhealthy
        {
            get; set;
        } = false;

        // This static property is *only* used - and set to true - for local development unit test runs. 
        // Do not set this to true otherwise.
        public static bool IsTestRun
        {
            get; set;
        } = false;

        public Utilities.ConfigSettings ConfigurationSettings
        {
            get;
            set;
        }

        public bool EnableVerboseLogging
        {
            get;
            private set;
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

        public List<string> HealthReportSourceIds
        {
            get; set;
        } = new List<string>();

        public List<string> HealthReportProperties
        {
            get; set;
        } = new List<string>();

        public List<string> AppNames
        {
            get; set;
        } = new List<string>();

        public TimeSpan RunInterval
        {
            get; set;
        } = TimeSpan.MinValue;

        public TimeSpan AsyncClusterOperationTimeoutSeconds
        {
            get; set;
        } = TimeSpan.FromSeconds(60);

        public int DataCapacity
        {
            get; set;
        } = 30;

        public bool UseCircularBuffer
        {
            get; set;
        } = false;

        public TimeSpan MonitorDuration
        {
            get; set;
        } = TimeSpan.MinValue;

        protected bool IsTelemetryProviderEnabled
        {
            get; set;
        } = ObserverManager.TelemetryEnabled;

        protected ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        protected bool IsEtwEnabled
        {
            get; set;
        } = ObserverManager.EtwEnabled;

        protected FabricClient FabricClientInstance
        {
            get; set;
        }

        protected string ConfigurationSectionName
        {
            get;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverBase"/> class.
        /// </summary>
        protected ObserverBase()
        {
            ObserverName = GetType().Name;
            ConfigurationSectionName = ObserverName + "Configuration";
            FabricClientInstance = ObserverManager.FabricClientInstance;

            if (IsTelemetryProviderEnabled)
            {
                TelemetryClient = ObserverManager.TelemetryClient;
            }

            FabricServiceContext = ObserverManager.FabricServiceContext;
            NodeName = FabricServiceContext.NodeContext.NodeName;
            NodeType = FabricServiceContext.NodeContext.NodeType;
            ConfigurationSectionName = ObserverName + "Configuration";

            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName,
                ObserverConstants.ObserverLogPathParameter);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "observer_logs");
                logFolderBasePath = logFolderBase;
            }

            ObserverLogger = new Logger(ObserverName, logFolderBasePath);

            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                SetDefaultSfDumpPath();
            }

            // DataLogger setup
            if (bool.TryParse(
                GetSettingParameterValue(
                ConfigurationSectionName,
                ObserverConstants.EnableLongRunningCsvLogging),
                out bool enableDataLogging))
            {
                if (enableDataLogging)
                {
                    CsvFileLogger = new DataTableFileLogger
                    {
                        EnableCsvLogging = enableDataLogging,
                    };

                    string dataLogPath = GetSettingParameterValue(
                        ObserverConstants.ObserverManagerConfigurationSectionName,
                        ObserverConstants.DataLogPathParameter);

                    if (!string.IsNullOrEmpty(dataLogPath))
                    {
                        CsvFileLogger.DataLogFolderPath = dataLogPath;
                    }
                    else
                    {
                        CsvFileLogger.DataLogFolderPath = Path.Combine(Environment.CurrentDirectory, "observer_data_logs");
                    }
                }
            }

            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                SetDefaultSfDumpPath();
            }

            if (!IsTestRun)
            {
                ConfigurationSettings = new Utilities.ConfigSettings(
                    FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings,
                    ConfigurationSectionName);

                EnableVerboseLogging = ConfigurationSettings.EnableVerboseLogging;
                IsEnabled = ConfigurationSettings.IsEnabled;
                IsObserverTelemetryEnabled = ConfigurationSettings.IsObserverTelemetryEnabled;
                MonitorDuration = ConfigurationSettings.MonitorDuration;
                RunInterval = ConfigurationSettings.RunInterval;
                ObserverLogger.EnableVerboseLogging = EnableVerboseLogging;
                DataCapacity = ConfigurationSettings.DataCapacity;
                UseCircularBuffer = ConfigurationSettings.UseCircularBuffer;
            }

            HealthReporter = new ObserverHealthReporter(ObserverLogger);
        }

        public abstract Task ObserveAsync(CancellationToken token);

        public abstract Task ReportAsync(CancellationToken token);

        public void WriteToLogWithLevel(
            string property,
            string description,
            LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Information:
                    ObserverLogger.LogInfo("{0} logged at level {1}: {2}", property, level, description);
                    break;

                case LogLevel.Warning:
                    ObserverLogger.LogWarning("{0} logged at level {1}: {2}", property, level, description);
                    break;

                case LogLevel.Error:
                    ObserverLogger.LogError("{0} logged at level {1}: {2}", property, level, description);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level, null);
            }

            Logger.Flush();
        }

        /// <summary>
        /// Gets a parameter value from the specified section.
        /// </summary>
        /// <param name="sectionName">Name of the section.</param>
        /// <param name="parameterName">Name of the parameter.</param>
        /// <param name="defaultValue">Default value.</param>
        /// <returns>parameter value.</returns>
        public string GetSettingParameterValue(
            string sectionName,
            string parameterName,
            string defaultValue = null)
        {
            if (string.IsNullOrEmpty(sectionName) || string.IsNullOrEmpty(parameterName))
            {
                return null;
            }

            try
            {
                var serviceConfiguration = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

                if (serviceConfiguration == null)
                {
                    return null;
                }

                if (!serviceConfiguration.Settings.Sections.Any(sec => sec.Name == sectionName))
                {
                    return null;
                }

                if (!serviceConfiguration.Settings.Sections[sectionName].Parameters.Any(param => param.Name == parameterName))
                {
                    return null;
                }

                string setting = serviceConfiguration.Settings.Sections[sectionName].Parameters[parameterName]?.Value;

                if (string.IsNullOrEmpty(setting) && defaultValue != null)
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

        // Windows process dmp creator.
        public bool DumpServiceProcess(int processId, DumpType dumpType = DumpType.Full)
        {
            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                return false;
            }

            string processName = string.Empty;

            NativeMethods.MINIDUMP_TYPE miniDumpType;

            switch (dumpType)
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
                    throw new ArgumentOutOfRangeException(nameof(dumpType), dumpType, null);
            }

            try
            {
                // This is to ensure friendly-name of resulting dmp file.
                processName = Process.GetProcessById(processId).ProcessName;

                if (string.IsNullOrEmpty(processName))
                {
                    return false;
                }

                IntPtr processHandle = Process.GetProcessById(processId).Handle;

                processName += "_" + DateTime.Now.ToString("ddMMyyyyHHmmss") + ".dmp";

                // Check disk space availability before writing dump file.

                // This will not work on Linux
                string driveName = this.dumpsPath.Substring(0, 2);
                if (DiskUsage.GetCurrentDiskSpaceUsedPercent(driveName) > 90)
                {
                    HealthReporter.ReportFabricObserverServiceHealth(
                        FabricServiceContext.ServiceName.OriginalString,
                        ObserverName,
                        HealthState.Warning,
                        "Not enough disk space available for dump file creation.");
                    return false;
                }

                using (var file = File.Create(Path.Combine(this.dumpsPath, processName)))
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
                }

                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
            {
                ObserverLogger.LogInfo(
                    $"Unable to generate dump file {processName} with error{Environment.NewLine}{e}");
            }

            return false;
        }

        /// <summary>
        /// This function processes numeric data held in FRUD instances and generates Application or Node level Health Reports depending on supplied thresholds. 
        /// </summary>
        /// <typeparam name="T">This represents the numeric type of data this function will operate on.</typeparam>
        /// <param name="data">FabricResourceUsageData instance.</param>
        /// <param name="thresholdError">Error threshold (numeric)</param>
        /// <param name="thresholdWarning">Warning threshold (numeric)</param>
        /// <param name="healthReportTtl">Health report Time to Live (TimeSpan)</param>
        /// <param name="healthReportType">HealthReport type. Note, only Application and Node health report types are supported.</param>
        /// <param name="replicaOrInstance">Replica or Instance information contained in a type.</param>
        /// <param name="dumpOnError">Wheter or not to dump process if Error threshold has been reached.</param>
        public void ProcessResourceDataReportHealth<T>(
            FabricResourceUsageData<T> data,
            T thresholdError,
            T thresholdWarning,
            TimeSpan healthReportTtl,
            HealthReportType healthReportType = HealthReportType.Node,
            ReplicaOrInstanceMonitoringInfo replicaOrInstance = null,
            bool dumpOnError = false)
            where T : struct
        {
            if (data == null)
            {
                throw new ArgumentException("Supply all required parameters with non-null value.");
            }

            if (healthReportType != HealthReportType.Application && healthReportType != HealthReportType.Node)
            {
                this.ObserverLogger.LogWarning($"ProcessResourceDataReportHealth: Unsupported HealthReport type -> {Enum.GetName(typeof(HealthReportType), healthReportType)}");
                return;
            }

            var thresholdName = "Minimum";
            bool warningOrError = false;
            string repPartitionId = null, repOrInstanceId = null, name = null, id = null, procName = null;
            T threshold = thresholdWarning;
            var healthState = HealthState.Ok;
            Uri appName = null;
            Uri serviceName = null;
            TelemetryData telemetryData = null;

            if (healthReportType == HealthReportType.Application)
            {
                if (replicaOrInstance != null)
                {
                    repPartitionId = $"Partition: {replicaOrInstance.PartitionId}";
                    repOrInstanceId = $"Replica: {replicaOrInstance.ReplicaOrInstanceId}";

                    // Create a unique id which will be used for health Warnings and OKs (clears).
                    appName = replicaOrInstance.ApplicationName;
                    serviceName = replicaOrInstance.ServiceName;
                    name = appName.OriginalString.Replace("fabric:/", string.Empty);
                }
                else
                {
                    appName = new Uri("fabric:/System");
                    name = data.Id;
                }

                id = name + "_" + data.Property.Replace(" ", string.Empty);

                // The health event description will be a serialized instance of telemetryData,
                // so it should be completely constructed (filled with data) regardless
                // of user telemetry settings.
                telemetryData = new TelemetryData(FabricClientInstance, Token)
                {
                    ApplicationName = appName?.OriginalString ?? string.Empty,
                    Code = FOErrorWarningCodes.Ok,
                    HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                    NodeName = NodeName,
                    ObserverName = ObserverName,
                    Metric = data.Property,
                    Value = Math.Round(data.AverageDataValue, 1),
                    PartitionId = replicaOrInstance?.PartitionId.ToString(),
                    ReplicaId = replicaOrInstance?.ReplicaOrInstanceId.ToString(),
                    ServiceName = serviceName?.OriginalString ?? string.Empty,
                    Source = ObserverConstants.FabricObserverName,
                };

                try
                {
                    if (replicaOrInstance != null && replicaOrInstance.HostProcessId > 0)
                    {
                        procName = Process.GetProcessById((int)replicaOrInstance.HostProcessId).ProcessName;
                    }
                    else
                    {
                        // The name of the target service process is always the id for data containers coming from FSO.
                        procName = data.Id;
                    }

                    telemetryData.ServiceName = procName;

                    if (IsTelemetryProviderEnabled && IsObserverTelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportMetricAsync(
                            telemetryData,
                            Token).ConfigureAwait(false);
                    }

                    if (IsEtwEnabled)
                    {
                        Logger.EtwLogger?.Write(
                            ObserverConstants.FabricObserverETWEventName,
                            new
                            {
                                ApplicationName = appName?.OriginalString ?? string.Empty,
                                Code = FOErrorWarningCodes.Ok,
                                HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                                NodeName,
                                ObserverName,
                                Metric = data.Property,
                                Value = Math.Round(data.AverageDataValue, 1),
                                PartitionId = replicaOrInstance?.PartitionId.ToString(),
                                ReplicaId = replicaOrInstance?.ReplicaOrInstanceId.ToString(),
                                ServiceName = procName,
                                Source = ObserverConstants.FabricObserverName,
                            });
                    }
                }
                catch (ArgumentException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
            else
            {
                string drive = string.Empty;

                if (ObserverName == ObserverConstants.DiskObserverName)
                {
                    drive = $"{data.Id}: ";

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        drive = $"{data.Id.Remove(1, 2)}: ";
                    }
                }

                // The health event description will be a serialized instance of telemetryData,
                // so it should be completely constructed (filled with data) regardless
                // of user telemetry settings.
                telemetryData = new TelemetryData(FabricClientInstance, Token)
                {
                    Code = FOErrorWarningCodes.Ok,
                    HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                    NodeName = NodeName,
                    ObserverName = ObserverName,
                    Metric = $"{drive}{data.Property}",
                    Source = ObserverConstants.FabricObserverName,
                    Value = Math.Round(data.AverageDataValue, 1),
                };

                if (IsTelemetryProviderEnabled && IsObserverTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportMetricAsync(
                        telemetryData,
                        Token);
                }

                if (IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Code = FOErrorWarningCodes.Ok,
                            HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                            NodeName,
                            ObserverName,
                            Metric = $"{drive}{data.Property}",
                            Source = ObserverConstants.FabricObserverName,
                            Value = Math.Round(data.AverageDataValue, 1),
                        });
                }
            }

            // Health Error
            if (data.IsUnhealthy(thresholdError))
            {
                thresholdName = "Maximum";
                threshold = thresholdError;
                warningOrError = true;
                healthState = HealthState.Error;

                // This is primarily useful for AppObserver, but makes sense to be
                // part of the base class for future use, like for FSO.
                if (replicaOrInstance != null && dumpOnError)
                {
                    try
                    {
                        int procId = (int)replicaOrInstance.HostProcessId;

                        if (!this.serviceDumpCountDictionary.ContainsKey(procName))
                        {
                            this.serviceDumpCountDictionary.Add(procName, 0);
                        }

                        if (this.serviceDumpCountDictionary[procName] < this.maxDumps)
                        {
                            // DumpServiceProcess defaults to a Full dump with
                            // process memory, handles and thread data.
                            bool success = DumpServiceProcess(procId);

                            if (success)
                            {
                                this.serviceDumpCountDictionary[procName]++;
                            }
                        }
                    }

                    // Ignore these, it just means no dmp will be created.This is not
                    // critical to FO. Log as info, not warning.
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                    {
                        ObserverLogger.LogInfo($"Unable to generate dmp file:{Environment.NewLine}{e}");
                    }
                }
            }

            // Health Warning
            if (!warningOrError && data.IsUnhealthy(thresholdWarning))
            {
                warningOrError = true;
                healthState = HealthState.Warning;
            }

            if (warningOrError)
            {
                string errorWarningCode = null;

                switch (data.Property)
                {
                    case ErrorWarningProperty.TotalCpuTime when healthReportType == HealthReportType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorCpuPercent : FOErrorWarningCodes.AppWarningCpuPercent;
                        break;

                    case ErrorWarningProperty.TotalCpuTime:
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

                    case ErrorWarningProperty.TotalMemoryConsumptionMb when healthReportType == HealthReportType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorMemoryMB : FOErrorWarningCodes.AppWarningMemoryMB;
                        break;
                    case ErrorWarningProperty.TotalMemoryConsumptionMb:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorMemoryMB : FOErrorWarningCodes.NodeWarningMemoryMB;
                        break;

                    case ErrorWarningProperty.TotalMemoryConsumptionPct when replicaOrInstance != null:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorMemoryPercent : FOErrorWarningCodes.AppWarningMemoryPercent;
                        break;

                    case ErrorWarningProperty.TotalMemoryConsumptionPct:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorMemoryPercent : FOErrorWarningCodes.NodeWarningMemoryPercent;
                        break;

                    case ErrorWarningProperty.DiskAverageQueueLength:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorDiskAverageQueueLength : FOErrorWarningCodes.NodeWarningDiskAverageQueueLength;
                        break;

                    case ErrorWarningProperty.TotalActiveFirewallRules:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.ErrorTooManyFirewallRules : FOErrorWarningCodes.WarningTooManyFirewallRules;
                        break;

                    case ErrorWarningProperty.TotalActivePorts when healthReportType == HealthReportType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyActiveTcpPorts : FOErrorWarningCodes.AppWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.TotalActivePorts:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTooManyActiveTcpPorts : FOErrorWarningCodes.NodeWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.TotalEphemeralPorts when healthReportType == HealthReportType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.AppErrorTooManyActiveEphemeralPorts : FOErrorWarningCodes.AppWarningTooManyActiveEphemeralPorts;
                        break;

                    case ErrorWarningProperty.TotalEphemeralPorts:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FOErrorWarningCodes.NodeErrorTooManyActiveEphemeralPorts : FOErrorWarningCodes.NodeWarningTooManyActiveEphemeralPorts;
                        break;
                }

                var healthMessage = new StringBuilder();

                string drive = string.Empty;

                if (ObserverName == ObserverConstants.DiskObserverName)
                {
                    drive = $"{data.Id}: ";

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        drive = $"{data.Id.Remove(1, 2)}: ";
                    }
                }

                _ = healthMessage.Append($"{drive}{data.Property} is at or above the specified {thresholdName} limit ({threshold}{data.Units})");
                _ = healthMessage.AppendLine($" - {data.Property}: {Math.Round(data.AverageDataValue)}{data.Units}");

                // The health event description will be a serialized instance of telemetryData,
                // so it should be completely constructed (filled with data) regardless
                // of user telemetry settings.
                telemetryData.ApplicationName = appName?.OriginalString ?? string.Empty;
                telemetryData.Code = errorWarningCode;
                if (replicaOrInstance != null && !string.IsNullOrEmpty(replicaOrInstance.ContainerId))
                {
                    telemetryData.ContainerId = replicaOrInstance.ContainerId;
                }
                telemetryData.HealthState = Enum.GetName(typeof(HealthState), healthState);
                telemetryData.HealthEventDescription = healthMessage.ToString();
                telemetryData.Metric = $"{drive}{data.Property}";
                telemetryData.ServiceName = serviceName?.OriginalString ?? string.Empty;
                telemetryData.Source = ObserverConstants.FabricObserverName;
                telemetryData.Value = Math.Round(data.AverageDataValue, 1);

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (IsTelemetryProviderEnabled && IsObserverTelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            telemetryData,
                            Token);
                }

                // ETW.
                if (IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            ApplicationName = appName?.OriginalString ?? string.Empty,
                            Code = errorWarningCode,
                            ContainerId = replicaOrInstance != null ? replicaOrInstance.ContainerId ?? string.Empty : string.Empty,
                            HealthState = Enum.GetName(typeof(HealthState), healthState),
                            HealthEventDescription = healthMessage.ToString(),
                            Metric = $"{drive}{data.Property}",
                            Node = NodeName,
                            ServiceName = serviceName?.OriginalString ?? string.Empty,
                            Source = ObserverConstants.FabricObserverName,
                            Value = Math.Round(data.AverageDataValue, 1),
                        });
                }

                var healthReport = new HealthReport
                {
                    AppName = appName,
                    Code = errorWarningCode,
                    EmitLogEvent = true,
                    HealthData = telemetryData,
                    HealthMessage = healthMessage.ToString(),
                    HealthReportTimeToLive = healthReportTtl,
                    ReportType = healthReportType,
                    State = healthState,
                    NodeName = NodeName,
                    Observer = ObserverName,
                    ResourceUsageDataProperty = data.Property,
                };

                if (!AppNames.Any(a => a == appName?.OriginalString))
                {
                    AppNames.Add(appName?.OriginalString);
                }

                // From FSO.
                if (replicaOrInstance == null && healthReportType == HealthReportType.Application)
                {
                    HealthReportProperties.Add(id);
                }
                else
                {
                    if (HealthReportProperties.Count == 0)
                    {
                        HealthReportProperties.Add(ObserverName switch
                        {
                            ObserverConstants.AppObserverName => "ApplicationHealth",
                            ObserverConstants.CertificateObserverName => "SecurityHealth",
                            ObserverConstants.DiskObserverName => "DiskHealth",
                            ObserverConstants.FabricSystemObserverName => "FabricSystemServiceHealth",
                            ObserverConstants.NetworkObserverName => "NetworkHealth",
                            ObserverConstants.OSObserverName => "MachineInformation",
                            ObserverConstants.NodeObserverName => "MachineResourceHealth",
                            _ => $"{data.Property}",
                        });
                    }
                }

                healthReport.Property = HealthReportProperties[^1];

                if (HealthReportSourceIds.Count == 0)
                {
                    HealthReportSourceIds.Add(ObserverName);
                }

                // Set internal health state info on data instance.
                data.ActiveErrorOrWarning = true;
                data.ActiveErrorOrWarningCode = errorWarningCode;

                // FOErrorWarningCode for distinct sourceid.
                if (!HealthReportSourceIds[^1].Contains($"({errorWarningCode})"))
                {
                    HealthReportSourceIds[^1] += $"({errorWarningCode})";
                }

                healthReport.SourceId = HealthReportSourceIds[^1];

                // Generate a Service Fabric Health Report.
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // This means this observer created a Warning or Error SF Health Report
                HasActiveFabricErrorOrWarning = true;

                // Clean up sb.
                _ = healthMessage.Clear();
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
                    if (replicaOrInstance != null && !string.IsNullOrEmpty(replicaOrInstance.ContainerId))
                    {
                        telemetryData.ContainerId = replicaOrInstance.ContainerId;
                    }
                    telemetryData.HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok);
                    telemetryData.HealthEventDescription = $"{data.Property} is now within normal/expected range.";
                    telemetryData.Metric = data.Property;
                    telemetryData.Source = ObserverConstants.FabricObserverName;
                    telemetryData.Value = Math.Round(data.AverageDataValue, 1);

                    // Telemetry
                    if (IsTelemetryProviderEnabled && IsObserverTelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportMetricAsync(
                                telemetryData,
                                Token);
                    }

                    // ETW.
                    if (IsEtwEnabled)
                    {
                        Logger.EtwLogger?.Write(
                            ObserverConstants.FabricObserverETWEventName,
                            new
                            {
                                ApplicationName = appName != null ? appName.OriginalString : string.Empty,
                                Code = data.ActiveErrorOrWarningCode,
                                ContainerId = replicaOrInstance != null ? replicaOrInstance.ContainerId ?? string.Empty : string.Empty,
                                HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                                HealthEventDescription = $"{data.Property} is now within normal/expected range.",
                                Metric = data.Property,
                                Node = NodeName,
                                ServiceName = name ?? string.Empty,
                                Source = ObserverConstants.FabricObserverName,
                                Value = Math.Round(data.AverageDataValue, 1),
                            });
                    }

                    var healthReport = new HealthReport
                    {
                        AppName = appName,
                        Code = data.ActiveErrorOrWarningCode,
                        EmitLogEvent = true,
                        HealthData = telemetryData,
                        HealthMessage = $"{data.Property} is now within normal/expected range.",
                        HealthReportTimeToLive = default(TimeSpan),
                        ReportType = healthReportType,
                        State = HealthState.Ok,
                        NodeName = NodeName,
                        Observer = ObserverName,
                        ResourceUsageDataProperty = data.Property,
                    };

                    if (!AppNames.Any(a => a == appName?.OriginalString))
                    {
                        AppNames.Add(appName?.OriginalString);
                    }

                    // From FSO.
                    if (replicaOrInstance == null && healthReportType == HealthReportType.Application)
                    {
                        HealthReportProperties.Add(id);
                    }
                    else
                    {
                        if (HealthReportProperties.Count == 0)
                        {
                            HealthReportProperties.Add(ObserverName switch
                            {
                                ObserverConstants.AppObserverName => "ApplicationHealth",
                                ObserverConstants.CertificateObserverName => "SecurityHealth",
                                ObserverConstants.DiskObserverName => "DiskHealth",
                                ObserverConstants.FabricSystemObserverName => "FabricSystemServiceHealth",
                                ObserverConstants.NetworkObserverName => "NetworkHealth",
                                ObserverConstants.OSObserverName => "MachineInformation",
                                ObserverConstants.NodeObserverName => "MachineResourceHealth",
                                _ => $"{data.Property}",
                            });
                        }
                    }

                    healthReport.Property = HealthReportProperties[^1];

                    if (HealthReportSourceIds.Count == 0)
                    {
                        HealthReportSourceIds.Add($"{ObserverName}({data.ActiveErrorOrWarningCode})");
                    }

                    healthReport.SourceId = HealthReportSourceIds[^1];

                    // Emit an Ok Health Report to clear Fabric Health warning.
                    HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // Reset health states.
                    data.ActiveErrorOrWarning = false;
                    data.ActiveErrorOrWarningCode = FOErrorWarningCodes.Ok;
                    HealthReportProperties.Clear();
                    HealthReportSourceIds.Clear();
                    HasActiveFabricErrorOrWarning = false;
                }
            }

            // No need to keep data in memory.
            if (data.Data is List<T> list)
            {
                // List<T> impl.
                list.Clear();
                list.TrimExcess();
            }
            else
            {
                // CircularBufferCollection<T> impl.
                data.Data.Clear();
            }
        }

        public TimeSpan SetHealthReportTimeToLive()
        {
            // First run.
            if (LastRunDateTime == DateTime.MinValue)
            {
                return TimeSpan.FromSeconds(ObserverManager.ObserverExecutionLoopSleepSeconds)
                       .Add(TimeSpan.FromMinutes(TtlAddMinutes));
            }

            return DateTime.Now.Subtract(LastRunDateTime)
                .Add(TimeSpan.FromSeconds(
                    RunDuration > TimeSpan.MinValue ? RunDuration.TotalSeconds : 0))
                .Add(TimeSpan.FromSeconds(
                    ObserverManager.ObserverExecutionLoopSleepSeconds))
                .Add(RunInterval > TimeSpan.MinValue ? RunInterval : TimeSpan.Zero);
        }

        // This is here so each Observer doesn't have to implement IDisposable.
        // If an Observer needs to dispose, then override this non-impl.
        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (this.disposedValue)
            {
                return;
            }

            if (disposing)
            {
                if (FabricClientInstance != null)
                {
                    FabricClientInstance.Dispose();
                    FabricClientInstance = null;
                }
            }

            this.disposedValue = true;
        }

        private void SetDefaultSfDumpPath()
        {
            // This only needs to be set once.
            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                this.SFLogRoot = ServiceFabricConfiguration.Instance.FabricLogRoot;

                if (!string.IsNullOrEmpty(this.SFLogRoot))
                {
                    this.dumpsPath = Path.Combine(this.SFLogRoot, "CrashDumps");
                }
            }

            if (Directory.Exists(this.dumpsPath))
            {
                return;
            }

            try
            {
                _ = Directory.CreateDirectory(this.dumpsPath);
            }
            catch (IOException e)
            {
                HealthReporter.ReportFabricObserverServiceHealth(
                    FabricServiceContext.ServiceName.ToString(),
                    ObserverName,
                    HealthState.Warning,
                    $"Unable to create dumps directory:{Environment.NewLine}{e}");

                this.dumpsPath = null;
            }
        }
    }
}