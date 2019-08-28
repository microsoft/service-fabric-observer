// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Utilities;
using System.Fabric;
using System.Fabric.Description;

namespace FabricObserver.Model
{
    public static class ConfigSettings
    {   
        public static string ObserversConfigPackagePath
        {
            get
            {
                return ObserverManager.FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(ObserverConstants.ConfigPackageName)?.Path;
            }
        }

        public static string ObserversDataPackagePath
        {
            get
            {
                return ObserverManager.FabricServiceContext.CodePackageActivationContext.GetDataPackageObject(ObserverConstants.ObserverDataPackageName)?.Path;
            }
        }

        public static string AppObserverDataFileName { get; set; } = null;
        public static string NetworkObserverDataFileName { get; set; } = null;

        public static void Initialize(ConfigurationSettings configurationSettings,
                                      string configurationSectionName,
                                      string dataFileName)
        {
            ConfigSettings.configurationSettings = configurationSettings;

            if (configurationSectionName == ObserverConstants.AppObserverConfiguration)
            {
                AppObserverDataFileName = new ConfigurationSetting<string>(configurationSettings,
                                                                           configurationSectionName,
                                                                           dataFileName, "").Value;
            }
            else if (configurationSectionName == ObserverConstants.NetworkObserverConfiguration)
            {
                NetworkObserverDataFileName = new ConfigurationSetting<string>(configurationSettings,
                                                                               configurationSectionName,
                                                                               dataFileName, "").Value;
            }
        }

        internal static void UpdateCommonConfigurationSettings(ConfigurationSettings newConfigurationSettings,
                                                               string configurationSectionName,
                                                               string dataFileName)
        {
            configurationSettings = newConfigurationSettings;

            // Fabric Client settings
            if (configurationSectionName == ObserverConstants.AppObserverConfiguration)
            {
                AppObserverDataFileName = new ConfigurationSetting<string>(configurationSettings,
                                                                           configurationSectionName,
                                                                           dataFileName, "").Value;
            }
            else if (configurationSectionName == ObserverConstants.NetworkObserverConfiguration)
            {
                NetworkObserverDataFileName = new ConfigurationSetting<string>(configurationSettings,
                                                                               configurationSectionName,
                                                                               dataFileName, "").Value;
            }
        }

        private static ConfigurationSettings configurationSettings;
    }
}
