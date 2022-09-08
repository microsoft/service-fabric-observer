// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;

namespace FabricObserver.Observers.Utilities
{
    public interface IProcessInfoProvider
    {
        /// <summary>
        /// Gets the amount, in megabytes, of Working Set memory for a specified process. By default, this is the full Working Set amount (private plus shared process memory).
        /// If you want Private Working Set data, then you must provide a process name and pass true for getPrivateWorkingSet.
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <param name="procName">Optional: The name of the process. This value is required if you supply true for getPrivateWorkingSet.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <param name="getPrivateWorkingSet">Optional: return data for Private working set only.</param>
        /// <returns>The amount, in megabytes, of Working Set memory for a specified process (total or active private, depending on how the function is called).</returns>
        float GetProcessWorkingSetMb(int processId, string procName, CancellationToken token, bool getPrivateWorkingSet = false);

        /// <summary>
        ///  Gets the amount, in megabytes, of private memory allocated for a specified process.
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <returns></returns>
        float GetProcessPrivateBytesMb(int processId);

        /// <summary>
        /// Gets the number of allocated (in use) file handles for a specified process.
        /// </summary>
        /// <param name="processId">The id of the process.</param>
        /// <param name="configPath">Full path to Configuration Settings file.</param>
        /// <returns>The float value representing number of allocated file handles for the process.</returns>
        float GetProcessAllocatedHandles(int processId, string configPath = null);

        /// <summary>
        /// Gets process information (name, pid) for descendants of the parent process represented by the supplied process id.
        /// </summary>
        /// <param name="parentPid">The parent process id.</param>
        /// <param name="handleToSnapshot">Windows only. Handle to a Windows process snapshot(created using NativeMethods.CreateToolhelp32Snapshot). This has no meaning for Linux, just supply IntPtr.Zero.</param>
        /// <returns>List of tuple (string ProcName, int Pid) for descendants of the parent process or null if the parent has no children.</returns>
        List<(string ProcName, int Pid)> GetChildProcessInfo(int parentPid, NativeMethods.SafeObjectHandle handleToSnapshot = null);

        /// <summary>
        /// Windows only. Determines the percentage of Windows KVS LVIDs currently in use.
        /// </summary>
        /// <param name="procName">The name of the target process.</param>
        /// <param name="procId" type="optional">If there may be multiple processes with the same name, 
        /// then also supply this value to ensure the correct process is measured.</param>
        /// <returns>double representing the current percentage of LVIDs in use out of a possible int.MaxValue total.</returns>
        double GetProcessKvsLvidsUsagePercentage(string procName, CancellationToken token, int procId = -1);
    }
}