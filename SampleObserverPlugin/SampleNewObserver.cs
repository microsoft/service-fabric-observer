// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Polly;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    /// <summary>
    /// An observer plugin muse derive from ObserverBase and implement 2 abstract functions, ObserveAsync and ReportAsync.
    /// </summary>
    public class SampleNewObserver : ObserverBase
    {
        private readonly StringBuilder message;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fabricClient">FabricClient instance. This is managed by FabricObserver.</param>
        /// <param name="context">StatelessServiceContext instance. This is managed by FabricObserver.</param>
        public SampleNewObserver(FabricClient fabricClient, StatelessServiceContext context) : base(fabricClient, context)
        {
            message = new StringBuilder();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }
            
            var stopwatch = Stopwatch.StartNew();
            int totalNumberOfDeployedServices = 0, totalNumberOfPartitions = 0, totalNumberOfReplicas = 0;
            int servicesInWarningError = 0, partitionsInWarningError = 0, replicasInWarningError = 0;

            await EmitNodeSnapshotEtwAsync(NodeName);
            
            // Let's make sure that we page through app lists that are huge (like 4MB result set (that's a lot of apps)).
            var deployedAppQueryDesc = new PagedDeployedApplicationQueryDescription(NodeName)
            {
                IncludeHealthState = false,
                MaxResults = 150
            };

            // Fabric retry. This is built into FabricObserver. Commented out here to showcase using a different retry approach using an external library, Polly.
            // Note how you handle external libraries that your plugin library requires by simply building this sample, which will place this plugin (SampleNewObserver.dll) and
            // Polly.dll - *and any of its dependencies* - into the FO package's PackageRoot/Data folder.
            /*var appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                             () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                                                         deployedAppQueryDesc,
                                                                                         ConfigurationSettings.AsyncTimeout,
                                                                                         Token),
                                             Token);*/

            // Polly retry policy for when FabricException is thrown by its Execute predicate (GetDeployedApplicationPagedListAsync).
            // The purpose of using Polly here is to show how you deal with dependencies (put them all in the same folder where your
            // plugin dll lives, including the dependencies, if any, of the primary dependencies you employ).
            var policy = Policy.Handle<FabricException>()
                                   .Or<TimeoutException>()
                                   .WaitAndRetryAsync(
                                        new[]
                                        {
                                            TimeSpan.FromSeconds(1),
                                            TimeSpan.FromSeconds(3),
                                            TimeSpan.FromSeconds(5),
                                            TimeSpan.FromSeconds(10),
                                        });

            var appList = 
                await policy.ExecuteAsync(
                                () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                        deployedAppQueryDesc,
                                        ConfigurationSettings.AsyncTimeout,
                                        Token));

            // DeployedApplicationList is a wrapper around List, but does not support AddRange.. Thus, cast it ToList and add to the temp list, then iterate through it.
            // In reality, this list will never be greater than, say, 1000 apps deployed to a node, but it's a good idea to be prepared since AppObserver supports
            // all-app service process monitoring with a very simple configuration pattern.
            var apps = appList.ToList();

            // The GetDeployedApplicationPagedList api will set a continuation token value if it knows it did not return all the results in one swoop.
            // Check that it is not null, and make a new query passing back the token it gave you.
            while (appList.ContinuationToken != null)
            {
                Token.ThrowIfCancellationRequested();

                deployedAppQueryDesc.ContinuationToken = appList.ContinuationToken;

                // This is how you can do retries without using an external library.
                /*appList = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                        () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                deployedAppQueryDesc,
                                                ConfigurationSettings.AsyncTimeout,
                                                Token),
                                        Token);*/

                appList = await policy.ExecuteAsync(
                                        () => FabricClientInstance.QueryManager.GetDeployedApplicationPagedListAsync(
                                                deployedAppQueryDesc,
                                                ConfigurationSettings.AsyncTimeout,
                                                Token));

                apps.AddRange(appList.ToList());

                // Wait a second before grabbing the next batch of apps..
                await Task.Delay(TimeSpan.FromSeconds(1), Token);
            }

            var totalNumberOfDeployedSFApps = apps.Count;
            var appsInWarningError = apps.Count(a => a.HealthState == HealthState.Warning || a.HealthState == HealthState.Error);

            foreach (var app in apps)
            {
                var services = await FabricClientInstance.QueryManager.GetServiceListAsync(
                                        app.ApplicationName,
                                        null,
                                        AsyncClusterOperationTimeoutSeconds,
                                        token);

                totalNumberOfDeployedServices += services.Count;
                servicesInWarningError += services.Count(s => s.HealthState == HealthState.Warning || s.HealthState == HealthState.Error);

                foreach (var service in services)
                {
                    var partitions = await FabricClientInstance.QueryManager.GetPartitionListAsync(
                                            service.ServiceName,
                                            null,
                                            AsyncClusterOperationTimeoutSeconds,
                                            token);

                    totalNumberOfPartitions += partitions.Count;
                    partitionsInWarningError += partitions.Count(p => p.HealthState == HealthState.Warning || p.HealthState == HealthState.Error);

                    foreach (var partition in partitions)
                    {
                        var replicas = await FabricClientInstance.QueryManager.GetReplicaListAsync(
                                                partition.PartitionInformation.Id,
                                                null,
                                                AsyncClusterOperationTimeoutSeconds,
                                                token);

                        totalNumberOfReplicas += replicas.Count;
                        replicasInWarningError += replicas.Count(r => r.HealthState == HealthState.Warning || r.HealthState == HealthState.Error);
                    }
                }
            }

            message.AppendLine($"Total number of Applications: {totalNumberOfDeployedSFApps}");
            message.AppendLine($"Total number of Applications in Warning or Error: {appsInWarningError}");
            message.AppendLine($"Total number of Services: {totalNumberOfDeployedServices}");
            message.AppendLine($"Total number of Services in Warning or Error: {servicesInWarningError}");
            message.AppendLine($"Total number of Partitions: {totalNumberOfPartitions}");
            message.AppendLine($"Total number of Partitions in Warning or Error: {partitionsInWarningError}");
            message.AppendLine($"Total number of Replicas: {totalNumberOfReplicas}");
            message.AppendLine($"Total number of Replicas in Warning or Error: {replicasInWarningError}");

            // The time it took to run ObserveAsync; for use in computing HealthReport TTL.
            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;
            message.AppendLine($"Time it took to run {ObserverName}.ObserveAsync: {RunDuration.TotalSeconds} seconds.");

            await ReportAsync(token);
            LastRunDateTime = DateTime.Now;
        }

        /// <summary>
        /// Emits an ETW event containing Fabric node data.
        /// </summary>
        /// <param name="nodeName">Name of the target node. If absent (or null), then the local Fabric node.</param>
        /// <returns>Task</returns>
        private async Task EmitNodeSnapshotEtwAsync(string nodeName = null)
        {
            // This function isn't useful if you don't enable ETW for the plugin.
            if (!IsEtwEnabled)
            {
                return;
            }

            try
            {
                var nodes = await FabricClientInstance.QueryManager.GetNodeListAsync(nodeName ?? this.NodeName, ConfigurationSettings.AsyncTimeout, Token);

                if (nodes?.Count == 0)
                {
                    return;
                }

                Node node = nodes[0];
                string SnapshotId = Guid.NewGuid().ToString();
                string NodeName = node.NodeName, IpAddressOrFQDN = node.IpAddressOrFQDN, NodeType = node.NodeType, CodeVersion = node.CodeVersion, ConfigVersion = node.ConfigVersion;
                string NodeUpAt = node.NodeUpAt.ToString("o"), NodeDownAt = node.NodeDownAt.ToString("o");

                // These are obsolete.
                //string NodeUpTime = node.NodeUpTime.ToString(), NodeDownTime = node.NodeDownTime.ToString();

                string HealthState = node.HealthState.ToString(), UpgradeDomain = node.UpgradeDomain;
                string FaultDomain = node.FaultDomain.OriginalString, NodeId = node.NodeId.ToString(), NodeInstanceId = node.NodeInstanceId.ToString(), NodeStatus = node.NodeStatus.ToString();
                bool IsSeedNode = node.IsSeedNode;

                ObserverLogger.LogEtw(
                    ObserverConstants.FabricObserverETWEventName,
                    new
                    {
                        SnapshotId,
                        SnapshotTimestamp = DateTime.UtcNow.ToString("o"),
                        NodeName,
                        NodeType,
                        NodeId,
                        NodeInstanceId,
                        NodeStatus,
                        NodeUpAt,
                        NodeDownAt,
                        CodeVersion,
                        ConfigVersion,
                        HealthState,
                        IpAddressOrFQDN,
                        UpgradeDomain,
                        FaultDomain,
                        IsSeedNode
                    });
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is TimeoutException)
            {
                ObserverLogger.LogWarning($"Failed to generate node stats:{Environment.NewLine}{e}");
                // Retry or try again later..
            }
        }

        public override Task ReportAsync(CancellationToken token)
        {
            // Local log.
            ObserverLogger.LogInfo(message.ToString());

            /* Report to Fabric */

            var healthReporter = new ObserverHealthReporter(ObserverLogger);
            var healthReport = new HealthReport
            {
                Code = FOErrorWarningCodes.Ok,
                HealthMessage = message.ToString(),
                NodeName = NodeName,
                Observer = ObserverName,
                Property = "SomeUniquePropertyForMyHealthEvent",
                EntityType = EntityType.Node,
                State = HealthState.Ok
            };

            healthReporter.ReportHealthToServiceFabric(healthReport);

            // Emit Telemetry - This will use whatever telemetry provider you have configured in FabricObserver Settings.xml.
            var telemetryData = new TelemetryData()
            {
                Code = FOErrorWarningCodes.Ok,
                Description = message.ToString(),
                HealthState = HealthState.Ok,
                NodeName = NodeName,
                ObserverName = ObserverName,
                Source = ObserverConstants.FabricObserverName
            };

            if (IsTelemetryEnabled)
            {
                TelemetryClient?.ReportHealthAsync(telemetryData, Token);
            }

            // ETW.
            if (IsEtwEnabled)
            {
                ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
            }

            message.Clear();
            return Task.CompletedTask;
        }
    }
}