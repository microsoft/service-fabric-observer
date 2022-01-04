using Aggregator.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class ClusterData: DataBase<ClusterData>
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


        public static ClusterData AverageClusterData(List<ClusterData> list)
        {
            AverageDictionary avg = new AverageDictionary();
            foreach (var data in list)
            {
                avg.addValue("miliseconds", data.miliseconds);
                avg.addValue("primary", data.PrimaryCount);
                avg.addValue("replica", data.ReplicaCount);
                avg.addValue("instance", data.InstanceCount);
                avg.addValue("count", data.Count);
            }
            return new ClusterData(
               avg.getAverage("miliseconds"),
               (int)avg.getAverage("primary"),
               (int)avg.getAverage("replica"),
               (int)avg.getAverage("instance"),
               (int)avg.getAverage("count")
                );

        }

        public override string ToString()
        {
            String res;
            res =
                "\n Custom - Miliseconds: " + this.miliseconds % (SFUtilities.intervalMiliseconds * 100) +
                "\n PrimaryCount: " + this.PrimaryCount +
                "\n ReplicaCount: " + this.ReplicaCount +
                "\n InstanceCount: " + this.InstanceCount +
                "\n Count: " + this.Count +
                "\n";
            return res;
        }
        public string ToStringMiliseconds()
        {
            String res;
            res =
                "\n Custom - Miliseconds: " + this.miliseconds % (SFUtilities.intervalMiliseconds * 100);
            return res;
        }

        public ClusterData AverageData(List<ClusterData> list)
        {
            AverageDictionary avg = new AverageDictionary();
            foreach (var data in list)
            {
                avg.addValue("miliseconds", data.miliseconds);
                avg.addValue("primary", data.PrimaryCount);
                avg.addValue("replica", data.ReplicaCount);
                avg.addValue("instance", data.InstanceCount);
                avg.addValue("count", data.Count);
            }
            return new ClusterData(
               avg.getAverage("miliseconds"),
               (int)avg.getAverage("primary"),
               (int)avg.getAverage("replica"),
               (int)avg.getAverage("instance"),
               (int)avg.getAverage("count")
                );
        }

    }
}
