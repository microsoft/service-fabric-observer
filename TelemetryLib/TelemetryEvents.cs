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
using Newtonsoft.Json;

namespace FabricObserver.TelemetryLib
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
                InstrumentationKey = TelemetryConstants.AppInsightsInstrumentationKey
            };

            var (ClusterId, TenantId, ClusterType) = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, token).GetAwaiter().GetResult();
            clusterId = ClusterId;
            tenantId = TenantId;
            clusterType = ClusterType;
        }

        public bool FabricObserverRuntimeNodeEvent(FabricObserverInternalTelemetryData foData)
        {
            // This means that the token replacement did not take place and this is not an 
            // SFPKG/NUPKG signed Release build of FO. So, don't do anything.
            if (TelemetryConstants.AppInsightsInstrumentationKey.Contains("Token"))
            {
                return false;
            }

            eventSource.FabricObserverRuntimeNodeEvent(foData);

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
                { "NodeNameHash",  nodeHashString },
                { "ObserverData", JsonConvert.SerializeObject(foData)},
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