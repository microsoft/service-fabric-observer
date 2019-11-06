// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using System;

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

        public TelemetryEvents(ITelemetryEventSource eventSource)
        {
            this.eventSource = eventSource;
            var appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            this.telemetryClient = new TelemetryClient(appInsightsTelemetryConf)
            {
                InstrumentationKey = AppInsightsInstrumentationKey
            };
        }

        public void FabricObserverRuntimeNodeEvent(
            Guid eventSourceId,
            string applicationVersion,
            string foConfigInfo,
            string foHealthInfo)
        {
            this.eventSource.FabricObserverRuntimeNodeEvent(
                eventSourceId,
                applicationVersion,
                foConfigInfo,
                foHealthInfo);

            IDictionary<string, string> eventProperties = new Dictionary<string, string>
            {
                { "EventId", eventSourceId.ToString() },
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