// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClusterObserver.Interfaces;
using ClusterObserver.Utilities;
using ClusterObserver.Utilities.Telemetry;

namespace ClusterObserver
{
    public sealed class ClusterObserverManager : IDisposable
    {
        private static bool etwEnabled;
        private readonly string nodeName;
        private readonly CancellationTokenSource linkedSFRuntimeObserverTokenSource;
        private readonly CancellationToken token;
        private ClusterObserver observer;
        private int shutdownGracePeriodInSeconds = 2;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(30);
        private CancellationTokenSource cts;
        private volatile bool shutdownSignaled;
        private bool hasDisposed;
        private bool appParamsUpdating;

        public bool IsObserverRunning
        {
            get; set;
        }

        private static int ObserverExecutionLoopSleepSeconds 
        { 
            get;
            set; 
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
            get;
            private set;
        }

        public static bool TelemetryEnabled
        {
            get;
            private set;
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

            await Task.Delay(shutdownGracePeriodInSeconds).ConfigureAwait(true);

            shutdownSignaled = true;
            await StopAsync();
        }

        private void SetPropertiesFromConfigurationParameters()
        {
            // Observer
            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout), out int result))
            {
                observerExecTimeout = TimeSpan.FromSeconds(result);
            }

            // Logger
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter), out bool enableVerboseLogging))
            {
                Logger.EnableVerboseLogging = enableVerboseLogging;
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverLoopSleepTimeSeconds), out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;
            }

            // Shutdown
            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverShutdownGracePeriodInSeconds), out int gracePeriodInSeconds))
            {
                shutdownGracePeriodInSeconds = gracePeriodInSeconds;
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.AsyncOperationTimeoutSeconds), out int asyncTimeout))
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
                    
                        string logAnalyticsLogType = GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter) ?? "Application";
                        string logAnalyticsSharedKey = GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter);
                        string logAnalyticsWorkspaceId = GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter);

                        if (string.IsNullOrEmpty(logAnalyticsSharedKey) || string.IsNullOrEmpty(logAnalyticsWorkspaceId))
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
                while (true)
                {
                    if (!appParamsUpdating && (shutdownSignaled || token.IsCancellationRequested))
                    {
                        Logger.LogInfo("Shutdown signaled. Stopping.");
                        await StopAsync().ConfigureAwait(true);
                        break;
                    }

                    await RunObserverAync().ConfigureAwait(true);
                    await Task.Delay(TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds > 0 ? ObserverExecutionLoopSleepSeconds : 10), token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
            {

            }
            catch (Exception e)
            {
                string message = $"Unhanded Exception in ClusterObserverManager on node {nodeName}. Taking down CO process. Error info:{Environment.NewLine}{e}";
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
                                            Source = ObserverConstants.ClusterObserverName
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

            await SignalAbortToRunningObserverAsync().ConfigureAwait(true);
        }

        private Task SignalAbortToRunningObserverAsync()
        {
            Logger.LogInfo("Signalling task cancellation to currently running Observer.");
            
            try
            {
                cts?.Cancel();
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
                                            Source = ObserverConstants.ClusterObserverName
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

            try
            {
                Logger.LogInfo($"Starting {observer.ObserverName}");
                IsObserverRunning = true;

                // Synchronous call.
                var isCompleted = observer.ObserveAsync(linkedSFRuntimeObserverTokenSource != null ? linkedSFRuntimeObserverTokenSource.Token : token).Wait(observerExecTimeout);

                // The observer is taking too long (hung?)
                if (!isCompleted)
                {
                    string observerHealthWarning = $"{observer.ObserverName} has exceeded its specified run time of {observerExecTimeout.TotalSeconds} seconds. Aborting.";
                    
                    await SignalAbortToRunningObserverAsync().ConfigureAwait(true);

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
                                                Source = ObserverConstants.ClusterObserverName
                                            });
                    }

                    // Create new instance of CO.
                    observer = new ClusterObserver();
                    cts = new CancellationTokenSource();
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                string msg = $"Unhandled exception in ClusterObserverManager.RunObserverAync(). Taking down process. Error info:{Environment.NewLine}{e}";
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
                                            Source = ObserverConstants.ClusterObserverName
                                        });
                }

                throw;
            }

            IsObserverRunning = false;
        }

        /// <summary>
        /// App parameter config update handler. This will recreate CO instance with new ConfigSettings applied.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            appParamsUpdating = true;
            Logger.LogInfo("Application Parameter upgrade started...");

            await SignalAbortToRunningObserverAsync();

            observer = new ClusterObserver(e.NewPackage.Settings);
            cts = new CancellationTokenSource();

            Logger.LogInfo("Application Parameter upgrade complete...");
            appParamsUpdating = false;
        }

        private void Dispose(bool disposing)
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
