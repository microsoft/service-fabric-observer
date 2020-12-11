// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using System.Fabric;

namespace FabricObserver
{
    public interface IFabricObserverStartup
    {
        void ConfigureServices(IServiceCollection services, FabricClient fabricClient, StatelessServiceContext context);
    }
}
