// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;

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

        /// <summary>
        /// Returns a list of Process objects that are active descendants (e.g., children and grandchildren) of the provided Process object.
        /// </summary>
        /// <param name="process"></param>
        /// <returns></returns>
        List<Process> GetChildProcesses(Process process);

        void Dispose();
    }
}
