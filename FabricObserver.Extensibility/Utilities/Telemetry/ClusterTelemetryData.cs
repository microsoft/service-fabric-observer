// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    public class ClusterTelemetryData : TelemetryDataBase
    {
        [JsonConstructor]
        public ClusterTelemetryData() : base()
        {

        }
    }
}
