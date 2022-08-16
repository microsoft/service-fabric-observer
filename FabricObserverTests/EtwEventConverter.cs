// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using Logger = FabricObserver.Observers.Utilities.Logger;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Newtonsoft.Json;

namespace FabricObserverTests
{
    /// <summary>
    /// Class that converts FabricObserverDataEvent event data into *TelemetryData instances.
    /// </summary> 
    public class EtwEventConverter
    {
        internal Logger Logger
        {
            get; set;
        }

        internal List<TelemetryData> TelemetryData
        {
            get; private set;
        }

        internal MachineTelemetryData MachineTelemetryData
        {
            get; private set;
        }

        internal List<List<ChildProcessTelemetryData>> ChildProcessTelemetry
        {
            get; private set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EtwEventConverter"/> class.
        /// </summary>
        /// <param name="observerLogger"> Observer logger for local logging.</param>
        public EtwEventConverter(Logger observerLogger)
        {
            Logger = observerLogger;
        }

        /// <summary>
        /// Parses the event data into Telemtry data.
        /// </summary>
        /// <param name="eventData">Instance of information associated with the event dispatched for fabric observer event listener.</param>
        public void EventDataToTelemetryData(EventWrittenEventArgs eventData)
        {
            try
            {
                if (eventData?.Payload == null || eventData.Payload.Count == 0)
                {
                    return;
                }

                // FabricObserver ETW events will not be emitted as multiple payload items. There is one item in the Payload: 
                // A serialized instance of a custom data type. Most often, these will be TelemetryData instances, but can also be
                // ChildProcessTelemetryData, MachineTelemetryData or an anonymous type. Any event that is written to FabricObserverETWProvider will be
                // available in the payload. These will always be FabricObserverDataEvent events.
                string json = eventData.Payload[0].ToString();
                var payloadName = eventData.PayloadNames[0];

                if (payloadName == ObserverConstants.PayloadNameAnonData)
                {
                    return;
                }

                // Child procs - ChildProcessTelemetryData (only from AppObserver).
                if (payloadName == ObserverConstants.PayloadNameChildProcessTelemetryData && JsonHelper.TryDerializeObject(json, out List<ChildProcessTelemetryData> childProcTelemData))
                {
                    Logger.LogInfo($"JSON-serialized List<ChildProcessTelemetryData>{Environment.NewLine}{json}");
                    ChildProcessTelemetry ??= new List<List<ChildProcessTelemetryData>>();
                    ChildProcessTelemetry.Add(childProcTelemData);
                }
                // TelemetryData (from all observers but OSObserver).
                else if (payloadName == ObserverConstants.PayloadNameTelemetryData && JsonHelper.TryDerializeObject(json, out TelemetryData telemetryData))
                {
                    Logger.LogInfo($"JSON-serialized TelemetryData{Environment.NewLine}{json}");
                    TelemetryData ??= new List<TelemetryData>();
                    TelemetryData.Add(telemetryData); 
                }
                // MachineTelemetryData (only from OSObserver).
                else if (payloadName == ObserverConstants.PayloadNameMachineTelemetryData && JsonHelper.TryDerializeObject(json, out MachineTelemetryData machineTelemetryData))
                {
                    Logger.LogInfo($"JSON-serialized MachineTelemetryData{Environment.NewLine}{json}");
                    MachineTelemetryData = machineTelemetryData;
                }
                else
                {
                    Logger.LogInfo($"Not the droid we're looking for.{Environment.NewLine}{json}");
                }
            }
            catch (Exception e) when (e is ArgumentException || e is JsonReaderException || e is JsonSerializationException || e is InvalidOperationException)
            {
                Logger.LogError($"Unable to deserialize ETW event data to supported type:{Environment.NewLine}{e}");

                // For unit tests, re-throw.
                throw;
            }
        }
    }
}