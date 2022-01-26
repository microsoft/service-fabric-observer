using Aggregator.Data;
using System;
using System.Collections.Generic;

namespace Aggregator
{
    /// <summary>
    /// NodeData is the wrapper class for everything that's collected with the logic of FabricObserver.
    /// Each Snapshot has List<NodeData> where every element of the list comes from a different node.
    /// </summary>
    [Serializable]
    public class NodeData:DataBase<NodeData>
    {   
        // You extend this class by adding new properties below. They must be serializable. 
        public string NodeName { get; }

        public double Milliseconds { get; }

        public Hardware Hardware { get; set; }

        public List<ProcessData> ProcessList { get; set; }

        public Hardware SfHardware { get; set; }
        //Implement List<byte[]> to store arbitrary data


        public NodeData(double miliseconds,string nodeName)
        {
            Milliseconds = miliseconds;
            NodeName = nodeName;
        }
        
        public NodeData AverageData(List<NodeData> list)
        {
            string nodeName = "";
            bool sameNodes = true;
            AverageDictionary avg = new AverageDictionary();
            DictionaryList<Uri, ProcessData> dicList = new DictionaryList<Uri, ProcessData>();
            List<Hardware> hardwareList = new List<Hardware>();
            List<Hardware> SfHardwareList = new List<Hardware>();

            foreach (var data in list) 
            {
                if (nodeName == "")
                {
                    nodeName = data.NodeName;
                }

                if (nodeName != data.NodeName)
                {
                    sameNodes = false;
                }

                avg.AddValue("miliseconds", data.Milliseconds);
                
                hardwareList.Add(data.Hardware);
                SfHardwareList.Add(data.SfHardware);

                foreach (var process in data.ProcessList)
                {
                    Uri key = process.GetProcessUri();
                    dicList.Add(key, process);
                }
                
            }

            if (!sameNodes)
            {
                nodeName = "Averaged Different Nodes";
            }

            List<ProcessData> finalData = new List<ProcessData>();

            foreach (var key in dicList.GetKeys())
            {
                var processList = dicList.GetList(key);
                finalData.Add(processList[0].AverageData(processList));
            }

            var nodeData =  new NodeData(avg.GetAverage("miliseconds"), nodeName)
            {
                Hardware = hardwareList[0].AverageData(hardwareList),
                SfHardware = SfHardwareList[0].AverageData(SfHardwareList),
                ProcessList = finalData
            };

            return nodeData;
        }

        public override string ToString()
        {
            string res = "\n  Node: " + NodeName +
                         "\n                 Miliseconds: "+Milliseconds%(SFUtilities.intervalMiliseconds*100) +
                         "\n Cpu %: " + Hardware.Cpu +
                         "\n Total RAM(GB): " + Hardware.TotalMemoryGb +
                         "\n Used RAM(MB): " + Hardware.MemoryInUseMb +
                         "\n % or RAM: " + Hardware.PercentInUse;

            foreach (var d in Hardware.allDrives)
            {
                res += d.ToString();
            }

            foreach(var p in ProcessList)
            {
                res += p.ToString();
            }

            return res;
        }

        public string ToStringMiliseconds()
        {
            return $"{Environment.NewLine} {NodeName} - Miliseconds: {Milliseconds % (SFUtilities.intervalMiliseconds * 100)}";
        }
    }
}
