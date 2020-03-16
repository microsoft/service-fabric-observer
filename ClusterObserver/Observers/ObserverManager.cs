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

namespace FabricClusterObserver.Observers
{
    // This class manages the lifetime of all observers from instantiation to "destruction",
    // and sequentially runs all observer instances in a never-ending while loop,
    // with optional sleeps, and reliable shutdown event handling.
    public class ObserverManager : IDisposable
    {
        private readonly string nodeName;
        private readonly List<ObserverBase> observers;
        private EventWaitHandle globalShutdownEventHandle;
        private volatile bool shutdownSignaled;
        private int shutdownGracePeriodInSeconds = 2;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(30);
        private CancellationToken token;
        private CancellationTokenSource cts;
        private bool hasDisposed;
        private static bool etwEnabled;

        public string ApplicationName { get; set; }

        public bool IsObserverRunning { get; set; }

        public static int ObserverExecutionLoopSleepSeconds { get; private set; } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

        public static int AsyncClusterOperationTimeoutSeconds { get; private set; }

        public static FabricClient FabricClientInstance { get; set; }

        public static StatelessServiceContext FabricServiceContext { get; set; }

        public static ITelemetryProvider TelemetryClient { get; set; }

        public static bool TelemetryEnabled { get; set; }

        public static bool EtwEnabled
        {
            get => bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableEventSourceProvider), out etwEnabled) && etwEnabled;

            set => etwEnabled = value;
        }

        public static string EtwProviderName
        {
            get
            {
                if (!EtwEnabled)
                {
                    return null;
                }

                string key = GetConfigSettingValue(ObserverConstants.EventSourceProviderName);

                return !string.IsNullOrEmpty(key) ? key : null;
            }
        }

        private Logger Logger { get; }


        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// </summary>
        public ObserverManager(
            StatelessServiceContext context,
            CancellationToken token)
        {
            this.token = token;
            this.cts = new CancellationTokenSource();
            _ = this.token.Register(() => { this.ShutdownHandler(this, null); });
            FabricClientInstance = new FabricClient();
            FabricServiceContext = context;
            this.nodeName = FabricServiceContext?.NodeContext.NodeName;

            // Observer Logger setup.
            string logFolderBasePath;
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
            this.Logger = new Logger(ObserverConstants.ObserverManagerName, logFolderBasePath);

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
            _ = this.token.Register(() => { this.ShutdownHandler(this, null); });
            this.Logger = new Logger("ObserverManagerSingleObserverRun");

            this.observers = new List<ObserverBase>(new[]
            {
                observer,
            });
        }

        private static string GetConfigSettingValue(string parameterName)
        {
            try
            {
                var configSettings = FabricServiceContext.CodePackageActivationContext?.GetConfigurationPackageObject("Config")?.Settings;

                var section = configSettings?.Sections[ObserverConstants.ObserverManagerConfigurationSectionName];

                var parameter = section?.Parameters[parameterName];

                return parameter?.Value;
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
            if (this.hasDisposed)
            {
                return;
            }

            Thread.Sleep(this.shutdownGracePeriodInSeconds * 1000);

            this.shutdownSignaled = true;
            _ = this.globalShutdownEventHandle?.Set();
            this.StopObservers();
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

            while (!this.shutdownSignaled &&
                   !this.token.IsCancellationRequested &&
                   timeout > elapsedTime)
            {
                stopwatch.Start();

                // The event can be signaled by CtrlC,
                // Exit ASAP when the program terminates (i.e., shutdown/abort is signaled.)
                _ = ewh.WaitOne(timeout.Subtract(elapsedTime));
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
            return observers.Where(obs => obs.IsEnabled).ToList();
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
                out int gracePeriodInSeconds))
            {
                this.shutdownGracePeriodInSeconds = gracePeriodInSeconds;
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.AsyncClusterOperationTimeoutSeconds),
                out int asyncTimeout))
            {
                AsyncClusterOperationTimeoutSeconds = asyncTimeout;
            }

            // (Assuming Diagnostics/Analytics cloud service implemented) Telemetry.
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.TelemetryEnabled), out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;
            }

            if (TelemetryEnabled)
            {
                string telemetryProviderType = GetConfigSettingValue(ObserverConstants.TelemetryProviderType);

                if (string.IsNullOrEmpty(telemetryProviderType))
                {
                    TelemetryEnabled = false;

                    return;
                }

                if (!Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
                {
                    TelemetryEnabled = false;

                    return;
                }

                switch (telemetryProvider)
                {
                     case TelemetryProviderType.AzureLogAnalytics:
                        {
                            var logAnalyticsLogType =
                                GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter) ?? "Application";

                            var logAnalyticsSharedKey =
                                GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter);

                            var logAnalyticsWorkspaceId =
                                GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter);

                            if (string.IsNullOrEmpty(logAnalyticsSharedKey)
                                || string.IsNullOrEmpty(logAnalyticsWorkspaceId))
                            {
                                TelemetryEnabled = false;

                                return;
                            }

                            TelemetryClient = new LogAnalyticsTelemetry(
                                logAnalyticsWorkspaceId,
                                logAnalyticsSharedKey,
                                logAnalyticsLogType,
                                FabricClientInstance,
                                token);

                            break;
                        }

                    case TelemetryProviderType.AzureApplicationInsights:
                        {
                            string aiKey = GetConfigSettingValue(ObserverConstants.AiKey);

                            if (string.IsNullOrEmpty(aiKey))
                            {
                                TelemetryEnabled = false;

                                return;
                            }

                            TelemetryClient = new AppInsightsTelemetry(aiKey);

                            break;
                        }
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
                    if (this.shutdownSignaled || this.token.IsCancellationRequested)
                    {
                        _ = this.globalShutdownEventHandle.Set();
                        this.Logger.LogInfo("Shutdown signaled. Stopping.");
                        break;
                    }

                    if (!this.RunObservers())
                    {
                        continue;
                    }

                    if (ObserverExecutionLoopSleepSeconds > 0)
                    {
                        this.Logger.LogInfo($"Sleeping for {ObserverExecutionLoopSleepSeconds} seconds before running again.");
                        this.ThreadSleep(this.globalShutdownEventHandle, TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds));
                    }

                    Logger.Flush();
                }
            }
            catch (Exception ex)
            {
                var message = $"Unhanded Exception in ObserverManager on node {this.nodeName}. Taking down FO process. Error info: {ex}";
                this.Logger.LogError(message);

                // Telemetry.
                if (TelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                        HealthScope.Application,
                        "ClusterObserverServiceHealth",
                        HealthState.Warning,
                        message,
                        ObserverConstants.ObserverManagerName,
                        this.token);
                }

                // Don't swallow the unhandled exception. Fix the bug.
                throw;
            }
        }

        public void StopObservers()
        {
            try
            {
                if (!this.shutdownSignaled)
                {
                    this.shutdownSignaled = true;
                }

                this.SignalAbortToRunningObserver();
                this.IsObserverRunning = false;
            }
            catch (Exception e)
            {
                this.Logger.LogWarning($"Unhandled Exception thrown during ObserverManager.Stop(): {e}");

                throw;
            }
        }

        private void SignalAbortToRunningObserver()
        {
            this.Logger.LogInfo("Signalling task cancellation to currently running Observer.");
            this.cts.Cancel();
            this.Logger.LogInfo("Successfully signaled cancellation to currently running Observer.");
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
                    if (this.token.IsCancellationRequested || this.shutdownSignaled)
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
                        string observerHealthWarning = $"{observer.ObserverName} has exceeded its specified run time of {this.observerExecTimeout.TotalSeconds} seconds. " +
                                                       $"This means something is wrong with {observer.ObserverName}. It will not be run again. Look into it.";

                        this.Logger.LogWarning(observerHealthWarning);
                        observer.IsUnhealthy = true;

                        if (TelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportHealthAsync(
                                HealthScope.Application,
                                "ObserverHealthReport",
                                HealthState.Warning,
                                observerHealthWarning,
                                ObserverConstants.ObserverManagerName,
                                this.token);
                        }
                    }
                }
                catch (AggregateException ex)
                {
                    this.IsObserverRunning = false;

                    if (ex.InnerException is OperationCanceledException ||
                        ex.InnerException is TaskCanceledException)
                    {
                        continue;
                    }

                    _ = exceptionBuilder.AppendLine($"Handled Exception from {observer.ObserverName}:\r\n{ex.InnerException}");
                    allExecuted = false;
                }
            }

            if (allExecuted)
            {
                this.Logger.LogInfo(ObserverConstants.AllObserversExecutedMessage);

            }
            else
            {
                this.Logger.LogError(exceptionBuilder.ToString());
                _ = exceptionBuilder.Clear();
            }

            return allExecuted;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (this.hasDisposed)
            {
                return;
            }

            if (!disposing)
            {
                return;
            }

            if (this.IsObserverRunning)
            {
                this.StopObservers();
            }

            globalShutdownEventHandle?.Dispose();

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

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
