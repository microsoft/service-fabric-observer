using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
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
}
