// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System.Fabric;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClusterObserver.Utilities.Telemetry
{
    public class ServiceFabricUpgradeEventData
    {
        public string ClusterId
        {
            get; set;
        }

        public ApplicationUpgradeProgress ApplicationUpgradeProgress
        {
            get; set;
        }

        public FabricUpgradeProgress FabricUpgradeProgress
        {
            get; set;
        }

        public string OS => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";

        [JsonConstructor]
        public ServiceFabricUpgradeEventData()
        {

        }

        public ServiceFabricUpgradeEventData (FabricClient fabricClient, CancellationToken cancellationToken)
        {
            var (clusterId, _) = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, cancellationToken).Result;
            ClusterId = clusterId;
        }
    }
}
