// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    public static class MemInfoConstants
    {
        /*
        ** Source code: https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/fs/proc/meminfo.c
        */
        public const string MemTotal = nameof(MemTotal);

        public const string MemFree = nameof(MemFree);

        public const string SwapTotal = nameof(SwapTotal);

        public const string SwapFree = nameof(SwapFree);

        public const string VmallocTotal = nameof(VmallocTotal);

        public const string VmallocUsed = nameof(VmallocUsed);

        public const string MemAvailable = nameof(MemAvailable);
    }
}
