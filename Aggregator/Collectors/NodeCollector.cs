using FabricObserver.Observers.Utilities;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Aggregator.Collectors
{
    public class NodeCollector : CollectorBase<NodeData>
    {
        public NodeCollector(ServiceContext context, CancellationToken cancellationToken) 
            : base(context, cancellationToken) 
        { 

        }

        protected async override Task<NodeData> CollectData()
        {
            float cpu = CpuUtilizationProvider.Instance.GetProcessorTimePercentage();
            var (TotalMemoryGb, MemoryInUseMb, PercentInUse) = OSInfoProvider.Instance.TupleGetSystemMemoryInfo();
            DriveInfo[] allDrives = DriveInfo.GetDrives();
            string nodeName = Context.NodeContext.NodeName;
            var (totalMiliseconds, _) = SFUtilities.TupleGetTime();
            Dictionary<int, ProcessData> pids = await SFUtilities.Instance.GetDeployedProcesses(nodeName, Context, cancellationToken);
            List<ProcessData> processDataList = new List<ProcessData>();
            double totalSfCpu = 0, totalSfRamMB = 0, totalSfRamPercentage = 0;

            for (int i = 0; i < pids.Count; ++i)
            {
                int pid = pids.ElementAt(i).Key;
                var processData = pids[pid];
                var (cpuPercentage, ramMb) = SFUtilities.Instance.TupleGetResourceUsageForProcess(pid);
                processData.CpuPercentage = cpuPercentage;
                processData.RamMb = ramMb;
                float ramPercentage = 100;

                if (TotalMemoryGb > 0)
                {
                    ramPercentage = (100 * ramMb) / (1024 * TotalMemoryGb);
                }

                processData.RamPercentage = ramPercentage;
                processDataList.Add(processData);
                totalSfCpu += cpuPercentage;
                totalSfRamMB += ramMb;
                totalSfRamPercentage += ramPercentage;

                foreach (var (procName, Pid) in processData.ChildProcesses)
                {
                    (cpuPercentage, ramMb) = SFUtilities.Instance.TupleGetResourceUsageForProcess(Pid);
                    totalSfCpu += cpuPercentage;
                    totalSfRamMB += ramMb;
                }

                if (TotalMemoryGb > 0)
                {
                    totalSfRamPercentage = (100 * totalSfRamMB) / (1024 * TotalMemoryGb);
                }
            }

            var data = new NodeData(totalMiliseconds, nodeName)
            {
                Hardware = new Hardware(cpu, TotalMemoryGb, MemoryInUseMb, PercentInUse, allDrives),
                ProcessList = processDataList,
                SfHardware = new Hardware((float)totalSfCpu, TotalMemoryGb, (long)totalSfRamMB, totalSfRamPercentage, null)
            };

            return data;
        }
    }
}
