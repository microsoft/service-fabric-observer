// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Fabric;
using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ServiceFabric.TelemetryLib.Helper;

namespace Microsoft.ServiceFabric.TelemetryLib
{
    /// <summary>
    /// Contains common telemetry events
    /// </summary>
    public class TelemetryEvents
    {
        //private string AppInsightsInstrumentationKey = TelemetryConstants.AppInsightsInstrumentationKey;
        private const string EventName = "FabricObserverRuntimeInfo";
        private readonly TelemetryClient telemetryClient;
        private readonly FabricClient fabricClient;
        private readonly ITelemetryEventSource eventSource;
        
        // Every time a new version of application is released, manually update this version.
        // This application version is used for telemetry
        // For consistency keep this application version same as application version from application manifest.
        private const string ApplicationVersion = "1.0.0";

        public TelemetryEvents(FabricClient fabricClient, ITelemetryEventSource eventSource)
        {
            this.fabricClient = fabricClient;
            this.eventSource = eventSource;

            this.telemetryClient = new TelemetryClient()
            {
                InstrumentationKey = TelemetryConstants.AppInsightsInstrumentationKey
            };
        }

        public void FabricObserverRuntimeClusterEvent(string nodeName, string foConfigInfo, string foHealthInfo)
        {
            var clusterIdentificationUtility = new ClusterIdentificationUtility(this.fabricClient);

            try
            {
                if (!this.telemetryClient.IsEnabled() || string.IsNullOrEmpty(this.telemetryClient.InstrumentationKey))
                {
                    this.eventSource.VerboseMessage("Skipping sending telemetry as Telemetry is disabled for this cluster");

                    return;
                }

                clusterIdentificationUtility.GetClusterIdAndType(out string clusterId, 
                                                                 out string tenantId, 
                                                                 out string clusterType);

                this.FabricObserverRuntimeClusterEvent(clusterId,
                                                       tenantId,
                                                       clusterType,
                                                       ApplicationVersion,
                                                       nodeName,
                                                       foConfigInfo,
                                                       foHealthInfo);
            }
            finally
            {
                clusterIdentificationUtility?.Dispose();
            }
        }

        private void FabricObserverRuntimeClusterEvent(string clusterId,
                                                       string tenantId,
                                                       string clusterType,
                                                       string applicationVersion,
                                                       string nodeName,
                                                       string foConfigInfo,
                                                       string foHealthInfo)
        {
            this.eventSource.FabricObserverRuntimeClusterEvent(clusterId,
                                                               tenantId,
                                                               clusterType,
                                                               nodeName,
                                                               applicationVersion,
                                                               foConfigInfo,
                                                               foHealthInfo);

            IDictionary<string, string> eventProperties = new Dictionary<string, string>
            {
                { "ClusterId", clusterId },
                { "TenantId", tenantId },
                { "ClusterType", clusterType },
                { "FabricObserverVersion", applicationVersion },
                { "NodeNameHash", nodeName?.GetHashCode().ToString() ?? "" },
                { "FabricObserverHealthInfo", foHealthInfo ?? "" },
                { "FabricObserverConfigInfo", foConfigInfo ?? "" },
            };

            this.telemetryClient.TrackEvent(EventName, eventProperties);
            this.telemetryClient.Flush();
            
            // allow time for flushing
            System.Threading.Thread.Sleep(1000);
        }
    }
}