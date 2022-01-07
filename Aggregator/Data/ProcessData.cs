using Aggregator.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    [Serializable]
    public class ProcessData:DataBase<ProcessData>
    {
        public int processId { get; }
        public double cpuPercentage { get; set; }
        public float ramMb { get; set; }
        public float ramPercentage { get; set; }
        public List<Uri> serviceUris { get; }
        public Counts allCounts { get; set; }
        public List<(string procName, int Pid)> ChildProcesses { get; set; }

        
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
            allCounts = new Counts(0, 0, 0, 0);
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

        public ProcessData AverageData(List<ProcessData> list)
        {
            
            AverageDictionary avg = new AverageDictionary();
            List<Counts> countsList = new List<Counts>();

            foreach (var data in list)
            {
                avg.addValue("cpu%", data.cpuPercentage);
                avg.addValue("ramMB", data.ramMb);
                avg.addValue("ram%", data.ramPercentage);
                countsList.Add(data.allCounts);
            }
            ProcessData p = new ProcessData(-1);
            p.cpuPercentage = avg.getAverage("cpu%");
            p.ramMb = (float)avg.getAverage("ramMB");
            p.allCounts = countsList[0].AverageData(countsList);
            p.serviceUris.Add(new Uri("fabric:/Internship/AggregatorNeedToImplementThis"));
            //still have to add ChildProcess List

            return p;

            
        }
    }
}
