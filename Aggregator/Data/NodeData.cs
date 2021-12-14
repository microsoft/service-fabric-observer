using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Aggregator
{
    /// <summary>
    /// This class is a wrapper for Node specific data that will be passed to the Aggregator. Everything needs to be serializable.
    /// </summary>
    [Serializable]
    public class NodeData
    {   
        //You extend this class by adding new properties bellow. They must be serializable. 
        public string nodeName { get; }
        public double miliseconds { get; }
        public Hardware hardware { get; set; }
        public List<ProcessData> processList { get; set; }

        public Hardware sfHardware { get; set; }
        //Implement List<byte[]> to store arbitrary data


        public NodeData(double miliseconds,string nodeName)
        {
            this.miliseconds = miliseconds;
            this.nodeName = nodeName;
        }
        
        public static NodeData AverageNodeData(List<NodeData> list)
        {
            AverageDictionary avg = new AverageDictionary();
            Dictionary<Uri, List<ProcessData>> dic = new Dictionary<Uri, List<ProcessData>>();
            foreach(var data in list) 
            {
                avg.addValue("cpu%", data.hardware.Cpu);
                avg.addValue("ramTotalGB", data.hardware.TotalMemoryGb);
                avg.addValue("ramInUseMB", data.hardware.MemoryInUseMb);
                avg.addValue("ram%", data.hardware.PercentInUse);
                avg.addValue("disk%", data.hardware.DiskPercentInUse);
                avg.addValue("cpu%SF", data.sfHardware.Cpu);
                avg.addValue("ramInUseMB-SF", data.sfHardware.MemoryInUseMb);
                avg.addValue("ram%SF", data.sfHardware.PercentInUse);


                foreach (var process in data.processList)
                {
                    Uri key = process.GetProcessUri();
                    if (dic.ContainsKey(key))
                    {
                        dic[key].Add(process);
                    }
                    else
                    {
                        List<ProcessData> pList = new List<ProcessData>();
                        pList.Add(process);
                        dic.Add(key, pList);
                    }
                }
                
            }
            List<ProcessData> finalData = new List<ProcessData>();
            foreach (var key in dic.Keys)
            {
                finalData.Add(ProcessData.AverageProcessData(dic[key],key));
            }
                
            NodeData result = new NodeData(-1, "");
            Hardware hw = new Hardware(
                (float)avg.getAverage("cpu%"),
                (long)avg.getAverage("ramTotalGB"),
                (long)avg.getAverage("ramInUseMB"),
                avg.getAverage("ram%"),
                null);
            Hardware SfHw = new Hardware(
                (float)avg.getAverage("cpu%SF"),
                (long)avg.getAverage("ramTotalGB"),
                (long)avg.getAverage("ramInUseMB-SF"),
                avg.getAverage("ram%SF"),
                null);
            hw.DiskPercentInUse =(float)avg.getAverage("disk%");
            result.hardware = hw;
            result.processList = finalData;
            result.sfHardware = SfHw;
            return result;


        }

        public override string ToString()
        {
            String res;
            res =
                "\n  Node: "+nodeName+
                "\n                 Miliseconds: "+this.miliseconds%(SFUtilities.interval*100)+
                "\n Cpu %: " + hardware.Cpu +
                "\n Total RAM(GB): " + hardware.TotalMemoryGb +
                "\n Used RAM(MB): " + hardware.MemoryInUseMb +
                "\n % or RAM: " + hardware.PercentInUse;

            foreach (var d in hardware.allDrives)
            {
                res += d.ToString();
            }
            foreach(var p in processList)
            {
                res += p.ToString();
            }



            return res;
        }

        
    }
}
