// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System.Fabric;
using FabricObserver.TelemetryLib;
using System;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    public class ServiceFabricUpgradeEventData
    {
        private readonly string _os;

        public static string ClusterId => ClusterInformation.ClusterInfoTuple.ClusterId;

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

        [EventField]
        public string OS => _os;

        [JsonConstructor]
        public ServiceFabricUpgradeEventData()
        {
            _os = OperatingSystem.IsWindows() ? "Windows" : "Linux";
        }
    }
}
