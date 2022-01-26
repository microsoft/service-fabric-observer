using System;
using System.Collections.Generic;

namespace Aggregator
{
    [Serializable]
    public class Snapshot
    {
        // All these attributes represent cluster usage in % at this snapshot
        private float AverageClusterCpuUsage;
        private float AverageClusterRamUsage;
        private float AverageClusterDiskUsage;

        public static readonly string queueName = "snapshot";
        public static readonly long queueCapacity = 100;

        public double Miliseconds { get; }

        public ClusterData CustomMetrics { get; }

        public List<NodeData> NodeMetrics { get; }

        public Snapshot(double miliseconds,ClusterData customMetrics,List<NodeData> nodeMetrics)
        {
            Miliseconds = miliseconds;
            CustomMetrics = customMetrics;
            NodeMetrics = nodeMetrics;
        }

        public float CalculateAverageCapacity()
        {
            AverageClusterResourseUsage();
            float bottleneck;
            bottleneck = Math.Max(AverageClusterCpuUsage, Math.Max(AverageClusterRamUsage, AverageClusterDiskUsage));
            return (float)Math.Floor((100 * CustomMetrics.AllCounts.Count) / bottleneck);
        }

        public static bool CheckTime(double minTime, double dataTime)
        {
            double delta = Math.Abs(minTime - dataTime);

            if (delta < SFUtilities.intervalMiliseconds)
            {
                return true;
            }

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
            int cnt = NodeMetrics.Count;

            foreach (NodeData nodeMetric in NodeMetrics)
            {
                cpuSum += nodeMetric.Hardware.Cpu;
                ramSum += (float) nodeMetric.Hardware.PercentInUse;
                diskSum += nodeMetric.Hardware.DiskPercentageInUse();
            }

            AverageClusterCpuUsage = ((float)cpuSum) / cnt;
            AverageClusterRamUsage = ((float)ramSum) / cnt;
            AverageClusterDiskUsage = ((float)diskSum) / cnt;

            return (AverageClusterCpuUsage, AverageClusterRamUsage, AverageClusterDiskUsage);
        }

        public string ToStringAllData()
        {
            string res=CustomMetrics.ToStringMiliseconds();
            res += "\n Nodes in this Snapshot: " + NodeMetrics.Count;

            foreach(var n in NodeMetrics)
            {
                res += n.ToStringMiliseconds();
            }

            res += "\n************************************************************************************************";
            return res;
        }

        public override string ToString()
        {
            string res;
            res = "\n Average cluster capacity: " + CalculateAverageCapacity() +
                  "\n Average resource usage: " + AverageClusterResourseUsage() +
                  "\n";

            return res;
        }

        public static NodeData AverageNodeData(string NodeName,List<Snapshot> snapshots)
        {
            List<NodeData> list = new List<NodeData>();
            
            foreach(Snapshot s in snapshots)
            {
                foreach(var nodeData in s.NodeMetrics)
                {
                    if (nodeData.NodeName == NodeName)
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
                avg.AddValue("miliseconds", s.Miliseconds);

                foreach (var nodeData in s.NodeMetrics)
                {
                    string nodeName = nodeData.NodeName;

                    if (nodeDic.ContainsKey(nodeName))
                    {
                        nodeDic[nodeName].Add(nodeData);
                    }
                    else
                    {
                        List<NodeData> l = new List<NodeData>
                        {
                            nodeData
                        };
                        nodeDic.Add(nodeName, l);
                    }
                }

                clusterList.Add(s.CustomMetrics);
            }

            List<NodeData> averageFromAllNodes = new List<NodeData>();

            foreach (List<NodeData> list in nodeDic.Values)
            {
                averageFromAllNodes.Add(list[0].AverageData(list));
            }

            ClusterData finalClusterData = clusterList[0].AverageData(clusterList);
            //NodeData finalNodeData = NodeData.AverageNodeData(averageFromAllNodes);

            return new Snapshot(avg.GetAverage("miliseconds"), finalClusterData, averageFromAllNodes); 
        }
    }
}
