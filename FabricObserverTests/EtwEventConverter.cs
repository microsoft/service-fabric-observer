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
    internal class EtwEventConverter
    {
        internal Logger Logger
        {
            get;
        }

        internal List<ContainerTelemetryData> ContainerTelemetryData
        {
            get; private set;
        }

        internal List<DiskTelemetryData> DiskTelemetryData
        {
            get; private set;
        }

        internal List<NodeTelemetryData> NodeTelemetryData
        {
            get; private set;
        }

        internal List<ServiceTelemetryData> ServiceTelemetryData
        {
            get; private set;
        }

        internal List<SystemServiceTelemetryData> SystemServiceTelemetryData
        {
            get; private set;
        }


        internal MachineTelemetryData MachineTelemetryData
        {
            get; private set;
        }

        internal NodeSnapshotTelemetryData NodeSnapshotTelemetryData
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
        internal EtwEventConverter(Logger observerLogger)
        {
            Logger = observerLogger;
        }

        /// <summary>
        /// Parses the event data into Telemtry data.
        /// </summary>
        /// <param name="eventData">Instance of information associated with the event dispatched for fabric observer event listener.</param>
        internal void EventDataToTelemetryData(EventWrittenEventArgs eventData)
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

                // ChildProcessTelemetryData. Strict type member handling.
                if (JsonHelper.TryDeserializeObject(json, out List<ChildProcessTelemetryData> childProcTelemData, treatMissingMembersAsError: true))
                {
                    Logger.LogInfo($"JSON-serialized List<ChildProcessTelemetryData>{Environment.NewLine}{json}");
                    ChildProcessTelemetry ??= new List<List<ChildProcessTelemetryData>>();
                    ChildProcessTelemetry.Add(childProcTelemData);
                }
                // MachineTelemetryData. Strict type member handling.
                else if (JsonHelper.TryDeserializeObject(json, out MachineTelemetryData machineTelemetryData, treatMissingMembersAsError: true))
                {
                    Logger.LogInfo($"JSON-serialized MachineTelemetryData{Environment.NewLine}{json}");
                    MachineTelemetryData = machineTelemetryData;
                }
                // NodeSnapshotTelemetryData. Strict type member handling.
                else if (JsonHelper.TryDeserializeObject(json, out NodeSnapshotTelemetryData nodeSnapshotTelemetryData, treatMissingMembersAsError: true))
                {
                    Logger.LogInfo($"JSON-serialized nodeSnapshotTelemetryData{Environment.NewLine}{json}");
                    NodeSnapshotTelemetryData = nodeSnapshotTelemetryData;
                }
                // TelemetryData: Specific Observer telemetry. Don't enforce strict type member handling in Json deserialization.
                else if (JsonHelper.TryDeserializeObject(json, out TelemetryData telemetryData))
                {
                    Logger.LogInfo($"JSON-serialized TelemetryDataBase{Environment.NewLine}{json}");
                    
                    switch (telemetryData.ObserverName)
                    {
                        case ObserverConstants.AppObserverName:

                            if (JsonHelper.TryDeserializeObject(json, out ServiceTelemetryData serviceTelemetryData))
                            {
                                ServiceTelemetryData ??= new List<ServiceTelemetryData>();
                                ServiceTelemetryData.Add(serviceTelemetryData);
                            }
                            break;
                        // This is tricky to test given the Docker requirement. This has been tested, however..
                        case ObserverConstants.ContainerObserverName:

                            if (JsonHelper.TryDeserializeObject(json, out ContainerTelemetryData containerTelemetryData))
                            {
                                ContainerTelemetryData ??= new List<ContainerTelemetryData>();
                                ContainerTelemetryData.Add(containerTelemetryData);
                            }

                            break;

                        case ObserverConstants.DiskObserverName:

                            // enforce strict type member handling in Json deserialization as this type has specific properties that are unique to it.
                            if (JsonHelper.TryDeserializeObject(json, out DiskTelemetryData diskTelemetryData, treatMissingMembersAsError: true))
                            {
                                DiskTelemetryData ??= new List<DiskTelemetryData>();
                                DiskTelemetryData.Add(diskTelemetryData);
                            }
                            break;

                        case ObserverConstants.FabricSystemObserverName:

                            if (JsonHelper.TryDeserializeObject(json, out SystemServiceTelemetryData sysServiceTelemetryData))
                            {
                                SystemServiceTelemetryData ??= new List<SystemServiceTelemetryData>();
                                SystemServiceTelemetryData.Add(sysServiceTelemetryData);
                            }
                            break;

                        case ObserverConstants.NodeObserverName:

                            if (JsonHelper.TryDeserializeObject(json, out NodeTelemetryData nodeTelemetryData))
                            {
                                NodeTelemetryData ??= new List<NodeTelemetryData>();
                                NodeTelemetryData.Add(nodeTelemetryData);
                            }
                            break;

                        default:
                            return;
                    }
                }
                else // ignore..
                {
                    Logger.LogInfo($"Not the droid we're looking for.{Environment.NewLine}{json}");
                }
            }
            catch (Exception e) when (e is ArgumentException || e is JsonReaderException || e is JsonSerializationException || e is InvalidOperationException)
            {
                Logger.LogError($"Unable to deserialize ETW event data to supported type:{Environment.NewLine}{e}");

                // For unit tests, always re-throw.
                throw;
            }
        }
    }
}