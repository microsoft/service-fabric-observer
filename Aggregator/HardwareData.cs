using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Aggregator
{

    //This Data is passed to the Aggregator using Remote Procedure Calls from Collector instances
    [Serializable]
    public class HardwareData
    {

        //This is a wrapper class for DriveInfo to serialize useful data
        [Serializable]
        public class Drive
        {
            public Drive(string Name, long TotalDiskSpaceGB, long AvailableDiskSpaceGB)
            {
                this.Name = Name;
                this.TotalDiskSpaceGB = TotalDiskSpaceGB;
                this.AvailableDiskSpaceGB = AvailableDiskSpaceGB;
            }
            public string Name { get; }
            public long TotalDiskSpaceGB { get; }
            public long AvailableDiskSpaceGB { get; }
        }

        public HardwareData(float Cpu, long TotalMemoryGb, long MemoryInUseMb, double PercentInUse, DriveInfo[] Drives)
        {
            this.Cpu = Cpu;
            this.TotalMemoryGb = TotalMemoryGb;
            this.MemoryInUseMb = MemoryInUseMb;
            this.PercentInUse = PercentInUse;
            foreach (var d in Drives)
            {
                var drive = new Drive(
                    d.Name,
                    d.TotalSize / 1024 / 1024 / 1024,
                    d.AvailableFreeSpace / 1024 / 1024 / 1024
                    );
                this.allDrives.Add(drive);
            }
        }

        public float Cpu { get; }
        public long TotalMemoryGb { get; }
        public long MemoryInUseMb { get; }
        public double PercentInUse { get; }
        public List<Drive> allDrives = new List<Drive>();
        //public DriveInfo[] allDrives { get; }

        public override string ToString()
        {
            String res;
            res =
                "\nCpu %: " + this.Cpu +
                "\n Total RAM(GB): " + this.TotalMemoryGb +
                "\n Used RAM(MB): " + this.MemoryInUseMb +
                "\n % or RAM: " + this.PercentInUse;

            foreach (var d in allDrives)
            {
                res += "\n      Drive name: " + d.Name +
                    "\n         Drive total size: " + d.TotalDiskSpaceGB +
                    "\n         Available space: " + d.AvailableDiskSpaceGB;
            }
            return res;
        }
    }
}
