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

        public string NodeName { get; set; }

        public string NodeType { get; private set; }

        public ObserverHealthReporter HealthReporter { get; }
        
        public ServiceContext FabricServiceContext { get; }

        public DateTime LastRunDateTime { get; set; }

        public TimeSpan RunDuration { get; set; }

        public CancellationToken Token { get; set; }

        public bool IsEnabled { get; set; } = true;

        public bool IsObserverTelemetryEnabled { get; set; }

        public bool IsUnhealthy { get; set; } = false;

        // This static property is *only* used - and set to true - for local development unit test runs. 
        // Do not set this to true otherwise.
        public static bool IsTestRun { get; set; } = false;

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

        
        public Logger ObserverLogger { get; set; }

        public DataTableFileLogger CsvFileLogger { get; set; }

        // Each derived Observer can set this to maintain health status across iterations.
        // This information is used by ObserverManager.
        public bool HasActiveFabricErrorOrWarning { get; set; }

        public TimeSpan RunInterval { get; set; } = TimeSpan.MinValue;

        public TimeSpan AsyncClusterOperationTimeoutSeconds { get; set; } = TimeSpan.FromSeconds(60);

        public int DataCapacity { get; set; } = 30;

        public bool UseCircularBuffer { get; set; } = false;

        public TimeSpan MonitorDuration { get; set; } = TimeSpan.MinValue;

        protected bool IsTelemetryProviderEnabled { get; set; } = ObserverManager.TelemetryEnabled;

        protected ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        protected bool IsEtwEnabled { get; set; } = ObserverManager.EtwEnabled;

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
            this.ObserverName = this.GetType().Name;
            this.ConfigurationSectionName = this.ObserverName + "Configuration";
            this.FabricClientInstance = ObserverManager.FabricClientInstance;

            if (this.IsTelemetryProviderEnabled)
            {
                this.TelemetryClient = ObserverManager.TelemetryClient;
            }

            this.FabricServiceContext = ObserverManager.FabricServiceContext;
            this.NodeName = this.FabricServiceContext.NodeContext.NodeName;
            this.NodeType = this.FabricServiceContext.NodeContext.NodeType;
            this.ConfigurationSectionName = this.ObserverName + "Configuration";
            
            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = this.GetSettingParameterValue(
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

            this.ObserverLogger = new Logger(this.ObserverName, logFolderBasePath);

            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                this.SetDefaultSfDumpPath();
            }

            // DataLogger setup
            if (bool.TryParse(
                this.GetSettingParameterValue(
                this.ConfigurationSectionName,
                ObserverConstants.EnableLongRunningCsvLogging),
                out bool enableDataLogging))
            {
                if (enableDataLogging)
                {
                    this.CsvFileLogger = new DataTableFileLogger
                    {
                        EnableCsvLogging = enableDataLogging,
                    };

                    string dataLogPath = this.GetSettingParameterValue(
                        ObserverConstants.ObserverManagerConfigurationSectionName,
                        ObserverConstants.DataLogPathParameter);

                    if (!string.IsNullOrEmpty(dataLogPath))
                    {
                        this.CsvFileLogger.DataLogFolderPath = dataLogPath;
                    }
                    else
                    {
                        this.CsvFileLogger.DataLogFolderPath = Path.Combine(Environment.CurrentDirectory, "observer_data_logs");
                    }
                }
            }
            
            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                this.SetDefaultSfDumpPath();
            }

            if (!IsTestRun)
            {
                this.ConfigurationSettings = new Utilities.ConfigSettings(
                    FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings,
                    this.ConfigurationSectionName);

                this.EnableVerboseLogging = this.ConfigurationSettings.EnableVerboseLogging;
                this.IsEnabled = this.ConfigurationSettings.IsEnabled;
                this.IsObserverTelemetryEnabled = this.ConfigurationSettings.IsObserverTelemetryEnabled;
                this.MonitorDuration = this.ConfigurationSettings.MonitorDuration;
                this.RunInterval = this.ConfigurationSettings.RunInterval;
                this.ObserverLogger.EnableVerboseLogging = this.EnableVerboseLogging;
            }

            this.HealthReporter = new ObserverHealthReporter(this.ObserverLogger);
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
                    this.ObserverLogger.LogInfo("{0} logged at level {1}: {2}", property, level, description);
                    break;

                case LogLevel.Warning:
                    this.ObserverLogger.LogWarning("{0} logged at level {1}: {2}", property, level, description);
                    break;

                case LogLevel.Error:
                    this.ObserverLogger.LogError("{0} logged at level {1}: {2}", property, level, description);
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
                var serviceConfiguration = this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

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
            this.Dispose(true);
        }

        // Windows process dmp creator.
        internal bool DumpServiceProcess(int processId, DumpType dumpType = DumpType.Full)
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
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.OriginalString,
                        this.ObserverName,
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
                this.ObserverLogger.LogInfo(
                    $"Unable to generate dump file {processName} with error{Environment.NewLine}{e}");
            }

            return false;
        }

        internal void ProcessResourceDataReportHealth<T>(
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
                telemetryData = new TelemetryData(this.FabricClientInstance, this.Token)
                {
                    ApplicationName = appName?.OriginalString ?? string.Empty,
                    Code = FoErrorWarningCodes.Ok,
                    HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                    NodeName = this.NodeName,
                    ObserverName = this.ObserverName,
                    Metric = data.Property,
                    Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1),
                    PartitionId = replicaOrInstance?.PartitionId.ToString(),
                    ReplicaId = replicaOrInstance?.ReplicaOrInstanceId.ToString(),
                    ServiceName = serviceName?.OriginalString ?? string.Empty,
                    Source = ObserverConstants.FabricObserverName,
                };

                try
                {
                    if (replicaOrInstance != null)
                    {
                        procName = Process.GetProcessById((int)replicaOrInstance.HostProcessId).ProcessName;
                    }
                    else
                    {
                        // The name of the target service process is always the id for data containers coming from FSO.
                        procName = data.Id;
                    }

                    telemetryData.ServiceName = procName;

                    if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                    {
                        _ = this.TelemetryClient?.ReportMetricAsync(
                            telemetryData,
                            this.Token).ConfigureAwait(false);
                    }

                    if (this.IsEtwEnabled)
                    {
                        Logger.EtwLogger?.Write(
                            ObserverConstants.FabricObserverETWEventName,
                            new
                            {
                                ApplicationName = appName?.OriginalString ?? string.Empty,
                                Code = FoErrorWarningCodes.Ok,
                                HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                                this.NodeName,
                                this.ObserverName,
                                Metric = data.Property,
                                Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1),
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

                if (this.ObserverName == ObserverConstants.DiskObserverName)
                {
                    drive = $"{data.Id}: ";
                }

                // The health event description will be a serialized instance of telemetryData,
                // so it should be completely constructed (filled with data) regardless
                // of user telemetry settings.
                telemetryData = new TelemetryData(this.FabricClientInstance, this.Token)
                {
                    Code = FoErrorWarningCodes.Ok,
                    HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                    NodeName = this.NodeName,
                    ObserverName = this.ObserverName,
                    Metric = $"{drive}{data.Property}",
                    Source = ObserverConstants.FabricObserverName,
                    Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1),
                };

                if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                {
                    _ = this.TelemetryClient?.ReportMetricAsync(
                        telemetryData,
                        this.Token);
                }

                if (this.IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Code = FoErrorWarningCodes.Ok,
                            HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                            this.NodeName,
                            this.ObserverName,
                            Metric = $"{drive}{data.Property}",
                            Source = ObserverConstants.FabricObserverName,
                            Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1),
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
                            bool success = this.DumpServiceProcess(procId);

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
                        this.ObserverLogger.LogInfo($"Unable to generate dmp file:{Environment.NewLine}{e}");
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
                            FoErrorWarningCodes.AppErrorCpuTime : FoErrorWarningCodes.AppWarningCpuTime;
                        break;

                    case ErrorWarningProperty.TotalCpuTime:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorCpuTime : FoErrorWarningCodes.NodeWarningCpuTime;
                        break;

                    case ErrorWarningProperty.DiskSpaceUsagePercentage:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorDiskSpacePercentUsed : FoErrorWarningCodes.NodeWarningDiskSpacePercentUsed;
                        break;

                    case ErrorWarningProperty.DiskSpaceUsageMb:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorDiskSpaceMb : FoErrorWarningCodes.NodeWarningDiskSpaceMb;
                        break;

                    case ErrorWarningProperty.TotalMemoryConsumptionMb when healthReportType == HealthReportType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.AppErrorMemoryCommittedMb : FoErrorWarningCodes.AppWarningMemoryCommittedMb;
                        break;
                    case ErrorWarningProperty.TotalMemoryConsumptionMb:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorMemoryCommittedMb : FoErrorWarningCodes.NodeWarningMemoryCommittedMb;
                        break;

                    case ErrorWarningProperty.TotalMemoryConsumptionPct when replicaOrInstance != null:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.AppErrorMemoryPercentUsed : FoErrorWarningCodes.AppWarningMemoryPercentUsed;
                        break;

                    case ErrorWarningProperty.TotalMemoryConsumptionPct:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorMemoryPercentUsed : FoErrorWarningCodes.NodeWarningMemoryPercentUsed;
                        break;

                    case ErrorWarningProperty.DiskAverageQueueLength:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorDiskAverageQueueLength : FoErrorWarningCodes.NodeWarningDiskAverageQueueLength;
                        break;

                    case ErrorWarningProperty.TotalActiveFirewallRules:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.ErrorTooManyFirewallRules : FoErrorWarningCodes.WarningTooManyFirewallRules;
                        break;

                    case ErrorWarningProperty.TotalActivePorts when healthReportType == HealthReportType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.AppErrorTooManyActiveTcpPorts : FoErrorWarningCodes.AppWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.TotalActivePorts:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorTooManyActiveTcpPorts : FoErrorWarningCodes.NodeWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.TotalEphemeralPorts when healthReportType == HealthReportType.Application:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.AppErrorTooManyActiveEphemeralPorts : FoErrorWarningCodes.AppWarningTooManyActiveEphemeralPorts;
                        break;

                    case ErrorWarningProperty.TotalEphemeralPorts:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorTooManyActiveEphemeralPorts : FoErrorWarningCodes.NodeWarningTooManyActiveEphemeralPorts;
                        break;
                }

                var healthMessage = new StringBuilder();

                string drive = string.Empty;

                if (this.ObserverName == ObserverConstants.DiskObserverName)
                {
                    drive = $"{data.Id}: ";
                }

                _ = healthMessage.Append($"{drive}{data.Property} is at or above the specified {thresholdName} limit ({threshold}{data.Units})");
                _ = healthMessage.AppendLine($" - {data.Property}: {Math.Round(Convert.ToDouble(data.AverageDataValue))}{data.Units}");

                // The health event description will be a serialized instance of telemetryData,
                // so it should be completely constructed (filled with data) regardless
                // of user telemetry settings.
                telemetryData.ApplicationName = appName?.OriginalString ?? string.Empty;
                telemetryData.Code = errorWarningCode;
                telemetryData.HealthState = Enum.GetName(typeof(HealthState), healthState);
                telemetryData.HealthEventDescription = healthMessage.ToString();
                telemetryData.Metric = $"{drive}{data.Property}";
                telemetryData.ServiceName = serviceName?.OriginalString ?? string.Empty;
                telemetryData.Source = ObserverConstants.FabricObserverName;
                telemetryData.Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1);

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                {
                    _ = this.TelemetryClient?.ReportHealthAsync(
                            telemetryData,
                            this.Token);
                }

                // ETW.
                if (this.IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            ApplicationName = appName?.OriginalString ?? string.Empty,
                            Code = errorWarningCode,
                            HealthState = Enum.GetName(typeof(HealthState), healthState),
                            HealthEventDescription = healthMessage.ToString(),
                            Metric = $"{drive}{data.Property}",
                            Node = this.NodeName,
                            ServiceName = serviceName?.OriginalString ?? string.Empty,
                            Source = ObserverConstants.FabricObserverName,
                            Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1),
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
                    NodeName = this.NodeName,
                    Observer = this.ObserverName,
                    ResourceUsageDataProperty = data.Property,
                };

                // From FSO.
                if (replicaOrInstance == null && healthReportType == HealthReportType.Application)
                {
                    healthReport.Property = id;
                }

                // Emit a Fabric Health Report and optionally a local log write.
                this.HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Set internal health state info on data instance.
                data.ActiveErrorOrWarning = true;
                data.ActiveErrorOrWarningCode = errorWarningCode;

                // This means this observer created a Warning or Error SF Health Report
                this.HasActiveFabricErrorOrWarning = true;

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
                    telemetryData.Code = data.ActiveErrorOrWarningCode;
                    telemetryData.HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok);
                    telemetryData.HealthEventDescription = $"{data.Property} is now within normal/expected range.";
                    telemetryData.Metric = data.Property;
                    telemetryData.Source = ObserverConstants.FabricObserverName;
                    telemetryData.Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1);

                    // Telemetry
                    if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                    {
                        _ = this.TelemetryClient?.ReportMetricAsync(
                                telemetryData,
                                this.Token);
                    }

                    // ETW.
                    if (this.IsEtwEnabled)
                    {
                        Logger.EtwLogger?.Write(
                            ObserverConstants.FabricObserverETWEventName,
                            new
                            {
                                ApplicationName = appName != null ? appName.OriginalString : string.Empty,
                                Code = data.ActiveErrorOrWarningCode,
                                HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok),
                                HealthEventDescription = $"{data.Property} is now within normal/expected range.",
                                Metric = data.Property,
                                Node = this.NodeName,
                                ServiceName = name ?? string.Empty,
                                Source = ObserverConstants.FabricObserverName,
                                Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1),
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
                        NodeName = this.NodeName,
                        Observer = this.ObserverName,
                        ResourceUsageDataProperty = data.Property,
                    };

                    // From FSO.
                    if (replicaOrInstance == null && healthReportType == HealthReportType.Application)
                    {
                        healthReport.Property = id;
                    }

                    // Emit an Ok Health Report to clear Fabric Health warning.
                    this.HealthReporter.ReportHealthToServiceFabric(healthReport);

                    // Reset health states.
                    data.ActiveErrorOrWarning = false;
                    data.ActiveErrorOrWarningCode = FoErrorWarningCodes.Ok;
                    this.HasActiveFabricErrorOrWarning = false;
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

        internal TimeSpan SetHealthReportTimeToLive()
        {
            // First run.
            if (this.LastRunDateTime == DateTime.MinValue)
            {
                return TimeSpan.FromSeconds(ObserverManager.ObserverExecutionLoopSleepSeconds)
                       .Add(TimeSpan.FromMinutes(TtlAddMinutes));
            }

            return DateTime.Now.Subtract(this.LastRunDateTime)
                .Add(TimeSpan.FromSeconds(
                    this.RunDuration > TimeSpan.MinValue ? this.RunDuration.TotalSeconds : 0))
                .Add(TimeSpan.FromSeconds(
                    ObserverManager.ObserverExecutionLoopSleepSeconds))
                .Add(this.RunInterval > TimeSpan.MinValue ? this.RunInterval : TimeSpan.Zero);
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
                if (this.FabricClientInstance != null)
                {
                    this.FabricClientInstance.Dispose();
                    this.FabricClientInstance = null;
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
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.ToString(),
                    this.ObserverName,
                    HealthState.Warning,
                    $"Unable to create dumps directory:{Environment.NewLine}{e}");

                this.dumpsPath = null;
            }
        }
    }
}