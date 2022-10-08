// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities.Telemetry
{
    /// <summary>
    /// Service Fabric entity types.
    /// </summary>
    public enum EntityType
    {
        /// <summary>
        /// Unknown (default value).
        /// </summary>
        Unknown,
        /// <summary>
        /// Application type.
        /// </summary>
        Application,
        /// <summary>
        /// Node type.
        /// </summary>
        Node,
        /// <summary>
        /// Service type.
        /// </summary>
        Service,
        /// <summary>
        /// StatefulService type.
        /// </summary>
        StatefulService,
        /// <summary>
        /// StatelessService type.
        /// </summary>
        StatelessService,
        /// <summary>
        /// Partition report.
        /// </summary>
        Partition,
        /// <summary>
        /// DeployedApplication type.
        /// </summary>
        DeployedApplication,
        /// <summary>
        /// Process. This is only for direct process restarts of a Service Fabric system service executable.
        /// </summary>
        Process,
        /// <summary>
        /// Machine (physical or virtual) type. This is for machine reboot repairs.
        /// </summary>
        Machine,
        /// <summary>
        /// Replica type.
        /// </summary>
        Replica,
        /// <summary>
        /// Disk type.
        /// </summary>
        Disk
    }
}