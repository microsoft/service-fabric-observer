using Aggregator.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace Aggregator
{
    [Serializable]
    public class Hardware : DataBase<Hardware>
    {
        public float Cpu { get; }

        public long TotalMemoryGb { get; }

        public long MemoryInUseMb { get; }

        public double PercentInUse { get; }

        //public float DiskPercentInUse { get; set; }

        public List<Drive> allDrives = new List<Drive>();

        public Hardware(float Cpu, long TotalMemoryGb, long MemoryInUseMb, double PercentInUse, DriveInfo[] Drives = null)
        {
            this.Cpu = Cpu;
            this.TotalMemoryGb = TotalMemoryGb;
            this.MemoryInUseMb = MemoryInUseMb;
            this.PercentInUse = PercentInUse;

            if (Drives == null)
            {
                return;
            }

            foreach (var d in Drives)
            {
                try
                {
                    var drive = new Drive(
                        d.Name,
                        d.TotalSize / 1024 / 1024 / 1024,
                        d.AvailableFreeSpace / 1024 / 1024 / 1024
                        );

                    allDrives.Add(drive);
                }
                catch
                {

                }
            }
            //this.DiskPercentInUse = DiskPercentageInUse();
        }
        public Hardware AverageData(List<Hardware> list)
        {
            AverageDictionary avg = new AverageDictionary();
            DictionaryList<string, Drive> dicList = new DictionaryList<string, Drive>();
            Drive dummyObject = new Drive("",0,0);

            foreach (var data in list)
            {
                avg.AddValue("cpu%", data.Cpu);
                avg.AddValue("ramTotalGB", data.TotalMemoryGb);
                avg.AddValue("ramInUseMB", data.MemoryInUseMb);
                avg.AddValue("ram%", data.PercentInUse);
                if (data.allDrives.Count == 0)
                {
                    continue;
                }

                //avg.addValue("disk%", data.DiskPercentInUse);

                foreach (var disk in data.allDrives)
                {
                    dicList.Add(disk.Name, disk);
                }
            }

            List<Drive> finalDriveList = new List<Drive>();

            foreach (var key in dicList.GetKeys())
            {
                finalDriveList.Add(dummyObject.AverageData(dicList.GetList(key)));
            }

            var hardware = new Hardware(
                (float)avg.GetAverage("cpu%"),
                (long)avg.GetAverage("ramTotalGB"),
                (long)avg.GetAverage("ramInUseMB"),
                avg.GetAverage("ram%"))
            {
                allDrives = finalDriveList
            };

            return hardware;
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

            return cnt == 0 ? -1 : percentageSum / cnt;
        }
    }
}
