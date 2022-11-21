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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using FabricObserver.TelemetryLib;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;
using System.Fabric.Description;
using Octokit;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime;
using FabricObserver.Utilities.ServiceFabric;

namespace FabricObserver.Observers
{
    // This class manages the lifetime of all observers.
    public sealed class ObserverManager : IDisposable
    { 
        private static ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        private readonly string nodeName;
        private readonly TimeSpan OperationalTelemetryRunInterval = TimeSpan.FromDays(1);
        private readonly CancellationToken token;
        private readonly List<ObserverBase> observers;
        private readonly string sfVersion;
        private readonly bool isWindows;
        private volatile bool shutdownSignaled;
        private DateTime StartDateTime;
        private bool disposed;
        private bool isConfigurationUpdateInProgress;
        private CancellationTokenSource cts;
        private CancellationTokenSource linkedSFRuntimeObserverTokenSource;

        // Folks often use their own version numbers. This is for internal diagnostic telemetry.
        private const string InternalVersionNumber = "3.2.4.831";

        private bool RuntimeTokenCancelled =>
            linkedSFRuntimeObserverTokenSource?.Token.IsCancellationRequested ?? token.IsCancellationRequested;

        private int ObserverExecutionLoopSleepSeconds
        {
            get; set;
        } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;


        private bool FabricObserverOperationalTelemetryEnabled
        {
            get; set;
        }

        private ObserverHealthReporter HealthReporter
        {
            get;
        }

        private string Fqdn
        {
            get; set;
        }

        private Logger Logger
        {
            get;
        }

        private TimeSpan ObserverExecutionTimeout
        {
            get; set;
        } = TimeSpan.FromMinutes(30);

        private int MaxArchivedLogFileLifetimeDays
        {
            get;
        }

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

        public static FabricClient FabricClientInstance => FabricClientUtilities.FabricClientSingleton;

        public static bool TelemetryEnabled
        {
            get; set;
        }

        public static bool IsLvidCounterEnabled
        {
            get; set;
        }

        public static bool ObserverWebAppDeployed
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
            this.token = token;
            cts = new CancellationTokenSource();
            linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, this.token);
            FabricServiceContext ??= serviceProvider.GetRequiredService<StatelessServiceContext>();
            nodeName = FabricServiceContext.NodeContext.NodeName;
            FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            sfVersion = GetServiceFabricRuntimeVersion();

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

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.MaxArchivedLogFileLifetimeDaysParameter, null), out int maxArchivedLogFileLifetimeDays))
            {
                MaxArchivedLogFileLifetimeDays = maxArchivedLogFileLifetimeDays;
            }

            Logger = new Logger("ObserverManager", logFolderBasePath, MaxArchivedLogFileLifetimeDays);
            SetPropertiesFromConfigurationParameters();
            observers = serviceProvider.GetServices<ObserverBase>().ToList();
            HealthReporter = new ObserverHealthReporter(Logger);
        }

        private string GetServiceFabricRuntimeVersion()
        {
            try
            {
                var config = ServiceFabricConfiguration.Instance;
                return config.FabricVersion;
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                Logger.LogWarning($"GetServiceFabricRuntimeVersion failure:{Environment.NewLine}{e.Message}");
            }

            return null;
        }

        public async Task StartObserversAsync()
        {
            StartDateTime = DateTime.UtcNow;

            try
            {
                // Nothing to do here.
                if (observers.Count == 0)
                {
                    return;
                }

                // Continue running until a shutdown signal is sent
                Logger.LogInfo("Starting Observers loop.");

                // Observers run sequentially. See RunObservers impl.
                while (true)
                {
                    if (!isConfigurationUpdateInProgress && (shutdownSignaled || token.IsCancellationRequested))
                    {
                        await ShutDownAsync();
                        break;
                    }

                    await RunObserversAsync();

                    // Identity-agnostic internal operational telemetry sent to Service Fabric team (only) for use in
                    // understanding generic behavior of FH in the real world (no PII). This data is sent once a day and will be retained for no more
                    // than 90 days.
                    if (FabricObserverOperationalTelemetryEnabled && !(shutdownSignaled || token.IsCancellationRequested)
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
                        catch (Exception ex)
                        {
                            // Telemetry is non-critical and should *not* take down FO.
                            Logger.LogWarning($"Unable to send internal diagnostic telemetry:{Environment.NewLine}{ex}");
                        }
                    }

                    // Check for new version once a day.
                    if (!(shutdownSignaled || token.IsCancellationRequested) && DateTime.UtcNow.Subtract(LastVersionCheckDateTime) >= OperationalTelemetryRunInterval)
                    {
                        await CheckGithubForNewVersionAsync();
                        LastVersionCheckDateTime = DateTime.UtcNow;
                    }

                    if (ObserverExecutionLoopSleepSeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds), token);
                    }
                    else if (observers.Count == 1)
                    {
                        // This protects against loop spinning when you run FO with one observer enabled and no sleep time set.
                        await Task.Delay(TimeSpan.FromSeconds(15), token);
                    }

                    // All observers have run at this point. Try and empty the trash now.
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
            {
                if (!isConfigurationUpdateInProgress && (shutdownSignaled || token.IsCancellationRequested))
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
                    $"Error info:{Environment.NewLine}{e}";

                Logger.LogError(message);
                await ShutDownAsync();

                // Telemetry.
                if (TelemetryEnabled)
                {
                    var telemetryData = new TelemetryData()
                    {
                        Description = message,
                        HealthState = HealthState.Error,
                        Metric = $"{ObserverConstants.FabricObserverName}_ServiceHealth",
                        NodeName = nodeName,
                        ObserverName = ObserverConstants.ObserverManagerName,
                        Source = ObserverConstants.FabricObserverName
                    };

                    await TelemetryClient.ReportHealthAsync(telemetryData, token);
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

                // Operational telemetry sent to FO developer for use in understanding generic behavior of FO in the real world (no PII)
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
                        Logger.LogWarning($"Unable to send internal diagnostic telemetry:{Environment.NewLine}{ex}");
                        // Telemetry is non-critical and should not take down FO. Will not throw here.
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
            foreach (var obs in observers)
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
            foreach (var obs in observers)
            {
                var healthReport = new HealthReport
                {
                    Code = FOErrorWarningCodes.Ok,
                    HealthMessage = $"Clearing existing FabricObserver Health Reports as the service is stopping or updating.{configUpdateLinux}.",
                    State = HealthState.Ok,
                    NodeName = obs.NodeName
                };

                // Service Health reports.
                if (obs.ServiceNames.Any(a => !string.IsNullOrWhiteSpace(a) && a.Contains("fabric:/")))
                {
                    foreach (var service in obs.ServiceNames)
                    {
                        try
                        {
                            // App Health reports. NetworkObserver only generates App health reports and stores app name in ServiceNames field (TODO: Change that).
                            if (obs.ObserverName == ObserverConstants.NetworkObserverName)
                            {
                                Uri appName = new Uri(service);
                                var appHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(appName);
                                var fabricObserverAppHealthEvents = appHealth?.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

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

                                            await Task.Delay(150);
                                        }
                                        catch (FabricException)
                                        {

                                        }
                                    }
                                }
                            }
                            else // Service Health reports.
                            {
                                Uri serviceName = new Uri(service);
                                ServiceHealth serviceHealth = await FabricClientInstance.HealthManager.GetServiceHealthAsync(serviceName);
                                IEnumerable<HealthEvent> fabricObserverServiceHealthEvents =
                                    serviceHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

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

                                            await Task.Delay(150);
                                        }
                                        catch (FabricException)
                                        {

                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception e) when (e is FabricException || e is TimeoutException)
                        {

                        }
                    }
                }
                
                // FSO Health Reports.
                if (obs.ObserverName == ObserverConstants.FabricSystemObserverName)
                {
                    try
                    {
                        // System app reports.
                        var sysAppHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(new Uri(ObserverConstants.SystemAppName));
                        var sysAppHealthEvents = sysAppHealth?.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

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

                                    await Task.Delay(150);
                                }
                                catch (FabricException)
                                {

                                }
                            }
                        }
                    }
                    catch (Exception e) when (e is FabricException || e is TimeoutException)
                    {

                    }
                }
                
                // Node health reports. This will remove FSO's and OSO's informational reports.
                try
                {
                    // Node reports.
                    var nodeHealth = await FabricClientInstance.HealthManager.GetNodeHealthAsync(obs.NodeName);
                    var fabricObserverNodeHealthEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

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

                                await Task.Delay(150);
                            }
                            catch (FabricException)
                            {

                            }
                        }
                    }
                }
                catch (Exception e) when (e is FabricException || e is TimeoutException)
                {

                }

                obs.HasActiveFabricErrorOrWarning = false;
            }

            shutdownSignaled = isShutdownSignaled;

            if (!isConfigurationUpdateInProgress)
            {
                // Clear any ObserverManager warnings/errors.
                await RemoveObserverManagerHealthReportsAsync();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                linkedSFRuntimeObserverTokenSource?.Dispose();
                cts?.Dispose();
                FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent -= CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            }

            disposed = true;
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

                            await Task.Delay(150);
                        }
                        catch (FabricException)
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

                            await Task.Delay(150);
                        }
                        catch (FabricException)
                        {

                        }
                    }
                }
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {

            }
        }

        private bool IsObserverWebApiAppInstalled()
        {
            try
            {
                var deployedObsWebApps =
                        FabricClientInstance.QueryManager.GetDeployedApplicationListAsync(
                            nodeName,
                            new Uri("fabric:/FabricObserverWebApi"),
                            TimeSpan.FromSeconds(30),
                            token).GetAwaiter().GetResult();

                return deployedObsWebApps?.Count > 0;
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is TimeoutException)
            {

            }

            return false;
        }

        private static string GetConfigSettingValue(string parameterName, ConfigurationSettings settings, string sectionName = null)
        {
            try
            {
                ConfigurationSettings configSettings = null;
                
                if (sectionName == null)
                {
                    sectionName = ObserverConstants.ObserverManagerConfigurationSectionName;
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

        private async Task ShutDownAsync()
        {
            await StopObserversAsync();

            if (cts != null)
            {
                cts.Dispose();
                cts = null;
            }

            // Flush and Dispose all NLog targets. No more logging.
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
                    catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException || e is PathTooLongException)
                    {

                    }
                }

                telemetryData = new FabricObserverOperationalEventData
                {
                    UpTime = DateTime.UtcNow.Subtract(StartDateTime).ToString(),
                    Version = InternalVersionNumber,
                    EnabledObserverCount = observers.Count(obs => obs.IsEnabled),
                    HasPlugins = hasPlugins,
                    ParallelExecutionCapable = Environment.ProcessorCount >= 4,
                    SFRuntimeVersion = sfVersion,
                    ObserverData = GetObserverData(),
                };
            }
            catch (Exception e) when (e is ArgumentException)
            {

            }

            return telemetryData;
        }

        private Dictionary<string, ObserverData> GetObserverData()
        {
            var observerData = new Dictionary<string, ObserverData>();
            var enabledObs = observers.Where(o => o.IsEnabled);
            string[] builtInObservers = new string[]
            {
                ObserverConstants.AppObserverName,
                ObserverConstants.AzureStorageUploadObserverName,
                ObserverConstants.CertificateObserverName,
                ObserverConstants.ContainerObserverName,
                ObserverConstants.DiskObserverName,
                ObserverConstants.FabricSystemObserverName,
                ObserverConstants.NetworkObserverName,
                ObserverConstants.NodeObserverName,
                ObserverConstants.OSObserverName,
                ObserverConstants.SFConfigurationObserverName
            };

            foreach (var obs in enabledObs)
            {
                // We don't need to have any information about plugins besides whether or not there are any.
                if (!builtInObservers.Any(o => o == obs.ObserverName))
                {
                    continue;
                }

                // These built-in (non-plugin) observers monitor apps and/or services.
                if (obs.ObserverName == ObserverConstants.AppObserverName ||
                    obs.ObserverName == ObserverConstants.ContainerObserverName ||
                    obs.ObserverName == ObserverConstants.NetworkObserverName ||
                    obs.ObserverName == ObserverConstants.FabricSystemObserverName)
                {
                    if (!observerData.ContainsKey(obs.ObserverName))
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
                        observerData[obs.ObserverName].ErrorCount = obs.CurrentErrorCount;
                        observerData[obs.ObserverName].WarningCount = obs.CurrentWarningCount;
                        observerData[obs.ObserverName].ServiceData =
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
            Logger.LogWarning("Application Parameter upgrade started...");

            try
            {
                // For Linux, we need to restart the FO process due to the Linux Capabilities impl that enables us to run docker and netstat commands as elevated user (FO Linux should always be run as standard user on Linux).
                // During an upgrade event, SF touches the cap binaries which removes the cap settings so we need to run the FO app setup script again to reset them.
                if (!isWindows)
                {
                    // Graceful stop.
                    await StopObserversAsync(true, true);

                    // Bye.
                    Environment.Exit(42);
                }

                isConfigurationUpdateInProgress = true;
                await StopObserversAsync(false);

                // ObserverManager settings.
                SetPropertiesFromConfigurationParameters(e.NewPackage.Settings);
            }
            catch (Exception err)
            {
                var healthReport = new HealthReport
                {
                    AppName = new Uri(FabricServiceContext.CodePackageActivationContext.ApplicationName),
                    Code = FOErrorWarningCodes.Ok,
                    EntityType = EntityType.Application,
                    HealthMessage = $"Error updating FabricObserver with new configuration settings:{Environment.NewLine}{err}",
                    NodeName = FabricServiceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    Property = "Configuration_Upate_Error",
                    EmitLogEvent = true
                };

                HealthReporter.ReportHealthToServiceFabric(healthReport);
            }

            isConfigurationUpdateInProgress = false;

            // Refresh FO CancellationTokenSources.
            cts?.Dispose();
            linkedSFRuntimeObserverTokenSource?.Dispose();
            cts = new CancellationTokenSource();
            linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token);
            Logger.LogWarning("Application Parameter upgrade completed...");
        }

        /// <summary>
        /// Sets ObserverManager's related properties/fields to their corresponding Settings.xml or ApplicationManifest.xml (Overrides)
        /// configuration settings (parameter values).
        /// </summary>
        private void SetPropertiesFromConfigurationParameters(ConfigurationSettings settings = null)
        {
            ApplicationName = FabricServiceContext.CodePackageActivationContext.ApplicationName;

            // LVID monitoring.
            if (isWindows)
            {
                IsLvidCounterEnabled = IsLVIDPerfCounterEnabled(settings);
            }

            // ETW - Overridable
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableETWProvider, settings), out bool etwEnabled))
            {
                EtwEnabled = etwEnabled;

                if (Logger != null)
                {
                    Logger.EnableETWLogging = etwEnabled;
                }
            }

            // Maximum time, in seconds, that an observer can run - Override.
            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout, settings), out int timeoutSeconds))
            {
                ObserverExecutionTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            // ObserverManager verbose logging - Override.
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter, settings), out bool enableVerboseLogging))
            {
                if (Logger != null)
                {
                    Logger.EnableVerboseLogging = enableVerboseLogging;
                }
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverLoopSleepTimeSeconds, settings), out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;
            }

            // FQDN for use in warning or error hyperlinks in HTML output
            // This only makes sense when you have the FabricObserverWebApi app installed.
            string fqdn = GetConfigSettingValue(ObserverConstants.Fqdn, settings);

            if (!string.IsNullOrEmpty(fqdn))
            {
                Fqdn = fqdn;
            }

            // FabricObserver operational telemetry (No PII) - Override
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableFabricObserverOperationalTelemetry, settings), out bool foTelemEnabled))
            {
                FabricObserverOperationalTelemetryEnabled = foTelemEnabled;
            }

            // ObserverWebApi.
            ObserverWebAppDeployed = bool.TryParse(GetConfigSettingValue(ObserverConstants.ObserverWebApiEnabled, settings), out bool obsWeb) && obsWeb && IsObserverWebApiAppInstalled();

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
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.TelemetryEnabled, settings), out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;
            }

            if (!TelemetryEnabled)
            {
                return;
            }

            string telemetryProviderType = GetConfigSettingValue(ObserverConstants.TelemetryProviderType, settings);

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
                    
                    string logAnalyticsLogType = GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter, settings);
                    string logAnalyticsSharedKey = GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter, settings);
                    string logAnalyticsWorkspaceId = GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter, settings);

                    if (string.IsNullOrEmpty(logAnalyticsWorkspaceId) || string.IsNullOrEmpty(logAnalyticsSharedKey))
                    {
                        TelemetryEnabled = false;
                        return;
                    }

                    TelemetryClient = new LogAnalyticsTelemetry(
                                            logAnalyticsWorkspaceId,
                                            logAnalyticsSharedKey,
                                            logAnalyticsLogType);
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

                default:

                    TelemetryEnabled = false;
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
            foreach (var observer in observers)
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
                            linkedSFRuntimeObserverTokenSource?.Token ?? token).Wait(
                                (int)ObserverExecutionTimeout.TotalMilliseconds, linkedSFRuntimeObserverTokenSource?.Token ?? token);

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
                        linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token);

                        // Telemetry.
                        if (TelemetryEnabled)
                        {
                            var telemetryData = new TelemetryData()
                            {
                                Description = observerHealthWarning,
                                HealthState = HealthState.Error,
                                Metric = $"{observer.ObserverName}_HealthState",
                                NodeName = nodeName,
                                ObserverName = ObserverConstants.ObserverManagerName,
                                Source = ObserverConstants.FabricObserverName
                            };

                            await TelemetryClient?.ReportHealthAsync(telemetryData, token);
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

                    if (!ObserverWebAppDeployed)
                    {
                        continue;
                    }

                    if (observer.HasActiveFabricErrorOrWarning)
                    {
                        var errWarnMsg = !string.IsNullOrEmpty(Fqdn) ? $"<a style=\"font-weight: bold; color: red;\" href=\"http://{Fqdn}/api/ObserverLog/{observer.ObserverName}/{observer.NodeName}/json\">One or more errors or warnings detected</a>." : $"One or more errors or warnings detected. Check {observer.ObserverName} logs for details.";
                        Logger.LogWarning($"{observer.ObserverName}: " + errWarnMsg);
                    }
                    else
                    {
                        // Delete the observer's instance log (local file with Warn/Error details per run)..
                        _ = observer.ObserverLogger.TryDeleteInstanceLogFile();

                        try
                        {
                            if (File.Exists(Logger.FilePath))
                            {
                                // Replace the ObserverManager.log text that doesn't contain the observer Warn/Error line(s).
                                await File.WriteAllLinesAsync(
                                            Logger.FilePath,
                                            File.ReadLines(Logger.FilePath)
                                                .Where(line => !line.Contains(observer.ObserverName)).ToList(), token);
                            }
                        }
                        catch (IOException)
                        {

                        }
                    }
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
                        else if (e is OperationCanceledException || e is TaskCanceledException)
                        {
                            if (isConfigurationUpdateInProgress)
                            {
                                // Exit the loop and function. FO is processing a parameter-only versionless application upgrade.
                                return;
                            }

                            // FO will fail. Gracefully.
                        }
                        else if (e is FabricException || e is TimeoutException || e is Win32Exception)
                        {
                            // These are transient and will have been logged by related observer when they happened.
                            Logger.LogWarning($"Handled error from {observer.ObserverName}{Environment.NewLine}{e}");
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
                catch (Exception e) when (e is OperationCanceledException || e is TaskCanceledException)
                {
                    if (isConfigurationUpdateInProgress)
                    {
                        // Don't proceed further. FO is processing a parameter-only versionless application upgrade. No observers should run.
                        return;
                    }
                }
                catch (Exception e) when (e is FabricException || e is TimeoutException || e is Win32Exception)
                {
                    // Transient.
                    Logger.LogWarning($"Handled error from {observer.ObserverName}{Environment.NewLine}{e}");
                }
                catch (Exception e) when (!(e is LinuxPermissionException))
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
                var githubClient = new GitHubClient(new ProductHeaderValue(ObserverConstants.FabricObserverName));
                IReadOnlyList<Release> releases = await githubClient.Repository.Release.GetAll("microsoft", "service-fabric-observer");

                if (releases.Count == 0)
                {
                    return;
                }

                string releaseAssetName = releases[0].Name;
                string latestVersion = releaseAssetName.Split(" ")[1];
                Version latestGitHubVersion = new Version(latestVersion);
                Version localVersion = new Version(InternalVersionNumber);
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

                    var telemetryData = new TelemetryData
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
                    if (TelemetryEnabled)
                    {
                        await TelemetryClient?.ReportHealthAsync(telemetryData, token);
                    }

                    // ETW.
                    if (EtwEnabled)
                    {
                        Logger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                    }
                }
            }
            catch (Exception e)
            {
                // Don't take down FO due to error in version check...
                Logger.LogWarning($"Failure checking Github for latest FO version: {e.Message}");
            }
        }

        private bool IsLVIDPerfCounterEnabled(ConfigurationSettings settings = null)
        {
            if (!isWindows)
            {
                return false;
            }

            // We already figured this out the first time this function ran.
            if (IsLvidCounterEnabled)
            {
                // DEBUG
                Logger.LogInfo("IsLVIDPerfCounterEnabled: Counter has already been determined to be enabled. Not running the check again..");
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
            Logger.LogInfo("IsLVIDPerfCounterEnabled: Running check since a supported observer is enabled for LVID monitoring.");
            
            /* This counter will be enabled by default in a future version of SF (probably an 8.2 CU release).

                SF Version scheme: 
                
                8.2.1363.9590

                Major = 8
                Minor = 2
                Revision = 1363 (this is what is interesting for CUs)
                Build = 9590 (a Windows release build)
            */

            //var currentSFRuntimeVersion = ServiceFabricConfiguration.Instance.FabricVersion;
            //Version version = new Version(currentSFRuntimeVersion);
            // Windows 8.2 CUx with the fix.
            //if (version.Major == 8 && version.Minor == 2 && version.Revision >= 1365 /* This is not the right revision number. This is just placeholder code. */)
            //{
            //  return true;
            //}

            const string categoryName = "Windows Fabric Database";
            const string counterName = "Long-Value Maximum LID";

            // If there is corrupted state on the machine with respect to performance counters, an AV can occur (in native code, then wrapped in AccessViolationException)
            // when calling PerformanceCounterCategory.Exists below. This is actually a symptom of a problem that extends beyond just this counter category..
            // *Do not catch AV exception*. FO will crash, of course, but that is safer than pretending nothing is wrong.
            // To mitigate the issue in that case, you will need to restart the machine or rebuild performance counters manually. Other perf counters that FO relies on will most likely 
            // cause issues (not FO crashes necessarily, but inaccurate data related to the metrics they represent (like, you will always see 0 or -1 measurement values)).
            try
            {
                // This is a pretty expensive call.
                if (!PerformanceCounterCategory.Exists(categoryName))
                {
                    return false;
                }
                
                return PerformanceCounterCategory.CounterExists(counterName, categoryName);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is UnauthorizedAccessException || e is Win32Exception)
            {
                Logger.LogWarning($"IsLVIDPerfCounterEnabled: Failed to determine LVID perf counter state:{Environment.NewLine}{e.Message}");
            }

            return false;
        }
    }

    // Custom comparer for ConfigurationProperty
    class ConfigComparer : IEqualityComparer<ConfigurationProperty>
    {
        // ConfigurationProperty instances are equal if their Names and Values are equal.
        public bool Equals(ConfigurationProperty x, ConfigurationProperty y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || ReferenceEquals(y, null))
            {
                return false;
            }

            return x.Name == y.Name && x.Value == y.Value;
        }

        public int GetHashCode(ConfigurationProperty config)
        {
            if (ReferenceEquals(config, null))
            {
                return 0;
            }

            int hashProductName = config.Name == null ? 0 : config.Name.GetHashCode();
            int hashProductCode = config.Value.GetHashCode();

            return hashProductName ^ hashProductCode;
        }
    }
}
