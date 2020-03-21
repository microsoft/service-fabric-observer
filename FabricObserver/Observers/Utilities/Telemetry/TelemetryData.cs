// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Threading;
using Microsoft.ServiceFabric.TelemetryLib;

namespace FabricObserver.Observers.Utilities.Telemetry
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

        public string HealthEventDescription
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

        public string PartitionId
        {
            get; set;
        }

        public string ReplicaId
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

        public object Value
        {
            get; set;
        }

        public TelemetryData(
            FabricClient fabricClient,
            CancellationToken cancellationToken)
        {
            var (clusterId, tenantId, clusterType) =
              ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, cancellationToken).Result;

            this.ClusterId = clusterId;
        }
    }
}
