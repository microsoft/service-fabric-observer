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

        //public float DiskPercentInUse { get; set; }

        public List<Drive> allDrives = new List<Drive>();

        public Hardware(float Cpu, long TotalMemoryGb, long MemoryInUseMb, double PercentInUse, DriveInfo[] Drives=null)
        {

            this.Cpu = Cpu;
            this.TotalMemoryGb = TotalMemoryGb;
            this.MemoryInUseMb = MemoryInUseMb;
            this.PercentInUse = PercentInUse;
            if (Drives == null) return;
            foreach (var d in Drives)
            {
                try
                {
                    var drive = new Drive(
                        d.Name,
                        d.TotalSize / 1024 / 1024 / 1024,
                        d.AvailableFreeSpace / 1024 / 1024 / 1024
                        );
                    this.allDrives.Add(drive);
                }
                catch (Exception e)
                {

                }
            }
            //this.DiskPercentInUse = DiskPercentageInUse();
        }
        public static Hardware AverageData(List<Hardware> list)
        {
            AverageDictionary avg = new AverageDictionary();
            DictionaryList<string, Drive> dicList = new DictionaryList<string, Drive>();
            foreach (var data in list)
            {
                avg.addValue("cpu%", data.Cpu);
                avg.addValue("ramTotalGB", data.TotalMemoryGb);
                avg.addValue("ramInUseMB", data.MemoryInUseMb);
                avg.addValue("ram%", data.PercentInUse);
                if (data.allDrives.Count == 0) continue;
                //avg.addValue("disk%", data.DiskPercentInUse);
                foreach (var disk in data.allDrives)
                {
                    dicList.Add(disk.Name, disk);
                }
            }
            List<Drive> finalDriveList = new List<Drive>();
            foreach (var key in dicList.GetKeys())
            {
                finalDriveList.Add(Drive.AverageData(dicList.GetList(key)));
            }
            return new Hardware(
                (float)avg.getAverage("cpu%"),
                (long)avg.getAverage("ramTotalGB"),
                (long)avg.getAverage("ramInUseMB"),
                avg.getAverage("ram%")
                )
            {
                allDrives = finalDriveList
            };


        }
        public float DiskPercentageInUse()
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
