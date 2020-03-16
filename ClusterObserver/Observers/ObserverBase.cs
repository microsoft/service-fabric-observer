// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricClusterObserver.Interfaces;
using FabricClusterObserver.Utilities;

namespace FabricClusterObserver.Observers
{
    public abstract class ObserverBase : IObserverBase<StatelessServiceContext>
    {
        protected bool IsTelemetryEnabled { get; set; } = ObserverManager.TelemetryEnabled;

        protected ITelemetryProvider ObserverTelemetryClient { get; set; }

        protected FabricClient FabricClientInstance { get; set; }

        /// <inheritdoc/>
        public string ObserverName { get; set; }

        /// <inheritdoc/>
        public string NodeName { get; set; }

        public string NodeType { get; private set; }

        /// <inheritdoc/>
        public StatelessServiceContext FabricServiceContext { get; }

        /// <inheritdoc/>
        public DateTime LastRunDateTime { get; set; }

        public CancellationToken Token { get; set; }

        /// <inheritdoc/>
        public bool IsEnabled { get; set; } = true;

        /// <inheritdoc/>
        public bool IsUnhealthy { get; set; } = false;

        // Only set for unit test runs.
        public bool IsTestRun { get; set; } = false;

        // Loggers.

        /// <inheritdoc/>
        public Logger ObserverLogger { get; set; }

        // Each derived Observer can set this to maintain health status across iterations.
        // This information is used by ObserverManager.

        /// <inheritdoc/>
        public bool HasActiveFabricErrorOrWarning { get; set; } = false;

        /// <inheritdoc/>
        public TimeSpan RunInterval { get; set; } = TimeSpan.MinValue;

        public TimeSpan AsyncClusterOperationTimeoutSeconds { get; set; } = TimeSpan.FromSeconds(60);

        public List<string> Settings { get; }

        /// <inheritdoc/>
        public abstract Task ObserveAsync(CancellationToken token);

        /// <inheritdoc/>
        public abstract Task ReportAsync(CancellationToken token);

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverBase"/> class.
        /// </summary>
        protected ObserverBase(string observerName)
        {
            this.FabricClientInstance = ObserverManager.FabricClientInstance;

            if (this.IsTelemetryEnabled)
            {
                this.ObserverTelemetryClient = ObserverManager.TelemetryClient;
            }

            this.Settings = new List<string>();
            this.ObserverName = observerName;
            this.FabricServiceContext = ObserverManager.FabricServiceContext;
            this.NodeName = this.FabricServiceContext.NodeContext.NodeName;
            this.NodeType = this.FabricServiceContext.NodeContext.NodeType;
            this.AsyncClusterOperationTimeoutSeconds = TimeSpan.FromSeconds(ObserverManager.AsyncClusterOperationTimeoutSeconds);
            
            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = this.GetSettingParameterValue(
                ObserverConstants.ObserverManagerConfigurationSectionName,
                ObserverConstants.ObserverLogPath);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = $@"{Environment.CurrentDirectory}\observer_logs";
                logFolderBasePath = logFolderBase;
            }

            this.ObserverLogger = new Logger(observerName, logFolderBasePath);

            // Observer enabled?
            if (bool.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.ObserverEnabled),
                out bool enabled))
            {
                this.IsEnabled = enabled;
            }

            // Verbose logging?
            if (bool.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                this.ObserverLogger.EnableVerboseLogging = enableVerboseLogging;
            }

            // RunInterval?
            if (TimeSpan.TryParse(
                this.GetSettingParameterValue(
                observerName + "Configuration",
                ObserverConstants.ObserverRunIntervalParameterName),
                out TimeSpan runInterval))
            {
                this.RunInterval = runInterval;
            }
        }

        /// <inheritdoc/>
        public void WriteToLogWithLevel(string property, string description, LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Information:
                    this.ObserverLogger.LogInfo("{0} logged at level {1}: {2}", property, level, description);
                    break;

                case LogLevel.Warning:
                    this.ObserverLogger.LogWarning("{0} logged at level {1}: {2}", property, level, description);
                    break;

                case LogLevel.Error:
                    this.ObserverLogger.LogError("{0} logged at level {1}: {2}", property, level, description);
                    break;
            }

            Logger.Flush();
        }

        /// <summary>
        /// Gets a parameter value from the specified config section or returns supplied default value if 
        /// not specified in config.
        /// </summary>
        /// <param name="sectionName">Name of the section.</param>
        /// <param name="parameterName">Name of the parameter.</param>
        /// <param name="defaultValue">Default value.</param>
        /// <returns>parameter value.</returns>
        public string GetSettingParameterValue(string sectionName, string parameterName, string defaultValue = null)
        {
            if (string.IsNullOrEmpty(sectionName) || string.IsNullOrEmpty(parameterName))
            {
                return null;
            }

            try
            {
                var serviceConfiguration = this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

                if (!serviceConfiguration.Settings.Sections.Any(sec => sec.Name == sectionName))
                {
                    if (!string.IsNullOrEmpty(defaultValue))
                    {
                        return defaultValue;
                    }

                    return null;
                }

                if (!serviceConfiguration.Settings.Sections[sectionName].Parameters.Any(param => param.Name == parameterName))
                {
                    if (!string.IsNullOrEmpty(defaultValue))
                    {
                        return defaultValue;
                    }

                    return null;
                }

                string setting = serviceConfiguration.Settings.Sections[sectionName].Parameters[parameterName]?.Value;

                if (string.IsNullOrEmpty(setting) && defaultValue != null)
                {
                    return defaultValue;
                }

                return setting;
            }
            catch (ArgumentException)
            {
            }
            catch (KeyNotFoundException)
            { 
            }
            catch (NullReferenceException)
            { 
            }

            return null;
        }

        /// <summary>
        /// Gets a dictionary of Parameters of the specified section.
        /// </summary>
        /// <param name="sectionName">Name of the section.</param>
        /// <returns>A dictionary of Parameters key/value pairs (string, string) or null upon failure.</returns>
        public IDictionary<string, string> GetConfigSettingSectionParameters(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName))
            {
                return null;
            }

            IDictionary<string, string> container = new Dictionary<string, string>();

            var serviceConfiguration = this.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config");

            var sections = serviceConfiguration.Settings.Sections.FirstOrDefault(sec => sec.Name == sectionName);

            if (sections == null)
            {
                return null;
            }

            foreach (var param in sections.Parameters)
            {
                container.Add(param.Name, param.Value);
            }

            return container;
        }

        /// <summary>
        /// Gets the interval at which the Observer is to be run, i.e. "no more often than."
        /// This is useful for Observers that do not need to run very often (a la OSObserver, Certificate Observer, etc.)
        /// </summary>
        /// <param name="configSectionName">Observer configuration section name.</param>
        /// <param name="configParamName">Observer configuration parameter name.</param>
        /// <param name="defaultTo">Specify an optional default TimeSpan if the setting is not defined in config.
        /// Otherwise, this defaults to 24 hours.</param>
        /// <returns>Run interval as TimeSpan.</returns>
        public TimeSpan GetObserverRunInterval(
            string configSectionName,
            string configParamName,
            TimeSpan? defaultTo = null)
        {
            TimeSpan interval;

            try
            {
                interval = TimeSpan.Parse(
                    this.GetSettingParameterValue(
                                          configSectionName,
                                          configParamName),
                    CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is FormatException || e is OverflowException)
                {
                    // Parameter is not present or invalid, default to 24 hours or supplied defaultTo
                    if (defaultTo != null)
                    {
                        interval = (TimeSpan)defaultTo;
                    }
                    else
                    {
                        interval = TimeSpan.FromDays(1);
                    }
                }
                else
                {
                   
                    throw;
                }
            }

            return interval;
        }

        // This is here so each Observer doesn't have to implement IDisposable.
        // If an Observer needs to dispose, then override this non-impl.
        private bool disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    if (this.FabricClientInstance != null)
                    {
                        this.FabricClientInstance.Dispose();
                        this.FabricClientInstance = null;
                    }
                }

                this.disposedValue = true;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            this.Dispose(true);
        }
    }
}