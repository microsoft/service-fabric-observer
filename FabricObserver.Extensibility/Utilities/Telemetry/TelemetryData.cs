// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using FabricObserver.TelemetryLib;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    public class TelemetryData
    {
        public string ApplicationName
        {
            get; set;
        }

        public string ClusterId => ClusterInformation.ClusterInfoTuple.ClusterId;

        public string Code
        {
            get; set;
        }

        public string ContainerId
        {
            get; set;
        }

        public string Description
        {
            get; set;
        }

        public string HealthState
        {
            get; set;
        }

        public string Metric
        {
            get; set;
        }

        public string NodeName
        {
            get; set;
        }

        public string ObserverName
        {
            get; set;
        }

        public string OS
        {
            get; set;
        } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";

        public string PartitionId
        {
            get; set;
        }

        public int ProcessId
        {
            get; set;
        }

        public long ReplicaId
        {
            get; set;
        }

        public string ServiceName
        {
            get; set;
        }

        public string Source
        {
            get; set;
        }

        public string SystemServiceProcessName
        {
            get; set;
        }

        public double Value
        {
            get; set;
        }

        [JsonConstructor]
        public TelemetryData()
        {

        }
    }
}