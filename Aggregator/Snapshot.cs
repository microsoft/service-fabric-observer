using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class Snapshot
    {

        public static readonly string queueName = "snapshot";
        public ClusterData customMetrics { get; }
        public List<NodeData> nodeMetrics { get; }

        // All these attributes represent cluster usage in % at this snapshot
        private float AverageClusterCpuUsage;
        private float AverageClusterRamUsage;
        private float AverageClusterDiskUsage;

        public Snapshot(ClusterData customMetrics,List<NodeData> nodeMetrics)
        {
            this.customMetrics = customMetrics;
            this.nodeMetrics = nodeMetrics;
        }



        public float CalculateAverageCapacity()
        {
            AverageClusterResourseUsage();
            float bottleneck = 0;
            bottleneck = Math.Max(AverageClusterCpuUsage, Math.Max(AverageClusterRamUsage, AverageClusterDiskUsage));
            return (float)Math.Floor((100 * customMetrics.Count) / bottleneck);
            return 0;
        }

        public static bool checkTime(double minTime, double dataTime)
        {
            double delta = Math.Abs(minTime - dataTime);
            if (delta < SFUtilities.interval) return true;
            return false;
        }

        /// <summary>
        /// Calculates the average % resourse usage of the cluster at this snapshot
        /// </summary>
        /// <returns>% usage</returns>
        public (float cpu,float ram,float disk) AverageClusterResourseUsage()
        {
            float cpuSum = 0;
            float ramSum = 0;
            float diskSum = 0;
            int cnt = nodeMetrics.Count;
            foreach(NodeData nodeMetric in nodeMetrics)
            {
                cpuSum += nodeMetric.hardware.Cpu;
                ramSum += (float) nodeMetric.hardware.PercentInUse;
                diskSum += nodeMetric.hardware.DiskPercentageInUse();

            }
            AverageClusterCpuUsage = ((float)cpuSum) / cnt;
            AverageClusterRamUsage = ((float)ramSum) / cnt;
            AverageClusterDiskUsage = ((float)diskSum) / cnt;
            return (AverageClusterCpuUsage, AverageClusterRamUsage, AverageClusterDiskUsage);
        }


        public override string ToString()
        {
            String res;
            res =
                "\n Average cluster capacity: " + this.CalculateAverageCapacity() +
                "\n Average resource usage: " + this.AverageClusterResourseUsage() +
                "\n";
            return res;
        }

    }
}
