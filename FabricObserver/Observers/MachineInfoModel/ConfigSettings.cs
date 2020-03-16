// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric.Description;
using FabricObserver.Observers.Utilities;

namespace FabricObserver.Observers.MachineInfoModel
{
    public static class ConfigSettings
    {
        public static string ConfigPackagePath =>
            ObserverManager.FabricServiceContext.CodePackageActivationContext.
                GetConfigurationPackageObject(ObserverConstants.ObserverConfigurationPackageName)?.Path;

        public static string AppObserverDataFileName { get; set; }

        public static string NetworkObserverDataFileName { get; set; }

        public static void Initialize(
            ConfigurationSettings configSettings,
            string configurationSectionName,
            string dataFileName)
        {
            ConfigSettings.configurationSettings = configSettings;

            switch (configurationSectionName)
            {
                case ObserverConstants.AppObserverConfigurationSectionName:
                    AppObserverDataFileName = new ConfigurationSetting<string>(
                        configSettings,
                        configurationSectionName,
                        dataFileName,
                        string.Empty).Value;

                    break;

                case ObserverConstants.NetworkObserverConfigurationSectionName:
                    NetworkObserverDataFileName = new ConfigurationSetting<string>(
                        configSettings,
                        configurationSectionName,
                        dataFileName,
                        string.Empty).Value;
                    break;
            }
        }

        internal static void UpdateCommonConfigurationSettings(
            ConfigurationSettings newConfigurationSettings,
            string configurationSectionName,
            string dataFileName)
        {
            configurationSettings = newConfigurationSettings;

            switch (configurationSectionName)
            {
                // Fabric Client settings
                case ObserverConstants.AppObserverConfigurationSectionName:
                    AppObserverDataFileName = new ConfigurationSetting<string>(
                        configurationSettings,
                        configurationSectionName,
                        dataFileName,
                        string.Empty).Value;
                    break;

                case ObserverConstants.NetworkObserverConfigurationSectionName:
                    NetworkObserverDataFileName = new ConfigurationSetting<string>(
                        configurationSettings,
                        configurationSectionName,
                        dataFileName,
                        string.Empty).Value;

                    break;
            }
        }

        private static ConfigurationSettings configurationSettings;
    }
}
