// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics.Tracing;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    public class ContainerTelemetryData : TelemetryDataBase
    {
        public string ApplicationName
        {
            get; set;
        }

        public string ContainerId
        {
            get; set;
        }

        public string PartitionId
        {
            get; set;
        }

        public long ReplicaId
        {
            get; set;
        }

        public string ReplicaRole
        {
            get; set;
        }

        public string ReplicaStatus
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

        public string ServicePackageActivationMode
        {
            get; set;
        }

        [JsonConstructor]
        public ContainerTelemetryData() : base()
        {

        }
    }
}
