// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Linq;
using System.Threading;

namespace ClusterObserver.Utilities
{
    public class ConfigSettings
    {
        private ConfigurationSettings Settings
        {
            get; set;
        }

        private ConfigurationSection Section
        {
            get; set;
        }

        public TimeSpan RunInterval
        {
            get; set;
        }

        public bool IsEnabled
        {
            get; set;
        } = true;

        public bool EnableVerboseLogging
        {
            get; set;
        }

        public bool EmitWarningDetails
        {
            get; set;
        }

        public TimeSpan AsyncTimeout
        {
            get; set;
        }

        public TimeSpan MaxTimeNodeStatusNotOk
        {
            get; set;
        } = TimeSpan.FromHours(2.0);

        public ConfigSettings(ConfigurationSettings settings, string observerConfiguration)
        {
            Settings = settings;
            Section = settings?.Sections[observerConfiguration];

            UpdateConfigSettings();
        }

        public void UpdateConfigSettings(ConfigurationSettings settings = null)
        {
            if (settings != null)
            {
                Settings = settings;
            }

            // Observer enabled?
            if (bool.TryParse(
                GetConfigSettingValue(
                ObserverConstants.ObserverEnabled),
                out bool enabled))
            {
                IsEnabled = enabled;
            }
            
            // Verbose logging?
            if (bool.TryParse(
                GetConfigSettingValue(
                ObserverConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                EnableVerboseLogging = enableVerboseLogging;
            }

            // RunInterval?
            if (TimeSpan.TryParse(
                GetConfigSettingValue(
                ObserverConstants.ObserverRunIntervalParameterName),
                out TimeSpan runInterval))
            {
                RunInterval = runInterval;
            }

            // Async cluster operation timeout setting.
            if (int.TryParse(
                GetConfigSettingValue(
                ObserverConstants.AsyncOperationTimeoutSeconds),
                out int asyncOpTimeoutSeconds))
            {
                AsyncTimeout = TimeSpan.FromSeconds(asyncOpTimeoutSeconds);
            }

            // Get ClusterObserver settings (specified in PackageRoot/Config/Settings.xml).
            if (bool.TryParse(
                GetConfigSettingValue(
                    ObserverConstants.EmitHealthWarningEvaluationConfigurationSetting),
                    out bool emitWarningDetails))
            {
                EmitWarningDetails = emitWarningDetails;
            }

            if (TimeSpan.TryParse(
                GetConfigSettingValue(
                   ObserverConstants.MaxTimeNodeStatusNotOkSetting),
                   out TimeSpan maxTimeNodeStatusNotOk))
            {
                MaxTimeNodeStatusNotOk = maxTimeNodeStatusNotOk;
            }
        }

        private string GetConfigSettingValue(string parameterName)
        {
            try
            {
                var configSettings = Settings;

                if (configSettings == null || string.IsNullOrEmpty(Section.Name))
                {
                    return null;
                }

                if (Section == null)
                {
                    return null;
                }

                ConfigurationProperty parameter = null;

                if (Section.Parameters.Any(p => p.Name == parameterName))
                {
                    parameter = Section.Parameters[parameterName];
                }

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
