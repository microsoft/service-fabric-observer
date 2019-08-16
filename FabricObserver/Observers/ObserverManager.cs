// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Interfaces;
using FabricObserver.Utilities;
using FabricObserver.Utilities.Telemetry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver
{
    // This class manages the lifetime of all observers from instantiation to "destruction",
    // and sequentially runs all observer instances in a never-ending while loop, 
    // with optional sleeps, and reliable shutdown event handling.
    public class ObserverManager : IDisposable
    {
        private ObserverHealthReporter HealthReporter { get; set; }
        private EventWaitHandle globalShutdownEventHandle = null;
        private volatile bool shutdownSignalled = false;
        private int shutdownGracePeriodInSeconds = 2;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(5);
        private CancellationToken token;
        private CancellationTokenSource cts;
        private bool hasDisposed = false;
        private string Fqdn { get; set; }
        private Logger Logger { get; set; }
        private DataTableFileLogger DataLogger { get; set; }
        internal List<ObserverBase> Observers;
        public string ApplicationName { get; set; }
        public bool IsObserverRunning { get; set; } = false;
        public static int ObserverExecutionLoopSleepSeconds { get; private set; } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;
        public static FabricClient FabricClientInstance { get; set; }
        public static StatelessServiceContext FabricServiceContext { get; set; }
        public static IObserverTelemetryProvider TelemetryClient { get; set; }
        public static bool TelemetryEnabled { get; set; } = false;

        public static bool EtwEnabled { get; set; } = false;

        public static string EtwProviderName
        {
            get
            {
                try
                {
                    if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableEventSourceProvider), out bool etwEnabled))
                    {
                        EtwEnabled = etwEnabled;
                    }

                    if (EtwEnabled)
                    {
                        string key = GetConfigSettingValue(ObserverConstants.EventSourceProviderName);
                        if (!string.IsNullOrEmpty(key))
                        {
                            return key;
                        }

                        return null;
                    }
                }
                catch { }

                return null;
            }
        }


        public ObserverManager(StatelessServiceContext context,
                               CancellationToken token)
        {
            this.token = token;
            this.cts = new CancellationTokenSource();
            this.token.Register(() => { ShutdownHandler(this, null); });
            FabricClientInstance = new FabricClient();
            FabricServiceContext = context;
            DataLogger = new DataTableFileLogger();
            Logger = new Logger("ObserverManager");
            HealthReporter = new ObserverHealthReporter(Logger);
            SetPropertiesFromConfigurationParameters();
            
            // Populate the Observer list for the sequential run loop...
            this.Observers = GetObservers();
        }

        // For unit testing...
        public ObserverManager(ObserverBase observer)
        {
            this.cts = new CancellationTokenSource();
            this.token = this.cts.Token;
            this.token.Register(() => { ShutdownHandler(this, null); });
            Logger = new Logger("ObserverManagerSingleObserverRun");
            HealthReporter = new ObserverHealthReporter(Logger);
            this.Observers = new List<ObserverBase>(new ObserverBase[]
            {
                observer
            });
        }

        private static string GetConfigSettingValue(string parameterName)
        {
            try
            {
                var configSettings = FabricRuntime.GetActivationContext().GetConfigurationPackageObject("Config").Settings;

                if (configSettings == null)
                {
                    return null;
                }

                ConfigurationSection section = configSettings.Sections[ObserverConstants.ObserverManagerConfigurationSectionName];

                if (section == null)
                {
                    return null;
                }

                ConfigurationProperty parameter = section.Parameters[parameterName];

                if (parameter == null)
                {
                    return null;
                }

                return parameter.Value;
            }
            catch (FabricElementNotFoundException) { }

            return null;
        }

        private void ShutdownHandler(object sender, ConsoleCancelEventArgs consoleEvent)
        {
            if (!this.hasDisposed)
            {
                Thread.Sleep(this.shutdownGracePeriodInSeconds * 1000);

                shutdownSignalled = true;
                this.globalShutdownEventHandle?.Set();
                StopObservers();
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

            while (!shutdownSignalled &&
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
        // List order matters... These are cycled through sequentially.
        private static List<ObserverBase> GetObservers()
        {
            var observers = new List<ObserverBase>(new ObserverBase[]
            {
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
                new FabricSystemObserver(),

                // User-configurable, App-level (app service processes) machine resource observer that records and reports on service-level resource usage...
                // Long-running data is stored in app-specific CSVs (optional) or sent to diagnostic/telemetry service, for use in upstream analysis, etc...
                new AppObserver(),

                // NetworkObserver for Internet connection state of user-supplied host/port pairs, active port and firewall rule count monitoring...
                new NetworkObserver()
            });

            return observers;
        }

        private void SetPropertiesFromConfigurationParameters()
        {
            ApplicationName = FabricRuntime.GetActivationContext().ApplicationName;

            // Observers
            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout),
                                                   out int result))
            {
                this.observerExecTimeout = TimeSpan.FromSeconds(result);
            }

            // Loggers
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter),
                                                    out bool enableVerboseLogging))
            {
                Logger.EnableVerboseLogging = enableVerboseLogging;
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverLoopSleepTimeSeconds),
                                                   out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;

                Logger.LogInfo($"ExecutionFrequency is {ObserverExecutionLoopSleepSeconds} Seconds");
            }

            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableLongRunningCSVLogging),
                                                    out bool enableCSVLogging))
            {
                DataLogger.EnableCsvLogging = enableCSVLogging;
            }

            string dataLogPath = GetConfigSettingValue(ObserverConstants.DataLogPath);
            if (!string.IsNullOrEmpty(dataLogPath))
            {
                DataLogger.DataLogFolderPath = dataLogPath;
            }

            string observerLogPath = GetConfigSettingValue(ObserverConstants.ObserverLogPath);
            if (!string.IsNullOrEmpty(observerLogPath))
            {
                Logger.LogFolderBasePath = observerLogPath;
            }

            // Shutdown
            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverShutdownGracePeriodInSeconds),
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

            // (ApplicationInsights) Telemetry...
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
                Console.CancelKeyPress += ShutdownHandler;

                // Continue running until a shutdown signal is sent
                Logger.LogInfo("Starting Observers loop...");

                // Observers run sequentially. See RunObservers impl...
                while (true)
                {
                    if (this.shutdownSignalled || this.token.IsCancellationRequested)
                    {
                        this.globalShutdownEventHandle.Set();
                        Logger.LogInfo("Shutdown signalled. Stopping...");
                        break;
                    }

                    if (RunObservers())
                    { 
                        if (ObserverExecutionLoopSleepSeconds > 0)
                        {
                            Logger.LogInfo($"Sleeping for {ObserverExecutionLoopSleepSeconds} seconds before running again...");
                            ThreadSleep(this.globalShutdownEventHandle, TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds));
                        }
                        Logger.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Unhanded Exception. Taking down FO process.\n Details: {ex.ToString()}");

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

                SignalAbortToRunningObserver();
                IsObserverRunning = false;
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Unhandled Exception thrown during ObserverManager.Stop(): {e.ToString()}");

                throw;
            }
        }

        private void SignalAbortToRunningObserver()
        {
            Logger.LogInfo("Signalling task cancellation to currently running Observer...");
            this.cts.Cancel();
            Logger.LogInfo("Successfully signalled cancellation to currently running Observer...");
        }

        private bool RunObservers()
        {
            // Continue to run the next Observer if current one fails while initializing or running 
            var exceptionBuilder = new StringBuilder();
            var exceptionStackTraceBuilder = new StringBuilder();
            bool allExecuted = true;

            foreach (var observer in this.Observers)
            {
                try
                {
                    // Shutdown/cancellation signaled, so stop...
                    if (this.token.IsCancellationRequested || this.shutdownSignalled)
                    {
                        return false;
                    }

                    // Is the observer enabled? Is it healthy?
                    if (!observer.IsEnabled || observer.IsUnhealthy)
                    {
                        continue;
                    }

                    Logger.LogInfo($"Starting {observer.ObserverName}");

                    IsObserverRunning = true;

                    // Synchronous call...
                    var isCompleted = observer.ObserveAsync(this.cts.Token).Wait(this.observerExecTimeout);
                    IsObserverRunning = false;

                    // The observer is taking too long (hung?), move on to next observer...
                    // Currently, this observer will not run again for the lifetime of this FO service instance. 
                    // So, application will need a restart... 
                    // TODO: Be less restrictive, but do fix the broken observer if/when this happens...
                    if (!isCompleted)
                    {
                        Logger.LogError(observer.ObserverName + $" has exceeded its alloted run time of {this.observerExecTimeout.TotalSeconds} seconds. " +
                                                                $"This means something is wrong with {observer.ObserverName}. It will not be run again. Look into it...");
                        observer.IsUnhealthy = true;

                        continue;
                    }

                    string errWarnMsg = "No errors or warnings detected.";

                    if (observer.HasActiveFabricErrorOrWarning)
                    {
                        if (!string.IsNullOrEmpty(Fqdn))
                        {
                            errWarnMsg = $"<a style=\"font-weight: bold; color: red;\" href=\"http://{Fqdn}/api/ObserverLog/{observer.ObserverName}/{observer.NodeName}/json\">One or more errors or warnings detected</a>.";
                        }
                        else
                        {
                            errWarnMsg = $"One or more errors or warnings detected. Check {observer.ObserverName} logs for details.";
                        }

                        Logger.LogWarning($"{observer.ObserverName}: " + errWarnMsg);
                    }
                    else
                    {
                        // Delete the observer's instance log (local file with Warn/Error details per run)..
                        _ = observer.ObserverLogger.TryDeleteInstanceLog();
                        try
                        {
                            if (File.Exists(Logger.FilePath))
                            {
                                // Replace the ObserverManager.log text that doesn't contain the observer Warn/Error line(s)...
                                File.WriteAllLines(Logger.FilePath,
                                                   File.ReadLines(Logger.FilePath)
                                                   .Where(line => !line.Contains(observer.ObserverName)).ToList());
                            }
                        }
                        catch { }
                        Logger.LogInfo($"Successfully ran {observer.ObserverName}. " + errWarnMsg);
                    }
                }
                catch (AggregateException ex)
                {
                    IsObserverRunning = false;

                    if (!(ex.InnerException is OperationCanceledException) && !(ex.InnerException is TaskCanceledException))
                    {
                        Logger.LogError($"Exception while running {observer.ObserverName}");
                        Logger.LogError($"Exception: {ex.InnerException.Message}");
                        Logger.LogError($"StackTrace: {ex.InnerException.StackTrace}");

                        exceptionBuilder.AppendLine($"{observer.ObserverName} - Exception: {ex.InnerException.Message}");
                        exceptionStackTraceBuilder.AppendLine($"{observer.ObserverName} - StackTrace: {ex.InnerException.StackTrace}");

                        allExecuted = false;
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError($"Unhandled Exception from {observer.ObserverName} rethrown from ObserverManager: {e.ToString()}");

                    throw;
                }
            }

            if (allExecuted)
            {
                Logger.LogInfo(ObserverConstants.AllObserversExecutedMessage);
                HealthReporter.ReportFabricObserverServiceHealth(ObserverConstants.ObserverManangerName,
                                                                 ApplicationName,
                                                                 HealthState.Ok,
                                                                 ObserverConstants.AllObserversExecutedMessage);
            }
            else
            {
                Logger.LogError(exceptionBuilder.ToString());
                Logger.LogError(exceptionStackTraceBuilder.ToString());
                HealthReporter.ReportFabricObserverServiceHealth(ObserverConstants.ObserverManangerName,
                                                                 ApplicationName,
                                                                 HealthState.Error,
                                                                 exceptionBuilder.ToString());
            }

            return allExecuted;
        }

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (!this.hasDisposed)
            {
                if (disposing)
                {
                    if (IsObserverRunning)
                    {
                        StopObservers();
                    }

                    if (this.globalShutdownEventHandle != null)
                    {
                        this.globalShutdownEventHandle.Dispose();
                    }

                    if (this.Observers?.Count > 0)
                    {
                        foreach (var obs in this.Observers)
                        {
                            obs?.Dispose();
                        }

                        this.Observers.Clear();
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
                    this.Logger?.Dispose();
                    DataTableFileLogger.ShutDown();

                    this.hasDisposed = true;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
