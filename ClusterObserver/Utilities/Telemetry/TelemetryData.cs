using Microsoft.ApplicationInsights.Extensibility;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Text;
using System.Threading;

namespace FabricClusterObserver.Utilities.Telemetry
{
    public class TelemetryData
    {
        public string ApplicationName { get; set; } = "N/A";

        public string ClusterId { get; set; }

        public string Code { get; set; }

        public string HealthEventDescription { get; set; } = "N/A";

        public string HealthScope { get; set; } = "Cluster";

        public string HealthState { get; set; }

        public string Metric { get; set; }

        public string NodeName { get; set; }

        public string NodeStatus { get; set; }
       
        public string ObserverName { get; set; }

        public Guid Partition { get; set; }

        public long Replica { get; set; }

        public string ServiceName { get; set; } = "N/A";

        public string Source { get; set; } = "ClusterObserver";

        public object Value { get; set; }

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

