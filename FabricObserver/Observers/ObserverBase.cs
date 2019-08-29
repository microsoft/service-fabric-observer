// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Interfaces;
using FabricObserver.Model;
using FabricObserver.Utilities;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Fabric;
using System.Fabric.Health;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver
{
    public abstract class ObserverBase : IObserverBase<StatelessServiceContext>
    {
        // Dump...
        private string dumpsPath = null;
        private int maxDumps = 5;
        private Dictionary<string, int> serviceDumpCountDictionary = new Dictionary<string, int>();

        // SF Infra...
        private const string SFWindowsRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Service Fabric";
        private const string SFInfrastructureLogRootRegistryName = "FabricLogRoot";
        private string SFLogRoot = null;
        private const int TTLAddMinutes = 3;
        protected bool IsTelemetryEnabled { get; set; } = ObserverManager.TelemetryEnabled;
        protected bool IsEtwEnabled { get; set; } = ObserverManager.EtwEnabled;
        protected IObserverTelemetryProvider ObserverTelemetryClient { get; set; } = null;
        protected FabricClient FabricClientInstance { get; set; } = null;

        public string ObserverName { get; set; }
        public string NodeName { get; set; }
        public ObserverHealthReporter HealthReporter { get; }
        public StatelessServiceContext FabricServiceContext { get; }
        public DateTime LastRunDateTime { get; set; }
        public CancellationToken Token { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsUnhealthy { get; set; } = false;

        // Only set for unit test runs...
        public bool IsTestRun { get; set; } = false;
        
        // Loggers...
        public Logger ObserverLogger { get; set; }
        public DataTableFileLogger CsvFileLogger { get; set; }

        // Each derived Observer can set this to maintain health status across iterations.
        // This information is used by ObserverManager. 
        public bool HasActiveFabricErrorOrWarning { get; set; } = false;
        public TimeSpan RunInterval { get; set; } = TimeSpan.FromMinutes(10);
        public List<string> Settings { get; } = null;
        public abstract Task ObserveAsync(CancellationToken token);
        public abstract Task ReportAsync(CancellationToken token);

        protected ObserverBase(string observerName)
        {
            FabricClientInstance = ObserverManager.FabricClientInstance;

            if (IsTelemetryEnabled)
            {
                ObserverTelemetryClient = ObserverManager.TelemetryClient;
            }

            Settings = new List<string>();
            ObserverName = observerName;
            FabricServiceContext = ObserverManager.FabricServiceContext;
            NodeName = FabricServiceContext.NodeContext.NodeName;

            if (string.IsNullOrEmpty(this.dumpsPath))
            {
                SetDefaultSFDumpPath();
            }

            // Observer Logger setup...
            ObserverLogger = new Logger(observerName);
            string observerLogPath = GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName,
                                                              ObserverConstants.ObserverLogPath);
            if (!string.IsNullOrEmpty(observerLogPath))
            {
                ObserverLogger.LogFolderBasePath = observerLogPath;
            }
            else // like for test...
            {
                string logFolderBase = $@"{Environment.CurrentDirectory}\observer_logs";
                ObserverLogger.LogFolderBasePath = logFolderBase;
            }

            // Observer enabled?
            if (bool.TryParse(GetSettingParameterValue(observerName + "Configuration",
                                                       ObserverConstants.ObserverEnabled),
                                                       out bool enabled))
            {
                IsEnabled = enabled;
            }

            if (bool.TryParse(GetSettingParameterValue(observerName + "Configuration",
                                                       ObserverConstants.EnableVerboseLoggingParameter),
                                                       out bool enableVerboseLogging))
            {
                ObserverLogger.EnableVerboseLogging = enableVerboseLogging;
            }

            // DataLogger setup...
            CsvFileLogger = new DataTableFileLogger();
            string dataLogPath = GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName,
                                                          ObserverConstants.DataLogPath);
            if (!string.IsNullOrEmpty(observerLogPath))
            {
                CsvFileLogger.DataLogFolderPath = dataLogPath;
            }

            if (bool.TryParse(GetSettingParameterValue(ObserverConstants.ObserverManagerConfigurationSectionName,
                                                       ObserverConstants.EnableLongRunningCSVLogging),
                                                       out bool enableDataLogging))
            {
                CsvFileLogger.EnableCsvLogging = enableDataLogging;
            }

            HealthReporter = new ObserverHealthReporter(ObserverLogger);
        }

        public void WriteToLogWithLevel(string property, string description, LogLevel level)
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
            }

            Logger.Flush();
        }

        /// <summary>
        /// Gets a parameter value from the specified section
        /// </summary>
        /// <param name="sectionName">Name of the section</param>
        /// <param name="parameterName">Name of the parameter</param>
        /// <param name="defaultValue">Default value.</param>
        /// <returns>parameter value</returns>
        public string GetSettingParameterValue(string sectionName, string parameterName, string defaultValue = null)
        {
            if (string.IsNullOrEmpty(sectionName) || string.IsNullOrEmpty(parameterName))
            {
                return null;
            }

            try
            {
                var serviceConfiguration = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");
                string setting = serviceConfiguration.Settings.Sections[sectionName].Parameters[parameterName].Value;

                if (string.IsNullOrEmpty(setting) && defaultValue != null)
                {
                    return defaultValue;
                }

                return setting;
            }
            catch (KeyNotFoundException) // This will be the case for TestObservers for now...
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a dictionary of Parameters of the specified section
        /// </summary>
        /// <param name="sectionName">Name of the section</param>
        /// <returns>dictionary of Parameters</returns>
        public IDictionary<string, string> GetConfigSettingSectionParameters(string sectionName)
        {
            Contract.Assert(!string.IsNullOrEmpty(sectionName));
            IDictionary<string, string> container = new Dictionary<string, string>();

            var serviceConfiguration = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

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
        ///<param name="configSectionName">Observer configuration section name...</param>
        ///<param name="configParamName">Observer configuration parameter name...</param>
        ///<param name="defaultTo">Specific an optional TimeSpan to default to if setting is not found in config...
        ///else, it defaults to 24 hours</param>
        ///<returns>run interval</returns>
        public TimeSpan GetObserverRunInterval(string configSectionName,
                                               string configParamName,
                                               TimeSpan? defaultTo = null)
        {
            TimeSpan interval;

            try
            {
                interval = TimeSpan.Parse(GetSettingParameterValue(
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
                    HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                                     ObserverName,
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
                this.SFLogRoot = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureLogRootRegistryName, null);

                if (!string.IsNullOrEmpty(this.SFLogRoot))
                {
                    this.dumpsPath = Path.Combine(this.SFLogRoot, "CrashDumps");
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
                    HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.ToString(),
                                                                     ObserverName,
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

            string processName = "";

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
                        HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                                         ObserverName,
                                                                         HealthState.Warning,
                                                                         "Not enough disk space available for dump file creation...");
                        return false;
                    }
                }

                using (var file = File.Create(Path.Combine(this.dumpsPath, processName)))
                {
                    if (!NativeMethods.MiniDumpWriteDump(processHandle, 
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
                ObserverLogger.LogInfo("Unable to generate dump file {0} with error {1}", processName, ae.ToString());
            }
            catch (InvalidOperationException ie)
            {
                ObserverLogger.LogInfo("Unable to generate dump file {0} with error {1}", processName, ie.ToString());
            }
            catch (Win32Exception we)
            {
                ObserverLogger.LogInfo("Unable to generate dump file {0} with error {1}", processName, we.ToString());
            }

            return false;
        }

        public void ProcessResourceDataReportHealth<T>(FabricResourceUsageData<T> data,
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
                name = app.Replace("fabric:/", "");
                id = name + "_" + data.Property.Replace(" ", "");
            }

            // Telemetry...
            if (IsTelemetryEnabled)
            {
                _ = ObserverTelemetryClient?.ReportMetricAsync($"{NodeName}-{app}-{data.Id}-{data.Property}", data.AverageDataValue, Token);
            }

            // ETW...
            if (IsEtwEnabled)
            {
                Logger.EtwLogger?.Write($"FabricObserverDataEvent",
                                        new
                                        {
                                            Level = 0, // Info
                                            Node = NodeName,
                                            Observer = $" {ObserverName}",
                                            Property = data.Property,
                                            Id = data.Id,
                                            Value = $"{Math.Round(data.AverageDataValue)}",
                                            Unit = data.Units
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
                        
                        if (procName != null &&
                            !serviceDumpCountDictionary.ContainsKey(procName))
                        {
                            this.serviceDumpCountDictionary.Add(procName, 0);
                        }

                        if (procName != null &&
                            this.serviceDumpCountDictionary[procName] < maxDumps)
                        {
                            // DumpServiceProcess defaults to a Full dump with 
                            // process memory, handles and thread data... 
                            bool success = DumpServiceProcess(procId);

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
                        ObserverLogger.LogInfo($"Unable to generate dmp file:\n{ae.ToString()}");
                    }
                    catch (InvalidOperationException ioe)
                    {
                        ObserverLogger.LogInfo($"Unable to generate dmp file:\n{ioe.ToString()}");
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

                if (data.Property.Contains("CPU"))
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorCpuTime : ErrorWarningCode.WarningCpuTime;
                }
                else if (data.Property.Contains("Disk Space"))
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorDiskSpace : ErrorWarningCode.WarningDiskSpace;
                }
                else if (data.Property.Contains("Memory"))
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorMemoryCommitted : ErrorWarningCode.WarningMemoryCommitted;
                }
                else if (data.Property.Contains("Read"))
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorDiskIoReads : ErrorWarningCode.WarningDiskIoReads;
                }
                else if (data.Property.Contains("Write"))
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorDiskIoWrites : ErrorWarningCode.WarningDiskIoWrites;
                }
                else if (data.Property.Contains("Queue"))
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorDiskAverageQueueLength : ErrorWarningCode.WarningDiskAverageQueueLength;
                }
                else if (data.Property.Contains("Firewall"))
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorTooManyFirewallRules : ErrorWarningCode.WarningTooManyFirewallRules;
                }
                else if (data.Property.Contains("Ports"))
                {
                    errorWarningKind = (healthState == HealthState.Error) ? ErrorWarningCode.ErrorTooManyActivePorts : ErrorWarningCode.WarningTooManyActivePorts;
                }

                var healthMessage = new StringBuilder();

                if (name != null)
                {
                    healthMessage.Append($"{name} (Service Process: {procName}, {repPartitionId}, {repOrInstanceId}): ");
                }

                healthMessage.Append($"{data.Property} is at or above the specified {thresholdName} limit ({threshold}{data.Units})");
                healthMessage.AppendLine($" - Average {data.Property}: {Math.Round(data.AverageDataValue)}{data.Units}");

                // Set internal fabric health states...
                data.ActiveErrorOrWarning = true;

                // This means this observer created a Warning or Error SF Health Report 
                HasActiveFabricErrorOrWarning = true;

                var healthReport = new Utilities.HealthReport
                {
                    AppName = appName,
                    Code = errorWarningKind,
                    EmitLogEvent = true,
                    HealthMessage = healthMessage.ToString(),
                    HealthReportTimeToLive = healthReportTtl,
                    ReportType = healthReportType,
                    State = healthState,
                    NodeName = NodeName,
                    Observer = ObserverName
                };

                // Emit a Fabric Health Report and optionally a local log write...
                HealthReporter.ReportHealthToServiceFabric(healthReport);

                // Send Health Report as Telemetry event (perhaps it signals an Alert from App Insights, for example...)...
                if (IsTelemetryEnabled)
                {
                    _ = ObserverTelemetryClient?.ReportHealthAsync(id,
                                                                   FabricServiceContext.ServiceName.OriginalString,
                                                                   "FabricObserver",
                                                                   ObserverName,
                                                                   $"{NodeName}/{errorWarningKind}/{data.Property}/{Math.Round(data.AverageDataValue)}",
                                                                   healthState,
                                                                   Token);
                }

                // ETW...
                if (IsEtwEnabled)
                {
                    Logger.EtwLogger?.Write($"FabricObserverDataEventHealth{Enum.GetName(typeof(HealthState), healthState)}",
                                            new
                                            {
                                                Level = (healthState == HealthState.Warning) ? 1 : 2,
                                                Node = NodeName,
                                                Observer = ObserverName,
                                                HealthEventErrorCode = errorWarningKind,
                                                Property = data.Property,
                                                Id = data.Id,
                                                Value = $"{Math.Round(data.AverageDataValue)}",
                                                Unit = data.Units
                                            });
                }

                // Clean up sb...
                healthMessage.Clear();
            }
            else
            {
                if (data.ActiveErrorOrWarning)
                {
                    Utilities.HealthReport report = new Utilities.HealthReport
                    {
                        AppName = appName,
                        EmitLogEvent = true,
                        HealthMessage = $"{data.Id}: {data.Property} is now within normal/expected range.",
                        HealthReportTimeToLive = default(TimeSpan),
                        ReportType = healthReportType,
                        State = HealthState.Ok,
                        NodeName = NodeName,
                        Observer = ObserverName
                    };

                    // Emit an Ok Health Report to clear Fabric Health warning...
                    HealthReporter.ReportHealthToServiceFabric(report);

                    // Reset health states...
                    data.ActiveErrorOrWarning = false;
                    HasActiveFabricErrorOrWarning = false;
                }
            }

            // No need to keep data in memory...
            data.Data.Clear();
            data.Data.TrimExcess();
        }

        public TimeSpan SetTimeToLiveWarning(int runDuration = 0)
        {
            // Set TTL...
            if (LastRunDateTime == DateTime.MinValue) // First run...
            {
                return TimeSpan.FromSeconds(ObserverManager.ObserverExecutionLoopSleepSeconds)
                                 .Add(TimeSpan.FromMinutes(TTLAddMinutes));
            }
            else
            {
                return DateTime.Now.Subtract(LastRunDateTime)
                       .Add(TimeSpan.FromSeconds(runDuration))
                       .Add(TimeSpan.FromSeconds(ObserverManager.ObserverExecutionLoopSleepSeconds));
            }
        }

        #region IDisposable Support

        // This is here so each Observer doesn't have to implement IDisposable. 
        // If an Observer needs to dispose, then override this non-impl...
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    if (FabricClientInstance != null)
                    {
                        FabricClientInstance.Dispose();
                        FabricClientInstance = null;
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~ObserverBase()
        // {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public virtual void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }

    internal enum DumpType
    {
        Mini,
        MiniPlus,
        Full
    }
}