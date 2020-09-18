// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

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
        private readonly StringBuilder message;

        public SampleNewObserver()
        {
            this.message = new StringBuilder();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            int totalNumberOfDeployedSFApps = 0, totalNumberOfDeployedServices = 0, totalNumberOfPartitions = 0, totalNumberOfReplicas = 0;
            int appsInWarningError = 0, servicesInWarningError = 0, partitionsInWarningError = 0, replicasInWarningError = 0;

            var apps = await FabricClientInstance.QueryManager.GetApplicationListAsync(
                null,
                AsyncClusterOperationTimeoutSeconds,
                token).ConfigureAwait(false);

            totalNumberOfDeployedSFApps = apps.Count;
            appsInWarningError = apps.Where(a => a.HealthState == HealthState.Warning || a.HealthState == HealthState.Error).Count();

            foreach (var app in apps)
            {
                var services = await FabricClientInstance.QueryManager.GetServiceListAsync(
                    app.ApplicationName,
                    null,
                    AsyncClusterOperationTimeoutSeconds,
                    token).ConfigureAwait(false);

                totalNumberOfDeployedServices += services.Count;
                servicesInWarningError += services.Where(s => s.HealthState == HealthState.Warning || s.HealthState == HealthState.Error).Count();
                
                foreach (var service in services)
                {
                    var partitions = await FabricClientInstance.QueryManager.GetPartitionListAsync(
                        service.ServiceName,
                        null,
                        AsyncClusterOperationTimeoutSeconds,
                        token).ConfigureAwait(false);

                    totalNumberOfPartitions += partitions.Count;
                    partitionsInWarningError += partitions.Where(p => p.HealthState == HealthState.Warning || p.HealthState == HealthState.Error).Count();

                    foreach (var partition in partitions)
                    {
                        var replicas = await FabricClientInstance.QueryManager.GetReplicaListAsync(
                            partition.PartitionInformation.Id,
                            null,
                            AsyncClusterOperationTimeoutSeconds,
                            token).ConfigureAwait(false);

                        totalNumberOfReplicas += replicas.Count;
                        replicasInWarningError += replicas.Where(r => r.HealthState == HealthState.Warning || r.HealthState == HealthState.Error).Count();
                    }
                }
            }

            this.message.AppendLine($"Total number of Applications: {totalNumberOfDeployedSFApps}");
            this.message.AppendLine($"Total number of Applications in Warning or Error: {appsInWarningError}");
            this.message.AppendLine($"Total number of Services: {totalNumberOfDeployedServices}");
            this.message.AppendLine($"Total number of Services in Warning or Error: {servicesInWarningError}");
            this.message.AppendLine($"Total number of Partitions: {totalNumberOfPartitions}");
            this.message.AppendLine($"Total number of Partitions in Warning or Error: {partitionsInWarningError}");
            this.message.AppendLine($"Total number of Replicas: {totalNumberOfReplicas}");
            this.message.AppendLine($"Total number of Replicas in Warning or Error: {replicasInWarningError}");

            // The time it took to run ObserveAsync; for use in computing HealthReport TTL.
            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;

            this.message.AppendLine($"Time it took to run {base.ObserverName}.ObserveAsync: {RunDuration}");

            await ReportAsync(token);
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            // Local log.
            ObserverLogger.LogInfo(message.ToString());

            /* Report to Fabric */

            // These values will be preserved across observer runs and are useful for clearing warnings 
            // by reporting Ok health state health events with the same property and sourceid values 
            // as the error/warning health events when FO is safely taken down (e.g., app is being uninstalled, 
            // safe restart of fabric node it's running on, etc.).
            HealthReportProperties.Add("SomePropertyName");
            HealthReportSourceIds.Add($"{ObserverName}_SomethingUniqueToThisReport");

            var healthReporter = new ObserverHealthReporter(ObserverLogger);
            var healthReport = new Utilities.HealthReport
            {
                Code = FoErrorWarningCodes.Ok,
                HealthMessage = this.message.ToString(),
                NodeName = NodeName,
                Observer = ObserverName,
                Property = HealthReportProperties[^1],
                ReportType = HealthReportType.Node,
                State = HealthState.Ok,
            };

            healthReporter.ReportHealthToServiceFabric(healthReport);

            // Emit Telemetry - This will use whatever telemetry provider you have configured in FabricObserver Settings.xml.
            var telemetryData = new TelemetryData(FabricClientInstance, Token)
            {
                Code = FoErrorWarningCodes.Ok,
                HealthEventDescription = this.message.ToString(),
                HealthState = "Ok",
                NodeName = NodeName,
                ObserverName = ObserverName,
                Source = ObserverConstants.FabricObserverName,
            };

            if (IsTelemetryProviderEnabled && IsObserverTelemetryEnabled)
            {
                _ = TelemetryClient?.ReportHealthAsync(
                        telemetryData,
                        Token);
            }

            // ETW.
            if (IsEtwEnabled)
            {
                Logger.EtwLogger?.Write(
                    ObserverConstants.FabricObserverETWEventName,
                    new
                    {
                        Code = FoErrorWarningCodes.Ok,
                        HealthEventDescription = this.message.ToString(),
                        HealthState = "Ok",
                        NodeName,
                        ObserverName,
                        Source = ObserverConstants.FabricObserverName,
                    });
            }

            this.message.Clear();

            return Task.CompletedTask;
        }
    }
}