// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClusterObserver.Interfaces;
using ClusterObserver.Utilities;
using ClusterObserver.Utilities.Telemetry;

namespace ClusterObserver
{
    public class ClusterObserverManager : IDisposable
    {
        private readonly string nodeName;
        private ClusterObserver observer;
        private EventWaitHandle globalShutdownEventHandle;
        private volatile bool shutdownSignaled;
        private int shutdownGracePeriodInSeconds = 2;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(30);
        private CancellationToken token;
        private CancellationTokenSource cts;
        private CancellationTokenSource linkedSFRuntimeObserverTokenSource;
        private bool hasDisposed;
        private bool appParamsUpdating;
        private static bool etwEnabled;

        public bool IsObserverRunning
        {
            get; set;
        }

        public static int ObserverExecutionLoopSleepSeconds 
        { 
            get; private set; 
        } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

        public static int AsyncOperationTimeoutSeconds
        {
            get; private set;
        }

        public static FabricClient FabricClientInstance
        {
            get; set;
        }

        public static StatelessServiceContext FabricServiceContext
        {
            get; set;
        }

        public static ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        public static bool TelemetryEnabled
        {
            get; set;
        }

        public static bool EtwEnabled
        {
            get => bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableEventSourceProvider), out etwEnabled) && etwEnabled;

            set => etwEnabled = value;
        }

        public static string LogPath
        {
            get; private set;
        }

        private Logger Logger
        {
            get;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserverManager"/> class.
        /// </summary>
        public ClusterObserverManager(StatelessServiceContext context, CancellationToken token)
        {
            this.token = token;
            cts = new CancellationTokenSource();
            linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, this.token);
            _ = this.token.Register(() => { ShutdownHandler(this, null); });
            FabricClientInstance = new FabricClient();
            FabricServiceContext = context;
            nodeName = FabricServiceContext?.NodeContext.NodeName;
            context.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;

            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetConfigSettingValue(ObserverConstants.ObserverLogPath);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "observer_logs");
                logFolderBasePath = logFolderBase;
            }

            LogPath = logFolderBasePath;

            // This logs error/warning/info messages for ObserverManager.
            Logger = new Logger(ObserverConstants.ObserverManagerName, logFolderBasePath);
            SetPropertiesFromConfigurationParameters();
            observer = new ClusterObserver();
        }

        private static string GetConfigSettingValue(string parameterName)
        {
            try
            {
                var configSettings = FabricServiceContext?.CodePackageActivationContext?.GetConfigurationPackageObject("Config")?.Settings;
                var section = configSettings?.Sections[ObserverConstants.ObserverManagerConfigurationSectionName];
                var parameter = section?.Parameters[parameterName];

                return parameter?.Value;
            }
            catch (Exception e) when (e is KeyNotFoundException || e is FabricElementNotFoundException)
            {
            }

            return null;
        }

        private async void ShutdownHandler(object sender, ConsoleCancelEventArgs consoleEvent)
        {
            if (hasDisposed)
            {
                return;
            }

            Thread.Sleep(shutdownGracePeriodInSeconds * 1000);

            shutdownSignaled = true;
            _ = globalShutdownEventHandle?.Set();
            await StopAsync();
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

            while (!shutdownSignaled &&
                   !token.IsCancellationRequested &&
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

        private void SetPropertiesFromConfigurationParameters()
        {
            // Observer
            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout),
                out int result))
            {
                observerExecTimeout = TimeSpan.FromSeconds(result);
            }

            // Logger
            if (bool.TryParse(
                GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                Logger.EnableVerboseLogging = enableVerboseLogging;
            }

            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverLoopSleepTimeSeconds),
                out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;

                Logger.LogInfo($"ExecutionFrequency is {ObserverExecutionLoopSleepSeconds} Seconds");
            }

            // Shutdown
            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverShutdownGracePeriodInSeconds),
                out int gracePeriodInSeconds))
            {
                shutdownGracePeriodInSeconds = gracePeriodInSeconds;
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.AsyncOperationTimeoutSeconds),
                out int asyncTimeout))
            {
                AsyncOperationTimeoutSeconds = asyncTimeout;
            }

            // (Assuming Diagnostics/Analytics cloud service implemented) Telemetry.
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableTelemetry), out bool telemEnabled))
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
                    
                    case TelemetryProviderType.AzureApplicationInsights:
                    
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

        public async Task StartAsync()
        {
            try
            {
                if (globalShutdownEventHandle == null)
                {
                    globalShutdownEventHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
                }

                while (true)
                {
                    if (!appParamsUpdating && (shutdownSignaled || token.IsCancellationRequested))
                    {
                        _ = globalShutdownEventHandle.Set();
                        Logger.LogInfo("Shutdown signaled. Stopping.");
                        break;
                    }

                    await RunObserverAync().ConfigureAwait(false);

                    Logger.LogInfo($"Sleeping for {(ObserverExecutionLoopSleepSeconds > 0 ? ObserverExecutionLoopSleepSeconds : 10)} seconds before running again.");
                    ThreadSleep(globalShutdownEventHandle, TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds > 0 ? ObserverExecutionLoopSleepSeconds : 10));

                    Logger.Flush();
                }
            }
            catch (Exception ex)
            {
                var message = $"Unhanded Exception in ClusterObserverManager on node {nodeName}. Taking down CO process. Error info:{Environment.NewLine}{ex}";
                Logger.LogError(message);

                // Telemetry.
                if (TelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                                            HealthScope.Application,
                                            "ClusterObserverServiceHealth",
                                            HealthState.Warning,
                                            message,
                                            ObserverConstants.ObserverManagerName,
                                            token);
                }

                // ETW.
                if (EtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            HealthScope = "Application",
                                            HealthState = "Warning",
                                            HealthEventDescription = message,
                                            Metric = "ClusterObserverServiceHealth",
                                            Source = ObserverConstants.ClusterObserverName,
                                        });
                }

                // Don't swallow the unhandled exception. Fix the bug.
                throw;
            }
        }

        public async Task StopAsync()
        { 
            if (!shutdownSignaled)
            {
                shutdownSignaled = true;
            }

            await SignalAbortToRunningObserverAsync().ConfigureAwait(false);
        }

        private Task SignalAbortToRunningObserverAsync()
        {
            Logger.LogInfo("Signalling task cancellation to currently running Observer.");
            
            try
            {
                cts.Cancel();
                IsObserverRunning = false;
            }
            catch(Exception e) when (e is AggregateException || e is ObjectDisposedException)
            {
                // This shouldn't happen, but if it does this info will be useful in identifying the bug..
                // Telemetry.
                if (TelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                                            HealthScope.Application,
                                            "ClusterObserverServiceHealth",
                                            HealthState.Warning,
                                            $"{e}",
                                            ObserverConstants.ObserverManagerName,
                                            token);
                }

                // ETW.
                if (EtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            HealthScope = "Application",
                                            HealthState = "Warning",
                                            HealthEventDescription = $"{e}",
                                            Metric = "ClusterObserverServiceHealth",
                                            Source = ObserverConstants.ClusterObserverName,
                                        });
                }
            }

            return Task.CompletedTask;
        }

        private async Task RunObserverAync()
        {
            if (!observer.IsEnabled)
            {
                return;
            }

            var exceptionBuilder = new StringBuilder();

            try
            {
                Logger.LogInfo($"Starting {observer.ObserverName}");
                IsObserverRunning = true;

                // Synchronous call.
                var isCompleted = observer.ObserveAsync(
                    linkedSFRuntimeObserverTokenSource != null ? linkedSFRuntimeObserverTokenSource.Token : token).Wait(observerExecTimeout);

                // The observer is taking too long (hung?)
                if (!isCompleted)
                {
                    string observerHealthWarning = $"{observer.ObserverName} has exceeded its specified run time of {observerExecTimeout.TotalSeconds} seconds. Aborting.";
                    
                    await SignalAbortToRunningObserverAsync();

                    Logger.LogWarning(observerHealthWarning);

                    if (TelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportHealthAsync(
                                                HealthScope.Application,
                                                "ObserverHealthReport",
                                                HealthState.Warning,
                                                observerHealthWarning,
                                                ObserverConstants.ObserverManagerName,
                                                token);
                    }

                    if (EtwEnabled)
                    {
                        Logger.EtwLogger?.Write(
                                ObserverConstants.ClusterObserverETWEventName,
                                new
                                {
                                    HealthScope = "Application",
                                    HealthState = "Warning",
                                    HealthEventDescription = observerHealthWarning,
                                    Metric = "ClusterObserverServiceHealth",
                                    Source = ObserverConstants.ClusterObserverName,
                                });
                    }

                    observer = new ClusterObserver();
                    cts = new CancellationTokenSource();
                }
            }
            catch (AggregateException ex) when (
                ex.InnerException is OperationCanceledException ||
                ex.InnerException is TaskCanceledException ||
                ex.InnerException is TimeoutException)
            {
                IsObserverRunning = false;
                _ = exceptionBuilder.AppendLine($"Handled Exception from {observer.ObserverName}:{Environment.NewLine}{ex.InnerException}");
                Logger.LogError(exceptionBuilder.ToString());
                _ = exceptionBuilder.Clear();
            }
            catch (Exception e)
            {
                string msg = $"Unhandled exception in ClusterObserverManager.Run(). Taking down process. Error info:{Environment.NewLine}{e}";
                Logger.LogError(msg);

                if (TelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                                            HealthScope.Application,
                                            "ObserverHealthReport",
                                            HealthState.Warning,
                                            msg,
                                            ObserverConstants.ObserverManagerName,
                                            token);
                }

                if (EtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                                        ObserverConstants.ClusterObserverETWEventName,
                                        new
                                        {
                                            HealthScope = "Application",
                                            HealthState = "Warning",
                                            HealthEventDescription = msg,
                                            Metric = "ClusterObserverServiceHealth",
                                            Source = ObserverConstants.ClusterObserverName,
                                        });
                }

                throw;
            }

            IsObserverRunning = false;
        }

        private void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            appParamsUpdating = true;
            Logger.LogInfo("Application Parameter upgrade started...");
            SignalAbortToRunningObserverAsync();
            observer = new ClusterObserver(e.NewPackage.Settings);
            cts = new CancellationTokenSource();
            Logger.LogInfo("Application Parameter upgrade complete...");
            appParamsUpdating = false;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (hasDisposed)
            {
                return;
            }

            if (!disposing)
            {
                return;
            }

            if (IsObserverRunning)
            {
                StopAsync().GetAwaiter().GetResult();
            }

            globalShutdownEventHandle?.Dispose();

            if (FabricClientInstance != null)
            {
                FabricClientInstance.Dispose();
                FabricClientInstance = null;
            }

            if (cts != null)
            {
                cts.Dispose();
                cts = null;
            }

            // Flush and Dispose all NLog targets. No more logging.
            Logger.Flush();
            Logger.ShutDown();

            FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent -= CodePackageActivationContext_ConfigurationPackageModifiedEvent;

            hasDisposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
