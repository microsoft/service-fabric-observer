// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;

namespace FabricObserver.Utilities
{
    /// <summary>
    /// Reports health data to Service Fabric Health Manager and logs locally (optional)...
    /// </summary>
    public class ObserverHealthReporter
    {
        private readonly Logger logger;
        private FabricClient fabricClient = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverHealthReporter"/> class.
        /// </summary>
        /// <param name="logger">file logger instance...</param>
        public ObserverHealthReporter(Logger logger)
        {
            this.fabricClient = ObserverManager.FabricClientInstance;
            this.fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
            this.fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
            this.logger = logger;
        }

        /// <summary>
        /// Report FabricObserver service health as log event (not to SF Health)...
        /// </summary>
        /// <param name="serviceName">Name of the service.</param>
        /// <param name="propertyName">Name of the health property.</param>
        /// <param name="healthState">Health state (Ok, Error, etc).</param>
        /// <param name="description">Description of the health condition.</param>
        public void ReportFabricObserverServiceHealth(
            string serviceName,
            string propertyName,
            HealthState healthState,
            string description)
        {
            if (healthState == HealthState.Error)
            {
                this.logger.LogError("FabricObserver service health error: " + serviceName + " | " + propertyName + "{0}", description);
            }

            if (healthState == HealthState.Warning)
            {
                this.logger.LogWarning("FabricObserver service health warning: " + serviceName + " | " + propertyName + "{0}", description);
            }
        }

        public void ReportHealthToServiceFabric(Utilities.HealthReport healthReport)
        {
            if (healthReport == null)
            {
                return;
            }

            // There is no real need to change Immediate to true here for errors/warnings. This only adds unecessary stress to the
            // Health subsystem...
            var sendOptions = new HealthReportSendOptions { Immediate = false };

            // Quickly send OK (clears warning/errors states)...
            if (healthReport.State == HealthState.Ok)
            {
                sendOptions.Immediate = true;
            }

            var timeToLive = TimeSpan.FromMinutes(5);

            if (healthReport.HealthReportTimeToLive != default(TimeSpan))
            {
                timeToLive = healthReport.HealthReportTimeToLive;
            }

            string kind = string.Empty;

            if (healthReport.Code != null)
            {
                kind = healthReport.Code + ": ";
            }

            string source = healthReport.Observer;
            string property;

            // In order for multiple Error/Warning/Ok events to show up in SFX Details view from observer instances,
            // Event Source Ids must be unique, thus the seemingly strange conditionals inside the cases below:
            // The apparent duplicity in OR checks is for the case when the incoming report is an OK report, where there is 
            // no error code, but the specific ErrorWarningProperty is known...
            switch (healthReport.Observer)
            {
                case ObserverConstants.AppObserverName:
                    property = "AppHealth";

                    if (healthReport.Code == ErrorWarningCode.WarningCpuTime
                        || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalCpuTime)
                    {
                        source += "(CPU)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningMemoryPercentUsed
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalMemoryConsumptionPct)
                    {
                        source += "(Memory%)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningMemoryCommitted
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalMemoryConsumptionMB)
                    {
                        source += "(MemoryMB)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningTooManyActiveEphemeralPorts
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalEphemeralPorts)
                    {
                        source += "(ActiveEphemeralPorts)";
                    }

                    break;
                case ObserverConstants.CertificateObserverName:
                    property = "SecurityHealth";
                    break;
                case ObserverConstants.DiskObserverName:
                    property = "DiskHealth";

                    if (healthReport.Code == ErrorWarningCode.WarningDiskAverageQueueLength
                        || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.DiskAverageQueueLength)
                    {
                        source += "(DiskQueueLength)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningDiskSpacePercentUsed
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.DiskSpaceUsagePercentage)
                    {
                        source += "(DiskSpace%)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningDiskSpaceMB
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.DiskSpaceUsageMB)
                    {
                        source += "(DiskSpaceMB)";
                    }

                    break;
                case ObserverConstants.FabricSystemObserverName:
                    property = "FabricSystemServiceHealth";

                    if (healthReport.Code == ErrorWarningCode.WarningCpuTime
                        || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalCpuTime)
                    {
                        source += "(CPU)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningMemoryPercentUsed
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalMemoryConsumptionPct)
                    {
                        source += "(Memory%)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningMemoryCommitted
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalMemoryConsumptionMB)
                    {
                        source += "(MemoryMB)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningTooManyActiveTcpPorts
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalActivePorts)
                    {
                        source += "(ActivePorts)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningTooManyActiveEphemeralPorts
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalEphemeralPorts)
                    {
                        source += "(ActiveEphemeralPorts)";
                    }

                    break;
                case ObserverConstants.NetworkObserverName:
                    property = "NetworkingHealth";
                    break;
                case ObserverConstants.OSObserverName:
                    property = "MachineInformation";
                    break;
                case ObserverConstants.NodeObserverName:
                    property = "MachineResourceHealth";

                    if (healthReport.Code == ErrorWarningCode.WarningCpuTime
                        || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalCpuTime)
                    {
                        source += "(CPU)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningTooManyFirewallRules
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalActiveFirewallRules)
                    {
                        source += "(FirewallRules)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningMemoryPercentUsed
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalMemoryConsumptionPct)
                    {
                        source += "(Memory%)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningMemoryCommitted
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalMemoryConsumptionMB)
                    {
                        source += "(MemoryMB)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningTooManyActiveTcpPorts
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalActivePorts)
                    {
                        source += "(ActivePorts)";
                    }
                    else if (healthReport.Code == ErrorWarningCode.WarningTooManyActiveEphemeralPorts
                             || healthReport.ResourceUsageDataProperty == ErrorWarningProperty.TotalEphemeralPorts)
                    {
                        source += "(ActiveEphemeralPorts)";
                    }

                    break;
                default:
                    property = "FOGenericHealth";
                    break;
            }

            var healthInformation = new HealthInformation(source, property, healthReport.State)
            {
                Description = kind + healthReport.HealthMessage,
                TimeToLive = timeToLive,
                RemoveWhenExpired = true,
            };

            // Log event only if ObserverWebApi (REST Log reader...) app is deployed...
            if (ObserverManager.ObserverWebAppDeployed
                && healthReport.EmitLogEvent)
            {
                if (healthReport.State == HealthState.Error)
                {
                    this.logger.LogError(healthReport.NodeName + ": {0}", healthInformation.Description);
                }
                else if (healthReport.State == HealthState.Warning)
                {
                    this.logger.LogWarning(healthReport.NodeName + ": {0}", healthInformation.Description);
                }
                else
                {
                    this.logger.LogInfo(healthReport.NodeName + ": {0}", healthInformation.Description);
                }
            }

            // To SFX and Telemetry provider...
            if (healthReport.ReportType == HealthReportType.Application && healthReport.AppName != null)
            {
                var report = new ApplicationHealthReport(healthReport.AppName, healthInformation);
                this.fabricClient.HealthManager.ReportHealth(report, sendOptions);
            }
            else
            {
                var report = new NodeHealthReport(healthReport.NodeName, healthInformation);
                this.fabricClient.HealthManager.ReportHealth(report, sendOptions);
            }
        }
    }

    public enum HealthReportType
    {
        Application,
        Node,
    }
}
