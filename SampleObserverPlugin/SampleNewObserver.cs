using System;
using System.Diagnostics;
using System.Fabric.Health;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers
{
    public class SampleNewObserver : ObserverBase
    {
        StringBuilder _message = new StringBuilder();
        
        public SampleNewObserver()
            : base(nameof(SampleNewObserver))
        {
           
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            int _totalNumberOfDeployedSFApps = 0, _totalNumberOfDeployedServices = 0, _totalNumberOfPartitions = 0, _totalNumberOfReplicas = 0;
            int _appsInWarningError = 0, _servicesInWarningError = 0, _partitionsInWarningError = 0, _replicasInWarningError = 0;

            var apps = await this.FabricClientInstance.QueryManager.GetApplicationListAsync(
                null,
                this.AsyncClusterOperationTimeoutSeconds,
                token).ConfigureAwait(false);
            
            _totalNumberOfDeployedSFApps = apps.Count;
            _appsInWarningError = apps.Where(a => a.HealthState == HealthState.Warning || a.HealthState == HealthState.Error).Count();
            
            foreach (var app in apps)
            {
                var services = await this.FabricClientInstance.QueryManager.GetServiceListAsync(
                    app.ApplicationName,
                    null,
                    this.AsyncClusterOperationTimeoutSeconds,
                    token).ConfigureAwait(false);

                _totalNumberOfDeployedServices += services.Count;
                _servicesInWarningError += services.Where(s => s.HealthState == HealthState.Warning || s.HealthState == HealthState.Error).Count();
                
                foreach (var service in services)
                {
                    var partitions = await this.FabricClientInstance.QueryManager.GetPartitionListAsync(
                        service.ServiceName,
                        null,
                        this.AsyncClusterOperationTimeoutSeconds,
                        token).ConfigureAwait(false);

                    _totalNumberOfPartitions += partitions.Count;
                    _partitionsInWarningError += partitions.Where(p => p.HealthState == HealthState.Warning || p.HealthState == HealthState.Error).Count();

                    foreach (var partition in partitions)
                    {
                        var replicas = await this.FabricClientInstance.QueryManager.GetReplicaListAsync(
                            partition.PartitionInformation.Id,
                            null,
                            this.AsyncClusterOperationTimeoutSeconds,
                            token).ConfigureAwait(false);

                        _totalNumberOfReplicas += replicas.Count;
                        _replicasInWarningError += replicas.Where(r => r.HealthState == HealthState.Warning || r.HealthState == HealthState.Error).Count();
                    }
                }
            }

            this._message.AppendLine($"Total number of Applications: {_totalNumberOfDeployedSFApps}");
            this._message.AppendLine($"Total number of Applications in Warning or Error: {_appsInWarningError}");
            this._message.AppendLine($"Total number of Services: {_totalNumberOfDeployedServices}");
            this._message.AppendLine($"Total number of Services in Warning or Error: {_servicesInWarningError}");
            this._message.AppendLine($"Total number of Partitions: {_totalNumberOfPartitions}");
            this._message.AppendLine($"Total number of Partitions in Warning or Error: {_partitionsInWarningError}");
            this._message.AppendLine($"Total number of Replicas: {_totalNumberOfReplicas}");
            this._message.AppendLine($"Total number of Replicas in Warning or Error: {_replicasInWarningError}");

            // The time it took to run ObserveAsync; for use in computing HealthReport TTL.
            stopwatch.Stop();
            this.RunDuration = stopwatch.Elapsed;

            this._message.AppendLine($"Time it took to run {base.ObserverName}.ObserveAsync: {this.RunDuration}");
            
            await this.ReportAsync(token);
            this.LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            // Report to Fabric.
            var healthReporter = new ObserverHealthReporter(this.ObserverLogger);
            var healthReport = new Utilities.HealthReport
            {
                Code = FoErrorWarningCodes.Ok,
                HealthMessage = this._message.ToString(),
                NodeName = this.NodeName,
                Observer = this.ObserverName,
                ReportType = HealthReportType.Node,
                State = HealthState.Ok,
            };

            healthReporter.ReportHealthToServiceFabric(healthReport);

            // Emit Telemetry - This will use whatever telemetry provider you have configured in FabricObserver Settings.xml.
            var telemetryData = new TelemetryData(this.FabricClientInstance, this.Token)
            {
                Code = FoErrorWarningCodes.Ok,
                HealthEventDescription = this._message.ToString(),
                HealthState = "Ok",
                NodeName = this.NodeName,
                ObserverName = this.ObserverName,
                Source = ObserverConstants.FabricObserverName,
            };

            // Remember that these settings live in FabricObserver project's Settings.xml. You are writing
            // an observer plugin that will, well, plug into the existing FabricObserver runtime environment
            // simply by putting the compiled output of this project (a .NET Core 3.1 dll) into the plugins folder in
            // the FabricObserver project's PackageRoot/Data/Plugins folder.
            if (this.IsTelemetryProviderEnabled && this.IsObserverTelemetryEnabled)
            {
                _ = this.TelemetryClient?.ReportHealthAsync(
                        telemetryData,
                        this.Token);
            }

            this._message.Clear();
           
            return Task.CompletedTask;
        }
    }
}