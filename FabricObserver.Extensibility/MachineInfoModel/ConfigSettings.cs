// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using System.Fabric.Description;
using FabricObserver.Observers.Utilities;

namespace FabricObserver.Observers.MachineInfoModel
{
    public class ConfigSettings
    {
        public string ConfigPackagePath =>
            serviceContext.CodePackageActivationContext.
                GetConfigurationPackageObject(ObserverConstants.ObserverConfigurationPackageName)?.Path;

        public string AppObserverConfigFileName
        {
            get; set;
        }

        public string NetworkObserverConfigFileName
        {
            get; set;
        }

        public ConfigSettings(StatelessServiceContext context)
        {
            serviceContext = context;
        }

        public void Initialize(
            ConfigurationSettings configSettings,
            string configurationSectionName,
            string dataFileName)
        {
            configurationSettings = configSettings;

            switch (configurationSectionName)
            {
                case ObserverConstants.AppObserverConfigurationSectionName:
                    AppObserverConfigFileName = new ConfigurationSetting<string>(
                        configSettings,
                        configurationSectionName,
                        dataFileName,
                        string.Empty).Value;

                    break;

                case ObserverConstants.NetworkObserverConfigurationSectionName:
                    NetworkObserverConfigFileName = new ConfigurationSetting<string>(
                        configSettings,
                        configurationSectionName,
                        dataFileName,
                        string.Empty).Value;
                    break;
            }
        }

        internal void UpdateCommonConfigurationSettings(
            ConfigurationSettings newConfigurationSettings,
            string configurationSectionName,
            string dataFileName)
        {
            configurationSettings = newConfigurationSettings;

            switch (configurationSectionName)
            {
                // Fabric Client settings
                case ObserverConstants.AppObserverConfigurationSectionName:
                    AppObserverConfigFileName = new ConfigurationSetting<string>(
                        configurationSettings,
                        configurationSectionName,
                        dataFileName,
                        string.Empty).Value;
                    break;

                case ObserverConstants.NetworkObserverConfigurationSectionName:
                    NetworkObserverConfigFileName = new ConfigurationSetting<string>(
                        configurationSettings,
                        configurationSectionName,
                        dataFileName,
                        string.Empty).Value;

                    break;
            }
        }

        private ConfigurationSettings configurationSettings;
        private readonly StatelessServiceContext serviceContext;
    }
}

