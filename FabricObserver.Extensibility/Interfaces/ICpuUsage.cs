using System;
using System.Collections.Generic;
using System.Text;

namespace FabricObserver.Interfaces
{
    /// <summary>
    /// 
    /// </summary>
    public interface ICpuUsage
    {
        /// <summary>
        /// Gets the current CPU usage for the supplied process identifier.
        /// </summary>
        /// <param name="procId">Process identifier.</param>
        /// <param name="procName">OPtional: Process name.</param>
        /// <returns>Percentage of usage across all cores.</returns>
        double GetCurrentCpuUsagePercentage(int procId, string procName = null);
    }
}
