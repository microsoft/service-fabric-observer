// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace ClusterObserver.Utilities.Telemetry
{
    /// <summary>
    /// Service Fabric entity types.
    /// </summary>
    public enum EntityType
    {
        /// <summary>
        /// Application type.
        /// </summary>
        Application,
        /// <summary>
        /// Node report.
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
        /// Partition type.
        /// </summary>
        Partition,
        /// <summary>
        /// DeployedApplication type.
        /// </summary>
        DeployedApplication,
        /// <summary>
        /// Process. This is only for direct process restarts of a Service Fabric system service executable.
        /// </summary>
        Process
    }
}