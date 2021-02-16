// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public interface IProcessInfoProvider
    {
        float GetProcessPrivateWorkingSetInMB(int processId);

        /// <summary>
        /// Returns the number of allocated (in use) file handles for a specified process.
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <param name="context">StatelessServiceContext instance.</param>
        /// <returns>float value representing number of allocated file handles for the process.</returns>
        float GetProcessAllocatedHandles(int processId, StatelessServiceContext context);
    }
}
