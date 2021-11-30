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
        /// <param name="logger">File logger instance. Will throw ArgumentException if null.</param>
        /// <param name="fabricClient">FabricClient instance. Will throw ArgumentException if null.</param>
        public ObserverHealthReporter(Logger logger, FabricClient fabricClient)
        {
            this.fabricClient = fabricClient ?? throw new ArgumentException("fabricClient can't be null.");
            this.logger = logger ?? throw new ArgumentException("logger can't be null.");
            this.fabricClient.Settings.HealthReportSendInterval = TimeSpan.FromSeconds(1);
            this.fabricClient.Settings.HealthReportRetrySendInterval = TimeSpan.FromSeconds(3);
        }

        /// <summary>
        /// This function generates Service Fabric Health Reports that will show up in SFX. It supports generating health reports for the following SF entities:
        /// Application, DeployedApplication, Node, Partition, Service, StatefulService, StatelessService.
        /// </summary>
        /// <param name="healthReport">Utilities.HealthReport instance.</param>
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

            if (healthReport.State == HealthState.Error || healthReport.State == HealthState.Warning)
            {
                // OSObserver does not monitor resources and therefore does not support related usage threshold configuration.
                if (healthReport.Observer == ObserverConstants.OSObserverName && healthReport.Property == "OSConfiguration")
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
                switch(healthReport.Observer)
                {
                    case ObserverConstants.AppObserverName:
                        healthReport.Property = "ApplicationHealth";
                        break;
                    case ObserverConstants.CertificateObserverName:
                        healthReport.Property = "SecurityHealth";
                        break;
                    case ObserverConstants.DiskObserverName:
                        healthReport.Property = "DiskHealth";
                        break;
                    case ObserverConstants.FabricSystemObserverName:
                        healthReport.Property = "FabricSystemServiceHealth";
                        break;
                    case ObserverConstants.NetworkObserverName:
                        healthReport.Property = "NetworkHealth";
                        break;
                    case ObserverConstants.OSObserverName:
                        healthReport.Property = "MachineInformation";
                        break;
                    case ObserverConstants.NodeObserverName:
                        healthReport.Property = "MachineResourceHealth";
                        break;

                    default:
                        healthReport.Property = $"{healthReport.Observer}_{(!string.IsNullOrWhiteSpace(healthReport.ResourceUsageDataProperty) ? healthReport.ResourceUsageDataProperty : "GenericHealth")}";
                        break;

                }
            }

            var healthInformation = new HealthInformation(healthReport.SourceId, healthReport.Property, healthReport.State)
            {
                Description = $"{message}",
                TimeToLive = timeToLive,
                RemoveWhenExpired = true
            };

            // Log health event locally.
            if (healthReport.EmitLogEvent)
            {
                switch (healthReport.State)
                {
                    case HealthState.Error:
                        logger.LogError(healthReport.NodeName + ": {0}", healthInformation.Description);
                        break;

                    case HealthState.Warning:
                        logger.LogWarning(healthReport.NodeName + ": {0}", healthInformation.Description);
                        break;

                    default:
                        logger.LogInfo(healthReport.NodeName + ": {0}", healthInformation.Description);
                        break;
                }
            }

            try
            {
                switch (healthReport.ReportType)
                {
                    // To SFX.
                    case HealthReportType.Application when healthReport.AppName != null:

                        var appHealthReport = new ApplicationHealthReport(healthReport.AppName, healthInformation);
                        fabricClient.HealthManager.ReportHealth(appHealthReport, sendOptions);
                        break;

                    case HealthReportType.Service when healthReport.ServiceName != null:

                        var serviceHealthReport = new ServiceHealthReport(healthReport.ServiceName, healthInformation);
                        fabricClient.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                        break;

                    case HealthReportType.StatefulService when healthReport.PartitionId != Guid.Empty && healthReport.ReplicaOrInstanceId > 0:

                        var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(healthReport.PartitionId, healthReport.ReplicaOrInstanceId, healthInformation);
                        fabricClient.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                        break;

                    case HealthReportType.StatelessService when healthReport.PartitionId != Guid.Empty && healthReport.ReplicaOrInstanceId > 0:

                        var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(healthReport.PartitionId, healthReport.ReplicaOrInstanceId, healthInformation);
                        fabricClient.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                        break;

                    case HealthReportType.Partition when healthReport.PartitionId != Guid.Empty:
                        var partitionHealthReport = new PartitionHealthReport(healthReport.PartitionId, healthInformation);
                        fabricClient.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                        break;

                    case HealthReportType.DeployedApplication when healthReport.AppName != null:

                        var deployedApplicationHealthReport = new DeployedApplicationHealthReport(healthReport.AppName, healthReport.NodeName, healthInformation);
                        fabricClient.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                        break;

                    case HealthReportType.Node:
                        var nodeHealthReport = new NodeHealthReport(healthReport.NodeName, healthInformation);
                        fabricClient.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                        break;
                }
            }
            catch (FabricException fe)
            {
                logger.LogWarning($"Handled Exception while generating SF health report:{Environment.NewLine}{fe}");
            }
        }
    }

    public enum HealthReportType
    {
        Application,
        Node,
        Service,
        StatefulService,
        StatelessService,
        Partition,
        DeployedApplication
    }
}
