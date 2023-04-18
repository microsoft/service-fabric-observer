// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.Utilities.ServiceFabric;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Reports health data to Service Fabric Health Manager and logs locally (optional).
    /// </summary>
    public class ObserverHealthReporter
    {
        private readonly Logger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverHealthReporter"/> class.
        /// </summary>
        /// <param name="logger">File logger instance. Will throw ArgumentException if null.</param>
        /// <param name="fabricClient">Unused. Exists for compatibility reasons for older plugin impls. Update your plugins to not pass a FabricClient instance
        /// to this constructor as the instance is not used anywhere in this type.</param>
        public ObserverHealthReporter(Logger logger, FabricClient fabricClient = null)
        {
            this.logger = logger;
        }

        /// <summary>
        /// This function generates Service Fabric Health Reports that will show up in SFX. It supports generating health reports for the following SF entities:
        /// Application, DeployedApplication, Node, Partition, Service, StatefulService, StatelessService.
        /// </summary>
        /// <param name="healthReport">Utilities.HealthReport instance.</param>
        public void ReportHealthToServiceFabric(HealthReport healthReport)
        {
            // Nothing to do here.
            if (healthReport == null || healthReport.EntityType == EntityType.Unknown && healthReport.ReportType == HealthReportType.Invalid)
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

            var healthData = healthReport.HealthData;
            string errWarnPreamble = string.Empty;

            if (healthReport.State is HealthState.Error or HealthState.Warning)
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
                healthReport.Property = healthReport.Observer switch
                {
                    ObserverConstants.AppObserverName => "ApplicationHealth",
                    ObserverConstants.CertificateObserverName => "SecurityHealth",
                    ObserverConstants.DiskObserverName => "DiskHealth",
                    ObserverConstants.FabricSystemObserverName => "FabricSystemServiceHealth",
                    ObserverConstants.NetworkObserverName => "NetworkHealth",
                    ObserverConstants.OSObserverName => "MachineInformation",
                    ObserverConstants.NodeObserverName => "MachineResourceHealth",
                    _ => $"{healthReport.Observer}_{(!string.IsNullOrWhiteSpace(healthReport.ResourceUsageDataProperty) ? healthReport.ResourceUsageDataProperty : "GenericHealth")}",
                };
            }

            var healthInformation = new HealthInformation(healthReport.SourceId, healthReport.Property, healthReport.State)
            {
                Description = message,
                TimeToLive = timeToLive,
                RemoveWhenExpired = true
            };

            // Log health event locally.
            if (healthReport.EmitLogEvent && logger != null)
            {
                switch (healthReport.State)
                {
                    case HealthState.Error:
                        logger?.LogError(healthReport.NodeName + ": {0}", healthInformation.Description);
                        break;

                    case HealthState.Warning:
                        logger?.LogWarning(healthReport.NodeName + ": {0}", healthInformation.Description);
                        break;

                    default:
                        logger?.LogInfo(healthReport.NodeName + ": {0}", healthInformation.Description);
                        break;
                }
            }

            try
            {
                if (healthReport.EntityType != EntityType.Unknown)
                {
                    switch (healthReport.EntityType)
                    {
                        case EntityType.Application when healthReport.AppName != null:

                            var appHealthReport = new ApplicationHealthReport(healthReport.AppName, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(appHealthReport, sendOptions);
                            break;

                        case EntityType.Service when healthReport.ServiceName != null:

                            var serviceHealthReport = new ServiceHealthReport(healthReport.ServiceName, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                            break;

                        case EntityType.StatefulService when healthReport.PartitionId != Guid.Empty && healthReport.ReplicaOrInstanceId > 0:

                            var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(healthReport.PartitionId, healthReport.ReplicaOrInstanceId, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                            break;

                        case EntityType.StatelessService when healthReport.PartitionId != Guid.Empty && healthReport.ReplicaOrInstanceId > 0:

                            var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(healthReport.PartitionId, healthReport.ReplicaOrInstanceId, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                            break;

                        case EntityType.Partition when healthReport.PartitionId != Guid.Empty:
                            var partitionHealthReport = new PartitionHealthReport(healthReport.PartitionId, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                            break;

                        case EntityType.DeployedApplication when healthReport.AppName != null && !string.IsNullOrWhiteSpace(healthReport.NodeName):

                            var deployedApplicationHealthReport = new DeployedApplicationHealthReport(healthReport.AppName, healthReport.NodeName, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                            break;

                        case EntityType.Disk:
                        case EntityType.Machine:
                        case EntityType.Node when !string.IsNullOrWhiteSpace(healthReport.NodeName):

                            var nodeHealthReport = new NodeHealthReport(healthReport.NodeName, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                            break;
                    }
                }
                else if (healthReport.ReportType != HealthReportType.Invalid) // For backwards compatibility.
                {
                    switch (healthReport.ReportType)
                    {
                        case HealthReportType.Application when healthReport.AppName != null:

                            var appHealthReport = new ApplicationHealthReport(healthReport.AppName, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(appHealthReport, sendOptions);
                            break;

                        case HealthReportType.Service when healthReport.ServiceName != null:

                            var serviceHealthReport = new ServiceHealthReport(healthReport.ServiceName, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(serviceHealthReport, sendOptions);
                            break;

                        case HealthReportType.StatefulService when healthReport.PartitionId != Guid.Empty && healthReport.ReplicaOrInstanceId > 0:

                            var statefulServiceHealthReport = new StatefulServiceReplicaHealthReport(healthReport.PartitionId, healthReport.ReplicaOrInstanceId, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(statefulServiceHealthReport, sendOptions);
                            break;

                        case HealthReportType.StatelessService when healthReport.PartitionId != Guid.Empty && healthReport.ReplicaOrInstanceId > 0:

                            var statelessServiceHealthReport = new StatelessServiceInstanceHealthReport(healthReport.PartitionId, healthReport.ReplicaOrInstanceId, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(statelessServiceHealthReport, sendOptions);
                            break;

                        case HealthReportType.Partition when healthReport.PartitionId != Guid.Empty:
                            var partitionHealthReport = new PartitionHealthReport(healthReport.PartitionId, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(partitionHealthReport, sendOptions);
                            break;

                        case HealthReportType.DeployedApplication when healthReport.AppName != null && !string.IsNullOrWhiteSpace(healthReport.NodeName):

                            var deployedApplicationHealthReport = new DeployedApplicationHealthReport(healthReport.AppName, healthReport.NodeName, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(deployedApplicationHealthReport, sendOptions);
                            break;

                        case HealthReportType.Node when !string.IsNullOrWhiteSpace(healthReport.NodeName):

                            var nodeHealthReport = new NodeHealthReport(healthReport.NodeName, healthInformation);
                            FabricClientUtilities.FabricClientSingleton.HealthManager.ReportHealth(nodeHealthReport, sendOptions);
                            break;
                    }
                }
            }
            catch (FabricException fe)
            {
                logger?.LogWarning($"Handled Exception while generating SF health report: {fe.Message}");
            }
        }
    }

    public enum HealthReportType
    {
        Invalid,
        Application,
        Node,
        Service,
        StatefulService,
        StatelessService,
        Partition,
        DeployedApplication
    }
}
