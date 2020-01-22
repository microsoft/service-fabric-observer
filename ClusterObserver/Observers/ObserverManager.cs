// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Interfaces;
using FabricClusterObserver.Utilities;
using FabricClusterObserver.Utilities.Telemetry;

namespace FabricClusterObserver
{
    // This class manages the lifetime of all observers from instantiation to "destruction",
    // and sequentially runs all observer instances in a never-ending while loop,
    // with optional sleeps, and reliable shutdown event handling.
    public class ObserverManager : IDisposable
    {
        private readonly string nodeName = null;
        private readonly List<ObserverBase> observers;
        private EventWaitHandle globalShutdownEventHandle = null;
        private volatile bool shutdownSignalled = false;
        private int shutdownGracePeriodInSeconds = 2;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(30);
        private CancellationToken token;
        private CancellationTokenSource cts;
        private bool hasDisposed = false;

        public string ApplicationName { get; set; }

        public bool IsObserverRunning { get; set; } = false;

        public static int ObserverExecutionLoopSleepSeconds { get; private set; } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

        public static FabricClient FabricClientInstance { get; set; }

        public static StatelessServiceContext FabricServiceContext { get; set; }

        public static IObserverTelemetryProvider TelemetryClient { get; set; }

        public static bool TelemetryEnabled { get; set; } = false;

        private Logger Logger { get; set; }

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
            this.nodeName = FabricServiceContext?.NodeContext.NodeName;

            // Observer Logger setup.
            string logFolderBasePath = null;
            string observerLogPath = GetConfigSettingValue(ObserverConstants.ObserverLogPath);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = $@"{Environment.CurrentDirectory}\observer_logs";
                logFolderBasePath = logFolderBase;
            }

            // This logs error/warning/info messages for ObserverManager.
            this.Logger = new Logger(ObserverConstants.ObserverManangerName, logFolderBasePath);
           
            this.SetPropertiesFromConfigurationParameters();

            // Populate the Observer list for the sequential run loop.
            this.observers = GetObservers();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// This is for unit testing purposes.
        /// </summary>
        public ObserverManager(ObserverBase observer)
        {
            this.cts = new CancellationTokenSource();
            this.token = this.cts.Token;
            this.token.Register(() => { this.ShutdownHandler(this, null); });
            this.Logger = new Logger("ObserverManagerSingleObserverRun");

            this.observers = new List<ObserverBase>(new ObserverBase[]
            {
                observer,
            });
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
            catch (KeyNotFoundException)
            {
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

        // This impl is to ensure FCO exits if shutdown is requested while the over loop is sleeping
        // So, instead of blocking with a Thread.Sleep, for example, ThreadSleep is used to ensure
        // we can receive signals and act accordingly during thread sleep state.
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

                // The event can be signalled by CtrlC,
                // Exit ASAP when the program terminates (i.e., shutdown/abort is signalled.)
                ewh.WaitOne(timeout.Subtract(elapsedTime));
                stopwatch.Stop();

                elapsedTime = stopwatch.Elapsed;
            }

            if (stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }
        }

        // Observers are instance types. Create them, store in persistent list.
        // List order matters. These are cycled through sequentially, one observer at a time.
        private static List<ObserverBase> GetObservers()
        {
            // You can simply not create an instance of an observer you don't want to run. The list
            // below is just for reference. GetObservers only returns enabled observers, anyway.
            var observers = new List<ObserverBase>(new ObserverBase[]
            {
                new ClusterObserver(),
            });

            // Only return a list with user-enabled observer instances.
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

            // Shutdown
            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverShutdownGracePeriodInSeconds),
                out int shutdownGracePeriodInSeconds))
            {
                this.shutdownGracePeriodInSeconds = shutdownGracePeriodInSeconds;
            }

            // (Assuming Diagnostics/Analytics cloud service implemented) Telemetry.
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
                this.Logger.LogInfo("Starting Observers loop.");

                // Observers run sequentially. See RunObservers impl.
                while (true)
                {
                    if (this.shutdownSignalled || this.token.IsCancellationRequested)
                    {
                        this.globalShutdownEventHandle.Set();
                        this.Logger.LogInfo("Shutdown signalled. Stopping.");
                        break;
                    }

                    if (this.RunObservers())
                    {
                        if (ObserverExecutionLoopSleepSeconds > 0)
                        {
                            this.Logger.LogInfo($"Sleeping for {ObserverExecutionLoopSleepSeconds} seconds before running again.");
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

                // Telemetry.
                if (TelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                        HealthScope.Application,
                        "ClusterObserverServiceHealth",
                        HealthState.Warning,
                        message,
                        ObserverConstants.ObserverManangerName,
                        this.token);
                }

                // Take down FCO process. Fix the bugs this identifies. This code should never run if observers aren't buggy.
                // Don't swallow the exception.
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
            this.Logger.LogInfo("Signalling task cancellation to currently running Observer.");
            this.cts.Cancel();
            this.Logger.LogInfo("Successfully signalled cancellation to currently running Observer.");
        }

        private bool RunObservers()
        {
            var exceptionBuilder = new StringBuilder();
            bool allExecuted = true;

            foreach (var observer in this.observers)
            {
                try
                {
                    // Shutdown/cancellation signaled, so stop.
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

                    // Synchronous call.
                    var isCompleted = observer.ObserveAsync(this.cts.Token).Wait(this.observerExecTimeout);
                    this.IsObserverRunning = false;

                    // The observer is taking too long (hung?), move on to next observer.
                    // Currently, this observer will not run again for the lifetime of this FO service instance.
                    if (!isCompleted)
                    {
                        string observerHealthWarning = observer.ObserverName + $" has exceeded its alloted run time of {this.observerExecTimeout.TotalSeconds} seconds. " +
                                                       $"This means something is wrong with {observer.ObserverName}. It will not be run again. Look into it.";

                        this.Logger.LogError(observerHealthWarning);

                        // TODO: Add HealthReport (App Level).
                        observer.IsUnhealthy = true;

                        if (TelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportMetricAsync($"ObserverHealthError", $"{observer.ObserverName} on node {this.nodeName} has exceeded its alloted run time of {this.observerExecTimeout.TotalSeconds} seconds.", this.token);
                        }

                        continue;
                    }
                }
                catch (AggregateException ex)
                {
                    this.IsObserverRunning = false;

                    if (!(ex.InnerException is OperationCanceledException) && !(ex.InnerException is TaskCanceledException))
                    {
                        exceptionBuilder.AppendLine($"Handled Exception from {observer.ObserverName}:\r\n{ex.InnerException.ToString()}");
                        allExecuted = false;
                    }
                }
            }

            if (allExecuted)
            {
                this.Logger.LogInfo(ObserverConstants.AllObserversExecutedMessage);

            }
            else
            {
                this.Logger.LogError(exceptionBuilder.ToString());


                exceptionBuilder.Clear();
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

                    // Flush and Dispose all NLog targets. No more logging.
                    Logger.Flush();
                    Logger.ShutDown();

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
