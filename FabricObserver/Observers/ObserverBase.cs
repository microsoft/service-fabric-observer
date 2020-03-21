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
using System.Globalization;
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
using Microsoft.Win32;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    public abstract class ObserverBase : IObserverBase<StatelessServiceContext>
    {
        // SF Infra.
        private const string SfWindowsRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Service Fabric";
        private const string SfInfrastructureLogRootRegistryName = "FabricLogRoot";
        private const int TtlAddMinutes = 5;
        private string sFLogRoot;

        // Dump.
        private string dumpsPath;
        private readonly int maxDumps = 5;
        private readonly Dictionary<string, int> serviceDumpCountDictionary = new Dictionary<string, int>();

        protected bool IsTelemetryProviderEnabled { get; set; } = ObserverManager.TelemetryEnabled;

        protected ITelemetryProvider TelemetryClient { get; set; }

        protected bool IsEtwEnabled { get; set; } = ObserverManager.EtwEnabled;

        protected FabricClient FabricClientInstance { get; set; }

        /// <inheritdoc/>
        public string ObserverName { get; set; }

        /// <inheritdoc/>
        public string NodeName { get; set; }

        public string NodeType { get; private set; }

        /// <inheritdoc/>
        public ObserverHealthReporter HealthReporter { get; }

        /// <inheritdoc/>
        public StatelessServiceContext FabricServiceContext { get; }

        /// <inheritdoc/>
        public DateTime LastRunDateTime { get; set; }

        public TimeSpan RunDuration { get; set; }

        public CancellationToken Token { get; set; }

        /// <inheritdoc/>
        public bool IsEnabled { get; set; } = true;

        public bool IsObserverTelemetryEnabled { get; set; }

        /// <inheritdoc/>
        public bool IsUnhealthy { get; set; } = false;

        // Only set for unit test runs.
        public bool IsTestRun { get; set; } = false;

        /// <inheritdoc/>
        public Logger ObserverLogger { get; set; }

        /// <inheritdoc/>
        public DataTableFileLogger CsvFileLogger { get; set; }

        // Each derived Observer can set this to maintain health status across iterations.
        // This information is used by ObserverManager.
        //
        /// <inheritdoc/>
        public bool HasActiveFabricErrorOrWarning { get; set; }

        /// <inheritdoc/>
        public TimeSpan RunInterval { get; set; } = TimeSpan.MinValue;

        public TimeSpan AsyncClusterOperationTimeoutSeconds { get; set; } = TimeSpan.FromSeconds(60);

        public int DataCapacity { get; set; } = 30;

        public bool UseCircularBuffer { get; set; } = false;

        public TimeSpan MonitorDuration { get; set; } = TimeSpan.MinValue;

        /// <inheritdoc/>
        public abstract Task ObserveAsync(CancellationToken token);

        /// <inheritdoc/>
        public abstract Task ReportAsync(CancellationToken token);

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverBase"/> class.
        /// </summary>
        protected ObserverBase(string observerName)
        {
            this.FabricClientInstance = ObserverManager.FabricClientInstance;

            if (this.IsTelemetryProviderEnabled)
            {
                this.TelemetryClient = ObserverManager.TelemetryClient;
            }

            this.ObserverName = observerName;
            this.FabricServiceContext = ObserverManager.FabricServiceContext;
            this.NodeName = this.FabricServiceContext.NodeContext.NodeName;
            this.NodeType = this.FabricServiceContext.NodeContext.NodeType;

            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                this.SetDefaultSfDumpPath();
            }

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
                string logFolderBase = $@"{Environment.CurrentDirectory}\observer_logs";
                logFolderBasePath = logFolderBase;
            }

            this.ObserverLogger = new Logger(observerName, logFolderBasePath);

            // Observer enabled?
            if (bool.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.ObserverEnabledParameter),
                out bool enabled))
            {
                this.IsEnabled = enabled;
            }

            // Observer telemetry enabled?
            if (bool.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.ObserverTelemetryEnabledParameter),
                out bool telemetryEnabled))
            {
                this.IsObserverTelemetryEnabled = telemetryEnabled;
            }

            // Verbose logging?
            if (bool.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                this.ObserverLogger.EnableVerboseLogging = enableVerboseLogging;
            }

            // RunInterval?
            if (TimeSpan.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.ObserverRunIntervalParameter),
                out TimeSpan runInterval))
            {
                this.RunInterval = runInterval;
            }

            // Monitor duration.
            if (TimeSpan.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.MonitorDurationParameter),
                out TimeSpan monitorDuration))
            {
                this.MonitorDuration = monitorDuration;
            }

            // Async cluster operation timeout setting..
            if (int.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.AsyncClusterOperationTimeoutSeconds),
                out int asyncOpTimeoutSeconds))
            {
                this.AsyncClusterOperationTimeoutSeconds = TimeSpan.FromSeconds(asyncOpTimeoutSeconds);
            }

            // DataLogger setup.
            this.CsvFileLogger = new DataTableFileLogger();
            string dataLogPath = this.GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName,
                ObserverConstants.DataLogPathParameter);
            if (!string.IsNullOrEmpty(observerLogPath))
            {
                this.CsvFileLogger.DataLogFolderPath = dataLogPath;
            }

            if (bool.TryParse(
                this.GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName,
                ObserverConstants.EnableLongRunningCsvLogging),
                out bool enableDataLogging))
            {
                this.CsvFileLogger.EnableCsvLogging = enableDataLogging;
            }

            // Resource usage data collection item capacity.
            if (int.TryParse(
               this.GetSettingParameterValue(
               observerName + "Configuration",
               ObserverConstants.DataCapacityParameter),
               out int dataCapacity))
            {
                this.DataCapacity = dataCapacity;
            }

            // Resource usage data collection type.
            if (bool.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.UseCircularBufferParameter),
                out bool useCircularBuffer))
            {
                this.UseCircularBuffer = useCircularBuffer;
            }

            this.HealthReporter = new ObserverHealthReporter(this.ObserverLogger);
        }

        /// <inheritdoc/>
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
        public string GetSettingParameterValue(string sectionName, string parameterName, string defaultValue = null)
        {
            if (string.IsNullOrEmpty(sectionName) || string.IsNullOrEmpty(parameterName))
            {
                return null;
            }

            try
            {
                var serviceConfiguration = this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

                if (serviceConfiguration.Settings.Sections.All(sec => sec.Name != sectionName))
                {
                    return null;
                }

                if (serviceConfiguration.Settings.Sections[sectionName].Parameters.All(param => param.Name != parameterName))
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
            catch (KeyNotFoundException)
            {
            }
            catch (NullReferenceException)
            {
            }

            return null;
        }

        /// <summary>
        /// Gets a dictionary of Parameters of the specified section.
        /// </summary>
        /// <param name="sectionName">Name of the section.</param>
        /// <returns>A dictionary of Parameters key/value pairs (string, string) or null upon failure.</returns>
        public IDictionary<string, string> GetConfigSettingSectionParameters(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName))
            {
                return null;
            }

            IDictionary<string, string> container = new Dictionary<string, string>();

            var serviceConfiguration = this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

            var sections = serviceConfiguration.Settings.Sections.FirstOrDefault(sec => sec.Name == sectionName);

            if (sections == null)
            {
                return null;
            }

            foreach (var param in sections.Parameters)
            {
                container.Add(param.Name, param.Value);
            }

            return container;
        }

        /// <summary>
        /// Gets the interval at which the Observer is to be run, i.e. "no more often than."
        /// This is useful for Observers that do not need to run very often (a la OSObserver, Certificate Observer, etc.)
        /// </summary>
        /// <param name="configSectionName">Observer configuration section name.</param>
        /// <param name="configParamName">Observer configuration parameter name.</param>
        /// <param name="defaultTo">Specific an optional TimeSpan to default to if setting is not found in config.
        /// else, it defaults to 24 hours.</param>
        /// <returns>run interval.</returns>
        public TimeSpan GetObserverRunInterval(
            string configSectionName,
            string configParamName,
            TimeSpan? defaultTo = null)
        {
            TimeSpan interval;

            try
            {
                interval = TimeSpan.Parse(
                    this.GetSettingParameterValue(
                                          configSectionName,
                                          configParamName),
                    CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is FormatException || e is OverflowException)
                {
                    // Parameter is not present or invalid, default to 24 hours or supplied defaultTo
                    if (defaultTo != null)
                    {
                        interval = (TimeSpan)defaultTo;
                    }
                    else
                    {
                        interval = TimeSpan.FromDays(1);
                    }
                }
                else
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.OriginalString,
                        this.ObserverName,
                        HealthState.Warning,
                        $"Unhandled exception running GetObserverRunInterval:{Environment.NewLine}{e}");

                    throw;
                }
            }

            return interval;
        }

        private void SetDefaultSfDumpPath()
        {
            // This only needs to be set once.
            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                this.sFLogRoot = (string)Registry.GetValue(SfWindowsRegistryPath, SfInfrastructureLogRootRegistryName, null);

                if (!string.IsNullOrEmpty(this.sFLogRoot))
                {
                    this.dumpsPath = Path.Combine(this.sFLogRoot, "CrashDumps");
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
                using (var diskUsage = new DiskUsage(this.dumpsPath.Substring(0, 1)))
                {
                    if (diskUsage.PercentUsedSpace >= 90)
                    {
                        this.HealthReporter.ReportFabricObserverServiceHealth(
                            this.FabricServiceContext.ServiceName.OriginalString,
                            this.ObserverName,
                            HealthState.Warning,
                            "Not enough disk space available for dump file creation.");
                        return false;
                    }
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

            TelemetryData telemetryData = null;

            if (replicaOrInstance != null)
            {
                repPartitionId = $"Partition: {replicaOrInstance.PartitionId}";
                repOrInstanceId = $"Replica: {replicaOrInstance.ReplicaOrInstanceId}";

                // Create a unique id which may be used in the case of warnings or OK clears.
                appName = replicaOrInstance.ApplicationName;
                name = appName.OriginalString.Replace("fabric:/", string.Empty);
                id = name + "_" + data.Property.Replace(" ", string.Empty);

                // Telemetry.
                if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                {
                    telemetryData = new TelemetryData(FabricClientInstance, Token)
                    {
                        ApplicationName = appName?.OriginalString ?? string.Empty,
                        Code = FoErrorWarningCodes.Ok,
                        NodeName = this.NodeName,
                        ObserverName = this.ObserverName,
                        Metric = data.Property,
                        Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1),
                        PartitionId = replicaOrInstance.PartitionId.ToString(),
                        ReplicaId = replicaOrInstance.ReplicaOrInstanceId.ToString(),
                        Source = ObserverConstants.FabricObserverName,
                    };
                }

                try
                {
                    procName = Process.GetProcessById((int)replicaOrInstance.HostProcessId).ProcessName;

                    if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                    {
                        telemetryData.ServiceName = procName;

                        _ = this.TelemetryClient?.ReportMetricAsync(
                            telemetryData,
                            Token).ConfigureAwait(false);
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
                // Telemetry.
                if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                {
                    telemetryData = new TelemetryData(FabricClientInstance, Token)
                    {
                        Code = FoErrorWarningCodes.Ok,
                        NodeName = this.NodeName,
                        ObserverName = this.ObserverName,
                        Metric = data.Property,
                        Source = ObserverConstants.FabricObserverName,
                        Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1),
                    };

                    _ = this.TelemetryClient?.ReportMetricAsync(
                        telemetryData,
                        this.Token);
                }
            }

            // ETW.
            if (this.IsEtwEnabled)
            {
                Logger.EtwLogger?.Write(
                    $"FabricObserverDataEvent",
                    new
                    {
                        Level = 0, // Info
                        Node = this.NodeName,
                        Observer = this.ObserverName,
                        data.Property,
                        data.Id,
                        Value = $"{Math.Round(Convert.ToDouble(data.AverageDataValue), 1)}",
                        Unit = data.Units,
                    });
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
                    case ErrorWarningProperty.TotalCpuTime when replicaOrInstance != null:
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

                    case ErrorWarningProperty.TotalMemoryConsumptionMb when replicaOrInstance != null:
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

                    case ErrorWarningProperty.TotalActivePorts when replicaOrInstance != null:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.AppErrorTooManyActiveTcpPorts : FoErrorWarningCodes.AppWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.TotalActivePorts:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorTooManyActiveTcpPorts : FoErrorWarningCodes.NodeWarningTooManyActiveTcpPorts;
                        break;

                    case ErrorWarningProperty.TotalEphemeralPorts when replicaOrInstance != null:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.AppErrorTooManyActiveEphemeralPorts : FoErrorWarningCodes.AppWarningTooManyActiveEphemeralPorts;
                        break;

                    case ErrorWarningProperty.TotalEphemeralPorts:
                        errorWarningCode = (healthState == HealthState.Error) ?
                            FoErrorWarningCodes.NodeErrorTooManyActiveEphemeralPorts : FoErrorWarningCodes.NodeWarningTooManyActiveEphemeralPorts;
                        break;
                }

                var healthMessage = new StringBuilder();

                if (name != null)
                {
                    _ = healthMessage.Append($"{name} (Service Process: {procName}, {repPartitionId}, {repOrInstanceId}): ");
                }

                string drive = string.Empty;

                if (data.Property.Contains("Disk"))
                {
                    drive = $"{data.Id}: ";
                }

                _ = healthMessage.Append($"{drive}{data.Property} is at or above the specified {thresholdName} limit ({threshold}{data.Units})");
                _ = healthMessage.AppendLine($" - Average {data.Property}: {Math.Round(Convert.ToDouble(data.AverageDataValue))}{data.Units}");

                var healthReport = new HealthReport
                {
                    AppName = appName,
                    Code = errorWarningCode,
                    EmitLogEvent = true,
                    HealthMessage = healthMessage.ToString(),
                    HealthReportTimeToLive = healthReportTtl,
                    ReportType = healthReportType,
                    State = healthState,
                    NodeName = this.NodeName,
                    Observer = this.ObserverName,
                    ResourceUsageDataProperty = data.Property,
                };

                // Emit a Fabric Health Report and optionally a local log write.
                this.HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Set internal health state info on data instance.
                data.ActiveErrorOrWarning = true;
                data.ActiveErrorOrWarningCode = errorWarningCode;

                // This means this observer created a Warning or Error SF Health Report
                this.HasActiveFabricErrorOrWarning = true;

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example.).
                if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                {
                    // Telemetry.
                    telemetryData.ApplicationName = appName?.OriginalString ?? string.Empty;
                    telemetryData.Code = errorWarningCode;
                    telemetryData.HealthState = Enum.GetName(typeof(HealthState), healthState);
                    telemetryData.HealthEventDescription = healthMessage.ToString();
                    telemetryData.Metric = $"{drive}{data.Property}";
                    telemetryData.Source = ObserverConstants.FabricObserverName;
                    telemetryData.Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1);

                    _ = this.TelemetryClient?.ReportMetricAsync(
                            telemetryData,
                            this.Token);
                }

                // ETW.
                if (this.IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        $"FabricObserverDataEvent",
                        new
                        {
                            Level = (healthState == HealthState.Warning) ? 1 : 2,
                            Node = this.NodeName,
                            Observer = this.ObserverName,
                            HealthEventErrorCode = errorWarningCode,
                            HealthEventDescription = healthMessage.ToString(),
                            data.Property,
                            data.Id,
                            Value = $"{Math.Round(Convert.ToDouble(data.AverageDataValue), 1)}",
                            Unit = data.Units,
                        });
                }

                // Clean up sb.
                _ = healthMessage.Clear();
            }
            else
            {
                if (data.ActiveErrorOrWarning)
                {
                    var report = new HealthReport
                    {
                        AppName = appName,
                        Code = data.ActiveErrorOrWarningCode,
                        EmitLogEvent = true,
                        HealthMessage = $"{data.Property} is now within normal/expected range.",
                        HealthReportTimeToLive = default(TimeSpan),
                        ReportType = healthReportType,
                        State = HealthState.Ok,
                        NodeName = this.NodeName,
                        Observer = this.ObserverName,
                        ResourceUsageDataProperty = data.Property,
                    };

                    // Emit an Ok Health Report to clear Fabric Health warning.
                    this.HealthReporter.ReportHealthToServiceFabric(report);

                    // Telemetry
                    if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
                    {
                        // Telemetry.
                        telemetryData.ApplicationName = appName?.OriginalString ?? string.Empty;
                        telemetryData.Code = data.ActiveErrorOrWarningCode;
                        telemetryData.HealthState = Enum.GetName(typeof(HealthState), HealthState.Ok);
                        telemetryData.HealthEventDescription = $"{data.Property} is now within normal/expected range.";
                        telemetryData.Metric = data.Property;
                        telemetryData.Source = ObserverConstants.FabricObserverName;
                        telemetryData.Value = Math.Round(Convert.ToDouble(data.AverageDataValue), 1);

                        _ = this.TelemetryClient?.ReportMetricAsync(
                                telemetryData,
                                this.Token);
                    }

                    // Reset health states.
                    data.ActiveErrorOrWarning = false;
                    data.ActiveErrorOrWarningCode = FoErrorWarningCodes.Ok;
                    this.HasActiveFabricErrorOrWarning = false;
                }
            }

            // No need to keep data in memory.
            data.Data.Clear();
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

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Dispose(true);
        }
    }
}