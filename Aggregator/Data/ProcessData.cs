using Aggregator.Data;
using System;
using System.Collections.Generic;

namespace Aggregator
{
    [Serializable]
    public class ProcessData : DataBase<ProcessData>
    {
        public int ProcessId { get; }

        public double CpuPercentage { get; set; }

        public float RamMb { get; set; }

        public float RamPercentage { get; set; }

        public List<Uri> ServiceUris { get; }

        public Counts AllCounts { get; set; }

        public List<(string procName, int Pid)> ChildProcesses { get; set; }

        /// <summary>
        /// this should return replica Uri for exlusive processes and APP Uri for shared
        /// For now it returns the first URI
        /// </summary>
        /// <returns></returns>
        public Uri GetProcessUri()
        {
            return ServiceUris[0];
        }

        public ProcessData(int processId)
        {
            ProcessId = processId;
            ServiceUris = new List<Uri>();
            ChildProcesses = new List<(string procName, int Pid)>();
            AllCounts = new Counts(0, 0, 0, 0);
        }

        public override string ToString()
        {
            string res =
                "\n --------------------------------------------------"+
                "\n Services in this process: ";

            foreach(var s in ServiceUris)
            {
                res +=  "\n " + s.ToString();
            }

            res +=
                "\n\n CPU %: " + CpuPercentage +
                "\n RAM used MB: " + RamMb +
                "\n RAM %: " + RamPercentage +
                "\n" + AllCounts.ToString();

            if (ChildProcesses.Count != 0)
            {
                res += "\n Child processes created by this process: ";
            }

            foreach(var (procName, Pid) in ChildProcesses)
            {
                res += "\n    " + procName;
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
                avg.AddValue("cpu%", data.CpuPercentage);
                avg.AddValue("ramMB", data.RamMb);
                avg.AddValue("ram%", data.RamPercentage);
                countsList.Add(data.AllCounts);

                foreach (Uri uri in data.ServiceUris)
                {
                    UriSet.Add(uri, true);
                }

                foreach (var (procName, _) in data.ChildProcesses)
                {
                    ChildProcessSet.Add(procName, true);
                }
            }

            ProcessData p = new ProcessData(-1)
            {
                CpuPercentage = avg.GetAverage("cpu%"),
                RamMb = (float)avg.GetAverage("ramMB"),
                RamPercentage = (float)avg.GetAverage("ram%"),
                AllCounts = countsList[0].AverageData(countsList)
            };

            foreach (Uri uri in UriSet.GetKeys())
            {
                p.ServiceUris.Add(uri);
            }

            foreach (string proc in ChildProcessSet.GetKeys())
            {
                p.ChildProcesses.Add((proc, -1));
            }
            
            return p; 
        }
    }
}
