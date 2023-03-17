// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics.Tracing;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    public class SystemServiceTelemetryData : TelemetryDataBase
    {
        public string ApplicationName
        {
            get;
        } = ObserverConstants.SystemAppName;

        public long ProcessId
        {
            get; set;
        }

        public string ProcessName
        {
            get; set;
        }

        public string ProcessStartTime
        {
            get; set;
        }
 
        [JsonConstructor]
        public SystemServiceTelemetryData() : base()
        {

        }
    }
}
