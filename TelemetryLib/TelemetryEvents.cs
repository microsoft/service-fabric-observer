// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using System;
using System.Fabric;

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
        private readonly ServiceContext serviceContext = null;
        private readonly ITelemetryEventSource eventSource;
        private readonly string clusterId, tenantId, clusterType;

        public TelemetryEvents(
            FabricClient fabricClient,
            ServiceContext context,
            ITelemetryEventSource eventSource)
        {
            this.eventSource = eventSource;
            this.serviceContext = context;
            var appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            appInsightsTelemetryConf.InstrumentationKey = TelemetryConstants.AppInsightsInstrumentationKey;
            appInsightsTelemetryConf.TelemetryChannel.EndpointAddress = TelemetryConstants.TelemetryEndpoint;
            this.telemetryClient = new TelemetryClient(appInsightsTelemetryConf);
            ClusterIdentificationUtility clusterIdentificationUtility = null;

            try
            {
                clusterIdentificationUtility = new ClusterIdentificationUtility(fabricClient);
                clusterIdentificationUtility.GetClusterIdAndType(
                    out this.clusterId,
                    out this.tenantId, 
                    out this.clusterType);

            }
            finally
            {
                clusterIdentificationUtility?.Dispose();
            }
        }

        public bool FabricObserverRuntimeNodeEvent(
            string applicationVersion,
            string foConfigInfo,
            string foHealthInfo)
        {
            // This means that the token replacement did not take place and this is not a 
            // SFPKG signed Release build of FO. So, don't do anything, just return;
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
                { "FabricObserverVersion", applicationVersion ?? "" },
                { "NodeNameHash", ((uint)this.serviceContext?.NodeContext?.NodeName.GetHashCode()).ToString() ?? "" },
                { "FabricObserverHealthInfo", foHealthInfo ?? "" },
                { "FabricObserverConfigInfo", foConfigInfo ?? "" },
                { "Timestamp", DateTime.Now.ToString("G") }
            };

            this.telemetryClient?.TrackEvent(string.Format("{0}.{1}", TaskName, EventName), eventProperties);
            this.telemetryClient?.Flush();
            
            // allow time for flushing
            System.Threading.Thread.Sleep(1000);

            return true;
        }
    }
}