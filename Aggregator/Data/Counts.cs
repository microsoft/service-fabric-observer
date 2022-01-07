using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator.Data
{
    [Serializable]
    public class Counts : DataBase<Counts>
    {
        public int PrimaryCount { get; set; }
        public int ReplicaCount { get; set; }
        public int Count { get; set; }
        public int InstanceCount { get; set; }

        public Counts(int primaryCount, int replicaCount, int instanceCount, int count)
        {
                        
            PrimaryCount = primaryCount;
            ReplicaCount = replicaCount;
            Count = count;
            InstanceCount = instanceCount;
        }


        public Counts AverageData(List<Counts> list)
        {
            AverageDictionary avg = new AverageDictionary();
            foreach (var data in list)
            {
                
                avg.addValue("primary", data.PrimaryCount);
                avg.addValue("replica", data.ReplicaCount);
                avg.addValue("instance", data.InstanceCount);
                avg.addValue("count", data.Count);
            }
            return new Counts(
               (int)avg.getAverage("primary"),
               (int)avg.getAverage("replica"),
               (int)avg.getAverage("instance"),
               (int)avg.getAverage("count")
                );
        }
    }
}
