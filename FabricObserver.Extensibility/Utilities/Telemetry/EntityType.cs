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
        /// Application report.
        /// </summary>
        Application,
        /// <summary>
        /// Node report.
        /// </summary>
        Node,
        /// <summary>
        /// Service report.
        /// </summary>
        Service,
        /// <summary>
        /// StatefulService report.
        /// </summary>
        StatefulService,
        /// <summary>
        /// StatelessService report.
        /// </summary>
        StatelessService,
        /// <summary>
        /// Partition report.
        /// </summary>
        Partition,
        /// <summary>
        /// DeployedApplication report.
        /// </summary>
        DeployedApplication,
        /// <summary>
        /// Process. This is only for direct process restarts of a Service Fabric system service executable.
        /// </summary>
        Process
    }
}