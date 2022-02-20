// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxCpuUtilizationProvider : CpuUtilizationProvider
    {
        private float uptimeInSeconds;
        private float idleTimeInSeconds;
        private float cpuUtilization;

        public override float GetProcessorTimePercentage()
        {
            (float ut, float it) = LinuxProcFS.ReadUptime();

            if (ut == uptimeInSeconds)
            {
                return cpuUtilization;
            }

            cpuUtilization = 100 - ((it - idleTimeInSeconds) / (ut - uptimeInSeconds) / Environment.ProcessorCount * 100);

            if (cpuUtilization < 0)
            {
                cpuUtilization = 0;
            }

            uptimeInSeconds = ut;
            idleTimeInSeconds = it;

            return cpuUtilization;
        }

        public override void Dispose()
        {
            // Nothing to do.
        }
    }
}
