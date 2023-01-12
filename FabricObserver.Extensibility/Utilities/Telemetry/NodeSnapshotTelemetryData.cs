// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [Serializable]
    public class NodeSnapshotTelemetryData
    {
        [EventField]
        public string SnapshotId { get; set; }
        [EventField]
        public string SnapshotTimestamp { get; set; }
        [EventField]
        public string NodeName { get; set; }
        [EventField]
        public string NodeType { get; set; }
        [EventField]
        public string NodeId { get; set; }
        [EventField]
        public string NodeInstanceId { get; set; }
        [EventField]
        public string NodeStatus { get; set; }
        [EventField]
        public string NodeUpAt { get; set; }
        [EventField]
        public string NodeDownAt { get; set; }
        [EventField]
        public string CodeVersion { get; set; }
        [EventField]
        public string ConfigVersion { get; set; }
        [EventField]
        public string HealthState { get; set; }
        [EventField]
        public string IpAddressOrFQDN { get; set; }
        [EventField]
        public string UpgradeDomain { get; set; }
        [EventField]
        public string FaultDomain { get; set; }
        [EventField]
        public string IsSeedNode { get; set; }
    }
}
