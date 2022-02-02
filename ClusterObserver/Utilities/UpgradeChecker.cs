// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using ClusterObserver.Utilities.Telemetry;
using System;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;

namespace ClusterObserver.Utilities
{
    public static class UpgradeChecker
    {
        private static readonly Logger Logger = new Logger("UpgradeLogger");

        /// <summary>
        /// Gets Application Upgrade Domains (integers) for application or applications
        /// currently upgrading (or rolling back).
        /// </summary>
        /// <param name="fabricClient">FabricClient instance</param>
        /// <param name="token">CancellationToken</param>
        /// <param name="app">ApplicationName (Uri)</param>
        /// <returns>List of integers representing UDs</returns>
        internal static async Task<ServiceFabricUpgradeEventData> GetApplicationUpgradeDetailsAsync(FabricClient fabricClient, CancellationToken token, Uri app)
        {
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

                var appUgradeEventData = new ServiceFabricUpgradeEventData(fabricClient, token)
                {
                    ApplicationUpgradeProgress = appUpgradeProgress
                };

                return appUgradeEventData;
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
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
        /// <returns>A tuple containing FabricUpgradeState and current UD. When State is Invalid and UD is -1 it means there is no cluster upgrade in progress.
        /// When State is Invalid and UD is int.MaxValue it means an exception took place and the function can't make a determination of cluster upgrade state/UD.</returns>
        internal static async Task<ServiceFabricUpgradeEventData> GetClusterUpgradeDetailsAsync(FabricClient fabricClient, CancellationToken token)
        {
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

                ServiceFabricUpgradeEventData serviceFabricUpgradeEventData = new ServiceFabricUpgradeEventData(fabricClient, token)
                {
                    FabricUpgradeProgress = clusterUpgradeProgress
                };

                return serviceFabricUpgradeEventData;
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                Logger.LogWarning($"Error determining cluster upgrade state:{Environment.NewLine}{e}");
                return null;
            }
        }
    }
}
