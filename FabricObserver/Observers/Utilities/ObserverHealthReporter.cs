// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using FabricObserver.Observers.Utilities.Telemetry;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Reports health data to Service Fabric Health Manager and logs locally (optional).
    /// </summary>
    public class ObserverHealthReporter
    {
        private readonly Logger logger;
        private readonly FabricClient fabricClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverHealthReporter"/> class.
        /// </summary>
        /// <param name="logger">file logger instance.</param>
        public ObserverHealthReporter(Logger logger)
        {
            this.fabricClient = ObserverManager.FabricClientInstance;
            this.fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
            this.fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
            this.logger = logger;
        }

        /// <summary>
        /// Report FabricObserver service health as log event (not to SF Health).
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
                this.logger.LogError("FabricObserver service health error: " + serviceName + " | " + propertyName + " | {0}", description);
            }

            if (healthState == HealthState.Warning)
            {
                this.logger.LogWarning("FabricObserver service health warning: " + serviceName + " | " + propertyName + " | {0}", description);
            }
        }

        public void ReportHealthToServiceFabric(HealthReport healthReport)
        {
            if (healthReport == null)
            {
                return;
            }

            // There is no real need to change Immediate to true here for errors/warnings. This only adds unecessary stress to the
            // Health subsystem.
            var sendOptions = new HealthReportSendOptions { Immediate = false };

            // Quickly send OK (clears warning/errors states).
            if (healthReport.State == HealthState.Ok)
            {
                sendOptions.Immediate = true;
            }

            var timeToLive = TimeSpan.FromMinutes(5);

            if (healthReport.HealthReportTimeToLive != default)
            {
                timeToLive = healthReport.HealthReportTimeToLive;
            }

            TelemetryData healthData = healthReport.HealthData;

            string errWarnPreamble = string.Empty;

            if (healthReport.State == HealthState.Error
                || healthReport.State == HealthState.Warning)
            {
                errWarnPreamble =
                    $"{healthReport.Observer} detected " +
                    $"{Enum.GetName(typeof(HealthState), healthReport.State)} threshold breach. ";

                // OSObserver does not monitor resources and therefore does not support related usage threshold configuration.
                if (healthReport.Observer == ObserverConstants.OSObserverName
                    && healthReport.Property == "OSConfiguration")
                {
                    errWarnPreamble = $"{ObserverConstants.OSObserverName} detected potential problem with OS configuration: ";
                }
            }

            string message = $"{errWarnPreamble}{healthReport.HealthMessage}";

            if (healthData != null)
            {
                message = JsonConvert.SerializeObject(healthData);
            }

            if (string.IsNullOrEmpty(healthReport.SourceId))
            {
                healthReport.SourceId = healthReport.Observer;
            }

            if (string.IsNullOrEmpty(healthReport.Property))
            {
                healthReport.Property = healthReport.Observer switch
                {
                    ObserverConstants.AppObserverName => "ApplicationHealth",
                    ObserverConstants.CertificateObserverName => "SecurityHealth",
                    ObserverConstants.DiskObserverName => "DiskHealth",
                    ObserverConstants.FabricSystemObserverName => "FabricSystemServiceHealth",
                    ObserverConstants.NetworkObserverName => "NetworkHealth",
                    ObserverConstants.OSObserverName => "MachineInformation",
                    ObserverConstants.NodeObserverName => "MachineResourceHealth",
                    _ => "FOGenericHealth",
                };
            }

            var healthInformation = new HealthInformation(healthReport.SourceId, healthReport.Property, healthReport.State)
            {
                Description = $"{message}",
                TimeToLive = timeToLive,
                RemoveWhenExpired = true,
            };

            // Log event only if ObserverWebApi (REST API Log reader service) app is deployed.
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

            // To SFX.
            if (healthReport.ReportType == HealthReportType.Application && healthReport.AppName != null)
            {
                var appHealthReport = new ApplicationHealthReport(healthReport.AppName, healthInformation);
                this.fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
            }
            else
            {
                var nodeHealthReport = new NodeHealthReport(healthReport.NodeName, healthInformation);
                this.fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
            }
        }
    }

    public enum HealthReportType
    {
        Application,
        Node,
    }
}
