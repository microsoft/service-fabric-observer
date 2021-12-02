using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class ProcessData
    {
        public int processId { get; }
        public double cpuPercentage { get; set; }
        public float ramMb { get; set; }
        public float ramPercentage { get; set; }

        public List<Uri> serviceUris { get; }

        public ProcessData(int processId)
        {
            this.processId = processId;
            this.serviceUris = new List<Uri>();
        }

        public override string ToString()
        {
            string res =
                "\n        PID:" + processId +
                "\n Services in this process: ";
            foreach(var s in serviceUris)
            {
                res +=
                    "\n" + s.ToString();
            }
            res +=
                "\n CPU %: " + cpuPercentage +
                "\n RAM used MB: " + ramMb +
                "\n RAM %: " + ramPercentage;
            return res;
        }
    }
}
