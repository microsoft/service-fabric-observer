// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics.Tracing;
using System.Fabric.Query;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    public class NodeSnapshotTelemetryData
    {
        public string SnapshotId { get; set; }
        public string SnapshotTimestamp { get; set; }
        public string NodeName { get; set; }
        public string NodeType { get; set; }
        public string NodeId { get; set; }
        public string NodeInstanceId { get; set; }
        public string NodeStatus { get; set; }
        public string NodeUpAt { get; set; }
        public string NodeDownAt { get; set; }
        public string CodeVersion { get; set; }
        public string ConfigVersion { get; set; }
        public string HealthState { get; set; }
        public string IpAddressOrFQDN { get; set; }
        public string UpgradeDomain { get; set; }
        public string FaultDomain { get; set; }
        public bool IsSeedNode { get; set; }
        public string InfrastructurePlacementID { get; set; }
        public NodeDeactivationResult NodeDeactivationInfo { get; set; }
        public bool IsNodeByNodeUpgradeInProgress { get; set; }
    }
}
