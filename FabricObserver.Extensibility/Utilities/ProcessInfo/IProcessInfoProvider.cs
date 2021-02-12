// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public interface IProcessInfoProvider
    {
        float GetProcessPrivateWorkingSetInMB(int processId);

        Task<float> GetProcessOpenFileHandlesAsync(int processId, StatelessServiceContext context, CancellationToken token);
    }
}
