// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Observers.Utilities.Telemetry;
using System;
using System.Fabric.Health;

namespace FabricObserver.Observers.Interfaces
{
    public interface ITelemetryData
    {
        /// <summary>
        /// Service Fabric ApplicationName as a string value. This would be the same value as the OriginalString property of the ApplicationName Uri instance.
        /// </summary>
        string ApplicationName { get; set; }
        /// <summary>
        /// The supported FO error code.
        /// </summary>
        string Code { get; set; }
        /// <summary>
        /// The id of the container.
        /// </summary>
        string ContainerId { get; set; }
        /// <summary>
        /// The description of the problem being reported.
        /// </summary>
        string Description { get; set; }
        /// <summary>
        /// The target Service Fabric entity type.
        /// </summary>
        EntityType EntityType { get; set; }
        /// <summary>
        /// The HealthState of the entity.
        /// </summary>
        HealthState HealthState { get; set; }
        /// <summary>
        /// The supported resource usage metric name.
        /// </summary>
        string Metric { get; set; }
        /// <summary>
        /// The name of the node where the entity resides.
        /// </summary>
        string NodeName { get; set; }
        /// <summary>
        /// The OS hosting Service Fabric. This is read-only.
        /// </summary>
        string OS { get; }
        /// <summary>
        /// The Partition Id where the replica or instance resides that is in Error or Warning state.
        /// </summary>
        Guid? PartitionId { get; set; }
        /// <summary>
        /// The host process id of the Service replica or instance.
        /// </summary>
        long ProcessId { get; set; }
        /// <summary>
        /// The Replica or Instance id of the target Service replica.
        /// </summary>
        long ReplicaId { get; set; }
        /// <summary>
        /// The name of the service as a string value. This would be the same value as the OriginalString property of the ServiceName Uri instance.
        /// </summary>
        string ServiceName { get; set; }
        /// <summary>
        /// This is the name of the entity that is generating the health report that houses this TelemetryData instance.
        /// </summary>
        string Source { get; set; }
        /// <summary>
        /// The supported resource usage metric value.
        /// </summary>
        double Value { get; set; }
        /// <summary>
        /// The Fabric node type.
        /// </summary>
        string NodeType { get; set; }
        /// <summary>
        /// This will be used as the Health Event Property.
        /// </summary>
        string Property { get; set; }
    }
}