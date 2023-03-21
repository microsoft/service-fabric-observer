// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using System;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;

namespace ClusterObserver.Utilities
{
    public static class UpgradeChecker
    {
        private static readonly Logger Logger = new("UpgradeLogger");

        /// <summary>
        /// Gets current Application Upgrade information for supplied application.
        /// </summary>
        /// <param name="fabricClient">FabricClient instance</param>
        /// <param name="token">CancellationToken</param>
        /// <param name="app">ApplicationName (Uri)</param>
        /// <returns>An instance of ServiceFabricUpgradeEventData containing ApplicationUpgradeProgress instance.</returns>
        internal static async Task<ServiceFabricUpgradeEventData> GetApplicationUpgradeDetailsAsync(FabricClient fabricClient, CancellationToken token, Uri app)
        {
            if (token.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                if (app == null)
                {
                    return null;
                }

                var appUpgradeProgress =
                    await fabricClient.ApplicationManager.GetApplicationUpgradeProgressAsync(
                            app,
                            TimeSpan.FromSeconds(ClusterObserverManager.AsyncOperationTimeoutSeconds),
                            token);

                if (appUpgradeProgress == null)
                {
                   return null;
                }

                // Nothing is going on (yet).
                if (appUpgradeProgress.UpgradeState == ApplicationUpgradeState.Invalid
                    || appUpgradeProgress.StartTimestampUtc == null
                    || appUpgradeProgress.StartTimestampUtc == DateTime.MinValue)
                {
                    return null;
                }

                var appUgradeEventData = new ServiceFabricUpgradeEventData()
                {
                    ApplicationUpgradeProgress = appUpgradeProgress,
                    TaskName = ClusterObserverConstants.ClusterObserverName
                };

                return appUgradeEventData;
            }
            catch (Exception e) when (e is not (OperationCanceledException or TaskCanceledException))
            {
                Logger.LogWarning($"Error determining application upgrade state:{Environment.NewLine}{e}");
                return null;
            }
        }

        /// <summary>
        /// Get cluster upgrade information.
        /// </summary>
        /// <param name="fabricClient">FabricClient</param>
        /// <param name="token"></param>
        /// <returns>An instance of ServiceFabricUpgradeEventData containing FabricUpgradeProgress instance.</returns>
        internal static async Task<ServiceFabricUpgradeEventData> GetClusterUpgradeDetailsAsync(FabricClient fabricClient, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                var clusterUpgradeProgress =
                    await fabricClient.ClusterManager.GetFabricUpgradeProgressAsync(
                            TimeSpan.FromSeconds(ClusterObserverManager.AsyncOperationTimeoutSeconds),
                            token);

                if (clusterUpgradeProgress == null)
                {
                    return null;
                }

                // Nothing is going on (yet).
                if (clusterUpgradeProgress.UpgradeState == FabricUpgradeState.Invalid
                    || clusterUpgradeProgress.StartTimestampUtc == null
                    || clusterUpgradeProgress.StartTimestampUtc == DateTime.MinValue)
                {
                   return null;
                }

                var serviceFabricUpgradeEventData = new ServiceFabricUpgradeEventData()
                {
                    FabricUpgradeProgress = clusterUpgradeProgress,
                    TaskName = ClusterObserverConstants.ClusterObserverName
                };

                return serviceFabricUpgradeEventData;
            }
            catch (Exception e) when (e is not (OperationCanceledException or TaskCanceledException))
            {
                Logger.LogWarning($"Error determining cluster upgrade state:{Environment.NewLine}{e}");
                return null;
            }
        }
    }
}
