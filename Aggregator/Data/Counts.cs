using System;
using System.Collections.Generic;

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
                avg.AddValue("primary", data.PrimaryCount);
                avg.AddValue("replica", data.ReplicaCount);
                avg.AddValue("instance", data.InstanceCount);
                avg.AddValue("count", data.Count);
            }

            return new Counts(
                       (int)Math.Round(avg.GetAverage("primary")),
                       (int)Math.Round(avg.GetAverage("replica")),
                       (int)Math.Round(avg.GetAverage("instance")),
                       (int)Math.Round(avg.GetAverage("count")));
        }

        public override string ToString()
        {
            string res = "";
            if (PrimaryCount != 0)
            {
                res += "\n Primary Count:" + PrimaryCount;
            }

            if (ReplicaCount != 0)
            {
                res += "\n Replica Count:" + ReplicaCount;
            }

            if (InstanceCount != 0) 
            {
                res += "\n Instance Count:" + InstanceCount;
            }

            if (Count != 0) 
            {
                res += "\n Count:" + Count;
            }

            return res;
        }
    }
}
