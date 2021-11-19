using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class SFData
    {

        /// <summary>
        /// Used for the IRealiableQueue in the Aggregator
        /// </summary>
        public static readonly string queueName = "CustomMetrics";

        public int PrimaryCount { get; }
        public int ReplicaCount { get; }
        public int Count { get; }
        public int InstanceCount { get; }


        public SFData(int primaryCount,int replicaCount, int instanceCount, int count)
        {
            PrimaryCount = primaryCount;
            ReplicaCount = replicaCount;
            Count = count;
            InstanceCount = instanceCount;
        }

        public override string ToString()
        {
            String res;
            res =
                "\n PrimaryCount: " + this.PrimaryCount +
                "\n ReplicaCount: " + this.ReplicaCount +
                "\n InstanceCount: " + this.InstanceCount +
                "\n Count: " + this.Count+
                "\n";
            return res;
        }

    }
}
