using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{

    //This Data is passed to the Aggregator using Remote Procedure Calls from Collector instances
    [Serializable]
    public class Data
    {

        public Data(float cpu)
        {
            this.cpu = cpu;
        }

        public float cpu { get; }
        
    }
}
