// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClusterObserver.Utilities;
using FabricObserver.Observers;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.TelemetryLib;
using FabricObserver.Utilities.ServiceFabric;
using Microsoft.Extensions.DependencyInjection;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace ClusterObserver
{
    public sealed class ClusterObserverManager : IDisposable
    {
        private static bool etwEnabled;
        private readonly string nodeName;
        private CancellationTokenSource linkedSFRuntimeObserverTokenSource;
        private readonly CancellationToken token;
        private int shutdownGracePeriodInSeconds = 2;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(30);
        private CancellationTokenSource cts;
        private volatile bool shutdownSignaled;
        private bool hasDisposed;
        private bool internalTelemetrySent;
        private bool appParamsUpdating;

        // Folks often use their own version numbers. This is for internal diagnostic telemetry.
        private const string InternalVersionNumber = "2.2.8";

        public bool EnableOperationalTelemetry
        {
            get; set;
        }

        public bool IsObserverRunning
        {
            get; set;
        }

        private static int ObserverExecutionLoopSleepSeconds 
        {
            get; set;
        } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

        public static int AsyncOperationTimeoutSeconds
        {
            get; private set;
        }

        public static FabricClient FabricClientInstance => FabricClientUtilities.FabricClientSingleton;

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
            get; set;
        }

        public static bool EtwEnabled
        {
            get => bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableETWProvider, null), out etwEnabled) && etwEnabled;
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

        private List<ObserverBase> Observers
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterObserverManager"/> class.
        /// </summary>
        public ClusterObserverManager(ServiceProvider serviceProvider, CancellationToken token)
        {
            this.token = token;
            cts = new CancellationTokenSource();
            linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, this.token);
            _ = this.token.Register(() => { ShutdownHandler(this, null); });
            FabricServiceContext = serviceProvider.GetRequiredService<StatelessServiceContext>();
            nodeName = FabricServiceContext?.NodeContext.NodeName;
            FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            Observers = serviceProvider.GetServices<ObserverBase>().ToList();
            
            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetConfigSettingValue(ObserverConstants.ObserverLogPathParameter, null);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "cluster_observer_logs");
                logFolderBasePath = logFolderBase;
            }

            LogPath = logFolderBasePath;
            _ = bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableETWProvider, null), out bool enableEtwProvider);
            EtwEnabled = enableEtwProvider;

            // ObserverManager logger EnableVerboseLogging.
            _ = bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter, null), out bool enableVerboseLogging);

            // Log archive lifetime.
            _ = int.TryParse(GetConfigSettingValue(ObserverConstants.MaxArchivedLogFileLifetimeDaysParameter, null), out int maxArchivedLogFileLifetimeDays);

            // This logs error/warning/info messages for ClusterObserverManager (local text log and optionally ETW).
            Logger = new Logger(ClusterObserverConstants.ClusterObserverManagerName, logFolderBasePath, maxArchivedLogFileLifetimeDays)
            {
                EnableETWLogging = EtwEnabled,
                EnableVerboseLogging = enableVerboseLogging
            };
            SetPropertiesFromConfigurationParameters();
        }

        private static string GetConfigSettingValue(string parameterName, ConfigurationSettings settings, string sectionName = null)
        {
            try
            {
                ConfigurationSettings configSettings = null;

                sectionName ??= ClusterObserverConstants.ObserverManagerConfigurationSectionName;

                if (settings != null)
                {
                    configSettings = settings;
                }
                else
                {
                    configSettings = FabricServiceContext.CodePackageActivationContext?.GetConfigurationPackageObject("Config")?.Settings;
                }

                var section = configSettings?.Sections[sectionName];
                var parameter = section?.Parameters[parameterName];

                return parameter?.Value;
            }
            catch (Exception e) when (e is KeyNotFoundException or FabricElementNotFoundException)
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

            await Task.Delay(shutdownGracePeriodInSeconds);

            shutdownSignaled = true;
            await StopAsync();
        }

        private void SetPropertiesFromConfigurationParameters(ConfigurationSettings settings = null)
        {
            // Observer
            if (int.TryParse(GetConfigSettingValue(ClusterObserverConstants.ObserverExecutionTimeoutParameter, settings), out int result))
            {
                observerExecTimeout = TimeSpan.FromSeconds(result);
            }

            // Logger settings - Overrides. Config update. \\

            // settings are not null if this is running due to a config update. Could also check for isConfigurationUpdateInProgress.
            if (settings != null && Logger != null)
            {
                // ObserverManager logger EnableETWLogging - Override.
                _ = bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableETWProvider, settings), out bool enableEtwProvider);
                EtwEnabled = enableEtwProvider;
                Logger.EnableETWLogging = enableEtwProvider;

                // ObserverManager logger EnableVerboseLogging - Override.
                _ = bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter, settings), out bool enableVerboseLogging);
                Logger.EnableVerboseLogging = enableVerboseLogging;

                // ObserverManager/Observer logger MaxArchiveLifetimeDays - Override.
                _ = int.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter, settings), out int maxArchiveLifetimeDays);
                Logger.MaxArchiveFileLifetimeDays = maxArchiveLifetimeDays;

                // ObserverManager/Observer logger ObserverLogPath - Override.
                string loggerBasePath = GetConfigSettingValue(ObserverConstants.ObserverLogPathParameter, settings);

                if (!string.IsNullOrWhiteSpace(loggerBasePath))
                {
                    Logger.LogFolderBasePath = loggerBasePath;
                }

                // This will reset existing logger instance's config state and employ updated settings immediately. See Logger.cs.
                Logger.InitializeLoggers(true);
            }

            // End Logger settings - Overrides. \\

            if (int.TryParse(GetConfigSettingValue(ClusterObserverConstants.ObserverLoopSleepTimeSecondsParameter, settings), out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;
            }

            // Shutdown
            if (int.TryParse(GetConfigSettingValue(ClusterObserverConstants.ObserverShutdownGracePeriodInSecondsParameter, settings), out int gracePeriodInSeconds))
            {
                shutdownGracePeriodInSeconds = gracePeriodInSeconds;
            }

            if (int.TryParse(GetConfigSettingValue(ClusterObserverConstants.AsyncOperationTimeoutSeconds, settings), out int asyncTimeout))
            {
                AsyncOperationTimeoutSeconds = asyncTimeout;
            }

            // Internal diagnostic telemetry.
            if (bool.TryParse(GetConfigSettingValue(ClusterObserverConstants.OperationalTelemetryEnabledParameter, settings), out bool opsTelemEnabled))
            {
                EnableOperationalTelemetry = opsTelemEnabled;
            }
            
            // (Assuming Diagnostics/Analytics cloud service implemented) Telemetry.
            if (bool.TryParse(GetConfigSettingValue(ClusterObserverConstants.EnableTelemetryParameter, settings), out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;
            }

            if (!TelemetryEnabled)
            {
                return;
            }

            string telemetryProviderType = GetConfigSettingValue(ClusterObserverConstants.TelemetryProviderTypeParameter, settings);

            if (string.IsNullOrWhiteSpace(telemetryProviderType))
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

                    string logAnalyticsLogType = GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter, settings) ?? "Application";
                    string logAnalyticsSharedKey = GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter, settings);
                    string logAnalyticsWorkspaceId = GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter, settings);

                    if (string.IsNullOrWhiteSpace(logAnalyticsSharedKey) || string.IsNullOrWhiteSpace(logAnalyticsWorkspaceId))
                    {
                        TelemetryEnabled = false;
                        return;
                    }

                    TelemetryClient = new LogAnalyticsTelemetry(logAnalyticsWorkspaceId, logAnalyticsSharedKey, logAnalyticsLogType);
                    break;

                case TelemetryProviderType.AzureApplicationInsights:

                    string aiConnString = GetConfigSettingValue(ObserverConstants.AppInsightsConnectionString, settings);

                    if (string.IsNullOrWhiteSpace(aiConnString))
                    {
                        TelemetryEnabled = false;
                        return;
                    }

                    TelemetryClient = new AppInsightsTelemetry(aiConnString);
                    break;
            }
        }

        public async Task StartAsync()
        {
            try
            {
                // This data is sent once over the lifetime of the deployed service instance and will be retained for no more
                // than 90 days.
                if (EnableOperationalTelemetry && !internalTelemetrySent)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(nodeName);
                        ClusterObserverOperationalEventData coData = GetClusterObserverInternalTelemetryData();

                        if (coData != null)
                        {
                            string filepath = Path.Combine(Logger.LogFolderBasePath, $"co_operational_telemetry.log");

                            if (telemetryEvents.EmitClusterObserverOperationalEvent(coData, filepath))
                            {
                                internalTelemetrySent = true;
                            }
                        }
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // Telemetry is non-critical and should not take down CO.
                        // TelemetryLib will log exception details to file in top level FO log folder.
                    }
                }

                // Run until the SF RunAsync CancellationToken is cancelled.
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!appParamsUpdating && shutdownSignaled)
                        {
                            Logger.LogInfo("Shutdown signaled. Stopping.");
                            break;
                        }

                        await RunAsync();
                        Logger.LogInfo($"Waiting {(ObserverExecutionLoopSleepSeconds > 0 ? ObserverExecutionLoopSleepSeconds : 15)} seconds until next observer run loop.");
                        await Task.Delay(TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds > 0 ? ObserverExecutionLoopSleepSeconds : 15), token);
                    }
                    catch (Exception e) when (e is ArgumentException or FabricException or OperationCanceledException or TaskCanceledException or TimeoutException)
                    {
                        if (token.IsCancellationRequested)
                        {
                            Logger.LogInfo("RunAsync CancellationToken has been canceled by the SF runtime. Stopping.");
                            break;
                        }
                    }
                }

                // Closing. Stop all observers.
                await StopAsync();
            }
            catch (Exception e)
            {
                string message = $"Unhandled Exception in ClusterObserver on node {nodeName}. Taking down CO process. Error info:{Environment.NewLine}{e}";
                Logger.LogError(message);

                // Telemetry.
                if (TelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "ClusterObserverServiceHealth",
                            HealthState.Error,
                            message,
                            ClusterObserverConstants.ClusterObserverManagerName,
                            token);
                }

                // ETW.
                if (EtwEnabled)
                {
                    Logger.LogEtw(
                            ClusterObserverConstants.ClusterObserverETWEventName,
                            new
                            {
                                Level = "Critical",
                                Message = message,
                                Source = ClusterObserverConstants.ClusterObserverName
                            });
                }

                // Operational telemetry sent to FO developer for use in understanding generic behavior of FO in the real world (no PII)
                if (EnableOperationalTelemetry)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(nodeName);

                        var data = new CriticalErrorEventData
                        {
                            Source = ClusterObserverConstants.ClusterObserverManagerName,
                            ErrorMessage = e.Message,
                            ErrorStack = e.StackTrace,
                            CrashTime = DateTime.UtcNow.ToString("o"),
                            Version = InternalVersionNumber
                        };

                        string filepath = Path.Combine(Logger.LogFolderBasePath, $"co_critical_error_telemetry.log");
                        _ = telemetryEvents.EmitCriticalErrorEvent(data, ClusterObserverConstants.ClusterObserverName, filepath);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Telemetry is non-critical and should not take down FO.
                    }
                }

                // Don't swallow the unhandled exception. Fix the bug.
                throw;
            }
        }

        private static ClusterObserverOperationalEventData GetClusterObserverInternalTelemetryData()
        {
            ClusterObserverOperationalEventData telemetryData = null;

            try
            {
                telemetryData = new ClusterObserverOperationalEventData
                {
                    Version = InternalVersionNumber
                };
            }
            catch (ArgumentException)
            {

            }

            return telemetryData;
        }

        public async Task StopAsync(bool isAppParamUpdate = false)
        { 
            if (!shutdownSignaled && !isAppParamUpdate)
            {
                shutdownSignaled = true;
            }

            await SignalAbortToRunningObserverAsync();
        }

        private Task SignalAbortToRunningObserverAsync()
        {
            Logger.LogInfo("Signalling task cancellation to currently running Observer.");

            try
            {
                cts?.Cancel();
                IsObserverRunning = false;
            }
            catch (Exception e) when (e is AggregateException or ObjectDisposedException)
            {
                // This shouldn't happen, but if it does this info will be useful in identifying the bug..
                // Telemetry.
                if (TelemetryEnabled)
                {
                    _ = TelemetryClient?.ReportHealthAsync(
                            "ClusterObserverServiceHealth",
                            HealthState.Warning,
                            $"{e}",
                            ClusterObserverConstants.ClusterObserverManagerName,
                            token);
                }

                // ETW.
                if (EtwEnabled)
                {
                    Logger.LogEtw(
                        ClusterObserverConstants.ClusterObserverETWEventName,
                        new
                        {
                            HealthState = "Warning",
                            HealthEventDescription = $"{e}",
                            Metric = "ClusterObserverServiceHealth",
                            Source = ClusterObserverConstants.ClusterObserverName
                        });
                }
            }

            return Task.CompletedTask;
        }

        private async Task RunAsync()
        {
            foreach (var observer in Observers)
            {
                if (!observer.IsEnabled)
                {
                    continue;
                }

                try
                {
                    Logger.LogInfo($"Started {observer.ObserverName} run.");
                    IsObserverRunning = true;

                    // Synchronous call.
                    bool isCompleted = 
                        observer.ObserveAsync(linkedSFRuntimeObserverTokenSource != null ? linkedSFRuntimeObserverTokenSource.Token : token).Wait(observerExecTimeout);

                    // The observer is taking too long (hung?)
                    if (!isCompleted && !(token.IsCancellationRequested || shutdownSignaled || appParamsUpdating))
                    {
                        string observerHealthWarning =
                            $"{observer.ObserverName} has exceeded its specified run time of {observerExecTimeout.TotalSeconds} seconds. Aborting.";
                        await SignalAbortToRunningObserverAsync();
    
                        // Refresh CO CancellationTokenSources.
                        cts?.Dispose();
                        linkedSFRuntimeObserverTokenSource?.Dispose();
                        cts = new CancellationTokenSource();
                        linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token);

                        Logger.LogWarning(observerHealthWarning);

                        if (TelemetryEnabled)
                        {
                            _ = TelemetryClient?.ReportHealthAsync(
                                    "ObserverHealthReport",
                                    HealthState.Warning,
                                    observerHealthWarning,
                                    ClusterObserverConstants.ClusterObserverManagerName,
                                    token);
                        }

                        if (EtwEnabled)
                        {
                            Logger.LogEtw(
                                ClusterObserverConstants.ClusterObserverETWEventName,
                                new
                                {
                                    HealthState = "Warning",
                                    HealthEventDescription = observerHealthWarning,
                                    Source = ClusterObserverConstants.ClusterObserverName
                                });
                        }
                    }

                    Logger.LogInfo($"Completed {observer.ObserverName} run.");
                }
                catch (AggregateException ae)
                {
                    foreach (var e in ae.Flatten().InnerExceptions)
                    {
                        if (e is OperationCanceledException or TaskCanceledException)
                        {
                            if (appParamsUpdating)
                            {
                                // Exit. CO is processing a versionless parameter-only application upgrade.
                                return;
                            }

                            // CO will fail. Gracefully.
                        }
                        else if (e is FabricException or TimeoutException)
                        {
                            // These are transient and will have already been logged.
                        }
                    }
                }
                catch (Exception e) when (e is not (OperationCanceledException or TaskCanceledException))
                {
                    string msg = $"Unhandled exception in ClusterObserverManager.RunObserverAync(). Taking down process. Error info:{Environment.NewLine}{e}";
                    Logger.LogError(msg);

                    if (TelemetryEnabled)
                    {
                        _ = TelemetryClient?.ReportHealthAsync(
                                "ObserverHealthReport",
                                HealthState.Warning,
                                msg,
                                ClusterObserverConstants.ClusterObserverManagerName,
                                token);
                    }

                    if (EtwEnabled)
                    {
                        Logger.LogEtw(
                            ClusterObserverConstants.ClusterObserverETWEventName,
                            new
                            {
                                HealthState = "Warning",
                                HealthEventDescription = msg,
                                Metric = "ClusterObserverServiceHealth",
                                Source = ClusterObserverConstants.ClusterObserverName
                            });
                    }

                    throw;
                }

                IsObserverRunning = false;
            }
        }

        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            try
            {
                Logger.LogWarning("Application Parameter upgrade started...");

                appParamsUpdating = true;
                await StopAsync(isAppParamUpdate: true);
                var newSettings = e.NewPackage.Settings;

                // ClusterObserverManager settings.
                SetPropertiesFromConfigurationParameters(newSettings);

                // ClusterObserver and plugin observer settings.
                foreach (var observer in Observers)
                {
                    string configSectionName = observer.ConfigurationSettings.ConfigSection.Name;
                    observer.ConfigPackage = e.NewPackage;
                    observer.ConfigurationSettings = new ConfigSettings(newSettings, configSectionName);
                    observer.InitializeObserverLoggingInfra(isConfigUpdate: true);

                    // Reset last run time so the observer restarts (if enabled) after the app parameter update completes.
                    observer.LastRunDateTime = DateTime.MinValue;
                }

                // Refresh CO CancellationTokenSources.
                cts = new CancellationTokenSource();
                linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, this.token);
                Logger.LogWarning("Application Parameter upgrade completed...");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var healthReport = new HealthReport
                {
                    AppName = new Uri(FabricServiceContext.CodePackageActivationContext.ApplicationName),
                    Code = FOErrorWarningCodes.Ok,
                    EntityType = EntityType.Application,
                    HealthMessage = $"Error updating ClusterObserver with new configuration settings:{Environment.NewLine}{ex}",
                    NodeName = FabricServiceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    Property = "CO_Configuration_Upate_Error",
                    EmitLogEvent = true
                };

                ObserverHealthReporter healthReporter = new(Logger);
                healthReporter.ReportHealthToServiceFabric(healthReport);
            }
            finally
            {
                appParamsUpdating = false;
            }
        }

        private void Dispose(bool disposing)
        {
            if (hasDisposed || !disposing)
            {
                return;
            }

            if (IsObserverRunning)
            {
                StopAsync().GetAwaiter().GetResult();
            }

            if (cts != null)
            {
                cts.Dispose();
                cts = null;
            }

            if (linkedSFRuntimeObserverTokenSource != null)
            {
                linkedSFRuntimeObserverTokenSource.Dispose();
                linkedSFRuntimeObserverTokenSource = null;
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
