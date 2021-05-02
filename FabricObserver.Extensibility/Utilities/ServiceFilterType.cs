// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Service filter types.
    /// </summary>
    public enum ServiceFilterType
    {
        /// <summary>
        /// Exclude services
        /// </summary>
        Exclude,

        /// <summary>
        /// Include services
        /// </summary>
        Include,

        /// <summary>
        /// No service filter
        /// </summary>
        None
    }
}
