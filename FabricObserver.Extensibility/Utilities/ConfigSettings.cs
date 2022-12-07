// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    public class ConfigSettings
    {
        public TimeSpan RunInterval
        {
            get; set;
        }

        public TimeSpan MonitorDuration
        {
            get; set;
        }

        // Default enablement for any observer is enabled (true).
        public bool IsEnabled
        {
            get; set;
        } = true;

        public bool EnableVerboseLogging
        {
            get; set;
        }

        public bool EnableCsvLogging
        {
            get; set;
        }

        public bool IsObserverTelemetryEnabled
        {
            get;
            private set;
        }

        public TimeSpan AsyncTimeout
        {
            get;
            private set;
        } = TimeSpan.FromSeconds(60);

        public int DataCapacity
        {
            get; set;
        }

        public bool UseCircularBuffer
        {
            get; set;
        }

        public bool IsObserverEtwEnabled
        {
            get;
            private set;
        }

        public ConfigurationSection ConfigSection
        {
            get; set;
        }

        public ConfigSettings(ConfigurationSettings settings, string observerConfiguration)
        {
            if (settings == null || string.IsNullOrWhiteSpace(observerConfiguration) || !settings.Sections.Contains(observerConfiguration))
            {
                return;
            }

            ConfigSection = settings.Sections[observerConfiguration];
            SetConfigSettings();
        }

        private void SetConfigSettings()
        {
            // Observer enabled?
            if (bool.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.ObserverEnabledParameter),
                    out bool enabled))
            {
                IsEnabled = enabled;
            }

            // Observer telemetry enabled?
            if (bool.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.ObserverTelemetryEnabledParameter),
                    out bool telemetryEnabled))
            {
                IsObserverTelemetryEnabled = telemetryEnabled;
            }

            // Observer etw enabled?
            if (bool.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.ObserverEtwEnabledParameter),
                    out bool etwEnabled))
            {
                IsObserverEtwEnabled = etwEnabled;
            }

            // Verbose logging?
            if (bool.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.EnableVerboseLoggingParameter),
                    out bool enableVerboseLogging))
            {
                EnableVerboseLogging = enableVerboseLogging;
            }

            // CSV Logging?
            if (bool.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.EnableCSVDataLogging),
                    out bool enableCsvLogging))
            {
                EnableCsvLogging = enableCsvLogging;
            }

            // RunInterval?
            if (TimeSpan.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.ObserverRunIntervalParameter),
                    out TimeSpan runInterval))
            {
                RunInterval = runInterval;
            }

            // Monitor duration.
            if (TimeSpan.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.MonitorDurationParameter),
                    out TimeSpan monitorDuration))
            {
                MonitorDuration = monitorDuration;
            }

            // Async cluster operation timeout setting..
            if (int.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.AsyncClusterOperationTimeoutSeconds),
                    out int asyncOpTimeoutSeconds))
            {
                AsyncTimeout = TimeSpan.FromSeconds(asyncOpTimeoutSeconds);
            }

            // Resource usage data collection item capacity.
            if (int.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.DataCapacityParameter),
                    out int dataCapacity))
            {
                DataCapacity = dataCapacity;
            }

            // Resource usage data collection type.
            if (bool.TryParse(
                    GetConfigSettingValue(
                    ObserverConstants.UseCircularBufferParameter),
                    out bool useCircularBuffer))
            {
                UseCircularBuffer = useCircularBuffer;
            }
        }

        public void UpdateConfigSettings(IEnumerable<ConfigurationProperty> props)
        {
            foreach (var prop in props)
            {
                // Observer enabled?
                if (prop.Name == ObserverConstants.ObserverEnabledParameter)    
                {
                    IsEnabled = bool.TryParse(prop.Value, out bool enabled) && enabled;
                }

                // TelemetryEnabled?
                else if (prop.Name == ObserverConstants.ObserverTelemetryEnabledParameter)
                {
                    IsObserverTelemetryEnabled = bool.TryParse(prop.Value, out bool telemEnabled) && telemEnabled;
                }

                // Observer etw enabled?
                else if (prop.Name == ObserverConstants.ObserverEtwEnabledParameter)
                {
                    IsObserverEtwEnabled = bool.TryParse(prop.Value, out bool etwEnabled) && etwEnabled;
                }

                // Verbose logging?
                else if (prop.Name == ObserverConstants.EnableVerboseLoggingParameter)
                {
                    EnableVerboseLogging = bool.TryParse(prop.Value, out bool enableVerboseLogging) && enableVerboseLogging;
                }

                // CSV Logging?
                else if (prop.Name == ObserverConstants.EnableCSVDataLogging)
                {
                    EnableCsvLogging = bool.TryParse(prop.Value, out bool enableCsvLogging) && enableCsvLogging;
                }

                // RunInterval?
                else if (prop.Name == ObserverConstants.ObserverRunIntervalParameter)
                {
                    if (TimeSpan.TryParse(prop.Value, out TimeSpan runInterval))
                    {
                        RunInterval = runInterval;
                    }
                }

                // Monitor duration.
                else if (prop.Name == ObserverConstants.MonitorDurationParameter)
                {
                    if (TimeSpan.TryParse(prop.Value, out TimeSpan monitorDuration))
                    {
                        MonitorDuration = monitorDuration;
                    }
                }

                // Async cluster operation timeout setting..
                else if (prop.Name == ObserverConstants.AsyncClusterOperationTimeoutSeconds)
                {
                    if (int.TryParse(prop.Value, out int asyncOpTimeoutSeconds))
                    {
                        AsyncTimeout = TimeSpan.FromSeconds(asyncOpTimeoutSeconds);
                    }
                }

                // Resource usage data collection item capacity.
                else if (prop.Name == ObserverConstants.DataCapacityParameter)
                {
                    if (int.TryParse(prop.Value, out int dataCapacity))
                    {
                        DataCapacity = dataCapacity;
                    }
                }

                // Resource usage data collection type.
                if (prop.Name == ObserverConstants.UseCircularBufferParameter)
                {
                    UseCircularBuffer = bool.TryParse(prop.Value, out bool useCircularBuffer) && useCircularBuffer;
                }
            }
        }

        private string GetConfigSettingValue(string parameterName)
        {
            try
            {
                if (ConfigSection.Parameters.Any(p => p.Name == parameterName))
                {
                    return ConfigSection.Parameters[parameterName]?.Value;
                }
            }
            catch (Exception e) when (e is KeyNotFoundException || e is FabricElementNotFoundException)
            {

            }

            return null;
        }
    }
}