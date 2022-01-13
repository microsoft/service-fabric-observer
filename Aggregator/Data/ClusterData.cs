using Aggregator.Data;
using System;
using System.Collections.Generic;
using System.Text;

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
        public static readonly string queueName = "CustomMetrics";

        public double miliseconds { get; }
        public Counts allCounts { get; set; }

        public ClusterData(double miliseconds, Counts counts)
        {

            this.miliseconds = miliseconds;
            allCounts = counts;
        }

        public ClusterData AverageData(List<ClusterData> list)
        {
            AverageDictionary avg = new AverageDictionary();
            List<Counts> countsList = new List<Counts>();
            foreach (var data in list)
            {
                avg.addValue("miliseconds", data.miliseconds);
                countsList.Add(data.allCounts);
            }
            return new ClusterData(
                avg.getAverage("miliseconds"),
                countsList[0].AverageData(countsList)
                );
        }

        public override string ToString()
        {
            String res;
            res =
                "\n Custom - Miliseconds: " + this.miliseconds % (SFUtilities.intervalMiliseconds * 100) +
                "\n PrimaryCount: " + this.allCounts.PrimaryCount +
                "\n ReplicaCount: " + this.allCounts.ReplicaCount +
                "\n InstanceCount: " + this.allCounts.InstanceCount +
                "\n Count: " + this.allCounts.Count +
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

        

    }
}
