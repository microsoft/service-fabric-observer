// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using System;
using System.Fabric;
using System.Threading.Tasks;
using System.Fabric.Query;

namespace Microsoft.ServiceFabric.TelemetryLib
{
    /// <summary>
    /// Contains common telemetry events
    /// </summary>
    public class TelemetryEvents
    {
        private readonly string AppInsightsInstrumentationKey = TelemetryConstants.AppInsightsInstrumentationKey;
        private const string EventName = "FabricObserverRuntimeInfo";
        private readonly TelemetryClient telemetryClient;
        private readonly ITelemetryEventSource eventSource;
        private readonly string clusterId, tenantId, clusterType;
        private int? nodeCount = 0;

        public TelemetryEvents(FabricClient fabricClient, ITelemetryEventSource eventSource)
        {
            this.eventSource = eventSource;
            var appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            this.telemetryClient = new TelemetryClient(appInsightsTelemetryConf)
            {
                InstrumentationKey = AppInsightsInstrumentationKey
            };

            ClusterIdentificationUtility clusterIdentificationUtility = null;
            NodeList nodes = null;

            try
            {
                clusterIdentificationUtility = new ClusterIdentificationUtility(fabricClient);
                clusterIdentificationUtility.GetClusterIdAndType(
                    out this.clusterId,
                    out this.tenantId, 
                    out this.clusterType);

                Task.Run(async () =>
                {
                    nodes = await fabricClient.QueryManager.GetNodeListAsync();
                    this.nodeCount = nodes?.Count;
                }).Wait();
            }
            catch (AggregateException)
            {
                // No-op originating from failed node count task... Do not throw...
            }
            catch (ObjectDisposedException)
            {
                // No-op originating from failed node count task... Do not throw...
            }
            finally
            {
                clusterIdentificationUtility?.Dispose();
            }
        }

        public void FabricObserverRuntimeNodeEvent(
            string applicationVersion,
            string foConfigInfo,
            string foHealthInfo)
        {
            this.eventSource.FabricObserverRuntimeNodeEvent(
                this.clusterId,
                applicationVersion,
                foConfigInfo,
                foHealthInfo);

            IDictionary<string, string> eventProperties = new Dictionary<string, string>
            {
                { "ClusterId", $"{this.clusterId}_{this.nodeCount}" },
                { "FabricObserverVersion", applicationVersion ?? "" },
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