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
                "\n --------------------------------------------------"+
                "\n Services in this process: ";
            foreach(var s in serviceUris)
            {
                res +=
                    "\n " + s.ToString();
            }
            res +=
                "\n\n CPU %: " + cpuPercentage +
                "\n RAM used MB: " + ramMb +
                "\n RAM %: " + ramPercentage;
            res +="\n"+ allCounts.ToString();
            if(ChildProcesses.Count!=0)res += "\n Child processes created by this process: ";
            foreach(var childProcess in ChildProcesses)
            {
                res += "\n    " + childProcess.procName;
            }

            return res;
        }

        public ProcessData AverageData(List<ProcessData> list)
        {
            
            AverageDictionary avg = new AverageDictionary();
            List<Counts> countsList = new List<Counts>();
            DictionaryList<Uri, bool> UriSet = new DictionaryList<Uri, bool>();
            DictionaryList<string, bool> ChildProcessSet = new DictionaryList<string, bool>();

            foreach (var data in list)
            {
                avg.addValue("cpu%", data.cpuPercentage);
                avg.addValue("ramMB", data.ramMb);
                avg.addValue("ram%", data.ramPercentage);
                countsList.Add(data.allCounts);
                foreach (Uri uri in data.serviceUris) UriSet.Add(uri, true);
                foreach (var proc in data.ChildProcesses) ChildProcessSet.Add(proc.procName, true);

            }
            ProcessData p = new ProcessData(-1);
            p.cpuPercentage = avg.getAverage("cpu%");
            p.ramMb = (float)avg.getAverage("ramMB");
            p.ramPercentage =(float) avg.getAverage("ram%");
            p.allCounts = countsList[0].AverageData(countsList);
            foreach (Uri uri in UriSet.GetKeys()) p.serviceUris.Add(uri);
            foreach (string proc in ChildProcessSet.GetKeys()) p.ChildProcesses.Add((proc, -1));
            

            return p;

            
        }
    }
}
