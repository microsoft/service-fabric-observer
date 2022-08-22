// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [JsonObject]
    public class ChildProcessInfo
    {
        public int ProcessId;
        public string ProcessName;
        public string ProcessStartTime;
        public double Value;
    }
}
