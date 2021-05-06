// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsMemoryUsageProvider : MemoryUsageProvider
    {
        public override ulong GetCommittedBytes()
        {
            PerformanceCounter memCommittedBytesPerfCounter = null;

            try
            {
                memCommittedBytesPerfCounter = new PerformanceCounter
                {
                    CategoryName = "Memory",
                    CounterName = "Committed Bytes",
                    ReadOnly = true
                };

                // warm up counter.
                _ = memCommittedBytesPerfCounter.NextValue();

                return (ulong)memCommittedBytesPerfCounter.NextValue();
            }
            finally
            {
                memCommittedBytesPerfCounter?.Dispose();
            }
        }
    }
}