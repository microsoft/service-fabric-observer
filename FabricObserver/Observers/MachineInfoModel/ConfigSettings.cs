// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using System.Fabric.Description;

namespace FabricObserver.Model
{
    public static class ConfigSettings
    {
        // Default Fabric config name...
        public const string ConfigPackageName = "Config";
        
        // AppObserver
        public const string AppObserverConfiguration = "AppObserverConfiguration";
  
        // NetworkObserver
        public const string NetworkObserverConfiguration = "NetworkObserverConfiguration";
        

        public static string ConfigPackagePath
        {
            get { return FabricRuntime.GetActivationContext().GetConfigurationPackageObject(ConfigPackageName).Path; }
        }

        public static string AppObserverDataFileName { get; set; } = null;
        public static string NetworkObserverDataFileName { get; set; } = null;

        public static void Initialize(ConfigurationSettings configurationSettings,
                                      string configurationSectionName,
                                      string dataFileName)
        {
            ConfigSettings.configurationSettings = configurationSettings;

            if (configurationSectionName == AppObserverConfiguration)
            {
                AppObserverDataFileName = new ConfigurationSetting<string>(
                                            configurationSettings,
                                            configurationSectionName,
                                            dataFileName,
                                            "").Value;
            }
            else if (configurationSectionName == NetworkObserverConfiguration)
            {
                NetworkObserverDataFileName = new ConfigurationSetting<string>(
                                                configurationSettings,
                                                configurationSectionName,
                                                dataFileName,
                                                "").Value;
            }
        }

        internal static void UpdateCommonConfigurationSettings(ConfigurationSettings newConfigurationSettings,
                                                               string configurationSectionName,
                                                               string dataFileName)
        {
            configurationSettings = newConfigurationSettings;

            // Fabric Client settings
            if (configurationSectionName == AppObserverConfiguration)
            {
                AppObserverDataFileName = new ConfigurationSetting<string>(
                                            configurationSettings,
                                            configurationSectionName,
                                            dataFileName,
                                            "").Value;
            }
            else if (configurationSectionName == NetworkObserverConfiguration)
            {
                NetworkObserverDataFileName = new ConfigurationSetting<string>(
                                                configurationSettings,
                                                configurationSectionName,
                                                dataFileName,
                                                "").Value;
            }
        }

        private static ConfigurationSettings configurationSettings;
    }
}
