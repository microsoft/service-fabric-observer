using Aggregator.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    //This is a wrapper class for DriveInfo to serialize useful data
    [Serializable]
    public class Drive : DataBase<Drive>
    {
        public string Name { get; }
        public long TotalDiskSpaceGB { get; }
        public long AvailableDiskSpaceGB { get; }
        public Drive(string Name, long TotalDiskSpaceGB, long AvailableDiskSpaceGB)
        {
            this.Name = Name;
            this.TotalDiskSpaceGB = TotalDiskSpaceGB;
            this.AvailableDiskSpaceGB = AvailableDiskSpaceGB;
        }

        public Drive AverageData(List<Drive> list)
        {
            AverageDictionary avg = new AverageDictionary();
            string name = "";
            bool sameName = true;

            foreach (var data in list)
            {
                if (name == "") name = data.Name;
                if (name != data.Name) sameName = false;
                avg.addValue("totalSpace", data.TotalDiskSpaceGB);
                avg.addValue("availableSpace", data.AvailableDiskSpaceGB);

            }
            if (!sameName) name = "Averaged Differnet Disks";
            return new Drive(
                name,
                (long)avg.getAverage("totalSpace"),
                (long)avg.getAverage("availableSpace")
                );

        }

        public override string ToString()
        {
            string res = "\n      Drive name: " + Name +
                    "\n         Drive total size: " + TotalDiskSpaceGB +
                    "\n         Available space: " + AvailableDiskSpaceGB; ;
            
            return res;
        }

       
    }
}
