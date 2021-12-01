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

        public double miliseconds { get; }
        public Hardware hardware { get; set; }


        public NodeData(double miliseconds)
        {
            this.miliseconds = miliseconds;
        }

        

        public override string ToString()
        {
            String res;
            res =
                "\n                 Miliseconds: "+this.miliseconds%(SFUtilities.interval*100)+
                "\n Cpu %: " + hardware.Cpu +
                "\n Total RAM(GB): " + hardware.TotalMemoryGb +
                "\n Used RAM(MB): " + hardware.MemoryInUseMb +
                "\n % or RAM: " + hardware.PercentInUse;

            foreach (var d in hardware.allDrives)
            {
                res += "\n      Drive name: " + d.Name +
                    "\n         Drive total size: " + d.TotalDiskSpaceGB +
                    "\n         Available space: " + d.AvailableDiskSpaceGB;
            }
            return res;
        }

      
    }
}
