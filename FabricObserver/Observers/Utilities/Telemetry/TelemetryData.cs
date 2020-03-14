using System;
using System.Fabric;
using System.Threading;
using Microsoft.ServiceFabric.TelemetryLib;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    public class TelemetryData
    {
        public string ApplicationName { get; set; } = "N/A";

        public string ClusterId { get; set; }

        public string Code { get; set; } = FoErrorWarningCodes.Ok;

        public string HealthEventDescription { get; set; } = "N/A";

        public string HealthState { get; set; } = "Ok";

        public string Metric { get; set; }

        public string NodeName { get; set; }

        public string ObserverName { get; set; }

        public Guid Partition { get; set; }

        public long Replica { get; set; }

        public string ServiceName { get; set; } = "N/A";

        public string Source { get; set; } = "FabricObserver";

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
