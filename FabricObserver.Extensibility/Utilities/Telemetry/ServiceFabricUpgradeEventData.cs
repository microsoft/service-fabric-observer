// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System.Fabric;
using System.Runtime.InteropServices;
using FabricObserver.TelemetryLib;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    public class ServiceFabricUpgradeEventData
    {
        public string ClusterId => ClusterInformation.ClusterInfoTuple.ClusterId;

        public string TaskName
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
    }
}
