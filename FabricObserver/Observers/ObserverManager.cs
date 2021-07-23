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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.TelemetryLib;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    // This class manages the lifetime of all observers from instantiation to "destruction",
    // and sequentially runs all observer instances in a never-ending while loop,
    // with optional sleeps, and reliable shutdown event handling.
    public class ObserverManager : IDisposable
    {
        private static bool etwEnabled;
        private readonly string nodeName;
        private readonly List<ObserverBase> observers;
        private volatile bool shutdownSignaled;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(30);
        private readonly CancellationToken token;
        private CancellationTokenSource cts;
        private CancellationTokenSource linkedSFRuntimeObserverTokenSource;
        private bool disposed;
        private readonly IEnumerable<ObserverBase> serviceCollection;
        private bool isConfigurationUpdateInProgress;

        private bool TaskCancelled =>
            linkedSFRuntimeObserverTokenSource?.Token.IsCancellationRequested ?? token.IsCancellationRequested;

        public static FabricClient FabricClientInstance
        {
            get; set;
        }

        private static int ObserverExecutionLoopSleepSeconds
        {
            get; set;
        } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

        public static StatelessServiceContext FabricServiceContext
        {
            get; set;
        }

        private static ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        public static bool TelemetryEnabled
        {
            get; set;
        }

        private static bool FabricObserverInternalTelemetryEnabled
        {
            get; set;
        }

        public static bool ObserverWebAppDeployed
        {
            get; set;
        }

        public static bool EtwEnabled
        {
            get => bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableETWProvider), out etwEnabled) && etwEnabled;

            set => etwEnabled = value;
        }

        public string ApplicationName
        {
            get; set;
        }

        public bool IsObserverRunning
        {
            get;
            private set;
        }

        public HealthState ObserverFailureHealthStateLevel
        {
            get;
            set;
        } = HealthState.Warning;

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

        private int MaxArchivedLogFileLifetimeDays
        {
            get;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// This is only used by unit tests.
        /// </summary>
        /// <param name="observer">Observer instance.</param>
        /// <param name="fabricClient">FabricClient instance</param>
        public ObserverManager(ObserverBase observer, FabricClient fabricClient)
        {
            cts = new CancellationTokenSource();
            token = cts.Token;
            Logger = new Logger("ObserverManagerSingleObserverRun");
            FabricClientInstance ??= fabricClient;
            HealthReporter = new ObserverHealthReporter(Logger, FabricClientInstance);

            // The unit tests expect file output from some observers.
            ObserverWebAppDeployed = true;

            observers = new List<ObserverBase>(new[]
            {
                observer
            });
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// </summary>
        /// <param name="serviceProvider">IServiceProvider for retrieving service instance.</param>
        /// <param name="fabricClient">FabricClient instance.</param>
        /// <param name="token">Cancellation token.</param>
        public ObserverManager(IServiceProvider serviceProvider, FabricClient fabricClient, CancellationToken token)
        {
            this.token = token;
            cts = new CancellationTokenSource();
            linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, this.token);
            FabricClientInstance = fabricClient;
            FabricServiceContext = serviceProvider.GetRequiredService<StatelessServiceContext>();
            nodeName = FabricServiceContext?.NodeContext.NodeName;

            if (FabricServiceContext == null)
            {
                return;
            }

            FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent +=
                CodePackageActivationContext_ConfigurationPackageModifiedEvent;

            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetConfigSettingValue(ObserverConstants.ObserverLogPathParameter);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "observer_logs");
                logFolderBasePath = logFolderBase;
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.MaxArchivedLogFileLifetimeDays), out int maxArchivedLogFileLifetimeDays))
            {
                MaxArchivedLogFileLifetimeDays = maxArchivedLogFileLifetimeDays;
            }

            // this logs error/warning/info messages for ObserverManager.
            Logger = new Logger("ObserverManager", logFolderBasePath, MaxArchivedLogFileLifetimeDays > 0 ? MaxArchivedLogFileLifetimeDays : 7);
            serviceCollection = serviceProvider.GetServices<ObserverBase>();

            // Populate the Observer list for the sequential run loop.
            int capacity = serviceCollection.Count(o => o.IsEnabled);

            if (capacity > 0)
            {
                observers = new List<ObserverBase>(capacity);
                observers.AddRange(serviceCollection.Where(o => o.IsEnabled));
            }
            else
            {
                Logger.LogWarning("There are no observers enabled. Aborting..");
                return;
            }

            HealthReporter = new ObserverHealthReporter(Logger, FabricClientInstance);
            SetPropertiesFromConfigurationParameters();

            // FabricObserver Internal Diagnostic Telemetry (Non-PII).
            // Internally, TelemetryEvents determines current Cluster Id as a unique identifier for transmitted events.
            if (!FabricObserverInternalTelemetryEnabled)
            {
                return;
            }

            string codePkgVersion = FabricServiceContext.CodePackageActivationContext.CodePackageVersion;
            string serviceManifestVersion = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Description.ServiceManifestVersion;
            string filepath = Path.Combine(Logger.LogFolderBasePath,
                                           $"fo_telemetry_sent_{codePkgVersion.Replace(".", string.Empty)}_" +                                                   
                                           $"{serviceManifestVersion.Replace(".", string.Empty)}_{FabricServiceContext.NodeContext.NodeType}.log");

#if !DEBUG
            // If this has already been sent for this activated version (code/config) of node type x
            if (File.Exists(filepath))
            {
                return;
            }
#endif
            try
            {
                var telemetryEvents = new TelemetryEvents(FabricClientInstance, FabricServiceContext, ServiceEventSource.Current, this.token);

                if (telemetryEvents.FabricObserverRuntimeNodeEvent(codePkgVersion, GetFabricObserverInternalConfiguration(), "HealthState.Initialized"))
                {
                    // Log a file to prevent re-sending this in case of process restart(s).
                    // This non-PII FO/Cluster info is versioned and should only be sent once per deployment (config or code updates).
                    _ = Logger.TryWriteLogFile(filepath, GetFabricObserverInternalConfiguration());
                }
            }
            catch
            {
                
            }
        }

        public async Task StartObserversAsync()
        {
            try
            {
                // Nothing to do here.
                if (observers == null || observers.Count == 0)
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
                        await ShutDownAsync().ConfigureAwait(false);
                        break;
                    }

                    if (!await RunObserversAsync().ConfigureAwait(false))
                    {
                        if (!isConfigurationUpdateInProgress && (shutdownSignaled || token.IsCancellationRequested))
                        {
                            Logger.LogWarning("All enabled observers did not run successfully. See observer logs for details.");
                        }
                    }

                 /* Note the below use of GC.Collect is NOT a general recommendation for what to do in your own managed service code or app code. Please don't 
                    make that connection. You should generally not have to call GC.Collect from user service code. It just depends on your performance needs.
                    As always, measure and understand what impact your code has on memory before employing the GC API in your own projects.
                    This is only used here to ensure gen0 and gen1 do not hold unecessary objects for any amount of time before FO goes to sleep and to compact the LOH. 
                    
                    All observers clear and null their internal lists, objects that maintain internal lists. They also dispose/null disposable objects, etc before this code runs.
                    This is a micro "optimization" and not really necessary. However, it does modestly decrease the already reasonable memory footprint of FO. 
                    Out of the box, FO will generally consume less than 100MB of workingset. Most of this (~65-70%) is held in native memory. 
                    FO workingset can increase depending upon how many services you monitor, how you write your plugins with respect to memory consumption, etc.. */
                    
                    // SOH, sweep-only collection (no compaction). This will clear the early generation objects (short-lived) from memory. This only impacts the FO process.
                    GC.Collect(0, GCCollectionMode.Forced, true, false);
                    GC.Collect(1, GCCollectionMode.Forced, true, false);

                    if (ObserverExecutionLoopSleepSeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds), token);
                    }
                    else if (observers.Count == 1) // This protects against loop spinning when you run FO with one observer enabled and no sleep time set.
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), token);
                    }
                }
            }
            catch (Exception e) when (e is TaskCanceledException || e is OperationCanceledException)
            {
                if (!isConfigurationUpdateInProgress && (shutdownSignaled || token.IsCancellationRequested))
                {
                    await ShutDownAsync().ConfigureAwait(true);
                }
            }
            catch (Exception e)
            {
                var message =
                    $"Unhandled Exception in {ObserverConstants.ObserverManagerName} on node " +
                    $"{nodeName}. Taking down FO process. " +
                    $"Error info:{Environment.NewLine}{e}";

                Logger.LogError(message);

                // Telemetry.
                if (TelemetryEnabled)
                {
                    var telemetryData = new TelemetryData(FabricClientInstance, token)
                    {
                        Description = message,
                        HealthState = "Error",
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

                // Don't swallow the exception.
                // Take down FO process. Fix the bug(s).
                throw;
            }
        }

        // Clear all existing FO health events during shutdown or update event.
        public async Task StopObserversAsync(bool isShutdownSignaled = true, bool isConfigurationUpdateLinux = false)
        {
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
                    ReportType = HealthReportType.Application,
                    NodeName = obs.NodeName
                };

                if (obs.AppNames.Count(a => !string.IsNullOrWhiteSpace(a) && a.Contains("fabric:/")) > 0)
                {
                    foreach (var app in obs.AppNames)
                    {
                        try
                        {
                            Uri appName = new Uri(app);
                            var appHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(appName).ConfigureAwait(true);
                            var fabricObserverAppHealthEvents = appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

                            if (isConfigurationUpdateInProgress)
                            {
                                fabricObserverAppHealthEvents = appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName)
                                                                                        && s.HealthInformation.HealthState == HealthState.Warning 
                                                                                        || s.HealthInformation.HealthState == HealthState.Error);
                            }

                            foreach (var evt in fabricObserverAppHealthEvents)
                            {
                                healthReport.AppName = appName;
                                healthReport.Property = evt.HealthInformation.Property;
                                healthReport.SourceId = evt.HealthInformation.SourceId;

                                var healthReporter = new ObserverHealthReporter(Logger, FabricClientInstance);
                                healthReporter.ReportHealthToServiceFabric(healthReport);

                                await Task.Delay(250).ConfigureAwait(true);
                            }
                        }
                        catch (FabricException)
                        {

                        }

                        await Task.Delay(250).ConfigureAwait(true);
                    }
                }
                else
                {
                    try
                    {
                        var nodeHealth = await FabricClientInstance.HealthManager.GetNodeHealthAsync(obs.NodeName).ConfigureAwait(true);
                        var fabricObserverNodeHealthEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

                        if (isConfigurationUpdateInProgress)
                        {
                            fabricObserverNodeHealthEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName)
                                                                                      && s.HealthInformation.HealthState == HealthState.Warning
                                                                                      || s.HealthInformation.HealthState == HealthState.Error);
                        }

                        healthReport.ReportType = HealthReportType.Node;

                        foreach (var evt in fabricObserverNodeHealthEvents)
                        {
                            healthReport.Property = evt.HealthInformation.Property;
                            healthReport.SourceId = evt.HealthInformation.SourceId;

                            var healthReporter = new ObserverHealthReporter(Logger, FabricClientInstance);
                            healthReporter.ReportHealthToServiceFabric(healthReport);

                            await Task.Delay(250).ConfigureAwait(true);
                        }
                            
                    }
                    catch (FabricException)
                    {

                    }

                    await Task.Delay(250).ConfigureAwait(true);
                }

                obs.HasActiveFabricErrorOrWarning = false;
            }

            shutdownSignaled = isShutdownSignaled;

            if (!isConfigurationUpdateInProgress)
            {
                SignalAbortToRunningObserver();
                IsObserverRunning = false;
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
                if (FabricClientInstance != null)
                {
                    FabricClientInstance.Dispose();
                    FabricClientInstance = null;
                }

                linkedSFRuntimeObserverTokenSource?.Dispose();
                cts?.Dispose();
                FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent -= CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            }

            disposed = true;
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
            await StopObserversAsync().ConfigureAwait(true);

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
        /// This function gets FabricObserver's internal configuration for telemetry for Charles and company.
        /// No PII. As you can see, this is generic information - number of enabled observer, observer names.
        /// </summary>
        private string GetFabricObserverInternalConfiguration()
        {
            int enabledObserverCount = observers.Count(obs => obs.IsEnabled);
            string observerList = observers.Aggregate("{ ", (current, obs) => current + $"{obs.ObserverName} ");

            observerList += "}";
            string ret = $"EnabledObserverCount: {enabledObserverCount}, EnabledObservers: {observerList}";

            return ret;
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
                // So, the netstats.sh FO setup script needs to run again so that the privileged operations can succeed when you enable/disable observers that need them, which is most observers (not all). These files are used by a shared utility.
                // Exiting here ensures that both the new config settings you provided will be applied when a new FO process is running after the FO setup script runs that puts
                // the Linux capabilities set in place for the elevated_netstat and elevated_docker_stats binaries. The reason this must happen is that SF touches the files during this upgrade (this may be an SF bug, but it is not preventing
                // the shipping of FO 3.1.1.) and this unsets the Capabilities on the binaries. It looks like SF changes attributes on the files (permissions).
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Graceful stop.
                    await StopObserversAsync(true, true).ConfigureAwait(true);

                    // Bye.
                    Environment.Exit(42);
                }

                isConfigurationUpdateInProgress = true;
                await StopObserversAsync(false).ConfigureAwait(true);
                observers.Clear();

                foreach (var observer in serviceCollection)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    observer.ConfigurationSettings = new ConfigSettings(e.NewPackage.Settings, $"{observer.ObserverName}Configuration");

                    if (!observer.ConfigurationSettings.IsEnabled)
                    {
                        continue;
                    }

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

                    observers.Add(observer);
                }

                cts ??= new CancellationTokenSource();
                linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token);
            }
            catch (Exception err)
            {
                var healthReport = new HealthReport
                {
                    AppName = new Uri(FabricServiceContext.CodePackageActivationContext.ApplicationName),
                    Code = FOErrorWarningCodes.Ok,
                    ReportType = HealthReportType.Application,
                    HealthMessage = $"Error updating FabricObserver with new configuration settings:{Environment.NewLine}{err}",
                    NodeName = FabricServiceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    Property = "Configuration_Upate_Error",
                    EmitLogEvent = true
                };

                HealthReporter.ReportHealthToServiceFabric(healthReport);
            }

            isConfigurationUpdateInProgress = false;
            Logger.LogWarning("Application Parameter upgrade completed...");
        }

        /// <summary>
        /// Sets ObserverManager's related properties/fields to their corresponding Settings.xml 
        /// configuration settings (parameter values).
        /// </summary>
        private void SetPropertiesFromConfigurationParameters()
        {
            ApplicationName = FabricRuntime.GetActivationContext().ApplicationName;

            // Observers
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

                Logger.LogInfo($"ExecutionFrequency is {ObserverExecutionLoopSleepSeconds} Seconds");
            }

            // FQDN for use in warning or error hyperlinks in HTML output
            // This only makes sense when you have the FabricObserverWebApi app installed.
            string fqdn = GetConfigSettingValue(ObserverConstants.Fqdn);

            if (!string.IsNullOrEmpty(fqdn))
            {
                Fqdn = fqdn;
            }

            // FabricObserver runtime telemetry (Non-PII)
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.FabricObserverTelemetryEnabled), out bool foTelemEnabled))
            {
                FabricObserverInternalTelemetryEnabled = foTelemEnabled;
            }

            // ObserverWebApi.
            ObserverWebAppDeployed = bool.TryParse(GetConfigSettingValue(ObserverConstants.ObserverWebApiEnabled), out bool obsWeb) && obsWeb && IsObserverWebApiAppInstalled();

            // ObserverFailure HealthState Level
            string state = GetConfigSettingValue(ObserverConstants.ObserverFailureHealthStateLevelParameter);
            if (string.IsNullOrWhiteSpace(state) || state?.ToLower() == "none")
            {
                ObserverFailureHealthStateLevel = HealthState.Unknown;
            }
            else if (Enum.TryParse(state, out HealthState healthState))
            {
                ObserverFailureHealthStateLevel = healthState;
            }

            // (Assuming Diagnostics/Analytics cloud service implemented) Telemetry.
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.TelemetryEnabled), out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;
            }

            if (!TelemetryEnabled)
            {
                return;
            }

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
                    string logAnalyticsLogType = GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter);
                    string logAnalyticsSharedKey = GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter);
                    string logAnalyticsWorkspaceId = GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter);

                    if (string.IsNullOrEmpty(logAnalyticsWorkspaceId) || string.IsNullOrEmpty(logAnalyticsSharedKey))
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
        private void SignalAbortToRunningObserver()
        {
            Logger.LogInfo("Signalling task cancellation to currently running Observer.");
            
            try
            {
                cts?.Cancel();
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
        private async Task<bool> RunObserversAsync()
        {
            var exceptionBuilder = new StringBuilder();
            bool allExecuted = true;

            for (int i = 0; i < observers.Count(); ++i)
            {
                var observer = observers[i];

                if (isConfigurationUpdateInProgress)
                {
                    return true;
                }

                try
                {
                    if (TaskCancelled || shutdownSignaled)
                    {
                        return false;
                    }

                    // Is it healthy?
                    if (observer.IsUnhealthy)
                    {
                        continue;
                    }

                    Logger.LogInfo($"Starting {observer.ObserverName}");

                    IsObserverRunning = true;

                    // Synchronous call.
                    bool isCompleted = observer.ObserveAsync(linkedSFRuntimeObserverTokenSource?.Token ?? token).Wait(observerExecTimeout);

                    // The observer is taking too long (hung?), move on to next observer.
                    // Currently, this observer will not run again for the lifetime of this FO service instance.
                    if (!isCompleted && !(TaskCancelled || shutdownSignaled))
                    {
                        string observerHealthWarning = $"{observer.ObserverName} has exceeded its specified Maximum run time of {observerExecTimeout.TotalSeconds} seconds. " +
                                                       $"This means something is wrong with {observer.ObserverName}. It will not be run again. Please look into it.";

                        Logger.LogError(observerHealthWarning);
                        observer.IsUnhealthy = true;

                        // Telemetry.
                        if (TelemetryEnabled)
                        {
                            var telemetryData = new TelemetryData(FabricClientInstance, token)
                            {
                                Description = observerHealthWarning,
                                HealthState = "Error",
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
                                AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                                EmitLogEvent = false,
                                HealthMessage = observerHealthWarning,
                                HealthReportTimeToLive = TimeSpan.MaxValue,
                                Property = $"{observer.ObserverName}_HealthState",
                                ReportType = HealthReportType.Application,
                                State = ObserverFailureHealthStateLevel,
                                NodeName = this.nodeName,
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
                catch (AggregateException ex)
                {
                    IsObserverRunning = false;

                    if (ex.InnerException is FabricException ||
                        ex.InnerException is OperationCanceledException ||
                        ex.InnerException is TaskCanceledException)
                    {
                        if (isConfigurationUpdateInProgress)
                        {
                            IsObserverRunning = false;

                            return true;
                        }

                        continue;
                    }

                    _ = exceptionBuilder.AppendLine($"Handled AggregateException from {observer.ObserverName}:{Environment.NewLine}{ex.InnerException}");
                    allExecuted = false;
                }
                catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException || e is TimeoutException)
                {
                    if (isConfigurationUpdateInProgress)
                    {
                        IsObserverRunning = false;
                        return true;
                    }

                    _ = exceptionBuilder.AppendLine($"Handled Exception from {observer.ObserverName}:{Environment.NewLine}{e}");
                    allExecuted = false;
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Unhandled Exception from {observer.ObserverName}:{Environment.NewLine}{e}");
                    allExecuted = false;
                }

                IsObserverRunning = false;
            }

            if (allExecuted)
            {
                Logger.LogInfo(ObserverConstants.AllObserversExecutedMessage);
            }
            else
            {
                Logger.LogWarning(exceptionBuilder.ToString());
                _ = exceptionBuilder.Clear();
            }

            return allExecuted;
        }
    }
}
