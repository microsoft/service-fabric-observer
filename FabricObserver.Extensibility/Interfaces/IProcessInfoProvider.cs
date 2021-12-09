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
        /// <summary>
        /// Returns the amount, in megabytes, of Working Set memory for a specified process. This is the full Working Set amount (private plus shared).
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <param name="procName">Optional: The name of the process. This value is required if you supply true for getPrivateWorkingSet.</param>
        /// <param name="getPrivateWorkingSet">Optional: return data for Private working set only.</param>
        /// <returns></returns>
        float GetProcessWorkingSetMb(int processId, string procName = null, bool getPrivateWorkingSet = false);

        /// <summary>
        /// Returns the number of allocated (in use) file handles for a specified process.
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <param name="context">StatelessServiceContext instance.</param>
        /// <returns>float value representing number of allocated file handles for the process.</returns>
        float GetProcessAllocatedHandles(int processId, StatelessServiceContext context = null, bool useProcessObject = false);

        /// <summary>
        /// Returns a list of Process objects that are active descendants (e.g., children and grandchildren) of the supplied process id.
        /// </summary>
        /// <param name="process"></param>
        /// <returns></returns>
        List<(string ProcName, int Pid)> GetChildProcessInfo(int processId);

        /// <summary>
        /// Gets the current percentage of KVS LVIDs in use for the supplied process name (Windows-only. Returns -1 if called on Linux).
        /// </summary>
        /// <param name="procName">Name of target process.</param>
        /// <returns>Percentage (double) of total LVIDs the process is currently consuming. A result of -1 means failure. Consumer should handle the case when the result is less than 0.</returns>
        double ProcessGetCurrentKvsLvidsUsedPercentage(string procName);
    }
}
