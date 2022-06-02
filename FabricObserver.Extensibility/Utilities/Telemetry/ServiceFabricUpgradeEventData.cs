// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System.Fabric;
using System.Runtime.InteropServices;
using FabricObserver.TelemetryLib;
using System;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [Serializable]
    public class ServiceFabricUpgradeEventData
    {
        private readonly string _os;

        [EventField]
        public string ClusterId => ClusterInformation.ClusterInfoTuple.ClusterId;

        [EventField]
        public string TaskName
        {
            get; set;
        }

        [EventField]
        public ApplicationUpgradeProgress ApplicationUpgradeProgress
        {
            get; set;
        }

        [EventField]
        public FabricUpgradeProgress FabricUpgradeProgress
        {
            get; set;
        }

        [EventField]
        public string OS => _os;

        [JsonConstructor]
        public ServiceFabricUpgradeEventData()
        {
            _os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
        }
    }
}
