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
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.TelemetryLib;

namespace FabricObserver.Observers
{
    // This class manages the lifetime of all observers from instantiation to "destruction",
    // and sequentially runs all observer instances in a never-ending while loop,
    // with optional sleeps, and reliable shutdown event handling.
    public class ObserverManager : IDisposable
    {
        private static bool etwEnabled;
        public readonly string nodeName;
        private readonly TelemetryEvents telemetryEvents;
        private List<ObserverBase> observers;
        private EventWaitHandle globalShutdownEventHandle;
        private volatile bool shutdownSignaled;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(30);
        private CancellationToken token;
        private CancellationTokenSource cts;
        private CancellationTokenSource linkedSFRuntimeObserverTokenSource;
        private bool disposedValue;
        private IEnumerable<ObserverBase> serviceCollection;
        private bool isConfigurationUpdateInProgess;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// This is used for unit testing.
        /// </summary>
        /// <param name="observer">Observer instance.</param>
        public ObserverManager(ObserverBase observer, FabricClient fabricClient)
        {
            this.cts = new CancellationTokenSource();
            this.token = this.cts.Token;
            this.Logger = new Logger("ObserverManagerSingleObserverRun");
            this.HealthReporter = new ObserverHealthReporter(this.Logger, fabricClient);

            // The unit tests expect file output from some observers.
            ObserverWebAppDeployed = true;

            this.observers = new List<ObserverBase>(new[]
            {
                observer,
            });
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// </summary>
        /// <param name="serviceProvider">IServiceProvider for retrieving service instance.</param>
        /// <param name="token">Cancellation token.</param>
        public ObserverManager(IServiceProvider serviceProvider, FabricClient fabricClient, CancellationToken token)
        {
            this.token = token;
            this.cts = new CancellationTokenSource();
            this.linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(this.cts.Token, this.token);
            FabricClientInstance = fabricClient;
            FabricServiceContext = serviceProvider.GetRequiredService<StatelessServiceContext>();
            this.nodeName = FabricServiceContext?.NodeContext.NodeName;
            FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;

            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetConfigSettingValue(
                ObserverConstants.ObserverLogPathParameter);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "observer_logs");
                logFolderBasePath = logFolderBase;
            }

            // this logs error/warning/info messages for ObserverManager.
            this.Logger = new Logger("ObserverManager", logFolderBasePath);
            this.HealthReporter = new ObserverHealthReporter(this.Logger, FabricClientInstance);
            this.SetPropertieSFromConfigurationParameters();
            this.serviceCollection = serviceProvider.GetServices<ObserverBase>();

            // Populate the Observer list for the sequential run loop.
            this.observers = this.serviceCollection.Where(o => o.IsEnabled).ToList();

            // FabricObserver Internal Diagnostic Telemetry (Non-PII).
            // Internally, TelemetryEvents determines current Cluster Id as a unique identifier for transmitted events.
            if (!FabricObserverInternalTelemetryEnabled)
            {
                return;
            }

            if (FabricServiceContext == null)
            {
                return;
            }

            string codePkgVersion = FabricServiceContext.CodePackageActivationContext.CodePackageVersion;
            string serviceManifestVersion = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Description.ServiceManifestVersion;
            string filepath = Path.Combine(this.Logger.LogFolderBasePath, $"fo_telemetry_sent_{codePkgVersion.Replace(".", string.Empty)}_{serviceManifestVersion.Replace(".", string.Empty)}_{FabricServiceContext.NodeContext.NodeType}.log");

#if !DEBUG
            // If this has already been sent for this activated version (code/config) of nodetype x
            if (File.Exists(filepath))
            {
                return;
            }
#endif
            this.telemetryEvents = new TelemetryEvents(
                FabricClientInstance,
                FabricServiceContext,
                ServiceEventSource.Current,
                this.token);

            string foInternalTelemetryData = this.GetFabricObserverInternalConfiguration();
            if (this.telemetryEvents.FabricObserverRuntimeNodeEvent(
                codePkgVersion,
                foInternalTelemetryData,
                "HealthState.Initialized"))
            {
                // Log a file to prevent re-sending this in case of process restart(s).
                // This non-PII FO/Cluster info is versioned and should only be sent once per deployment (config or code updates.).
                _ = this.Logger.TryWriteLogFile(filepath, foInternalTelemetryData);
            }
        }

        public static FabricClient FabricClientInstance
        {
            get; set;
        }

        public static int ObserverExecutionLoopSleepSeconds 
        { 
            get; private set; 
        } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

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

        public static bool FabricObserverInternalTelemetryEnabled 
        { get; set; } = true;

        public static bool ObserverWebAppDeployed
        {
            get; set;
        }

        public static bool EtwEnabled
        {
            get => bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableEventSourceProvider), out etwEnabled) && etwEnabled;

            set => etwEnabled = value;
        }

        public string ApplicationName
        {
            get; set;
        }

        public bool IsObserverRunning
        {
            get; set;
        }

        private ObserverHealthReporter HealthReporter
        {
            get; set;
        }

        private string Fqdn
        {
            get; set;
        }

        private Logger Logger
        {
            get; set;
        }

        public async Task StartObserversAsync()
        {
            try
            {
                // Nothing to do here.
                if (this.observers.Count == 0)
                {
                    return;
                }

                // Create Global Shutdown event handler
                this.globalShutdownEventHandle = new EventWaitHandle(false, EventResetMode.ManualReset);

                // Continue running until a shutdown signal is sent
                this.Logger.LogInfo("Starting Observers loop.");

                // Observers run sequentially. See RunObservers impl.
                while (true)
                {
                    if (!isConfigurationUpdateInProgess && (this.shutdownSignaled || this.token.IsCancellationRequested))
                    {
                        _ = this.globalShutdownEventHandle.Set();
                        this.Logger.LogWarning("Shutdown signaled. Stopping.");
                        await ShutDownAsync().ConfigureAwait(false);

                        break;
                    }

                    if (!await this.RunObserversAsync().ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (ObserverExecutionLoopSleepSeconds > 0)
                    {
                        this.Logger.LogInfo($"Sleeping for {ObserverExecutionLoopSleepSeconds} seconds before running again.");
                        this.ThreadSleep(this.globalShutdownEventHandle, TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds));
                    }
                }
            }
            catch (Exception ex)
            {
                var message =
                    $"Unhanded Exception in {ObserverConstants.ObserverManagerName} on node " +
                    $"{this.nodeName}. Taking down FO process. " +
                    $"Error info:{Environment.NewLine}{ex}";

                this.Logger.LogError(message);

                // Telemetry.
                if (TelemetryEnabled)
                {
                    await (TelemetryClient?.ReportHealthAsync(
                            HealthScope.Application,
                            "FabricObserverServiceHealth",
                            HealthState.Warning,
                            message,
                            ObserverConstants.ObserverManagerName,
                            this.token)).ConfigureAwait(false);
                }

                // ETW.
                if (EtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        $"FabricObserverServiceCriticalHealthEvent",
                        new
                        {
                            Level = 2, // Error
                            Node = this.nodeName,
                            Observer = ObserverConstants.ObserverManagerName,
                            Value = message,
                        });
                }

                // Don't swallow the exception.
                // Take down FO process. Fix the bugs in OM that this identifies.
                throw;
            }
        }

        public async Task StopObserversAsync(bool shutdownSignaled = true, bool isConfigurationUpdateLinux = false)
        {
            string configUpdateLinux = string.Empty;

            if (isConfigurationUpdateLinux)
            {
                configUpdateLinux = 
                    $" Note: This is due to a configuration update which requires an FO process restart on Linux (with UD walk (one by one) and safety checks).{Environment.NewLine}" +
                    $"The reason FO needs to be restarted as part of a parameter-only upgrade is due to the Linux Capabilities set FO employs not persisting across application upgrades (by design) " +
                    $"even when the upgrade is just a configuration parameter update. In order to re-create the Capabilities set, FO's setup script must be re-run by SF. Restarting FO is therefore required here.";
            }

            foreach (var obs in this.observers)
            {
                // If the node goes down, for example, or the app is gracefully closed, then clear all existing error or health reports suppled by FO.
                // NetworkObserver takes care of this internally, so ignore here.
                if (obs.HasActiveFabricErrorOrWarning &&
                    obs.ObserverName != ObserverConstants.NetworkObserverName)
                {
                    Utilities.HealthReport healthReport = new Utilities.HealthReport
                    {
                        Code = FOErrorWarningCodes.Ok,
                        HealthMessage = $"Clearing existing Health Error/Warning as FO is stopping or updating.{configUpdateLinux}.",
                        State = HealthState.Ok,
                        ReportType = HealthReportType.Application,
                        NodeName = obs.NodeName,
                    };

                    if (obs.AppNames.Count > 0 && obs.AppNames.All(a => !string.IsNullOrEmpty(a) && a.Contains("fabric:/")))
                    {
                        foreach (var app in obs.AppNames)
                        {
                            try
                            {
                                var appHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(new Uri(app)).ConfigureAwait(false);

                                int? unhealthyEventsCount = appHealth.HealthEvents?.Count(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

                                if (unhealthyEventsCount == null || unhealthyEventsCount == 0)
                                {
                                    continue;
                                }

                                foreach (var evt in appHealth.HealthEvents)
                                {
                                    if (!evt.HealthInformation.SourceId.Contains(obs.ObserverName))
                                    {
                                        continue;
                                    }

                                    healthReport.AppName = new Uri(app);
                                    healthReport.Property = evt.HealthInformation.Property;
                                    healthReport.SourceId = evt.HealthInformation.SourceId;

                                    var healthReporter = new ObserverHealthReporter(this.Logger, FabricClientInstance);
                                    healthReporter.ReportHealthToServiceFabric(healthReport);

                                    await Task.Delay(500, token).ConfigureAwait(false);
                                }
                            }
                            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException || e is TimeoutException)
                            {
                            }
                        }
                    }
                    else
                    {
                        try
                        {
                            var nodeHealth = await FabricClientInstance.HealthManager.GetNodeHealthAsync(obs.NodeName).ConfigureAwait(false);

                            int? unhealthyEventsCount = nodeHealth.HealthEvents?.Count(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

                            healthReport.ReportType = HealthReportType.Node;

                            if (unhealthyEventsCount != null && unhealthyEventsCount > 0)
                            {
                                foreach (var evt in nodeHealth.HealthEvents)
                                {
                                    if (!evt.HealthInformation.SourceId.Contains(obs.ObserverName))
                                    {
                                        continue;
                                    }

                                    healthReport.Property = evt.HealthInformation.Property;
                                    healthReport.SourceId = evt.HealthInformation.SourceId;

                                    var healthReporter = new ObserverHealthReporter(this.Logger, FabricClientInstance);
                                    healthReporter.ReportHealthToServiceFabric(healthReport);

                                    await Task.Delay(500, token).ConfigureAwait(false);
                                }
                            }
                        }
                        catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException || e is TimeoutException)
                        {
                        }
                    }

                    obs.HasActiveFabricErrorOrWarning = false;
                }
            }

            this.shutdownSignaled = shutdownSignaled;
            this.SignalAbortToRunningObserver();
            this.IsObserverRunning = false;
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            this.Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    if (FabricClientInstance != null)
                    {
                        FabricClientInstance.Dispose();
                        FabricClientInstance = null;
                    }
                }

                FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent -= this.CodePackageActivationContext_ConfigurationPackageModifiedEvent;

                this.disposedValue = true;
            }
        }

        private static bool IsObserverWebApiAppInstalled()
        {
            try
            {
                var deployedObsWebApps = FabricClientInstance.QueryManager.GetApplicationListAsync(new Uri("fabric:/FabricObserverWebApi")).GetAwaiter().GetResult();
                return deployedObsWebApps?.Count > 0;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
            }

            return false;
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
            catch (Exception e) when (e is KeyNotFoundException || e is FabricElementNotFoundException)
            {
            }

            return null;
        }

        private async Task ShutDownAsync()
        {
            await this.StopObserversAsync().ConfigureAwait(false);

            if (this.cts != null)
            {
                this.cts.Dispose();
                this.cts = null;
            }

            this.globalShutdownEventHandle?.Dispose();

            // Flush and Dispose all NLog targets. No more logging.
            Logger.Flush();
            DataTableFileLogger.Flush();
            Logger.ShutDown();
            DataTableFileLogger.ShutDown();
        }

        /// <summary>
        /// This function gets FabricObserver's internal configuration for telemetry for Charles and company.
        /// No PII. As you can see, this is generic information - number of enabled observer, observer names.
        /// </summary>
        private string GetFabricObserverInternalConfiguration()
        {
            int enabledObserverCount = this.observers.Count(obs => obs.IsEnabled);
            string ret;
            string observerList = this.observers.Aggregate("{ ", (current, obs) => current + $"{obs.ObserverName} ");

            observerList += "}";
            ret = $"EnabledObserverCount: {enabledObserverCount}, EnabledObservers: {observerList}";

            return ret;
        }

        // This impl is to ensure FO exits if shutdown is requested while the over loop is sleeping
        // So, instead of blocking with a Thread.Sleep, for example, ThreadSleep is used to ensure
        // we can receive signals and act accordingly during thread sleep state.
        private void ThreadSleep(WaitHandle ewh, TimeSpan timeout)
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

                // the event can be signaled by CtrlC,
                // Exit ASAP when the program terminates (i.e., shutdown/abort is signalled.)
                _ = ewh.WaitOne(timeout.Subtract(elapsedTime));
                stopwatch.Stop();

                elapsedTime = stopwatch.Elapsed;
            }

            if (stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }
        }

        /// <summary>
        /// Event handler for application parameter updates (Application Upgrades).
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">Contains the information necessary for setting new config params from updated package.</param>
        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            this.Logger.LogInfo("Application Parameter upgrade started...");

            try
            {
                // For Linux, we need to restart the FO process due to the Capabilities impl: the setup script needs to run again so that privileged operations can succeed when you enable
                // an observer that requires them, for example. So, exiting here ensures that both the new config settings will be applied when a new process is running after the FO setup script runs that puts
                // the Linux capabilities set in place for FO..
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Graceful stop.
                    await this.StopObserversAsync(true, true).ConfigureAwait(false);
                    Environment.Exit(42);
                }

                this.isConfigurationUpdateInProgess = true;
                await StopObserversAsync(false).ConfigureAwait(true);
                this.observers.Clear();

                foreach (var observer in this.serviceCollection)
                {
                    observer.ConfigurationSettings = new ConfigSettings(e.NewPackage.Settings, $"{observer.ObserverName}Configuration");

                    if (observer.ConfigurationSettings.IsEnabled)
                    {
                        // The ObserverLogger instance (member of each observer type) checks its EnableVerboseLogging setting before writing Info events (it won't write if this setting is false, thus non-verbose).
                        // So, we set it here in case the parameter update includes a change to this config setting. 
                        // This is the only update-able setting that requires we do this as part of the config update event handling.
                        string oldVerboseLoggingSetting = e.NewPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters[ObserverConstants.EnableVerboseLoggingParameter]?.Value.ToLower();
                        string newVerboseLoggingSetting = e.OldPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters[ObserverConstants.EnableVerboseLoggingParameter]?.Value.ToLower();
                        
                        if (newVerboseLoggingSetting != oldVerboseLoggingSetting)
                        {
                            observer.ObserverLogger.EnableVerboseLogging = observer.ConfigurationSettings.EnableVerboseLogging;
                        }

                        this.observers.Add(observer);
                    }
                }

                this.cts = new CancellationTokenSource();
                this.linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(this.cts.Token, this.token);
                this.Logger.LogInfo($"Application Parameter upgrade in progress: new observer list count -> {this.observers.Count}");
            }
            catch (Exception err)
            {
                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri(FabricServiceContext.CodePackageActivationContext.ApplicationName),
                    Code = FOErrorWarningCodes.Ok,
                    ReportType = HealthReportType.Application,
                    HealthMessage = $"Error updating FabricObserver with new configuration settings:{Environment.NewLine}{err}",
                    NodeName = FabricServiceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    Property = $"Configuration_Upate_Error",
                    EmitLogEvent = true,
                };

                this.HealthReporter.ReportHealthToServiceFabric(healthReport);
            }

            this.isConfigurationUpdateInProgess = false;
            this.Logger.LogWarning("Application Parameter upgrade completed...");
        }

        /// <summary>
        /// Sets ObserverManager's related properties/fields to their corresponding Settings.xml 
        /// configuration settings (parameter values).
        /// </summary>
        private void SetPropertieSFromConfigurationParameters()
        {
            this.ApplicationName = FabricRuntime.GetActivationContext().ApplicationName;

            // Observers
            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout),
                out int result))
            {
                this.observerExecTimeout = TimeSpan.FromSeconds(result);
            }

            // Logger
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

            // FQDN for use in warning or error hyperlinks in HTML output
            // This only makes sense when you have the FabricObserverWebApi app installed.
            string fqdn = GetConfigSettingValue(ObserverConstants.Fqdn);
            if (!string.IsNullOrEmpty(fqdn))
            {
                this.Fqdn = fqdn;
            }

            // FabricObserver runtime telemetry (Non-PII)
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.FabricObserverTelemetryEnabled), out bool foTelemEnabled))
            {
                FabricObserverInternalTelemetryEnabled = foTelemEnabled;
            }

            // ObserverWebApi.
            ObserverWebAppDeployed = bool.TryParse(GetConfigSettingValue(ObserverConstants.ObserverWebApiAppDeployed), out bool obsWeb) ? obsWeb : IsObserverWebApiAppInstalled();

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
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter);

                        var logAnalyticsSharedKey =
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter);

                        var logAnalyticsWorkspaceId =
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter);

                        if (string.IsNullOrEmpty(logAnalyticsWorkspaceId)
                            || string.IsNullOrEmpty(logAnalyticsSharedKey))
                        {
                            TelemetryEnabled = false;

                            return;
                        }

                        TelemetryClient = new LogAnalyticsTelemetry(
                            logAnalyticsWorkspaceId,
                            logAnalyticsSharedKey,
                            logAnalyticsLogType,
                            FabricClientInstance,
                            this.token);

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

                    default:

                        TelemetryEnabled = false;

                        break;
                }
            }
        }

        /// <summary>
        /// This function will signal cancellation on the token passed to an observer's ObserveAsync. 
        /// This will eventually cause the observer to stop processing as this will throw an OperationCancelledException 
        /// in one of the observer's executing code paths.
        /// </summary>
        private void SignalAbortToRunningObserver()
        {
            this.Logger.LogInfo("Signalling task cancellation to currently running Observer.");
            this.cts?.Cancel();
            this.Logger.LogInfo("Successfully signaled cancellation to currently running Observer.");
        }

        /// <summary>
        /// Runs all observers in a sequential loop.
        /// </summary>
        /// <returns>A boolean value indicating success of a complete observer loop run.</returns>
        private async Task<bool> RunObserversAsync()
        {
            var exceptionBuilder = new StringBuilder();
            bool allExecuted = true;

            for (int i = 0; i < this.observers.Count; i++)
            {
                if (isConfigurationUpdateInProgess)
                {
                    return true;
                }

                var observer = this.observers[i];

                try
                {
                    // Shutdown/cancellation signaled, so stop.
                    bool taskCancelled = this.linkedSFRuntimeObserverTokenSource != null ? this.linkedSFRuntimeObserverTokenSource.Token.IsCancellationRequested : this.token.IsCancellationRequested;

                    if (taskCancelled || this.shutdownSignaled)
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
                    var isCompleted = observer.ObserveAsync(
                        this.linkedSFRuntimeObserverTokenSource != null ? this.linkedSFRuntimeObserverTokenSource.Token : this.token).Wait(this.observerExecTimeout);

                    // The observer is taking too long (hung?), move on to next observer.
                    // Currently, this observer will not run again for the lifetime of this FO service instance.
                    if (!isCompleted)
                    {
                        string observerHealthWarning = observer.ObserverName + $" has exceeded its specified run time of {this.observerExecTimeout.TotalSeconds} seconds. " +
                                                       $"This means something is wrong with {observer.ObserverName}. It will not be run again. Look into it.";

                        this.Logger.LogError(observerHealthWarning);
                        observer.IsUnhealthy = true;

                        if (TelemetryEnabled)
                        {
                            await (TelemetryClient?.ReportHealthAsync(
                                    HealthScope.Application,
                                    $"{observer.ObserverName}HealthError",
                                    HealthState.Error,
                                    observerHealthWarning,
                                    ObserverConstants.ObserverManagerName,
                                    this.token)).ConfigureAwait(false);
                        }

                        continue;
                    }

                    this.Logger.LogInfo($"Successfully ran {observer.ObserverName}.");

                    if (!ObserverWebAppDeployed)
                    {
                        continue;
                    }

                    if (observer.HasActiveFabricErrorOrWarning)
                    {
                        var errWarnMsg = !string.IsNullOrEmpty(this.Fqdn) ? $"<a style=\"font-weight: bold; color: red;\" href=\"http://{this.Fqdn}/api/ObserverLog/{observer.ObserverName}/{observer.NodeName}/json\">One or more errors or warnings detected</a>." : $"One or more errors or warnings detected. Check {observer.ObserverName} logs for details.";

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
                                // Replace the ObserverManager.log text that doesn't contain the observer Warn/Error line(s).
                                File.WriteAllLines(
                                    this.Logger.FilePath,
                                    File.ReadLines(this.Logger.FilePath)
                                        .Where(line => !line.Contains(observer.ObserverName)).ToList());
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
                catch (AggregateException ex)
                {
                    this.IsObserverRunning = false;

                    if (ex.InnerException is FabricException ||
                        ex.InnerException is OperationCanceledException ||
                        ex.InnerException is TaskCanceledException)
                    {
                        if (this.isConfigurationUpdateInProgess)
                        {
                            this.IsObserverRunning = false;

                            return true;
                        }

                        continue;
                    }

                    _ = exceptionBuilder.AppendLine($"Handled AggregateException from {observer.ObserverName}:{Environment.NewLine}{ex.InnerException}");
                    allExecuted = false;
                }
                catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException || e is TimeoutException)
                {
                    if (this.isConfigurationUpdateInProgess)
                    {
                        this.IsObserverRunning = false;

                        return true;
                    }

                    _ = exceptionBuilder.AppendLine($"Handled Exception from {observer.ObserverName}:{Environment.NewLine}{e}");
                    allExecuted = false;
                }
                catch (Exception e)
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                            ObserverConstants.ObserverManagerName,
                            this.ApplicationName,
                            HealthState.Error,
                            $"Unhandled Exception from {observer.ObserverName}:{Environment.NewLine}{e}");

                    allExecuted = false;
                }

                this.IsObserverRunning = false;
            }

            if (allExecuted)
            {
                this.Logger.LogInfo(ObserverConstants.AllObserversExecutedMessage);
            }
            else
            {
                if (this.Logger.EnableVerboseLogging)
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                        ObserverConstants.ObserverManagerName,
                        this.ApplicationName,
                        HealthState.Warning,
                        exceptionBuilder.ToString());
                }

                _ = exceptionBuilder.Clear();
            }

            return allExecuted;
        }
    }
}
