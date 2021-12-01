using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class ClusterData
    {


        /// <summary>
        /// Used for the IRealiableQueue in the Aggregator
        /// </summary>
        public static readonly string queueName = "CustomMetrics";

        public double miliseconds { get; }
        public int PrimaryCount { get; }
        public int ReplicaCount { get; }
        public int Count { get; }
        public int InstanceCount { get; }


        public ClusterData(double miliseconds,int primaryCount,int replicaCount, int instanceCount, int count)
        {

            this.miliseconds = miliseconds;
            PrimaryCount = primaryCount;
            ReplicaCount = replicaCount;
            Count = count;
            InstanceCount = instanceCount;
        }

        public override string ToString()
        {
            String res;
            res =
                "\n                 Miliseconds: " + this.miliseconds % (SFUtilities.interval * 100) +
                "\n PrimaryCount: " + this.PrimaryCount +
                "\n ReplicaCount: " + this.ReplicaCount +
                "\n InstanceCount: " + this.InstanceCount +
                "\n Count: " + this.Count+
                "\n";
            return res;
        }

    }
}
