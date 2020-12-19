// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System;
using System.Fabric;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClusterObserver.Utilities.Telemetry
{
    public class TelemetryData
    {
        public string ApplicationName
        {
            get; set;
        }

        public string ClusterId
        {
            get; set;
        }

        public string Code
        {
            get; set;
        }

        public string ContainerId
        {
            get; set;
        }

        public string HealthEventDescription
        {
            get; set;
        }

        public string HealthScope
        {
            get; set;
        } = "Cluster";

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

        public string NodeStatus
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

        public Guid PartitionId
        {
            get; set;
        }

        = Guid.Empty;

        public long ReplicaId
        {
            get; set;
        }

        = 0;

        public string ServiceName
        {
            get; set;
        }

        public string Source
        {
            get; set;
        } = "ClusterObserver";

        public object Value
        {
            get; set;
        }

        [JsonConstructor]
        public TelemetryData()
        {
        }

        public TelemetryData(
            FabricClient fabricClient,
            CancellationToken cancellationToken)
        {
            var (clusterId, _) =
              ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, cancellationToken).Result;

            ClusterId = clusterId;
        }
    }
}

