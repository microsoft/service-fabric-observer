using System;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Service filter types.
    /// </summary>
    internal enum ServiceFilterType
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
        None,
    }
}
