using Aggregator.Data;
using System;
using System.Collections.Generic;

namespace Aggregator
{
    /// <summary>
    /// ClusterData is the wrapper class for everything that's collected with the logic of ClusterObserver.
    /// Each Snapshot has a single instance of ClusterData.
    /// Custom Metrics should be collected here.
    /// </summary>
    [Serializable]
    public class ClusterData: DataBase<ClusterData>
    {
        /// <summary>
        /// Used for the IRealiableQueue in the Aggregator
        /// </summary>
        public static readonly string QueueName = "CustomMetrics";

        public double Milliseconds { get; }

        public Counts AllCounts { get; set; }

        public ClusterData(double miliseconds, Counts counts)
        {
            Milliseconds = miliseconds;
            AllCounts = counts;
        }

        public ClusterData AverageData(List<ClusterData> list)
        {
            AverageDictionary avg = new AverageDictionary();
            List<Counts> countsList = new List<Counts>();

            foreach (var data in list)
            {
                avg.AddValue("miliseconds", data.Milliseconds);
                countsList.Add(data.AllCounts);
            }

            return new ClusterData(avg.GetAverage("miliseconds"), countsList[0].AverageData(countsList));
        }

        public override string ToString()
        {
            string res;
            res = "\n Custom - Miliseconds: " + Milliseconds % (SFUtilities.intervalMiliseconds * 100) +
                  "\n PrimaryCount: " + AllCounts.PrimaryCount +
                  "\n ReplicaCount: " + AllCounts.ReplicaCount +
                  "\n InstanceCount: " + AllCounts.InstanceCount +
                  "\n Count: " + AllCounts.Count +
                  "\n";

            return res;
        }

        public string ToStringMiliseconds()
        {
            string res;
            res = "\n Custom - Miliseconds: " + Milliseconds % (SFUtilities.intervalMiliseconds * 100);
            return res;
        }
    }
}
