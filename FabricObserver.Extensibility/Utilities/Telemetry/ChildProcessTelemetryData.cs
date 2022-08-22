// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [Serializable]
    public class ChildProcessTelemetryData
    {
        public string ApplicationName;
        public string ServiceName;
        public string Metric;
        public double Value;
        public int ProcessId;
        public string ProcessName;
        public string ProcessStartTime;
        public string PartitionId;
        public long ReplicaId;
        public string NodeName;
        public int ChildProcessCount;
        public List<ChildProcessInfo> ChildProcessInfo;
    }
}
