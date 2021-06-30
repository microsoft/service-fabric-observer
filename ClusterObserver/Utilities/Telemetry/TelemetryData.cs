// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json;

namespace ClusterObserver.Utilities.Telemetry
{
    public class TelemetryData
    {
        public string ApplicationName
        {
            get; set;
        }

        public string ChildProcessName
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

        public string ProcessId
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

        public string SystemServiceProcessName
        {
            get; set;
        }

        public object Value
        {
            get; set;
        }

        [JsonConstructor]
        public TelemetryData()
        {

        }

        public TelemetryData(FabricClient fabricClient, CancellationToken cancellationToken)
        {
            var (clusterId, _) = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, cancellationToken).Result;
            ClusterId = clusterId;
        }
    }
}

