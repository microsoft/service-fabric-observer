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
            string nodeName = "";
            bool sameNodes = true;
            AverageDictionary avg = new AverageDictionary();
            DictionaryList<Uri, ProcessData> dicList = new DictionaryList<Uri, ProcessData>();
            List<Hardware> hardwareList = new List<Hardware>();
            List<Hardware> SfHardwareList = new List<Hardware>();
            foreach (var data in list) 
            {
                if (nodeName == "") nodeName = data.nodeName;
                if (nodeName != data.nodeName) sameNodes = false;
                avg.addValue("miliseconds", data.miliseconds);
                
                hardwareList.Add(data.hardware);
                SfHardwareList.Add(data.sfHardware);

                foreach (var process in data.processList)
                {
                    Uri key = process.GetProcessUri();
                    dicList.Add(key, process);
                }
                
            }
            if (!sameNodes) nodeName = "Averaged Different Nodes";
            List<ProcessData> finalData = new List<ProcessData>();
            foreach (var key in dicList.GetKeys())
            {
                finalData.Add(ProcessData.AverageProcessData(dicList.GetList(key),key));
            }

            return new NodeData(
                avg.getAverage("miliseconds"),
                nodeName
                )
            {
                hardware = Hardware.AverageData(hardwareList),
                sfHardware = Hardware.AverageData(SfHardwareList),
                processList = finalData
            };
           


        }

        public override string ToString()
        {
            String res;
            res =
                "\n  Node: "+nodeName+
                "\n                 Miliseconds: "+this.miliseconds%(SFUtilities.intervalMiliseconds*100)+
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

        public string ToStringMiliseconds()
        {
            String res;
            res =
                "\n " + nodeName + " - Miliseconds: " + this.miliseconds % (SFUtilities.intervalMiliseconds * 100);

            return res;
        }
    }
}
