using Newtonsoft.Json;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [JsonObject]
    public class ChildProcessInfo
    {
        public string ProcessName;
        public double Value;
    }
}
