// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public abstract class CpuUtilizationProvider : IDisposable
    {
        public abstract Task<float> NextValueAsync();

        public static CpuUtilizationProvider Create()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsCpuUtilizationProvider();
            }

            return new LinuxCpuUtilizationProvider();
        }

        public void Dispose()
        {
            Dispose(disposing: true);
        }

        protected abstract void Dispose(bool disposing);
    }
}
