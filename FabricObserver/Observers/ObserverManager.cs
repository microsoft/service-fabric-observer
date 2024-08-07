// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using FabricObserver.TelemetryLib;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;
using Octokit;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime;
using FabricObserver.Utilities.ServiceFabric;
using ConfigurationSettings = System.Fabric.Description.ConfigurationSettings;
using System.Runtime.Versioning;

namespace FabricObserver.Observers
{
    // This class manages the lifetime of all observers.
    public sealed class ObserverManager
    { 
        private static ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        private List<ObserverBase> Observers
        {
            get; set;
        }

        private const string LVIDCounterName = "Long-Value Maximum LID";
        private readonly string nodeName;
        private readonly TimeSpan OperationalTelemetryRunInterval = TimeSpan.FromDays(1);
        private readonly TimeSpan NewReleaseCheckInterval = TimeSpan.FromDays(7);
        private readonly CancellationToken runAsyncToken;
        private readonly string sfVersion;
        private readonly ConfigurationPackage configurationPackage;
        private volatile bool shutdownSignaled;
        private DateTime StartDateTime;
        private bool isConfigurationUpdateInProgress;
        private CancellationTokenSource cts;
        private CancellationTokenSource linkedSFRuntimeObserverTokenSource;

        // Folks often use their own version numbers. This is for internal diagnostic telemetry.
        private const string InternalVersionNumber = "3.3.0";

        private static FabricClient FabricClientInstance => FabricClientUtilities.FabricClientSingleton;

        private bool RuntimeTokenCancelled =>
            linkedSFRuntimeObserverTokenSource?.Token.IsCancellationRequested ?? runAsyncToken.IsCancellationRequested;

        private int ObserverExecutionLoopSleepSeconds
        {
            get; set;
        }

        private bool FabricObserverOperationalTelemetryEnabled
        {
            get; set;
        }

        private ObserverHealthReporter HealthReporter
        {
            get;
        }

        private Logger Logger
        {
            get;
        }

        private TimeSpan ObserverExecutionTimeout
        {
            get; set;
        } = TimeSpan.FromMinutes(30);

        private DateTime LastTelemetrySendDate
        {
            get; set;
        }

        private DateTime LastVersionCheckDateTime 
        { 
            get; set; 
        }

        public static StatelessServiceContext FabricServiceContext
        {
            get; set;
        }

        public static bool TelemetryProviderEnabled
        {
            get; set;
        }

        public static bool IsLvidCounterEnabled
        {
            get; set;
        }

        public static bool EtwEnabled
        {
            get; set;
        }

        public static HealthState ObserverFailureHealthStateLevel
        {
            get; set;
        } = HealthState.Unknown;

        public string ApplicationName
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// </summary>
        /// <param name="serviceProvider">IServiceProvider for retrieving service instance.</param>
        /// <param name="token">Cancellation token.</param>
        public ObserverManager(IServiceProvider serviceProvider, CancellationToken token)
        {
            FabricServiceContext ??= serviceProvider.GetRequiredService<StatelessServiceContext>();
            runAsyncToken = token;
#if DEBUG
            runAsyncToken.Register(() => Logger.LogWarning("FabricObserver.RunAsync token cancellation signalled."));
#endif
            cts = new CancellationTokenSource();
            linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, runAsyncToken);
#if DEBUG
            cts.Token.Register(() => Logger.LogWarning("cts.Token token cancellation signalled."));
            linkedSFRuntimeObserverTokenSource.Token.Register(() => Logger.LogWarning("linkedSFRuntimeObserverTokenSource.Token token cancellation signalled."));
#endif   
            nodeName = FabricServiceContext.NodeContext.NodeName;
            sfVersion = GetServiceFabricRuntimeVersion();
            configurationPackage = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetConfigSettingValue(ObserverConstants.ObserverLogPathParameter, null);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "observer_logs");
                logFolderBasePath = logFolderBase;
            }

            _ = int.TryParse(GetConfigSettingValue(ObserverConstants.MaxArchivedLogFileLifetimeDaysParameter, null), out int maxArchivedLogFileLifetimeDays);
            _ = bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableETWProvider, null), out bool enableEtwProvider);
            _ = bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter, null), out bool enableVerboseLogging);
            EtwEnabled = enableEtwProvider;

            Logger = new Logger("ObserverManager", logFolderBasePath, maxArchivedLogFileLifetimeDays)
            {
                EnableETWLogging = enableEtwProvider,
                EnableVerboseLogging = enableVerboseLogging
            };

            SetPropertiesFromConfigurationParameters();
            Observers = serviceProvider.GetServices<ObserverBase>().ToList();
            HealthReporter = new ObserverHealthReporter(Logger);
            FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;
        }

        private string GetServiceFabricRuntimeVersion()
        {
            try
            {
                var config = ServiceFabricConfiguration.Instance;
                return config.FabricVersion.Trim();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (e is TaskCanceledException or OperationCanceledException)
                {
                    throw;
                }

                Logger.LogWarning($"GetServiceFabricRuntimeVersion failure: {e.Message}");
            }

            return null;
        }

        public async Task StartObserversAsync()
        {
            StartDateTime = DateTime.UtcNow;

            try
            {
                // Clear out any orphaned Error or Warning health reports left behind when FO ungracefully exited.
                FabricClientUtilities fabricClientUtilities = new(nodeName);
                await fabricClientUtilities.ClearFabricObserverHealthReportsAsync(true, CancellationToken.None);

                // Nothing to do here.
                if (Observers.Count == 0)
                {
                    return;
                }

                // Continue running until a shutdown signal is sent
                Logger.LogInfo("StartObserversAsync: Starting Observers loop.");

                // Observers run sequentially. See RunObservers impl.
                while (true)
                {
                    try
                    {
                        if (!isConfigurationUpdateInProgress && (shutdownSignaled || runAsyncToken.IsCancellationRequested))
                        {
                            await ShutDownAsync();
                            break;
                        }

                        await RunObserversAsync();

                        // Identity-agnostic internal operational telemetry sent to Service Fabric team (only) for use in
                        // understanding generic behavior of FH in the real world (no PII). This data is sent once a day and will be retained for no more
                        // than 90 days.
                        if (FabricObserverOperationalTelemetryEnabled && !(shutdownSignaled || runAsyncToken.IsCancellationRequested)
                            && DateTime.UtcNow.Subtract(LastTelemetrySendDate) >= OperationalTelemetryRunInterval)
                        {
                            try
                            {
                                using var telemetryEvents = new TelemetryEvents(nodeName);
                                var foData = GetFabricObserverInternalTelemetryData();

                                if (foData != null)
                                {
                                    string filepath = Path.Combine(Logger.LogFolderBasePath, $"fo_operational_telemetry.log");

                                    if (telemetryEvents.EmitFabricObserverOperationalEvent(foData, OperationalTelemetryRunInterval, filepath))
                                    {
                                        LastTelemetrySendDate = DateTime.UtcNow;
                                        ResetInternalErrorWarningDataCounters();
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                // Telemetry is non-critical and should *not* take down FO.
                                Logger.LogWarning($"Unable to send internal diagnostic telemetry: {ex.Message}");
                            }
                        }

                        // Check for new version once a day.
                        if (!(shutdownSignaled || runAsyncToken.IsCancellationRequested) && DateTime.UtcNow.Subtract(LastVersionCheckDateTime) >= NewReleaseCheckInterval)
                        {
                            await CheckGithubForNewVersionAsync();
                            LastVersionCheckDateTime = DateTime.UtcNow;
                        }

                        // Time to tale a nap before running observers again. 30 seconds is the minimum sleep time.
                        if (ObserverExecutionLoopSleepSeconds >= 30)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds), runAsyncToken);
                        }
                        else
                        {
                            // Prevent loop spinning. Let threads drain (in the case of AppObserver monitoring with concurrent Tasks). Be conservative here.
                            await Task.Delay(TimeSpan.FromSeconds(30), runAsyncToken);
                        }

                        // All observers have run at this point. Try and empty the trash now.
                        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                    }
                    catch (Exception e) when (e is FabricException or TimeoutException)
                    {

                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or TaskCanceledException)
            {
                if (!isConfigurationUpdateInProgress && (shutdownSignaled || runAsyncToken.IsCancellationRequested))
                {
                    await ShutDownAsync();
                }
            }
            catch (Exception e)
            {
                string handled = e is LinuxPermissionException ? "Handled LinuxPermissionException" : "Unhandled Exception";

                var message =
                    $"{handled} in {ObserverConstants.ObserverManagerName} on node " +
                    $"{nodeName}. Taking down FO process. " +
                    $"Error stack:{Environment.NewLine}{e}";

                Logger.LogError(message);
                await ShutDownAsync();

                // Telemetry.
                if (TelemetryProviderEnabled)
                {
                    var telemetryData = new NodeTelemetryData()
                    {
                        Description = message,
                        HealthState = HealthState.Error,
                        Metric = $"{ObserverConstants.FabricObserverName}_ServiceHealth",
                        NodeName = nodeName,
                        Source = ObserverConstants.ObserverManagerName
                    };

                    await TelemetryClient.ReportHealthAsync(telemetryData, CancellationToken.None);
                }

                // ETW.
                if (EtwEnabled)
                {
                    Logger.LogEtw(
                        ObserverConstants.FabricObserverETWEventName,
                        new
                        {
                            Description = message,
                            HealthState = "Error",
                            Metric = $"{ObserverConstants.FabricObserverName}_ServiceHealth",
                            NodeName = nodeName,
                            ObserverName = ObserverConstants.ObserverManagerName,
                            Source = ObserverConstants.FabricObserverName
                        });
                }

                // Operational telemetry sent to FO developer for use in understanding generic behavior of FO in the real world (no PII).
                if (FabricObserverOperationalTelemetryEnabled)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(nodeName);
                        var data = new CriticalErrorEventData
                        {
                            Source = ObserverConstants.ObserverManagerName,
                            ErrorMessage = e.Message,
                            ErrorStack = e.ToString(),
                            CrashTime = DateTime.UtcNow.ToString("o"),
                            Version = InternalVersionNumber
                        };
                        string filepath = Path.Combine(Logger.LogFolderBasePath, $"fo_critical_error_telemetry.log");
                        _ = telemetryEvents.EmitCriticalErrorEvent(data, ObserverConstants.FabricObserverName, filepath);
                    }
                    catch (Exception ex)
                    {
                        // Telemetry is non-critical and should not take down FO.
                        Logger.LogWarning($"Unable to send internal diagnostic telemetry: {ex.Message}");
                    }
                }

                // Don't swallow the unhandled exception.
                // Take down FO process. Fix the bug(s) or it may be by design (see LinuxPermissionException).
                throw;
            }
        }

        private void ResetInternalErrorWarningDataCounters()
        {
            // These props are only set for telemetry purposes. This does not remove err/warn state on an observer.
            foreach (var obs in Observers)
            {
                obs.CurrentErrorCount = 0;
                obs.CurrentWarningCount = 0;
            }
        }

        // Clear all existing FO health events during shutdown or update event.
        public async Task StopObserversAsync(bool isShutdownSignaled = true, bool isConfigurationUpdateLinux = false)
        {
            await SignalAbortToRunningObserverAsync(3);

            string configUpdateLinux = string.Empty;

            if (isConfigurationUpdateLinux)
            {
                configUpdateLinux =
                    $" Note: This is due to a configuration update which requires an FO process restart on Linux (with UD walk (one by one) and safety checks).{Environment.NewLine}" +
                    "The reason FO needs to be restarted as part of a parameter-only upgrade is due to the Linux Capabilities set FO employs not persisting across application upgrades (by design) " +
                    "even when the upgrade is just a configuration parameter update. In order to re-create the Capabilities set, FO's setup script must be re-run by SF. Restarting FO is therefore required here.";
            }

            // If the node goes down, for example, or the app is gracefully closed, then clear all existing error or health reports supplied by FO.
            await ClearHealthReportsAsync(configUpdateLinux);

            shutdownSignaled = isShutdownSignaled;

            if (!isConfigurationUpdateInProgress)
            {
                // Clear any ObserverManager warnings/errors.
                await RemoveObserverManagerHealthReportsAsync();
            }
        }

        public async Task ClearHealthReportsAsync(string configUpdateLinux)
        {
            HealthReport healthReport = new()
            {
                Code = FOErrorWarningCodes.Ok,
                HealthMessage = $"Clearing existing FabricObserver Health Reports as the service is stopping, starting, or updating.{configUpdateLinux}.",
                State = HealthState.Ok,
                NodeName = nodeName,
                HealthReportTimeToLive = TimeSpan.FromSeconds(1)
            };

            foreach (ObserverBase observer in Observers)
            {
                try
                {
                    if (observer.ObserverName == ObserverConstants.ContainerObserverName)
                    {
                        ServiceHealth serviceHealth = await FabricClientInstance.HealthManager.GetServiceHealthAsync(FabricServiceContext.ServiceName);
                        IEnumerable<HealthEvent> fabricObserverServiceHealthEvents =
                            serviceHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(observer.ObserverName));

                        if (fabricObserverServiceHealthEvents != null && fabricObserverServiceHealthEvents.Any())
                        {
                            foreach (var evt in fabricObserverServiceHealthEvents)
                            {
                                try
                                {
                                    healthReport.ServiceName = FabricServiceContext.ServiceName;
                                    healthReport.EntityType = EntityType.Service;
                                    healthReport.Property = evt.HealthInformation.Property;
                                    healthReport.SourceId = evt.HealthInformation.SourceId;

                                    var healthReporter = new ObserverHealthReporter(Logger);
                                    healthReporter.ReportHealthToServiceFabric(healthReport);
                                }
                                catch (Exception e) when (e is FabricException or TimeoutException)
                                {

                                }
                            }
                        }
                    }
                    
                    if (observer.ObserverName == ObserverConstants.AppObserverName || observer.ObserverName == ObserverConstants.NetworkObserverName)
                    {
                        // Service Health reports.
                        if (observer.ServiceNames.Any(a => !string.IsNullOrWhiteSpace(a) && a.Contains("fabric:/")))
                        {
                            foreach (string service in observer.ServiceNames)
                            {
                                if (string.IsNullOrWhiteSpace(service))
                                {
                                    continue;
                                }

                                try
                                {
                                    // App Health reports. NetworkObserver only generates App health reports and stores app name in ServiceNames field (TODO: Change that).
                                    if (observer.ObserverName == ObserverConstants.NetworkObserverName)
                                    {
                                        Uri appName = new(service);
                                        var appHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(appName);
                                        var fabricObserverAppHealthEvents =
                                                appHealth?.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(observer.ObserverName));

                                        if (fabricObserverAppHealthEvents != null && fabricObserverAppHealthEvents.Any())
                                        {
                                            foreach (var evt in fabricObserverAppHealthEvents)
                                            {
                                                try
                                                {
                                                    healthReport.AppName = appName;
                                                    healthReport.EntityType = EntityType.Application;
                                                    healthReport.Property = evt.HealthInformation.Property;
                                                    healthReport.SourceId = evt.HealthInformation.SourceId;

                                                    var healthReporter = new ObserverHealthReporter(Logger);
                                                    healthReporter.ReportHealthToServiceFabric(healthReport);
                                                }
                                                catch (Exception e) when (e is FabricException or TimeoutException)
                                                {

                                                }
                                            }
                                        }
                                    }
                                    else // Service Health reports.
                                    {
                                        Uri serviceName = new(service);
                                        ServiceHealth serviceHealth = await FabricClientInstance.HealthManager.GetServiceHealthAsync(serviceName);
                                        IEnumerable<HealthEvent> fabricObserverServiceHealthEvents =
                                            serviceHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(observer.ObserverName));

                                        if (fabricObserverServiceHealthEvents != null && fabricObserverServiceHealthEvents.Any())
                                        {
                                            foreach (var evt in fabricObserverServiceHealthEvents)
                                            {
                                                try
                                                {
                                                    healthReport.ServiceName = serviceName;
                                                    healthReport.EntityType = EntityType.Service;
                                                    healthReport.Property = evt.HealthInformation.Property;
                                                    healthReport.SourceId = evt.HealthInformation.SourceId;

                                                    var healthReporter = new ObserverHealthReporter(Logger);
                                                    healthReporter.ReportHealthToServiceFabric(healthReport);
                                                }
                                                catch (Exception e) when (e is FabricException or TimeoutException)
                                                {

                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception e) when (e is FabricException or TimeoutException)
                                {

                                }
                            }
                        }
                    }

                    // System reports (fabric:/System).
                    if (observer.ObserverName == ObserverConstants.FabricSystemObserverName)
                    {
                        try
                        {
                            var sysAppHealth =
                                    await FabricClientInstance.HealthManager.GetApplicationHealthAsync(new Uri(ObserverConstants.SystemAppName));
                            var sysAppHealthEvents = sysAppHealth?.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(observer.ObserverName));

                            if (sysAppHealthEvents != null && sysAppHealthEvents.Any())
                            {
                                foreach (var evt in sysAppHealthEvents)
                                {
                                    try
                                    {
                                        healthReport.AppName = new Uri(ObserverConstants.SystemAppName);
                                        healthReport.Property = evt.HealthInformation.Property;
                                        healthReport.SourceId = evt.HealthInformation.SourceId;
                                        healthReport.EntityType = EntityType.Application;

                                        var healthReporter = new ObserverHealthReporter(Logger);
                                        healthReporter.ReportHealthToServiceFabric(healthReport);
                                    }
                                    catch (Exception e) when (e is FabricException or TimeoutException)
                                    {

                                    }
                                }
                            }
                        }
                        catch (Exception e) when (e is FabricException or TimeoutException)
                        {

                        }
                    }

                    // Node reports.
                    if (observer.ObserverName == ObserverConstants.CertificateObserverName ||
                        observer.ObserverName == ObserverConstants.DiskObserverName ||
                        observer.ObserverName == ObserverConstants.FabricSystemObserverName ||
                        observer.ObserverName == ObserverConstants.NodeObserverName ||
                        observer.ObserverName == ObserverConstants.OSObserverName)
                    {
                        try
                        {
                            var nodeHealth = await FabricClientInstance.HealthManager.GetNodeHealthAsync(observer.NodeName);
                            var fabricObserverNodeHealthEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(observer.ObserverName));

                            if (fabricObserverNodeHealthEvents != null && fabricObserverNodeHealthEvents.Any())
                            {
                                healthReport.EntityType = EntityType.Machine;

                                foreach (var evt in fabricObserverNodeHealthEvents)
                                {
                                    try
                                    {
                                        healthReport.Property = evt.HealthInformation.Property;
                                        healthReport.SourceId = evt.HealthInformation.SourceId;

                                        var healthReporter = new ObserverHealthReporter(Logger);
                                        healthReporter.ReportHealthToServiceFabric(healthReport);
                                    }
                                    catch (Exception e) when (e is FabricException or TimeoutException)
                                    {

                                    }
                                }
                            }
                        }
                        catch (Exception e) when (e is FabricException or TimeoutException)
                        {

                        }
                    }

                    // Reset warning/error states.
                    observer.HasActiveFabricErrorOrWarning = false;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {

                }

                // This only applies to AppObs, ContainerObs, NetworkObs, and FabricSystemObs.
                observer.ServiceNames.Clear();
            }
        }

        private async Task RemoveObserverManagerHealthReportsAsync()
        {
            var healthReport = new HealthReport
            {
                Code = FOErrorWarningCodes.Ok,
                HealthMessage = $"Clearing existing FabricObserver Health Reports as the service is stopping or updating.",
                State = HealthState.Ok,
                NodeName = nodeName
            };

            try
            {
                // FO
                ServiceHealth serviceHealth = await FabricClientInstance.HealthManager.GetServiceHealthAsync(FabricServiceContext.ServiceName);
                var obsMgrServiceHealthEvents = serviceHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(ObserverConstants.ObserverManagerName));
                
                if (obsMgrServiceHealthEvents != null && obsMgrServiceHealthEvents.Any())
                {
                    healthReport.EntityType = EntityType.Service;
                    healthReport.ServiceName = FabricServiceContext.ServiceName;

                    foreach (var obsMgrAppHealthEvent in obsMgrServiceHealthEvents)
                    {
                        try
                        {
                            healthReport.Property = obsMgrAppHealthEvent.HealthInformation.Property;
                            healthReport.SourceId = obsMgrAppHealthEvent.HealthInformation.SourceId;

                            var healthReporter = new ObserverHealthReporter(Logger);
                            healthReporter.ReportHealthToServiceFabric(healthReport);
                        }
                        catch (Exception e) when (e is FabricException or TimeoutException)
                        {

                        }
                    }
                }

                // Node Level
                var nodeHealth = await FabricClientInstance.HealthManager.GetNodeHealthAsync(FabricServiceContext.NodeContext.NodeName);
                var obsMgrNodeHealthEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(ObserverConstants.ObserverManagerName));

                if (obsMgrNodeHealthEvents != null && obsMgrNodeHealthEvents.Any())
                {
                    healthReport.AppName = null;
                    healthReport.EntityType = EntityType.Node;

                    foreach (var evt in obsMgrNodeHealthEvents)
                    {
                        try
                        {
                            healthReport.Property = evt.HealthInformation.Property;
                            healthReport.SourceId = evt.HealthInformation.SourceId;

                            var healthReporter = new ObserverHealthReporter(Logger);
                            healthReporter.ReportHealthToServiceFabric(healthReport);
                        }
                        catch (Exception e) when (e is FabricException or TimeoutException)
                        {

                        }
                    }
                }
            }
            catch (Exception e) when (e is FabricException or TimeoutException)
            {

            }
        }

        private string GetConfigSettingValue(string parameterName, ConfigurationSettings settings, string sectionName = null)
        {
            try
            {
                ConfigurationSettings configSettings = null;
                sectionName ??= ObserverConstants.ObserverManagerConfigurationSectionName;

                if (settings != null)
                {
                    configSettings = settings;
                }
                else
                {
                    configSettings = configurationPackage.Settings;
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

        internal async Task ShutDownAsync()
        {
            await StopObserversAsync();
            cts?.Dispose();
            linkedSFRuntimeObserverTokenSource?.Dispose();
            Logger.Flush();
            DataTableFileLogger.Flush();
            Logger.ShutDown();
            DataTableFileLogger.ShutDown();
        }

        /// <summary>
        /// This function gets FabricObserver's internal observer operational data for telemetry sent to Microsoft (no PII).
        /// Any data sent to Microsoft is also stored in a file in the observer_logs directory so you can see exactly what gets transmitted.
        /// You can enable/disable this at any time by setting EnableFabricObserverDiagnosticTelemetry to true/false in Settings.xml, ObserverManagerConfiguration section.
        /// </summary>
        private FabricObserverOperationalEventData GetFabricObserverInternalTelemetryData()
        {
            FabricObserverOperationalEventData telemetryData = null;

            try
            {
                // plugins
                bool hasPlugins = false;
                string pluginsDir = Path.Combine(FabricServiceContext.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

                if (!Directory.Exists(pluginsDir))
                {
                    hasPlugins = false;
                }
                else
                {
                    try
                    {
                        string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);
                        hasPlugins = pluginDlls.Length > 0;
                    }
                    catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PathTooLongException)
                    {

                    }
                }

                telemetryData = new FabricObserverOperationalEventData
                {
                    UpTime = DateTime.UtcNow.Subtract(StartDateTime).ToString(),
                    Version = InternalVersionNumber,
                    EnabledObserverCount = Observers.Count(obs => obs.IsEnabled),
                    HasPlugins = hasPlugins,
                    SFRuntimeVersion = sfVersion,
                    ObserverData = GetObserverData(),
                };
            }
            catch (ArgumentException)
            {

            }

            return telemetryData;
        }

        private Dictionary<string, ObserverData> GetObserverData()
        {
            var observerData = new Dictionary<string, ObserverData>();
            var enabledObs = Observers.Where(o => o.IsEnabled);
            string[] builtInObservers =
            [
                ObserverConstants.AppObserverName,
                ObserverConstants.AzureStorageUploadObserverName,
                ObserverConstants.CertificateObserverName,
                ObserverConstants.ContainerObserverName,
                ObserverConstants.DiskObserverName,
                ObserverConstants.FabricSystemObserverName,
                ObserverConstants.NetworkObserverName,
                ObserverConstants.NodeObserverName,
                ObserverConstants.OSObserverName
            ];

            foreach (var obs in enabledObs)
            {
                // We don't need to have any information about plugins besides whether or not there are any.
                if (!builtInObservers.Any(o => o == obs.ObserverName))
                {
                    continue;
                }

                // These built-in (non-plugin) observers monitor apps and/or services.
                if (obs.ObserverName is ObserverConstants.AppObserverName or
                    ObserverConstants.ContainerObserverName or
                    ObserverConstants.NetworkObserverName or
                    ObserverConstants.FabricSystemObserverName)
                {
                    if (!observerData.TryGetValue(obs.ObserverName, out ObserverData value))
                    {
                        _ = observerData.TryAdd(
                                obs.ObserverName,
                                new ObserverData
                                {
                                    ErrorCount = obs.CurrentErrorCount,
                                    WarningCount = obs.CurrentWarningCount,
                                    ServiceData = new ServiceData()
                                    {
                                        MonitoredAppCount = obs.MonitoredAppCount,
                                        MonitoredServiceProcessCount = obs.MonitoredServiceProcessCount
                                    }
                                });
                    }
                    else
                    {
                        value.ErrorCount = obs.CurrentErrorCount;
                        value.WarningCount = obs.CurrentWarningCount;
                        value.ServiceData =
                                new ServiceData
                                {
                                    MonitoredAppCount = obs.MonitoredAppCount,
                                    MonitoredServiceProcessCount = obs.MonitoredServiceProcessCount
                                };
                    }

                    // Concurrency
                    if (obs.ObserverName == ObserverConstants.AppObserverName)
                    {
                        observerData[ObserverConstants.AppObserverName].ServiceData.ConcurrencyEnabled = (obs as AppObserver).EnableConcurrentMonitoring;
                    }
                    else if (obs.ObserverName == ObserverConstants.ContainerObserverName)
                    {
                        observerData[ObserverConstants.ContainerObserverName].ServiceData.ConcurrencyEnabled = (obs as ContainerObserver).EnableConcurrentMonitoring;
                    }
                }
                else
                {
                    if (!observerData.ContainsKey(obs.ObserverName))
                    {
                        _ = observerData.TryAdd(
                                obs.ObserverName,
                                    new ObserverData
                                    {
                                        ErrorCount = obs.CurrentErrorCount,
                                        WarningCount = obs.CurrentWarningCount
                                    });
                    }
                    else
                    {
                        observerData[obs.ObserverName] =
                                 new ObserverData
                                 {
                                     ErrorCount = obs.CurrentErrorCount,
                                     WarningCount = obs.CurrentWarningCount
                                 };
                    }
                }
            }

            return observerData;
        }

        /// <summary>
        /// Event handler for application parameter updates (Un-versioned application parameter-only Application Upgrades).
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">Contains the information necessary for setting new config params from updated package.</param>
        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            try
            {
                Logger.LogWarning("Application Parameter upgrade started...");

                // For Linux, we need to restart the FO process due to the Linux Capabilities impl that enables us to run docker and netstat commands as elevated user (FO Linux should always be run as standard user on Linux).
                // During an upgrade event, SF touches the cap binaries which removes the cap settings so we need to run the FO app setup script again to reset them.
                if (OperatingSystem.IsLinux())
                {
                    // Graceful stop.
                    await StopObserversAsync(true, true).ConfigureAwait(false);

                    // Bye.
                    Environment.Exit(42);
                }

                isConfigurationUpdateInProgress = true;
                await StopObserversAsync(false).ConfigureAwait(false);
                var newSettings = e.NewPackage.Settings;

                // ObserverManager settings.
                SetPropertiesFromConfigurationParameters(newSettings);

                // Observer settings.
                foreach (var observer in Observers)
                {
                    string configSectionName = observer.ConfigurationSettings.ConfigSection.Name;
                    observer.ConfigPackage = e.NewPackage;
                    observer.ConfigurationSettings = new ConfigSettings(newSettings, configSectionName);
                    observer.InitializeObserverLoggingInfra(isConfigUpdate: true);
                    
                    // Reset last run time so the observer restarts (if enabled) after the app parameter update completes.
                    observer.LastRunDateTime = DateTime.MinValue;
                }

                // Refresh FO CancellationTokenSources.
                cts = new CancellationTokenSource();
                linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, runAsyncToken);
                Logger.LogWarning("Application Parameter upgrade completed...");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var healthReport = new HealthReport
                {
                    AppName = new Uri(FabricServiceContext.CodePackageActivationContext.ApplicationName),
                    Code = FOErrorWarningCodes.Ok,
                    EntityType = EntityType.Application,
                    HealthMessage = $"Error updating FabricObserver with new configuration settings:{Environment.NewLine}{ex}",
                    NodeName = FabricServiceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    Property = "FO_Configuration_Upate_Error",
                    EmitLogEvent = true
                };

                HealthReporter.ReportHealthToServiceFabric(healthReport);
            }
            finally
            {
                isConfigurationUpdateInProgress = false;
            }
        }

        /// <summary>
        /// Sets ObserverManager's related properties/fields to their corresponding Settings.xml or ApplicationManifest.xml (Overrides)
        /// configuration settings (parameter values).
        /// </summary>
        private void SetPropertiesFromConfigurationParameters(ConfigurationSettings settings = null)
        {
            ApplicationName = FabricServiceContext.CodePackageActivationContext.ApplicationName;

            // LVID monitoring. Windows-only feature.
            if (OperatingSystem.IsWindows())
            {
                IsLvidCounterEnabled = IsLVIDPerfCounterEnabled(settings);
            }

            // Maximum time, in seconds, that an observer can run - Override.
            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout, settings), out int timeoutSeconds))
            {
                ObserverExecutionTimeout = TimeSpan.FromSeconds(timeoutSeconds);
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
                Logger.MaxArchiveFileLifetimeDays = maxArchiveLifetimeDays > 0 ? maxArchiveLifetimeDays : 7;

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

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverLoopSleepTimeSeconds, settings), out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;
            }

            // FabricObserver operational telemetry (No PII) - Override
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableFabricObserverOperationalTelemetry, settings), out bool foTelemEnabled))
            {
                FabricObserverOperationalTelemetryEnabled = foTelemEnabled;
            }

            // ObserverFailure HealthState Level - Override \\

            string state = GetConfigSettingValue(ObserverConstants.ObserverFailureHealthStateLevelParameter, settings);

            if (string.IsNullOrWhiteSpace(state) || state?.ToLower() == "none")
            {
                ObserverFailureHealthStateLevel = HealthState.Unknown;
            }
            else if (Enum.TryParse(state, out HealthState healthState))
            {
                ObserverFailureHealthStateLevel = healthState;
            }

            // Telemetry (AppInsights, LogAnalytics, etc) - Override
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.TelemetryProviderEnabled, settings), out bool telemProviderEnabled))
            {
                TelemetryProviderEnabled = telemProviderEnabled;
            }

            if (!TelemetryProviderEnabled)
            {
                return;
            }

            string telemetryProviderType = GetConfigSettingValue(ObserverConstants.TelemetryProviderType, settings);

            if (string.IsNullOrEmpty(telemetryProviderType))
            {
                TelemetryProviderEnabled = false;
                return;
            }

            if (!Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
            {
                TelemetryProviderEnabled = false;
                return;
            }

            switch (telemetryProvider)
            {
                case TelemetryProviderType.AzureLogAnalytics:
                    
                    string logAnalyticsLogType = GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter, settings);
                    string logAnalyticsSharedKey = GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter, settings);
                    string logAnalyticsWorkspaceId = GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter, settings);

                    if (string.IsNullOrEmpty(logAnalyticsWorkspaceId) || string.IsNullOrEmpty(logAnalyticsSharedKey))
                    {
                        TelemetryProviderEnabled = false;
                        return;
                    }

                    TelemetryClient = new LogAnalyticsTelemetry(logAnalyticsWorkspaceId, logAnalyticsSharedKey, logAnalyticsLogType);
                    break;
                    
                case TelemetryProviderType.AzureApplicationInsights:
                    
                    string aiConnString = GetConfigSettingValue(ObserverConstants.AppInsightsConnectionString, settings);

                    if (string.IsNullOrEmpty(aiConnString))
                    {
                        TelemetryProviderEnabled = false;
                        return;
                    }

                    TelemetryClient = new AppInsightsTelemetry(aiConnString);
                    break;

                default:

                    TelemetryProviderEnabled = false;
                    break;
            }
        }


        /// <summary>
        /// This function will signal cancellation on the token passed to an observer's ObserveAsync. 
        /// This will eventually cause the observer to stop processing as this will throw an OperationCancelledException 
        /// in one of the observer's executing code paths.
        /// </summary>
        private async Task SignalAbortToRunningObserverAsync(int waitTimeSeconds = 0)
        {
            Logger.LogInfo("Signalling task cancellation to currently running Observer.");

            try
            {
                cts?.Cancel();

                if (waitTimeSeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(waitTimeSeconds));
                }
            }
            catch (ObjectDisposedException)
            {

            }

            Logger.LogInfo("Successfully signaled cancellation to currently running Observer.");
        }

        /// <summary>
        /// Runs all observers in a sequential loop.
        /// </summary>
        /// <returns>A boolean value indicating success of a complete observer loop run.</returns>
        private async Task RunObserversAsync()
        {
            foreach (var observer in Observers)
            {
                if (!observer.IsEnabled)
                {
                    continue;
                }

                // Don't run observers during a versionless, parameter-only app upgrade.
                if (isConfigurationUpdateInProgress)
                {
                    return;
                }

                try
                {
                    if (RuntimeTokenCancelled || shutdownSignaled)
                    {
                        return;
                    }

                    Logger.LogInfo($"Starting {observer.ObserverName}");

                    // Synchronous call.
                    bool isCompleted = 
                        observer.ObserveAsync(
                            linkedSFRuntimeObserverTokenSource?.Token ?? runAsyncToken).Wait(
                                (int)ObserverExecutionTimeout.TotalMilliseconds, linkedSFRuntimeObserverTokenSource?.Token ?? runAsyncToken);

                    // The observer is taking too long. Abort the run. Move on to next observer.
                    if (!isCompleted && !(RuntimeTokenCancelled || shutdownSignaled || isConfigurationUpdateInProgress))
                    {
                        string observerHealthWarning = $"{observer.ObserverName} on node {nodeName} did not complete successfully within the allotted time. Aborting run.";
                        Logger.LogWarning(observerHealthWarning);
                        await SignalAbortToRunningObserverAsync(10);

                        // Refresh FO CancellationTokenSources.
                        cts?.Dispose();
                        linkedSFRuntimeObserverTokenSource?.Dispose();
                        cts = new CancellationTokenSource();
                        linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, runAsyncToken);

                        // Telemetry.
                        if (TelemetryProviderEnabled)
                        {
                            var telemetryData = new NodeTelemetryData()
                            {
                                Description = observerHealthWarning,
                                HealthState = HealthState.Error,
                                Metric = $"{observer.ObserverName}_HealthState",
                                NodeName = nodeName,
                                ObserverName = ObserverConstants.ObserverManagerName,
                                Source = ObserverConstants.FabricObserverName
                            };

                            await TelemetryClient?.ReportHealthAsync(telemetryData, runAsyncToken);
                        }

                        // ETW.
                        if (EtwEnabled)
                        {
                            Logger.LogEtw(
                                    ObserverConstants.FabricObserverETWEventName,
                                    new
                                    {
                                        Description = observerHealthWarning,
                                        HealthState = "Error",
                                        Metric = $"{observer.ObserverName}_HealthState",
                                        NodeName = nodeName,
                                        ObserverName = ObserverConstants.ObserverManagerName,
                                        Source = ObserverConstants.FabricObserverName
                                    });
                        }

                        // Put FO into Warning or Error (health state is configurable in Settings.xml)
                        if (ObserverFailureHealthStateLevel != HealthState.Unknown)
                        {
                            var healthReport = new HealthReport
                            {
                                ServiceName = FabricServiceContext.ServiceName,
                                EmitLogEvent = false,
                                HealthMessage = observerHealthWarning,
                                HealthReportTimeToLive = TimeSpan.FromMinutes(5),
                                Property = $"{observer.ObserverName}_HealthState",
                                EntityType = EntityType.Service,
                                State = ObserverFailureHealthStateLevel,
                                NodeName = nodeName,
                                Observer = ObserverConstants.ObserverManagerName,
                            };

                            // Generate a Service Fabric Health Report.
                            HealthReporter.ReportHealthToServiceFabric(healthReport);
                        }

                        continue;
                    }

                    Logger.LogInfo($"Successfully ran {observer.ObserverName}.");
                }
                catch (AggregateException ae)
                {
                    foreach (var e in ae.Flatten().InnerExceptions)
                    {
                        if (e is LinuxPermissionException)
                        {
                            Logger.LogWarning(
                                $"Handled LinuxPermissionException: was thrown by {observer.ObserverName}. " +
                                $"Capabilities have been unset on caps binary (due to SF Cluster Upgrade, most likely). " +
                                $"This will restart FO (by design).{Environment.NewLine}{e.Message}");

                            throw e;
                        }
                        else if (e is OperationCanceledException or TaskCanceledException)
                        {
                            if (isConfigurationUpdateInProgress)
                            {
                                // Exit the loop and function. FO is processing a parameter-only versionless application upgrade.
                                return;
                            }

                            // FO will fail. Gracefully.
                        }
                        else if (e is FabricException or TimeoutException or Win32Exception)
                        {
                            // These are transient and details will have been logged by related observer when they happened.
                            Logger.LogWarning($"Handled error from {observer.ObserverName}: {e.Message}");
                        }
                    }
                }
                catch (Exception e) when (e is LinuxPermissionException)
                {
                    Logger.LogWarning(
                        $"Handled LinuxPermissionException: was thrown by {observer.ObserverName}. " +
                        $"Capabilities have been unset on caps binary (due to SF Cluster Upgrade, most likely). " +
                        $"This will restart FO (by design).{Environment.NewLine}{e.Message}");

                    throw;
                }
                catch (Exception e) when (e is OperationCanceledException or TaskCanceledException)
                {
                    if (isConfigurationUpdateInProgress)
                    {
                        // Don't proceed further. FO is processing a parameter-only versionless application upgrade. No observers should run.
                        return;
                    }
                }
                catch (Exception e) when (e is FabricException or TimeoutException or Win32Exception)
                {
                    // Transient.
                    Logger.LogWarning($"Handled error from {observer.ObserverName}{Environment.NewLine}{e}");
                }
                catch (Exception e) when (e is not LinuxPermissionException)
                {
                    Logger.LogError($"Unhandled Exception from {observer.ObserverName}:{Environment.NewLine}{e}");
                    throw;
                }
            }
        }

        // https://stackoverflow.com/questions/25678690/how-can-i-check-github-releases-in-c
        private async Task CheckGithubForNewVersionAsync()
        {
            try
            {
                GitHubClient githubClient = new(new ProductHeaderValue(ObserverConstants.FabricObserverName));
                Release latestRelease = await githubClient.Repository.Release.GetLatest("microsoft", "service-fabric-observer");

                if (latestRelease == null)
                {
                    return;
                }

                string releaseAssetName = latestRelease.Name;
                string latestVersion = releaseAssetName.Split(" ")[1];
                Version latestGitHubVersion = new(latestVersion);
                Version localVersion = new(InternalVersionNumber);
                int versionComparison = localVersion.CompareTo(latestGitHubVersion);

                if (versionComparison < 0)
                {
                    string message = $"A newer version of FabricObserver is available: <a href='https://github.com/microsoft/service-fabric-observer/releases' target='_blank'>{latestVersion}</a>";

                    var healthReport = new HealthReport
                    {
                        AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                        EmitLogEvent = false,
                        HealthMessage = message,
                        HealthReportTimeToLive = TimeSpan.FromDays(1),
                        Property = "NewVersionAvailable",
                        EntityType = EntityType.Application,
                        State = HealthState.Ok,
                        NodeName = nodeName,
                        Observer = ObserverConstants.ObserverManagerName
                    };

                    // Generate a Service Fabric Health Report.
                    HealthReporter.ReportHealthToServiceFabric(healthReport);

                    var telemetryData = new ServiceTelemetryData
                    {
                        ApplicationName = $"fabric:/{ObserverConstants.FabricObserverName}",
                        Description = message,
                        EntityType = EntityType.Application,
                        HealthState = HealthState.Ok,
                        Metric = "NewVersionAvailable",
                        NodeName = nodeName,
                        ObserverName = ObserverConstants.ObserverManagerName,
                        Source = ObserverConstants.FabricObserverName
                    };

                    // Telemetry.
                    if (TelemetryProviderEnabled)
                    {
                        await TelemetryClient?.ReportHealthAsync(telemetryData, runAsyncToken);
                    }

                    // ETW.
                    if (EtwEnabled)
                    {
                        Logger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                    }
                }

                latestRelease = null;
                githubClient = null;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Don't take down FO due to error in version check.
                Logger.LogWarning($"Failure checking Github for latest FO version: {e.Message}");
            }
        }

        [SupportedOSPlatform("windows")]
        private bool IsLVIDPerfCounterEnabled(ConfigurationSettings settings = null)
        {
            // We already figured this out the first time this function ran.
            if (IsLvidCounterEnabled)
            {
                // DEBUG
                Logger.LogInfo("IsLVIDPerfCounterEnabled: Counter has already been determined to be enabled. Not running the check again.");
                return true;
            }

            // Get AO and FSO LVID monitoring settings. During a versionless, parameter-only app upgrade, settings instance will contain the updated observer settings.
            _ = bool.TryParse(
                GetConfigSettingValue(ObserverConstants.EnableKvsLvidMonitoringParameter, settings, ObserverConstants.AppObserverConfigurationSectionName), out bool isLvidEnabledAO);

            _ = bool.TryParse(
                GetConfigSettingValue(ObserverConstants.EnableKvsLvidMonitoringParameter, settings, ObserverConstants.FabricSystemObserverConfigurationName), out bool isLvidEnabledFSO);

            // If neither AO nor FSO are configured to monitor LVID usage, then do not proceed; it doesn't matter and this check is not cheap.
            if (!isLvidEnabledAO && !isLvidEnabledFSO)
            {
                // DEBUG
                Logger.LogInfo("IsLVIDPerfCounterEnabled: Not running check since no supported observer is enabled for LVID monitoring.");
                return false;
            }

            // DEBUG
            Logger.LogInfo($"IsLVIDPerfCounterEnabled: Running check since a supported observer is enabled for LVID monitoring. Detected Fabric version is {sfVersion}");
            string categoryName = "Windows Fabric Database";

            if (sfVersion.StartsWith('1'))
            {
                categoryName = "MSExchange Database";
            }

            // DEBUG
            Logger.LogInfo($"IsLVIDPerfCounterEnabled: using '{categoryName}' for CategoryName.");

            // If there is corrupted state on the machine with respect to performance counters, an AV can occur (in native code, then wrapped in AccessViolationException)
            // when calling PerformanceCounterCategory.Exists below. This is actually a symptom of a problem that extends beyond just this counter category..
            // *Do not catch AV exception*. FO will crash, of course, but that is safer than pretending nothing is wrong.
            // To mitigate the issue in that case, you will need to restart the machine or rebuild performance counters manually. Other perf counters that FO relies on will most likely 
            // cause issues (not FO crashes necessarily, but inaccurate data related to the metrics they represent (like, you will always see 0 or -1 measurement values)).
            try
            {
                return PerformanceCounterCategory.CounterExists(LVIDCounterName, categoryName);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
            {
                Logger.LogWarning($"IsLVIDPerfCounterEnabled: Failed to determine LVID perf counter state: {e.Message}. Detected SF Runtime Version: {sfVersion}");
            }

            return false;
        }
    }
}