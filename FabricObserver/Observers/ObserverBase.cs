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
using FabricObserver.Interfaces;
using FabricObserver.Model;
using FabricObserver.Utilities;
using Microsoft.Win32;

namespace FabricObserver
{
    public abstract class ObserverBase : IObserverBase<StatelessServiceContext>
    {
        // SF Infra...
        private const string SFWindowsRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Service Fabric";
        private const string SFInfrastructureLogRootRegistryName = "FabricLogRoot";
        private const int TTLAddMinutes = 10;
        private string sFLogRoot = null;

        // Dump...
        private string dumpsPath = null;
        private int maxDumps = 5;
        private Dictionary<string, int> serviceDumpCountDictionary = new Dictionary<string, int>();

        protected bool IsTelemetryEnabled { get; set; } = ObserverManager.TelemetryEnabled;

        protected bool IsEtwEnabled { get; set; } = ObserverManager.EtwEnabled;

        protected IObserverTelemetryProvider ObserverTelemetryClient { get; set; } = null;

        protected FabricClient FabricClientInstance { get; set; } = null;

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

        public CancellationToken Token { get; set; }

        /// <inheritdoc/>
        public bool IsEnabled { get; set; } = true;

        /// <inheritdoc/>
        public bool IsUnhealthy { get; set; } = false;

        // Only set for unit test runs...
        public bool IsTestRun { get; set; } = false;

        // Loggers...

        /// <inheritdoc/>
        public Logger ObserverLogger { get; set; }

        /// <inheritdoc/>
        public DataTableFileLogger CsvFileLogger { get; set; }

        // Each derived Observer can set this to maintain health status across iterations.
        // This information is used by ObserverManager.

        /// <inheritdoc/>
        public bool HasActiveFabricErrorOrWarning { get; set; } = false;

        /// <inheritdoc/>
        public TimeSpan RunInterval { get; set; } = TimeSpan.MinValue;

        public List<string> Settings { get; } = null;

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

            if (this.IsTelemetryEnabled)
            {
                this.ObserverTelemetryClient = ObserverManager.TelemetryClient;
            }

            this.Settings = new List<string>();
            this.ObserverName = observerName;
            this.FabricServiceContext = ObserverManager.FabricServiceContext;
            this.NodeName = this.FabricServiceContext.NodeContext.NodeName;
            this.NodeType = this.FabricServiceContext.NodeContext.NodeType;

            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                this.SetDefaultSFDumpPath();
            }

            // Observer Logger setup...
            string logFolderBasePath = null;
            string observerLogPath = this.GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName,
                ObserverConstants.ObserverLogPath);

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
                ObserverConstants.ObserverEnabled),
                out bool enabled))
            {
                this.IsEnabled = enabled;
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
                ObserverConstants.ObserverRunIntervalParameterName),
                out TimeSpan runInterval))
            {
                this.RunInterval = runInterval;
            }

            // DataLogger setup...
            this.CsvFileLogger = new DataTableFileLogger();
            string dataLogPath = this.GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName,
                ObserverConstants.DataLogPath);
            if (!string.IsNullOrEmpty(observerLogPath))
            {
                this.CsvFileLogger.DataLogFolderPath = dataLogPath;
            }

            if (bool.TryParse(
                this.GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName,
                ObserverConstants.EnableLongRunningCSVLogging),
                out bool enableDataLogging))
            {
                this.CsvFileLogger.EnableCsvLogging = enableDataLogging;
            }

            this.HealthReporter = new ObserverHealthReporter(this.ObserverLogger);
        }

        /// <inheritdoc/>
        public void WriteToLogWithLevel(string property, string description, LogLevel level)
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
                string setting = serviceConfiguration.Settings.Sections[sectionName].Parameters[parameterName].Value;

                if (string.IsNullOrEmpty(setting) && defaultValue != null)
                {
                    return defaultValue;
                }

                return setting;
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a dictionary of Parameters of the specified section...
        /// </summary>
        /// <param name="sectionName">Name of the section...</param>
        /// <returns>A dictionary of Parameters key/value pairs (string, string) or null upon failure...</returns>
        public IDictionary<string, string> GetConfigSettingSectionParameters(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName))
            {
                return null;
            }

            IDictionary<string, string> container = new Dictionary<string, string>();

            var serviceConfiguration = this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

            var sections = serviceConfiguration.Settings.Sections.Where(sec => sec.Name == sectionName)?.FirstOrDefault();

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
        /// Gets the interval at which the Observer is to be run, i.e. "no more often than..."
        /// This is useful for Observers that do not need to run very often (a la OSObserver, Certificate Observer, etc...)
        /// </summary>
        /// <param name="configSectionName">Observer configuration section name...</param>
        /// <param name="configParamName">Observer configuration parameter name...</param>
        /// <param name="defaultTo">Specific an optional TimeSpan to default to if setting is not found in config...
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
                        "Unhandled exception running GetObserverRunInterval:\n" + e.ToString());
                    throw;
                }
            }

            return interval;
        }

        private void SetDefaultSFDumpPath()
        {
            // This only needs to be set once.
            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                this.sFLogRoot = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureLogRootRegistryName, null);

                if (!string.IsNullOrEmpty(this.sFLogRoot))
                {
                    this.dumpsPath = Path.Combine(this.sFLogRoot, "CrashDumps");
                }
            }

            if (!Directory.Exists(this.dumpsPath))
            {
                try
                {
                    Directory.CreateDirectory(this.dumpsPath);
                }
                catch (IOException e)
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        this.FabricServiceContext.ServiceName.ToString(),
                        this.ObserverName,
                        HealthState.Warning,
                        $"Unable to create dumps directory: {e.ToString()}");
                    this.dumpsPath = null;
                }
            }
        }

        // Windows process dmp creator...
        internal bool DumpServiceProcess(int processId, DumpType dumpType = DumpType.Full)
        {
            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                return false;
            }

            string processName = string.Empty;

            var miniDumpType = NativeMethods.MINIDUMP_TYPE.MiniDumpNormal;

            if (dumpType == DumpType.Full)
            {
                miniDumpType = NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemory |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules;
            }
            else if (dumpType == DumpType.MiniPlus)
            {
                miniDumpType = NativeMethods.MINIDUMP_TYPE.MiniDumpWithPrivateReadWriteMemory |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithDataSegs |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithHandleData |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithThreadInfo |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpWithUnloadedModules;
            }
            else if (dumpType == DumpType.Mini)
            {
                miniDumpType = NativeMethods.MINIDUMP_TYPE.MiniDumpWithIndirectlyReferencedMemory |
                               NativeMethods.MINIDUMP_TYPE.MiniDumpScanMemory;
            }

            try
            {
                // This is to ensure friendly-name of resulting dmp file...
                processName = Process.GetProcessById(processId)?.ProcessName;

                if (string.IsNullOrEmpty(processName))
                {
                    return false;
                }

                IntPtr processHandle = Process.GetProcessById(processId).Handle;

                processName += "_" + DateTime.Now.ToString("ddMMyyyyHHmmss") + ".dmp";

                // Check disk space availability before writing dump file...
                using (var diskUsage = new DiskUsage(this.dumpsPath.Substring(0, 1)))
                {
                    if (diskUsage.PercentUsedSpace >= 90)
                    {
                        this.HealthReporter.ReportFabricObserverServiceHealth(
                            this.FabricServiceContext.ServiceName.OriginalString,
                            this.ObserverName,
                            HealthState.Warning,
                            "Not enough disk space available for dump file creation...");
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
            catch (ArgumentException ae)
            {
                this.ObserverLogger.LogInfo("Unable to generate dump file {0} with error {1}", processName, ae.ToString());
            }
            catch (InvalidOperationException ie)
            {
                this.ObserverLogger.LogInfo("Unable to generate dump file {0} with error {1}", processName, ie.ToString());
            }
            catch (Win32Exception we)
            {
                this.ObserverLogger.LogInfo("Unable to generate dump file {0} with error {1}", processName, we.ToString());
            }

            return false;
        }

        public void ProcessResourceDataReportHealth<T>(
            FabricResourceUsageData<T> data,
            T thresholdError,
            T thresholdWarning,
            TimeSpan healthReportTtl,
            HealthReportType healthReportType = HealthReportType.Node,
            string app = null,
            ReplicaMonitoringInfo replicaOrInstance = null,
            bool dumpOnError = false)
        {
            if (data == null)
            {
                throw new ArgumentException("Supply all required parameters with non-null value...");
            }

            var thresholdName = "Minimum";
            bool warningOrError = false;
            string repPartitionId = null, repOrInstanceId = null, name = null, id = null, procName = null;
            T threshold = thresholdWarning;
            var healthState = HealthState.Ok;
            Uri appName = null;

            if (replicaOrInstance != null)
            {
                repPartitionId = $"Partition: {replicaOrInstance.Partitionid}";
                repOrInstanceId = $"Replica: {replicaOrInstance.ReplicaOrInstanceId}";
                procName = Process.GetProcessById((int)replicaOrInstance.ReplicaHostProcessId)?.ProcessName;
            }

            // Create a unique node id which may be used in the case of warnings or OK clears...
            if (app != null)
            {
                appName = new Uri(app);
                name = app.Replace("fabric:/", string.Empty);
                id = name + "_" + data.Property.Replace(" ", string.Empty);
            }

            // Telemetry...
            if (this.IsTelemetryEnabled)
            {
                _ = this.ObserverTelemetryClient?.ReportMetricAsync($"{this.NodeName}-{app}-{data.Id}-{data.Property}", data.AverageDataValue, this.Token);
            }

            // ETW...
            if (this.IsEtwEnabled)
            {
                Logger.EtwLogger?.Write(
                    $"FabricObserverDataEvent",
                    new
                    {
                        Level = 0, // Info
                        Node = this.NodeName,
                        Observer = this.ObserverName,
                        Property = data.Property,
                        Id = data.Id,
                        Value = $"{Math.Round(data.AverageDataValue)}",
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
                // part of the base class for future use, like for FSO...
                if (replicaOrInstance != null && procName != null && dumpOnError)
                {
                    try
                    {
                        int procId = (int)replicaOrInstance.ReplicaHostProcessId;

                        if (!this.serviceDumpCountDictionary.ContainsKey(procName))
                        {
                            this.serviceDumpCountDictionary.Add(procName, 0);
                        }

                        if (this.serviceDumpCountDictionary[procName] < this.maxDumps)
                        {
                            // DumpServiceProcess defaults to a Full dump with
                            // process memory, handles and thread data...
                            bool success = this.DumpServiceProcess(procId);

                            if (success)
                            {
                                this.serviceDumpCountDictionary[procName]++;
                            }
                        }
                    }

                    // Ignore these, it just means no dmp will be created.This is not
                    // critical to FO... Log as info, not warning...
                    catch (ArgumentException ae)
                    {
                        this.ObserverLogger.LogInfo($"Unable to generate dmp file:\n{ae.ToString()}");
                    }
                    catch (InvalidOperationException ioe)
                    {
                        this.ObserverLogger.LogInfo($"Unable to generate dmp file:\n{ioe.ToString()}");
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
                string errorWarningKind = null;

                if (data.Property == ErrorWarningProperty.TotalCpuTime)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorCpuTime : ErrorWarningCode.WarningCpuTime;
                }
                else if (data.Property == ErrorWarningProperty.DiskSpaceUsagePercentage)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorDiskSpacePercentUsed : ErrorWarningCode.WarningDiskSpacePercentUsed;
                }
                else if (data.Property == ErrorWarningProperty.DiskSpaceUsageMB)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorDiskSpaceMB : ErrorWarningCode.WarningDiskSpaceMB;
                }
                else if (data.Property == ErrorWarningProperty.TotalMemoryConsumptionMB)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorMemoryCommitted : ErrorWarningCode.WarningMemoryCommitted;
                }
                else if (data.Property == ErrorWarningProperty.TotalMemoryConsumptionPct)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorMemoryPercentUsed : ErrorWarningCode.WarningMemoryPercentUsed;
                }
                else if (data.Property == ErrorWarningProperty.DiskAverageQueueLength)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorDiskAverageQueueLength : ErrorWarningCode.WarningDiskAverageQueueLength;
                }
                else if (data.Property == ErrorWarningProperty.TotalActiveFirewallRules)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorTooManyFirewallRules : ErrorWarningCode.WarningTooManyFirewallRules;
                }
                else if (data.Property == ErrorWarningProperty.TotalActivePorts)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorTooManyActivePorts : ErrorWarningCode.WarningTooManyActiveTcpPorts;
                }
                else if (data.Property == ErrorWarningProperty.TotalEphemeralPorts)
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorTooManyActiveEphemeralPorts : ErrorWarningCode.WarningTooManyActiveEphemeralPorts;
                }

                var healthMessage = new StringBuilder();

                if (name != null)
                {
                    healthMessage.Append($"{name} (Service Process: {procName}, {repPartitionId}, {repOrInstanceId}): ");
                }

                healthMessage.Append($"{data.Property} is at or above the specified {thresholdName} limit ({threshold}{data.Units})");
                healthMessage.AppendLine($" - Average {data.Property}: {Math.Round(data.AverageDataValue)}{data.Units}");

                var healthReport = new Utilities.HealthReport
                {
                    AppName = appName,
                    Code = errorWarningKind,
                    EmitLogEvent = true,
                    HealthMessage = healthMessage.ToString(),
                    HealthReportTimeToLive = healthReportTtl,
                    ReportType = healthReportType,
                    State = healthState,
                    NodeName = this.NodeName,
                    Observer = this.ObserverName,
                    ResourceUsageDataProperty = data.Property,
                };

                // Emit a Fabric Health Report and optionally a local log write...
                this.HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Set internal fabric health states...
                data.ActiveErrorOrWarning = true;

                // This means this observer created a Warning or Error SF Health Report
                this.HasActiveFabricErrorOrWarning = true;

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example...)...
                if (this.IsTelemetryEnabled)
                {
                    _ = this.ObserverTelemetryClient?.ReportHealthAsync(
                        id,
                        this.FabricServiceContext.ServiceName.OriginalString,
                        "FabricObserver",
                        this.ObserverName,
                        $"{this.NodeName}/{errorWarningKind}/{data.Property}/{Math.Round(data.AverageDataValue)}",
                        healthState,
                        this.Token);
                }

                // ETW...
                if (this.IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        $"FabricObserverDataEvent",
                        new
                        {
                            Level = (healthState == HealthState.Warning) ? 1 : 2,
                            Node = this.NodeName,
                            Observer = this.ObserverName,
                            HealthEventErrorCode = errorWarningKind,
                            HealthEventDescription = healthMessage.ToString(),
                            Property = data.Property,
                            Id = data.Id,
                            Value = $"{Math.Round(data.AverageDataValue)}",
                            Unit = data.Units,
                        });
                }

                // Clean up sb...
                healthMessage.Clear();
            }
            else
            {
                if (data.ActiveErrorOrWarning)
                {
                    var report = new Utilities.HealthReport
                    {
                        AppName = appName,
                        EmitLogEvent = true,
                        HealthMessage = $"{data.Property} is now within normal/expected range.",
                        HealthReportTimeToLive = default(TimeSpan),
                        ReportType = healthReportType,
                        State = HealthState.Ok,
                        NodeName = this.NodeName,
                        Observer = this.ObserverName,
                        ResourceUsageDataProperty = data.Property,
                    };

                    // Emit an Ok Health Report to clear Fabric Health warning...
                    this.HealthReporter.ReportHealthToServiceFabric(report);

                    // Reset health states...
                    data.ActiveErrorOrWarning = false;
                    this.HasActiveFabricErrorOrWarning = false;
                }
            }

            // No need to keep data in memory...
            data.Data.Clear();
            data.Data.TrimExcess();
        }

        public TimeSpan SetTimeToLiveWarning(int runDuration = 0)
        {
            // First run...
            if (this.LastRunDateTime == DateTime.MinValue)
            {
                return TimeSpan.FromSeconds(ObserverManager.ObserverExecutionLoopSleepSeconds)
                       .Add(TimeSpan.FromMinutes(TTLAddMinutes));
            }
            else
            {
                return DateTime.Now.Subtract(this.LastRunDateTime)
                       .Add(TimeSpan.FromSeconds(runDuration))
                       .Add(TimeSpan.FromSeconds(ObserverManager.ObserverExecutionLoopSleepSeconds))

                       // If a RunInterval is specified, it must be reflected in the TTL...
                       .Add(this.RunInterval > TimeSpan.MinValue ? this.RunInterval : TimeSpan.Zero)
                       .Add(TimeSpan.FromMinutes(TTLAddMinutes));
            }
        }

        // This is here so each Observer doesn't have to implement IDisposable.
        // If an Observer needs to dispose, then override this non-impl...
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
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
        }

        /// <inheritdoc/>
        public virtual void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            this.Dispose(true);
        }
    }

    internal enum DumpType
    {
        Mini,
        MiniPlus,
        Full,
    }
}