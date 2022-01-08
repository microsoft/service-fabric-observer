using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class Snapshot
    {

        public static readonly string queueName = "snapshot";
        public static readonly int queueCapacity = 100;
        public double miliseconds { get; }
        public ClusterData customMetrics { get; }
        public List<NodeData> nodeMetrics { get; }

        // All these attributes represent cluster usage in % at this snapshot
        private float AverageClusterCpuUsage;
        private float AverageClusterRamUsage;
        private float AverageClusterDiskUsage;

        public Snapshot(double miliseconds,ClusterData customMetrics,List<NodeData> nodeMetrics)
        {
            this.miliseconds = miliseconds;
            this.customMetrics = customMetrics;
            this.nodeMetrics = nodeMetrics;
        }



        public float CalculateAverageCapacity()
        {
            AverageClusterResourseUsage();
            float bottleneck = 0;
            bottleneck = Math.Max(AverageClusterCpuUsage, Math.Max(AverageClusterRamUsage, AverageClusterDiskUsage));
            return (float)Math.Floor((100 * customMetrics.allCounts.Count) / bottleneck);
            
        }

        public static bool checkTime(double minTime, double dataTime)
        {
            double delta = Math.Abs(minTime - dataTime);
            if (delta < SFUtilities.intervalMiliseconds) return true;
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

        public string ToStringAllData()
        {
            string res=customMetrics.ToStringMiliseconds();
            res += "\n Nodes in this Snapshot: " + nodeMetrics.Count;
            foreach(var n in nodeMetrics)
            {
                res += n.ToStringMiliseconds();
            }
            res += "\n************************************************************************************************";
            return res;
        }

        public override string ToString()
        {
            string res;
            res =
                "\n Average cluster capacity: " + this.CalculateAverageCapacity() +
                "\n Average resource usage: " + this.AverageClusterResourseUsage() +
                "\n";
            return res;
        }

        public static NodeData AverageNodeData(string NodeName,List<Snapshot> snapshots)
        {
            List<NodeData> list = new List<NodeData>();
            
            foreach(Snapshot s in snapshots)
            {
                foreach(var nodeData in s.nodeMetrics)
                {
                    if (nodeData.nodeName == NodeName)
                    {
                        list.Add(nodeData);
                    }
                }
            }
            return list[0].AverageData(list);
        }

        public static Snapshot AverageClusterData(List<Snapshot> snapshots)
        {
            Dictionary<string, List<NodeData>> nodeDic= new Dictionary<string, List<NodeData>>();
            List<ClusterData> clusterList = new List<ClusterData>();
            AverageDictionary avg = new AverageDictionary();
            foreach (Snapshot s in snapshots)
            {
                avg.addValue("miliseconds", s.miliseconds);
                foreach (var nodeData in s.nodeMetrics)
                {
                    string nodeName = nodeData.nodeName;
                    if (nodeDic.ContainsKey(nodeName))
                    {
                        nodeDic[nodeName].Add(nodeData);
                    }
                    else
                    {
                        List<NodeData> l = new List<NodeData>();
                        l.Add(nodeData);
                        nodeDic.Add(nodeName, l);
                    }
                  
                }
                clusterList.Add(s.customMetrics);
            }

            List<NodeData> averageFromAllNodes = new List<NodeData>();
            foreach(var list in nodeDic.Values)
            {
                averageFromAllNodes.Add(list[0].AverageData(list));
            }
            ClusterData finalClusterData = clusterList[0].AverageData(clusterList);
            //NodeData finalNodeData = NodeData.AverageNodeData(averageFromAllNodes);
            return new Snapshot(avg.getAverage("miliseconds"),finalClusterData, averageFromAllNodes);
            
            
        }
    }
}
