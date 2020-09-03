// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    internal class WindowsMemoryUsageProvider : MemoryUsageProvider
    {
        private static readonly PerformanceCounter MemCommittedBytesPerfCounter =
            new PerformanceCounter(categoryName: "Memory", counterName: "Committed Bytes", readOnly: true);

        internal override ulong GetCommittedBytes()
        {
            return (ulong)MemCommittedBytesPerfCounter.NextValue();
        }
    }
}