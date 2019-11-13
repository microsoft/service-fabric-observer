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

            // There is no real need to change Immediate to true here. This only adds unecessary stress to the
            // Health subsystem...
            var sendOptions = new HealthReportSendOptions { Immediate = false };
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

            string property = null, source = healthReport.Observer;

            switch (healthReport.Observer)
            {
                case ObserverConstants.AppObserverName:
                    property = "AppHealth";
                    break;
                case ObserverConstants.CertificateObserverName:
                    property = "SecurityHealth";
                    break;
                case ObserverConstants.DiskObserverName:
                    property = "DiskHealth";
                    break;
                case ObserverConstants.FabricSystemObserverName:
                    property = "FabricSystemHealth";
                    break;
                case ObserverConstants.NetworkObserverName:
                    property = "NetworkingHealth";
                    break;
                case ObserverConstants.OSObserverName:
                    property = "MachineInformation";
                    break;
                case ObserverConstants.NodeObserverName:
                    property = "MachineResourceHealth";

                    if (healthReport.Code == ErrorWarningCode.WarningCpuTime)
                    {
                        source += "(CPU)";
                    }

                    if (healthReport.Code == ErrorWarningCode.WarningTooManyFirewallRules)
                    {
                        source += "(FirewallRules)";
                    }

                    if (healthReport.Code == ErrorWarningCode.WarningMemoryCommitted
                        || healthReport.Code == ErrorWarningCode.WarningMemoryPercentUsed)
                    {
                        source += "(Memory)";
                    }

                    if (healthReport.Code == ErrorWarningCode.WarningTooManyActivePorts)
                    {
                        source += "(Ports)";
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

            // Log event...
            if (healthReport.EmitLogEvent)
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
