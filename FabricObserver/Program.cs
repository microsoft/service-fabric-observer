// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.ServiceFabric.Services.Runtime;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver
{
    internal static class Program
    {
        /// <summary>
        /// This is the entry point of the service host process.
        /// </summary>
        private static void Main()
        {
            try
            {
                ServiceRuntime.RegisterServiceAsync("FabricObserverType", context => new FabricObserverService(context)).GetAwaiter().GetResult();
                ServiceEventSource.Current?.ServiceTypeRegistered(Process.GetCurrentProcess().Id, nameof(FabricObserver));

                // Prevents this host process from terminating so services keep running.
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception e)
            {
                ServiceEventSource.Current?.ServiceHostInitializationFailed(e.ToString());
                throw;
            }
        }
    }
}
