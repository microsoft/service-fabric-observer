using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class Hardware
    {
        public float Cpu { get; }
        public long TotalMemoryGb { get; }
        public long MemoryInUseMb { get; }
        public double PercentInUse { get; }

        public float DiskPercentInUse { get; set; }

        public List<Drive> allDrives = new List<Drive>();

        public Hardware( float Cpu, long TotalMemoryGb, long MemoryInUseMb, double PercentInUse, DriveInfo[] Drives)
        {
            
            this.Cpu = Cpu;
            this.TotalMemoryGb = TotalMemoryGb;
            this.MemoryInUseMb = MemoryInUseMb;
            this.PercentInUse = PercentInUse;
            if (Drives == null) return;
            foreach (var d in Drives)
            {
                var drive = new Drive(
                    d.Name,
                    d.TotalSize / 1024 / 1024 / 1024,
                    d.AvailableFreeSpace / 1024 / 1024 / 1024
                    );
                this.allDrives.Add(drive);
            }
            this.DiskPercentInUse = DiskPercentageInUse();
        }
        private float DiskPercentageInUse()
        {
            int cnt = 0;
            float percentageSum = 0;

            foreach (Drive drive in allDrives)
            {
                percentageSum += ((float)drive.AvailableDiskSpaceGB) / drive.TotalDiskSpaceGB;
                cnt++;
            }
            if (cnt == 0) return -1;
            return percentageSum / cnt;
        }
    }
}
