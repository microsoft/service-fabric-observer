// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics.Tracing;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    public class ServiceTelemetryData : TelemetryDataBase
    {
        public string ApplicationName
        {
            get; set;
        }

        public string ApplicationType
        {
            get; set;
        }

        public string ApplicationTypeVersion
        {
            get; set;
        }

        public string PartitionId
        {
            get; set;
        }

        public int ProcessId
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

        public long ReplicaId
        {
            get; set;
        }

        public string ReplicaStatus
        {
            get; set;
        }

        public string ReplicaRole
        {
            get; set;
        }

        public bool RGMemoryEnabled
        {
            get; set;
        }

        public double RGAppliedMemoryLimitMb
        {
            get; set;
        }
        public bool RGCpuEnabled
        {
            get; set;
        }
        public double RGAppliedCpuLimitCores
        {
            get; set;
        }

        public string ServiceKind
        {
            get; set;
        }
        public string ServiceName
        {
            get; set;
        }
        public string ServiceTypeName
        {
            get; set;
        }
        public string ServiceTypeVersion
        {
            get; set;
        }
        public string ServicePackageActivationMode
        {
            get; set;
        }
        [JsonConstructor]
        public ServiceTelemetryData() : base()
        {

        }
    }
}