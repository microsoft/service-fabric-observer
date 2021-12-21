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
        /// Gets the amount, in megabytes, of Working Set memory for a specified process. By default, this is the full Working Set amount (private plus shared process memory). You can supply
        /// a boolean value for optional parameter getPrivateWorkingSet to inform the function that you want Private Working Set only.
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <param name="procName">Optional: The name of the process. This value is required if you supply true for getPrivateWorkingSet.</param>
        /// <param name="getPrivateWorkingSet">Optional: return data for Private working set only.</param>
        /// <returns>The amount, in megabytes, of Working Set memory for a specified process (total or private, depending on how the function is called).</returns>
        float GetProcessWorkingSetMb(int processId, string procName = null, bool getPrivateWorkingSet = false);

        /// <summary>
        /// Gets the number of allocated (in use) file handles for a specified process.
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <param name="context">StatelessServiceContext instance.</param>
        /// <returns>The float value representing number of allocated file handles for the process.</returns>
        float GetProcessAllocatedHandles(int processId, StatelessServiceContext context = null, bool useProcessObject = false);

        /// <summary>
        /// Gets process information (name, pid) for descendants of the parent process represented by the supplied process id.
        /// </summary>
        /// <param name="parentPid">The parent process id.</param>
        /// <returns>List of tuple (string ProcName, int Pid) for descendants of the parent process or null if the parent has no children.</returns>
        List<(string ProcName, int Pid)> GetChildProcessInfo(int parentPid);

        /// <summary>
        /// Gets the current percentage of KVS LVIDs in use for the supplied process name (Windows-only. Returns -1 if called on Linux).
        /// </summary>
        /// <param name="procName">Name of target process.</param>
        /// <returns>Percentage (double) of total LVIDs the process is currently consuming. A result of -1 means failure. Consumer should handle the case when the result is less than 0.</returns>
        double ProcessGetCurrentKvsLvidsUsedPercentage(string procName);
    }
}
