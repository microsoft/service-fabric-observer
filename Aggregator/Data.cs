using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{

    //This Data is passed to the Aggregator using Remote Procedure Calls from Collector instances
    [Serializable]
    public class Data
    {

        public Data(float cpu,long TotalMemoryGb, long MemoryInUseMb, double PercentInUse)
        {
            this.cpu = cpu;
            this.TotalMemoryGb = TotalMemoryGb;
            this.MemoryInUseMb = MemoryInUseMb;
            this.PercentInUse = PercentInUse;
        }

        public float cpu { get; }
        public long TotalMemoryGb { get; }
        public long MemoryInUseMb { get; }
        public double PercentInUse { get; }
    }
}
