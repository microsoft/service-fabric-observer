// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxMemoryUsageProvider : MemoryUsageProvider
    {
        public override ulong GetCommittedBytes()
        {
            Dictionary<string, ulong> memInfo = LinuxProcFS.ReadMemInfo();

            ulong memTotal = memInfo[MemInfoConstants.MemTotal];
            ulong memFree = memInfo[MemInfoConstants.MemFree];
            ulong memAvail = memInfo[MemInfoConstants.MemAvailable];
            ulong swapTotal = memInfo[MemInfoConstants.SwapTotal];
            ulong swapFree = memInfo[MemInfoConstants.SwapFree];

            return (memTotal - memAvail - memFree + (swapTotal - swapFree)) * 1024;
        }
    }
}