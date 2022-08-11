// -----------------------------------------------------------------------
// <copyright file="FabricObserverMetrics.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Extensions.Azure;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;
using Logger = FabricObserver.Observers.Utilities.Logger;

namespace FabricObserver.Observers
{
    /// <summary>
    /// Class to convert Fabric observer telemetry data to azure metrics.
    /// </summary>
    public class FabricObserverMetrics
    {
        internal Logger Logger
        {
            get; set;
        }

        internal static List<TelemetryData> TelemetryData
        {
            get; private set;
        }

        internal List<ChildProcessTelemetryData> ChildProcessTelemetry
        {
            get; private set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserverMetrics"/> class.
        /// </summary>
        /// <param name="telemetryData">Fabric observer telemetry data</param
        /// <param name="observerLogger"> Observer logger for local logging.</param>
        public FabricObserverMetrics(Logger observerLogger)
        {
            Logger = observerLogger;
        }

        /// <summary>
        /// Parses the event data into Telemtry data.
        /// </summary>
        /// <param name="eventData">Instance of information associated with the event dispatched for fabric observer event listener.</param>
        public Task EventDataToTelemetryData(EventWrittenEventArgs eventData)
        {
            try
            {
                if (eventData.Payload == null || eventData.Payload.Count == 0)
                {
                    return Task.FromResult(true);
                }

                // There won't be multiple payload items from FO. So, First() works.
                string json = eventData.Payload.First().ToString();

                // Child procs - ChildProcessTelemetryData type. 
                if (IsJson<List<ChildProcessTelemetryData>>(json))
                {
                    Logger.LogInfo($"FabricObserverMetricsInfo: JSON-serialized List<ChildProcessTelemetryData> {json}");
                    var childProcTelemData = JsonConvert.DeserializeObject<List<ChildProcessTelemetryData>>(json);
                    ChildProcessTelemetry = childProcTelemData;
                }
                else if (IsJson<TelemetryData>(json))
                {
                    Logger.LogInfo($"FabricObserverMetricsInfo: JSON-serialized TelemetryData {json}");
                    var foTelemetryData = JsonConvert.DeserializeObject<TelemetryData>(json);

                    if (foTelemetryData == null)
                    {
                        Logger.LogError($"FabricObserverMetricsError: No telemetry data");
                        return Task.FromResult(true);
                    }
                    else
                    {
                        if (TelemetryData == null)
                        {
                            TelemetryData = new List<TelemetryData>();
                        }

                        TelemetryData.Add(foTelemetryData);
                    }
                }

                return Task.CompletedTask;
            }
            catch (Exception e) when (e is ArgumentException || e is JsonReaderException || e is JsonSerializationException || e is InvalidOperationException)
            {
                Logger.LogError($"FabricObserverMetricsError: Unable to deserialize ETW event data to TelemetryData: {Environment.NewLine}{e}");
                return Task.FromResult(true);
            }
        }

        private static bool IsJson<T>(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                _ = JsonConvert.DeserializeObject<T>(text, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error});
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}