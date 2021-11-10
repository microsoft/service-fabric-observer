using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.MachineInfoModel;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Newtonsoft.Json;
using ConfigSettings = FabricObserver.Observers.MachineInfoModel.ConfigSettings;


namespace FabricObserver.Observers
{
    class MyObserver : ObserverBase
    {
        // Support for concurrent monitoring.
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppCpuData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppMemDataMb;
        private ConcurrentDictionary<string, FabricResourceUsageData<double>> AllAppMemDataPercent;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppTotalActivePortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppEphemeralPortsData;
        private ConcurrentDictionary<string, FabricResourceUsageData<float>> AllAppHandlesData;
        private ConcurrentDictionary<string, FabricResourceUsageData<int>> AllAppThreadsData;

        // userTargetList is the list of ApplicationInfo objects representing app/app types supplied in configuration. List<T> is thread-safe for reads.
        // There are no concurrent writes for this List.
        private List<ApplicationInfo> userTargetList;

        // deployedTargetList is the list of ApplicationInfo objects representing currently deployed applications in the user-supplied list.
        // List<T> is thread-safe for reads. There are no concurrent writes for this List.
        private List<ApplicationInfo> deployedTargetList;
        private readonly ConfigSettings configSettings;
        private string fileName;
        private readonly Stopwatch stopwatch;
        private readonly object lockObj = new object();

        public int MaxChildProcTelemetryDataCount
        {
            get; set;
        }

        public bool EnableChildProcessMonitoring
        {
            get; set;
        }

        // List<T> is thread-safe for reads. There are no concurrent writes for this List.
        public List<ReplicaOrInstanceMonitoringInfo> ReplicaOrInstanceList
        {
            get; set;
        }

        public string ConfigPackagePath
        {
            get; set;
        }

        public bool EnableConcurrentMonitoring
        {
            get; set;
        }

        ParallelOptions ParallelOptions
        {
            get; set;
        }

        public bool EnableProcessDumps
        {
            get; set;
        }

        public MyObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            configSettings = new ConfigSettings(FabricServiceContext);
            ConfigPackagePath = configSettings.ConfigPackagePath;
            stopwatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            int iterations = 0;
            Debug.WriteLine("HELLLOOOOOOOOOOOOOOOOOOOOOO "+iterations++);
            Debug.WriteLine("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            Debug.WriteLine("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
            float cpu =CpuUtilizationProvider.Instance.GetProcessorTimePercentage();
            Debug.WriteLine("CPU: ----------- " + cpu + " ------------");
            var (TotalMemoryGb, MemoryInUseMb, PercentInUse) = OSInfoProvider.Instance.TupleGetMemoryInfo();
            Debug.WriteLine("MEMORY-TOTAL: ----------- " + TotalMemoryGb + " ------------");
            Debug.WriteLine("MEMORY-USE: ----------- " + MemoryInUseMb + " ------------");
            Debug.WriteLine("MEMORY-%: ----------- " + PercentInUse + " ------------");

            //var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
            //    new Uri("fabric:/FabricObserverApp/Aggregator") );

            //AggregatorProxy.PutData(cpu);



        }

        public override Task ReportAsync(CancellationToken token)
        {
            throw new NotImplementedException();
        }
    }
}
