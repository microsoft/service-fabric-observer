﻿// ------------------------------------------------------------
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
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Interfaces;
using FabricObserver.Utilities;
using FabricObserver.Utilities.Telemetry;

namespace FabricObserver
{
    // This class manages the lifetime of all observers from instantiation to "destruction",
    // and sequentially runs all observer instances in a never-ending while loop,
    // with optional sleeps, and reliable shutdown event handling.
    public class ObserverManager : IDisposable
    {
        public string ApplicationName { get; set; }

        public bool IsObserverRunning { get; set; } = false;

        public static int ObserverExecutionLoopSleepSeconds { get; private set; } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

        public static FabricClient FabricClientInstance { get; set; }

        public static StatelessServiceContext FabricServiceContext { get; set; }

        public static IObserverTelemetryProvider TelemetryClient { get; set; }

        public static bool TelemetryEnabled { get; set; } = false;

        public static bool EtwEnabled
        {
            get
            {
                return bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableEventSourceProvider), out etwEnabled) ? etwEnabled : false;
            }

            set
            {
                etwEnabled = value;
            }
        }

        public static string EtwProviderName
        {
            get
            {
                if (EtwEnabled)
                {
                    string key = GetConfigSettingValue(ObserverConstants.EventSourceProviderName);

                    if (!string.IsNullOrEmpty(key))
                    {
                        return key;
                    }

                    return null;
                }

                return null;
            }
        }

        private readonly string nodeName = null;
        private EventWaitHandle globalShutdownEventHandle = null;
        private volatile bool shutdownSignalled = false;
        private int shutdownGracePeriodInSeconds = 2;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(5);
        private CancellationToken token;
        private CancellationTokenSource cts;
        private bool hasDisposed = false;
        private static bool etwEnabled = false;
        private readonly List<ObserverBase> observers;

        private ObserverHealthReporter HealthReporter { get; set; }

        private string Fqdn { get; set; }

        private Logger Logger { get; set; }

        private DataTableFileLogger DataLogger { get; set; }


        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// </summary>
        public ObserverManager(
            StatelessServiceContext context,
            CancellationToken token)
        {
            this.token = token;
            this.cts = new CancellationTokenSource();
            this.token.Register(() => { this.ShutdownHandler(this, null); });
            FabricClientInstance = new FabricClient();
            FabricServiceContext = context;
            this.nodeName = FabricServiceContext.NodeContext.NodeName;

            // Observer Logger setup...
            string logFolderBasePath = null;
            string observerLogPath = GetConfigSettingValue(
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

            // this logs metrics from observers, if enabled, and/or sends
            // telemetry data to your implemented provider...
            this.DataLogger = new DataTableFileLogger();

            // this logs error/warning/info messages for ObserverManager...
            this.Logger = new Logger("ObserverManager", logFolderBasePath);
            this.HealthReporter = new ObserverHealthReporter(this.Logger);
            this.SetPropertiesFromConfigurationParameters();

            // Populate the Observer list for the sequential run loop...
            this.observers = GetObservers();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// </summary>
        public ObserverManager(ObserverBase observer)
        {
            this.cts = new CancellationTokenSource();
            this.token = this.cts.Token;
            this.token.Register(() => { this.ShutdownHandler(this, null); });
            this.Logger = new Logger("ObserverManagerSingleObserverRun");
            this.HealthReporter = new ObserverHealthReporter(this.Logger);
            this.observers = new List<ObserverBase>(new ObserverBase[]
            {
                observer,
            });
        }

        public static Tuple<long, int> TupleGetTotalPhysicalMemorySizeAndPercentInUse()
        {
            ManagementObjectSearcher win32OSInfo = null;
            ManagementObjectCollection results = null;

            try
            {
                win32OSInfo = new ManagementObjectSearcher("SELECT FreePhysicalMemory,TotalVisibleMemorySize FROM Win32_OperatingSystem");
                results = win32OSInfo.Get();

                foreach (var prop in results)
                {
                    long visibleTotal = -1;
                    long freePhysical = -1;

                    foreach (var p in prop.Properties)
                    {
                        string n = p.Name;
                        var v = p.Value;

                        if (n.ToLower().Contains("totalvisible"))
                        {
                            visibleTotal = Convert.ToInt64(v);
                        }

                        if (n.ToLower().Contains("freephysical"))
                        {
                            freePhysical = Convert.ToInt64(v);
                        }
                    }

                    if (visibleTotal > -1 && freePhysical > -1)
                    {
                        double used = ((double)(visibleTotal - freePhysical)) / visibleTotal;
                        int usedPct = (int)(used * 100);

                        return Tuple.Create(visibleTotal / 1024 / 1024, usedPct);
                    }
                }
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (ManagementException)
            {
            }
            finally
            {
                win32OSInfo?.Dispose();
                results?.Dispose();
            }

            return Tuple.Create(-1L, -1);
        }

        private static string GetConfigSettingValue(string parameterName)
        {
            try
            {
                var configSettings = FabricServiceContext.CodePackageActivationContext?.GetConfigurationPackageObject("Config")?.Settings;

                if (configSettings == null)
                {
                    return null;
                }

                var section = configSettings.Sections[ObserverConstants.ObserverManagerConfigurationSectionName];

                if (section == null)
                {
                    return null;
                }

                var parameter = section.Parameters[parameterName];

                if (parameter == null)
                {
                    return null;
                }

                return parameter.Value;
            }
            catch (FabricElementNotFoundException)
            {
            }
            catch (NullReferenceException)
            {
            }

            return null;
        }

        private void ShutdownHandler(object sender, ConsoleCancelEventArgs consoleEvent)
        {
            if (!this.hasDisposed)
            {
                Thread.Sleep(this.shutdownGracePeriodInSeconds * 1000);

                this.shutdownSignalled = true;
                this.globalShutdownEventHandle?.Set();
                this.StopObservers();
            }
        }

        // This impl is to ensure FO exits if shutdown is requested while the over loop is sleeping
        // So, instead of blocking with a Thread.Sleep, for example, ThreadSleep is used to ensure
        // we can receive signals and act accordingly during thread sleep state...
        private void ThreadSleep(EventWaitHandle ewh, TimeSpan timeout)
        {
            // if timeout is <= 0, return. 0 is infinite, and negative is not valid
            if (timeout.TotalMilliseconds <= 0)
            {
                return;
            }

            var elapsedTime = new TimeSpan(0, 0, 0);
            var stopwatch = new Stopwatch();

            while (!this.shutdownSignalled &&
                   !this.token.IsCancellationRequested &&
                   timeout > elapsedTime)
            {
                stopwatch.Start();

                // the event can be signalled by CtrlC,
                // Exit ASAP when the program terminates (i.e., shutdown/abort is signalled...)
                ewh.WaitOne(timeout.Subtract(elapsedTime));
                stopwatch.Stop();

                elapsedTime = stopwatch.Elapsed;
            }

            if (stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }
        }

        // Observers are instance types. Create them, store in persistent list...
        // List order matters... These are cycled through sequentially, one observer at a time.
        private static List<ObserverBase> GetObservers()
        {
            // You can simply not create an instance of an observer you don't want to run. The list
            // below is just for reference. GetObservers only returns enabled observers, anyway...
            var observers = new List<ObserverBase>(new ObserverBase[]
            {
                // CertificateObserver monitors Certificate health and will emit Warnings for expiring
                // Cluster and App certificates that are housed in the LocalMachine/My Certificate Store
                new CertificateObserver(),

                // Observes, records and reports on general OS properties and state...
                // Run this first to get basic information about the VM state,
                // Firewall rules in place, active ports, basic resource infomation...
                new OSObserver(),

                // User-configurable observer that records and reports on local disk (node level, host level...) properties...
                // Long-running data is stored in app-specific CSVs (optional) or sent to diagnostic/telemetry service,
                // for use in upstream analysis, etc...
                new DiskObserver(),

                // User-configurable, VM-level machine resource observer that records and reports on node-level resource usage conditions,
                // recording data in CSVs. Long-running data is stored in app-specific CSVs (optional) or sent to diagnostic/telemetry service,
                // for use in upstream analysis, etc...
                new NodeObserver(),

                // This observer only collects basic SF information for use in reporting (from Windows Registry...) in user-configurable intervals...
                new SFConfigurationObserver(),

                // Observes, records and reports on CPU/Mem usage, established port count, Disk IO (r/w) for Service Fabric System services
                // (Fabric, FabricApplicationGateway, FabricDNS, FabricRM, etc...). Long-running data is stored in app-specific CSVs (optional)
                // or sent to diagnostic/telemetry service, for use in upstream analysis, etc...
                // ***NOTE***: It is not a good idea to run this observer with Warning thresholds unless you understand how your
                // service code impacts the resource usage behavior of the underlying fabric system services.
                // This is here for example only.
                new FabricSystemObserver(),

                // User-configurable, App-level (app service processes) machine resource observer that records and reports on service-level resource usage...
                // Long-running data is stored in app-specific CSVs (optional) or sent to diagnostic/telemetry service, for use in upstream analysis, etc...
                new AppObserver(),

                // NetworkObserver for Internet connection state of user-supplied host/port pairs, active port and firewall rule count monitoring...
                new NetworkObserver(),
            });

            // Only return a list with user-enabled observer instances...
            return observers.Where(obs => obs.IsEnabled)?.ToList();
        }

        private void SetPropertiesFromConfigurationParameters()
        {
            this.ApplicationName = FabricRuntime.GetActivationContext().ApplicationName;

            // Observers
            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout),
                out int result))
            {
                this.observerExecTimeout = TimeSpan.FromSeconds(result);
            }

            // Loggers
            if (bool.TryParse(
                GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                this.Logger.EnableVerboseLogging = enableVerboseLogging;
            }

            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverLoopSleepTimeSeconds),
                out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;

                this.Logger.LogInfo($"ExecutionFrequency is {ObserverExecutionLoopSleepSeconds} Seconds");
            }

            if (bool.TryParse(
                GetConfigSettingValue(ObserverConstants.EnableLongRunningCSVLogging),
                out bool enableCSVLogging))
            {
                this.DataLogger.EnableCsvLogging = enableCSVLogging;
            }

            string dataLogPath = GetConfigSettingValue(ObserverConstants.DataLogPath);
            if (!string.IsNullOrEmpty(dataLogPath))
            {
                this.DataLogger.DataLogFolderPath = dataLogPath;
            }

            // Shutdown
            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverShutdownGracePeriodInSeconds),
                out int shutdownGracePeriodInSeconds))
            {
                this.shutdownGracePeriodInSeconds = shutdownGracePeriodInSeconds;
            }

            // FQDN for use in warning or error hyperlinks in HTML output
            // This only makes sense when you have the FabricObserverWebApi app installed...
            string fqdn = GetConfigSettingValue(ObserverConstants.FQDN);
            if (!string.IsNullOrEmpty(fqdn))
            {
                this.Fqdn = fqdn;
            }

            // (Assuming Diagnostics/Analytics cloud service implemented) Telemetry...
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.TelemetryEnabled), out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;
            }

            if (TelemetryEnabled)
            {
                string key = GetConfigSettingValue(ObserverConstants.AIKey);

                if (!string.IsNullOrEmpty(key))
                {
                    TelemetryClient = new AppInsightsTelemetry(key);
                }
            }
        }

        public void StartObservers()
        {
            try
            {
                // Create Global Shutdown event handler
                this.globalShutdownEventHandle = new EventWaitHandle(false, EventResetMode.ManualReset);

                // Register for console cancel/shutdown events (Ctrl+C/Ctrl+Break/shutdown) and wait for an event
                Console.CancelKeyPress += this.ShutdownHandler;

                // Continue running until a shutdown signal is sent
                this.Logger.LogInfo("Starting Observers loop...");

                // Observers run sequentially. See RunObservers impl...
                while (true)
                {
                    if (this.shutdownSignalled || this.token.IsCancellationRequested)
                    {
                        this.globalShutdownEventHandle.Set();
                        this.Logger.LogInfo("Shutdown signalled. Stopping...");
                        break;
                    }

                    if (this.RunObservers())
                    {
                        if (ObserverExecutionLoopSleepSeconds > 0)
                        {
                            this.Logger.LogInfo($"Sleeping for {ObserverExecutionLoopSleepSeconds} seconds before running again...");
                            this.ThreadSleep(this.globalShutdownEventHandle, TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds));
                        }

                        Logger.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                var message = $"Unhanded Exception in ObserverManager on node {this.nodeName}. Taking down FO process. Error info: {ex.ToString()}";
                this.Logger.LogError(message);

                // Telemetry...
                if (TelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportMetricAsync($"ObserverManagerHealthError", message, token);
                }

                // Take down FO process. Fix the bugs this identifies. This code should never run if observers aren't buggy...
                // Don't swallow the exception...
                throw;
            }
        }

        public void StopObservers()
        {
            try
            {
                if (!this.shutdownSignalled)
                {
                    this.shutdownSignalled = true;
                }

                this.SignalAbortToRunningObserver();
                this.IsObserverRunning = false;
            }
            catch (Exception e)
            {
                this.Logger.LogWarning($"Unhandled Exception thrown during ObserverManager.Stop(): {e.ToString()}");

                throw;
            }
        }

        private void SignalAbortToRunningObserver()
        {
            this.Logger.LogInfo("Signalling task cancellation to currently running Observer...");
            this.cts.Cancel();
            this.Logger.LogInfo("Successfully signalled cancellation to currently running Observer...");
        }

        private bool RunObservers()
        {
            // Continue to run the next Observer if current one fails while initializing or running
            var exceptionBuilder = new StringBuilder();
            var exceptionStackTraceBuilder = new StringBuilder();
            bool allExecuted = true;

            foreach (var observer in this.observers)
            {
                try
                {
                    // Shutdown/cancellation signaled, so stop...
                    if (this.token.IsCancellationRequested || this.shutdownSignalled)
                    {
                        return false;
                    }

                    // Is it healthy?
                    if (observer.IsUnhealthy)
                    {
                        continue;
                    }

                    this.Logger.LogInfo($"Starting {observer.ObserverName}");

                    this.IsObserverRunning = true;

                    // Synchronous call...
                    var isCompleted = observer.ObserveAsync(this.cts.Token).Wait(this.observerExecTimeout);
                    this.IsObserverRunning = false;

                    // The observer is taking too long (hung?), move on to next observer...
                    // Currently, this observer will not run again for the lifetime of this FO service instance.
                    // So, application will need a restart...
                    // TODO: Be less restrictive, but do fix the broken observer if/when this happens, as it means there is probably
                    // a bug in your code...
                    if (!isCompleted)
                    {
                        string observerHealthWarning = observer.ObserverName + $" has exceeded its alloted run time of {this.observerExecTimeout.TotalSeconds} seconds. " +
                                                       $"This means something is wrong with {observer.ObserverName}. It will not be run again. Look into it...";

                        this.Logger.LogError(observerHealthWarning);
                        observer.IsUnhealthy = true;

                        // Telemetry...
                        if (TelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportMetricAsync($"ObserverHealthError", $"{observer.ObserverName} has exceeded its alloted run time of {this.observerExecTimeout.TotalSeconds} seconds.", token);
                        }

                        continue;
                    }

                    string errWarnMsg = "No errors or warnings detected.";

                    if (observer.HasActiveFabricErrorOrWarning)
                    {
                        if (!string.IsNullOrEmpty(this.Fqdn))
                        {
                            errWarnMsg = $"<a style=\"font-weight: bold; color: red;\" href=\"http://{this.Fqdn}/api/ObserverLog/{observer.ObserverName}/{observer.NodeName}/json\">One or more errors or warnings detected</a>.";
                        }
                        else
                        {
                            errWarnMsg = $"One or more errors or warnings detected. Check {observer.ObserverName} logs for details.";
                        }

                        this.Logger.LogWarning($"{observer.ObserverName}: " + errWarnMsg);
                    }
                    else
                    {
                        // Delete the observer's instance log (local file with Warn/Error details per run)..
                        _ = observer.ObserverLogger.TryDeleteInstanceLog();
                        try
                        {
                            if (File.Exists(this.Logger.FilePath))
                            {
                                // Replace the ObserverManager.log text that doesn't contain the observer Warn/Error line(s)...
                                File.WriteAllLines(
                                    this.Logger.FilePath,
                                    File.ReadLines(this.Logger.FilePath)
                                                   .Where(line => !line.Contains(observer.ObserverName)).ToList());
                            }
                        }
                        catch
                        {
                        }

                        this.Logger.LogInfo($"Successfully ran {observer.ObserverName}. " + errWarnMsg);
                    }
                }
                catch (AggregateException ex)
                {
                    this.IsObserverRunning = false;

                    if (!(ex.InnerException is OperationCanceledException) && !(ex.InnerException is TaskCanceledException))
                    {
                        this.Logger.LogError($"Exception while running {observer.ObserverName}");
                        this.Logger.LogError($"Exception: {ex.InnerException.Message}");
                        this.Logger.LogError($"StackTrace: {ex.InnerException.StackTrace}");

                        exceptionBuilder.AppendLine($"{observer.ObserverName} - Exception: {ex.InnerException.Message}");
                        exceptionStackTraceBuilder.AppendLine($"{observer.ObserverName} - StackTrace: {ex.InnerException.StackTrace}");

                        allExecuted = false;
                    }
                }
                catch (Exception e)
                {
                    var message = $"Unhandled Exception from {observer.ObserverName} on node {this.nodeName} rethrown from ObserverManager: {e.ToString()}";
                    this.Logger.LogError(message);

                    // Telemetry...
                    if (TelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportMetricAsync($"ObserverHealthError", message, token);
                    }

                    throw;
                }
            }

            if (allExecuted)
            {
                this.Logger.LogInfo(ObserverConstants.AllObserversExecutedMessage);
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    ObserverConstants.ObserverManangerName,
                    this.ApplicationName,
                    HealthState.Ok,
                    ObserverConstants.AllObserversExecutedMessage);
            }
            else
            {
                this.Logger.LogError(exceptionBuilder.ToString());
                this.Logger.LogError(exceptionStackTraceBuilder.ToString());
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    ObserverConstants.ObserverManangerName,
                    this.ApplicationName,
                    HealthState.Error,
                    exceptionBuilder.ToString());
            }

            return allExecuted;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.hasDisposed)
            {
                if (disposing)
                {
                    if (this.IsObserverRunning)
                    {
                        this.StopObservers();
                    }

                    if (this.globalShutdownEventHandle != null)
                    {
                        this.globalShutdownEventHandle.Dispose();
                    }

                    if (this.observers?.Count > 0)
                    {
                        foreach (var obs in this.observers)
                        {
                            obs?.Dispose();
                        }

                        this.observers.Clear();
                    }

                    if (FabricClientInstance != null)
                    {
                        FabricClientInstance.Dispose();
                        FabricClientInstance = null;
                    }

                    if (this.cts != null)
                    {
                        this.cts.Dispose();
                        this.cts = null;
                    }

                    // Flush and Dispose all NLog targets. No more logging...
                    Logger.Flush();
                    DataTableFileLogger.Flush();
                    Logger.ShutDown();
                    DataTableFileLogger.ShutDown();

                    this.hasDisposed = true;
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
