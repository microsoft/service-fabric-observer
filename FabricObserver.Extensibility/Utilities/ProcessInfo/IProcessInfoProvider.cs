// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;

namespace FabricObserver.Observers.Utilities
{
    public interface IProcessInfoProvider
    {
        float GetProcessPrivateWorkingSetInMB(int processId);

        float GetProcessOpenFileHandles(int processId, StatelessServiceContext context = null);
    }
}
