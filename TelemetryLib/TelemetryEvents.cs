// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using System;
using System.Fabric;
using System.Threading;

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

        public TelemetryEvents(
            FabricClient fabricClient,
            ServiceContext context,
            ITelemetryEventSource eventSource,
            CancellationToken token)
        {
            this.eventSource = eventSource;
            this.serviceContext = context;
            var appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            appInsightsTelemetryConf.InstrumentationKey = TelemetryConstants.AppInsightsInstrumentationKey;
            appInsightsTelemetryConf.TelemetryChannel.EndpointAddress = TelemetryConstants.TelemetryEndpoint;
            this.telemetryClient = new TelemetryClient(appInsightsTelemetryConf);

            var clusterInfoTuple = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, token).GetAwaiter().GetResult();
            this.clusterId = clusterInfoTuple.Item1;
            this.tenantId = clusterInfoTuple.Item2;
            this.clusterType = clusterInfoTuple.Item3;
        }

        public bool FabricObserverRuntimeNodeEvent(
            string applicationVersion,
            string foConfigInfo,
            string foHealthInfo)
        {
            // This means that the token replacement did not take place and this is not an 
            // SFPKG signed Release build of FO. So, don't do anything.
            if (TelemetryConstants.AppInsightsInstrumentationKey.Contains("Token"))
            {
                return false;
            }

            this.eventSource.FabricObserverRuntimeNodeEvent(
                this.clusterId,
                applicationVersion,
                foConfigInfo,
                foHealthInfo);

            IDictionary<string, string> eventProperties = new Dictionary<string, string>
            {
                { "EventName", $"{EventName}" },
                { "TaskName", $"{TaskName}" },
                { "ClusterId", $"{this.clusterId}" ?? "" },
                { "ClusterType", $"{this.clusterType}" ?? "" },
                { "TenantId", $"{this.tenantId}" ?? "" },
                { "FabricObserverVersion", applicationVersion ?? "" },
                { "NodeNameHash", ((uint)this.serviceContext?.NodeContext?.NodeName.GetHashCode()).ToString() ?? "" },
                { "FabricObserverHealthInfo", foHealthInfo ?? "" },
                { "FabricObserverConfigInfo", foConfigInfo ?? "" },
                { "Timestamp", DateTime.Now.ToString("G") }
            };

            this.telemetryClient?.TrackEvent(string.Format("{0}.{1}", TaskName, EventName), eventProperties);
            this.telemetryClient?.Flush();
            
            // allow time for flushing
            Thread.Sleep(1000);

            return true;
        }
    }
}