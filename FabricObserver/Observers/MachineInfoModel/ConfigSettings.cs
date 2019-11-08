// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric.Description;
using FabricObserver.Utilities;

namespace FabricObserver.Model
{
    public static class ConfigSettings
    {
        public static string ConfigPackagePath
        {
            get
            {
                return ObserverManager.FabricServiceContext.CodePackageActivationContext.
                    GetConfigurationPackageObject(ObserverConstants.ObserverConfigurationPackageName)?.Path;
            }
        }

        public static string AppObserverDataFileName { get; set; } = null;

        public static string NetworkObserverDataFileName { get; set; } = null;

        public static void Initialize(
            ConfigurationSettings configurationSettings,
            string configurationSectionName,
            string dataFileName)
        {
            ConfigSettings.configurationSettings = configurationSettings;

            if (configurationSectionName == ObserverConstants.AppObserverConfigurationSectionName)
            {
                AppObserverDataFileName = new ConfigurationSetting<string>(
                    configurationSettings,
                    configurationSectionName,
                    dataFileName,
                    string.Empty).Value;
            }
            else if (configurationSectionName == ObserverConstants.NetworkObserverConfigurationSectionName)
            {
                NetworkObserverDataFileName = new ConfigurationSetting<string>(
                    configurationSettings,
                    configurationSectionName,
                    dataFileName,
                    string.Empty).Value;
            }
        }

        internal static void UpdateCommonConfigurationSettings(
            ConfigurationSettings newConfigurationSettings,
            string configurationSectionName,
            string dataFileName)
        {
            configurationSettings = newConfigurationSettings;

            // Fabric Client settings
            if (configurationSectionName == ObserverConstants.AppObserverConfigurationSectionName)
            {
                AppObserverDataFileName = new ConfigurationSetting<string>(
                    configurationSettings,
                    configurationSectionName,
                    dataFileName,
                    string.Empty).Value;
            }
            else if (configurationSectionName == ObserverConstants.NetworkObserverConfigurationSectionName)
            {
                NetworkObserverDataFileName = new ConfigurationSetting<string>(
                    configurationSettings,
                    configurationSectionName,
                    dataFileName,
                    string.Empty).Value;
            }
        }

        private static ConfigurationSettings configurationSettings;
    }
}
