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
        private const string InternalVersionNumber = "2.1.16";

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

            // This logs error/warning/info messages for ObserverManager.
            Logger = new Logger(ClusterObserverConstants.ClusterObserverManagerName, logFolderBasePath);
            SetPropertiesFromConfigurationParameters();
        }

        private static string GetConfigSettingValue(string parameterName, ConfigurationSettings settings, string sectionName = null)
        {
            try
            {
                ConfigurationSettings configSettings = null;

                if (sectionName == null)
                {
                    sectionName = ClusterObserverConstants.ObserverManagerConfigurationSectionName;
                }

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

        private void SetPropertiesFromConfigurationParameters(ConfigurationSettings settings = null)
        {
            // Observer
            if (int.TryParse(GetConfigSettingValue(ClusterObserverConstants.ObserverExecutionTimeoutParameter, settings), out int result))
            {
                observerExecTimeout = TimeSpan.FromSeconds(result);
            }

            // Logger
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter, settings), out bool enableVerboseLogging))
            {
                Logger.EnableVerboseLogging = enableVerboseLogging;
            }

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

            if (TelemetryEnabled)
            {
                string telemetryProviderType = GetConfigSettingValue(ClusterObserverConstants.TelemetryProviderTypeParameter, settings);

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
                    
                        string logAnalyticsLogType = GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter, settings) ?? "Application";
                        string logAnalyticsSharedKey = GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter, settings);
                        string logAnalyticsWorkspaceId = GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter, settings);

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
                    
                        string aiKey = GetConfigSettingValue(ObserverConstants.AiKey, settings);

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
                // This data is sent once over the lifetime of the deployed service instance and will be retained for no more
                // than 90 days.
                if (EnableOperationalTelemetry && !internalTelemetrySent)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(FabricServiceContext);
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
                    catch
                    {
                        // Telemetry is non-critical and should not take down CO.
                        // TelemetryLib will log exception details to file in top level FO log folder.
                    }
                }

                while (true)
                {
                    if (!appParamsUpdating && (shutdownSignaled || token.IsCancellationRequested))
                    {
                        Logger.LogInfo("Shutdown signaled. Stopping.");
                        await StopAsync().ConfigureAwait(false);
                        break;
                    }

                    await RunAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds > 0 ? ObserverExecutionLoopSleepSeconds : 15), token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
            {
                if (!appParamsUpdating && (shutdownSignaled || token.IsCancellationRequested))
                {
                    await StopAsync().ConfigureAwait(false);
                }
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
                            HealthState.Warning,
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
                                HealthState = "Warning",
                                HealthEventDescription = message,
                                Metric = "ClusterObserverServiceHealth",
                                Source = ClusterObserverConstants.ClusterObserverName
                            });
                }

                // Operational telemetry sent to FO developer for use in understanding generic behavior of FO in the real world (no PII)
                if (EnableOperationalTelemetry)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(FabricServiceContext);

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
                    catch
                    {
                        // Telemetry is non-critical and should not take down FO.
                    }
                }

                // Don't swallow the unhandled exception. Fix the bug.
                throw;
            }
        }

        private ClusterObserverOperationalEventData GetClusterObserverInternalTelemetryData()
        {
            ClusterObserverOperationalEventData telemetryData = null;

            try
            {
                telemetryData = new ClusterObserverOperationalEventData
                {
                    Version = InternalVersionNumber
                };
            }
            catch (Exception e) when (e is ArgumentException)
            {

            }

            return telemetryData;
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
            catch (Exception e) when (e is AggregateException || e is ObjectDisposedException)
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
                    Logger.LogInfo($"Starting {observer.ObserverName}");
                    IsObserverRunning = true;

                    // Synchronous call.
                    bool isCompleted = observer.ObserveAsync(linkedSFRuntimeObserverTokenSource != null ? linkedSFRuntimeObserverTokenSource.Token : token).Wait(observerExecTimeout);

                    // The observer is taking too long (hung?)
                    if (!isCompleted)
                    {
                        string observerHealthWarning = $"{observer.ObserverName} has exceeded its specified run time of {observerExecTimeout.TotalSeconds} seconds. Aborting.";
                        await SignalAbortToRunningObserverAsync().ConfigureAwait(false);

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
                                    Metric = "ClusterObserverServiceHealth",
                                    Source = ClusterObserverConstants.ClusterObserverName
                                });
                        }
                    }

                }
                catch (AggregateException ae)
                {
                    foreach (var e in ae.Flatten().InnerExceptions)
                    {
                        if (e is OperationCanceledException || e is TaskCanceledException)
                        {
                            if (appParamsUpdating)
                            {
                                // Exit. CO is processing a versionless parameter-only application upgrade.
                                return;
                            }

                            // CO will fail. Gracefully.
                        }
                        else if (e is FabricException || e is TimeoutException)
                        {
                            // These are transient and will have already been logged.
                        }
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
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

        /// <summary>
        /// App parameter config update handler. This will recreate CO instance with new ConfigSettings applied.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            appParamsUpdating = true;
            Logger.LogWarning("Application Parameter upgrade started...");

            try
            {
                
                await StopAsync().ConfigureAwait(false);

                // Observer settings.
                foreach (var observer in Observers)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    observer.ConfigurationSettings = new ConfigSettings(e.NewPackage.Settings, $"{observer.ObserverName}Configuration");

                    // The ObserverLogger instance (member of each observer type) checks its EnableVerboseLogging setting before writing Info events (it won't write if this setting is false, thus non-verbose).
                    // So, we set it here in case the parameter update includes a change to this config setting. 
                    if (e.NewPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters.Contains(ObserverConstants.EnableVerboseLoggingParameter)
                        && e.OldPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters.Contains(ObserverConstants.EnableVerboseLoggingParameter))
                    {
                        string newLoggingSetting = e.NewPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters[ObserverConstants.EnableVerboseLoggingParameter].Value.ToLower();
                        string oldLoggingSetting = e.OldPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters[ObserverConstants.EnableVerboseLoggingParameter].Value.ToLower();

                        if (newLoggingSetting != oldLoggingSetting)
                        {
                            observer.ObserverLogger.EnableVerboseLogging = observer.ConfigurationSettings.EnableVerboseLogging;
                        }
                    }
                }

                // ClusterObserverManager settings.
                SetPropertiesFromConfigurationParameters(e.NewPackage.Settings);

                cts ??= new CancellationTokenSource();
                linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token);
            }
            catch (Exception err)
            {
                var healthReport = new HealthReport
                {
                    AppName = new Uri(FabricServiceContext.CodePackageActivationContext.ApplicationName),
                    Code = FOErrorWarningCodes.Ok,
                    EntityType = EntityType.Application,
                    HealthMessage = $"Error updating ClusterObserver with new configuration settings:{Environment.NewLine}{err}",
                    NodeName = FabricServiceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    Property = "CO_Configuration_Upate_Error",
                    EmitLogEvent = true
                };

                ObserverHealthReporter healthReporter = new ObserverHealthReporter(Logger);
                healthReporter.ReportHealthToServiceFabric(healthReport);
            }

            Logger.LogWarning("Application Parameter upgrade completed...");
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
