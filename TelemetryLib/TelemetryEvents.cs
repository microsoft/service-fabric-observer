// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Threading;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;

namespace Microsoft.ServiceFabric.TelemetryLib
{
    /// <summary>
    /// Contains common telemetry events
    /// </summary>
    public class TelemetryEvents
    {
        private const string EventName = "TraceSessionStats";
        private const string TaskName = "FabricObserver";
        private readonly TelemetryClient telemetryClient;
        private readonly ServiceContext serviceContext;
        private readonly ITelemetryEventSource eventSource;
        private readonly string clusterId, tenantId, clusterType;

        public TelemetryEvents(FabricClient fabricClient, ServiceContext context, ITelemetryEventSource eventSource, CancellationToken token)
        {
            this.eventSource = eventSource;
            serviceContext = context;
            var appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            appInsightsTelemetryConf.TelemetryChannel.EndpointAddress = TelemetryConstants.TelemetryEndpoint;
            telemetryClient = new TelemetryClient(appInsightsTelemetryConf)
            {
                InstrumentationKey = TelemetryConstants.AppInsightsInstrumentationKey,
            };

            var (item1, item2, item3) = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, token).GetAwaiter().GetResult();
            clusterId = item1;
            tenantId = item2;
            clusterType = item3;
        }

        public bool FabricObserverRuntimeNodeEvent(string applicationVersion, string foConfigInfo, string foHealthInfo)
        {
            // This means that the token replacement did not take place and this is not an 
            // SFPKG/NUPKG signed Release build of FO. So, don't do anything.
            if (TelemetryConstants.AppInsightsInstrumentationKey.Contains("Token"))
            {
                return false;
            }

            eventSource.FabricObserverRuntimeNodeEvent(clusterId, applicationVersion, foConfigInfo, foHealthInfo);

            string nodeHashString = string.Empty;
            int nodeNameHash = serviceContext?.NodeContext.NodeName.GetHashCode() ?? -1;

            if (nodeNameHash != -1)
            {
                nodeHashString = ((uint)nodeNameHash).ToString();
            }

            IDictionary<string, string> eventProperties = new Dictionary<string, string>
            {
                { "EventName", EventName},
                { "TaskName", TaskName},
                { "ClusterId", clusterId },
                { "ClusterType", clusterType },
                { "TenantId", tenantId },
                { "FabricObserverVersion", applicationVersion },
                { "NodeNameHash",  nodeHashString },
                { "FabricObserverHealthInfo", foHealthInfo },
                { "FabricObserverConfigInfo", foConfigInfo },
                { "Timestamp", DateTime.UtcNow.ToString("o") }
            };

            telemetryClient?.TrackEvent($"{TaskName}.{EventName}", eventProperties);
            telemetryClient?.Flush();
            
            // allow time for flushing
            Thread.Sleep(1000);

            return true;
        }
    }
}