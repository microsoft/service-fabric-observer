// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;

namespace FabricObserver.Observers.Utilities
{
    public class ConfigSettings
    {
        private ConfigurationSection section
        {
            get; set;
        }
        
        public TimeSpan RunInterval
        {
            get; set;
        }

        public TimeSpan MonitorDuration
        {
            get; set;
        }

        public bool IsEnabled
        {
            get; set;
        }

        public bool EnableVerboseLogging
        {
            get; set;
        }

        public bool IsObserverTelemetryEnabled
        {
            get; set;
        }

        public TimeSpan AsyncTimeout
        {
            get; set;
        } = TimeSpan.FromSeconds(60);

        public int DataCapacity
        {
            get;
            private set;
        }

        public ConfigurationSettings Settings
        {
            get; private set;
        }

        public bool UseCircularBuffer
        {
            get;
            private set;
        }

        public ConfigSettings(ConfigurationSettings settings, string observerConfiguration)
        {
            this.Settings = settings;
            this.section = settings.Sections[observerConfiguration];

            UpdateConfigSettings();
        }

        public void UpdateConfigSettings(
            ConfigurationSettings settings = null)
        {
            if (settings != null)
            {
                this.Settings = settings;
            }

            // Observer enabled?
            if (bool.TryParse(
                this.GetConfigSettingValue(
                ObserverConstants.ObserverEnabledParameter),
                out bool enabled))
            {
                this.IsEnabled = enabled;
            }

            // Observer telemetry enabled?
            if (bool.TryParse(
                this.GetConfigSettingValue(
                ObserverConstants.ObserverTelemetryEnabledParameter),
                out bool telemetryEnabled))
            {
                this.IsObserverTelemetryEnabled = telemetryEnabled;
            }

            // Verbose logging?
            if (bool.TryParse(
                this.GetConfigSettingValue(
                ObserverConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                this.EnableVerboseLogging = enableVerboseLogging;
            }

            // RunInterval?
            if (TimeSpan.TryParse(
                this.GetConfigSettingValue(
                ObserverConstants.ObserverRunIntervalParameter),
                out TimeSpan runInterval))
            {
                this.RunInterval = runInterval;
            }

            // Monitor duration.
            if (TimeSpan.TryParse(
                this.GetConfigSettingValue(
                ObserverConstants.MonitorDurationParameter),
                out TimeSpan monitorDuration))
            {
                this.MonitorDuration = monitorDuration;
            }

            // Async cluster operation timeout setting..
            if (int.TryParse(
                this.GetConfigSettingValue(
                ObserverConstants.AsyncClusterOperationTimeoutSeconds),
                out int asyncOpTimeoutSeconds))
            {
                this.AsyncTimeout = TimeSpan.FromSeconds(asyncOpTimeoutSeconds);
            }

            // Resource usage data collection item capacity.
            if (int.TryParse(
               this.GetConfigSettingValue(
               ObserverConstants.DataCapacityParameter),
               out int dataCapacity))
            {
                this.DataCapacity = dataCapacity;
            }

            // Resource usage data collection type.
            if (bool.TryParse(
                this.GetConfigSettingValue(
                ObserverConstants.UseCircularBufferParameter),
                out bool useCircularBuffer))
            {
                this.UseCircularBuffer = useCircularBuffer;
            }
        }

        private string GetConfigSettingValue(string parameterName)
        {
            try
            {
                var configSettings = this.Settings;

                if (configSettings == null || string.IsNullOrEmpty(this.section.Name))
                {
                    return null;
                }
               
                if (this.section == null)
                {
                    return null;
                }

                var parameter = this.section.Parameters[parameterName];

                if (parameter == null)
                {
                    return null;
                }

                return parameter.Value;
            }
            catch (Exception e) when (e is KeyNotFoundException || e is FabricElementNotFoundException)
            {

            }

            return null;
        }
    }
}