// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// The health scope.
    /// </summary>
    public enum HealthScope
    {
        /// <summary>
        /// Application scope.
        /// </summary>
        Application,

        /// <summary>
        /// Cluster scope.
        /// </summary>
        Cluster,

        /// <summary>
        /// Instance scope.
        /// </summary>
        Instance,

        /// <summary>
        /// Node scope.
        /// </summary>
        Node,

        /// <summary>
        /// Paritition scope.
        /// </summary>
        Partition,

        /// <summary>
        /// Replica scope.
        /// </summary>
        Replica,
    }
}