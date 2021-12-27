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
        public int primaryCount { get; set; }
        public int replicaCount { get; set; }
        public int instanceCount { get; set; }
        public int count { get; set; }
        public List<Uri> serviceUris { get; }


        public List<(string procName, int Pid)> ChildProcesses { get; set; }

        public static ProcessData AverageProcessData(List<ProcessData> list,Uri processUri)
        {
            AverageDictionary avg = new AverageDictionary();
            foreach(var data in list)
            {
                avg.addValue("cpu%", data.cpuPercentage);
                avg.addValue("ramMB", data.ramMb);
                avg.addValue("ram%", data.ramPercentage);
                avg.addValue("primary", data.primaryCount);
                avg.addValue("replica", data.replicaCount);
                avg.addValue("instance", data.instanceCount);
                avg.addValue("count", data.count);


            }
            ProcessData p = new ProcessData(-1);
            p.cpuPercentage = avg.getAverage("cpu%");
            p.ramMb =(float) avg.getAverage("ramMB");
            p.ramPercentage =(float) avg.getAverage("ram%");
            p.primaryCount =(int) avg.getAverage("primary");
            p.replicaCount =(int) avg.getAverage("replica");
            p.instanceCount =(int) avg.getAverage("instance");
            p.count = (int)avg.getAverage("count");
            p.serviceUris.Add(processUri);
            return p;

        }
        /// <summary>
        /// this should return replica Uri for exlusive processes and APP Uri for shared
        /// For now it returns the first URI
        /// </summary>
        /// <returns></returns>
        public Uri GetProcessUri()
        {
            return serviceUris[0];
        }

        public ProcessData(int processId)
        {
            this.processId = processId;
            this.serviceUris = new List<Uri>();
            this.ChildProcesses = new List<(string procName, int Pid)>();
            primaryCount = 0;
            replicaCount = 0;
            instanceCount = 0;
            count = 0;
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
