using FabricObserver.Observers.Utilities;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Aggregator.Collectors
{
    public class NodeCollector : CollectorBase<NodeData>
    {
        public NodeCollector(ServiceContext context, CancellationToken cancellationToken) : base(context, cancellationToken) { }
        protected async override Task<NodeData> CollectData()
        {
            float cpu = CpuUtilizationProvider.Instance.GetProcessorTimePercentage();
            var (TotalMemoryGb, MemoryInUseMb, PercentInUse) = OSInfoProvider.Instance.TupleGetMemoryInfo();
            DriveInfo[] allDrives = DriveInfo.GetDrives();
            string nodeName = Context.NodeContext.NodeName;


            var (totalMiliseconds, _) = SFUtilities.getTime();

            Dictionary<int, ProcessData> pids = await SFUtilities.Instance.GetDeployedProcesses(nodeName, this.Context, cancellationToken);
            List<ProcessData> processDataList = new List<ProcessData>();

            //for instead of Parallel.For because Parallel.For bursts the CPU before we load it and this is more lightweigh on the cpu even though timing isn't exact. This is more relevant.
            // CT: The problem with sequential here is that if there are a *lot* of service processes this could take a long time to complete. You can control the level
            // of parallelism (see what is done in AppObserver, for example). I do not see bursts of CPU in FO with Parallelism enabled (that is, at the default level of parallelism, which is 1/4 of available CPUs).
            // For now (MVP), this is fine.
            double totalSfCpu = 0, totalSfRamMB = 0, totalSfRamPercentage = 0;
            for (int i = 0; i < pids.Count; i++)
            {
                int pid = pids.ElementAt(i).Key;
                var processData = pids[pid];
                var (cpuPercentage, ramMb) = await SFUtilities.Instance.TupleGetResourceUsageForProcess(pid);
                processData.cpuPercentage = cpuPercentage;
                processData.ramMb = ramMb;
                float ramPercentage = 100;
                if (TotalMemoryGb > 0) ramPercentage = (100 * ramMb) / (1024 * TotalMemoryGb);

                processData.ramPercentage = ramPercentage;
                processDataList.Add(processData);
                totalSfCpu += cpuPercentage;
                totalSfRamMB += ramMb;
                totalSfRamPercentage += ramPercentage;

                foreach (var childProcess in processData.ChildProcesses)
                {
                    (cpuPercentage, ramMb) = await SFUtilities.Instance.TupleGetResourceUsageForProcess(childProcess.Pid);
                    totalSfCpu += cpuPercentage;
                    totalSfRamMB += ramMb;
                }
                if (TotalMemoryGb > 0) totalSfRamPercentage = (100 * totalSfRamMB) / (1024 * TotalMemoryGb);

            }

            var data = new NodeData(totalMiliseconds, nodeName)
            {
                hardware = new Hardware(cpu, TotalMemoryGb, MemoryInUseMb, PercentInUse, allDrives),
                processList = processDataList,
                sfHardware = new Hardware((float)totalSfCpu, TotalMemoryGb, (long)totalSfRamMB, totalSfRamPercentage, null)
            };
            return data;
        }
    }
}
