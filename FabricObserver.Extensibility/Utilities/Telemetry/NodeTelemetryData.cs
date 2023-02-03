// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics.Tracing;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    public class NodeTelemetryData : TelemetryDataBase
    {
        [JsonConstructor]
        public NodeTelemetryData() : base()
        { 
            
        }
    }
}
