using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [JsonObject]
    public class ChildProcessTelemetryData
    {
        public string ApplicationName;
        public string ServiceName;
        public string Metric;
        public double Value;
        public long ProcessId;
        public string PartitionId;
        public string ReplicaId;
        public string NodeName;
        public int ChildProcessCount;
        public List<ChildProcessInfo> ChildProcessInfo;
    }
}
