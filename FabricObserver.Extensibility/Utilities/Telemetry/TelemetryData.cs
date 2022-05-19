// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using Newtonsoft.Json;
using FabricObserver.Observers.Interfaces;
using System.Fabric.Health;
using System;
using System.Fabric;
using System.Fabric.Query;
using System.Fabric.Description;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [Serializable]
    public class TelemetryData : ITelemetryData
    {
        private readonly string _os;

        public string ApplicationName
        {
            get; set;
        }

        public string Code
        {
            get; set;
        }

        public string ContainerId
        {
            get; set;
        }

        public string ClusterId
        {
            get; set;
        }

        public string Description
        {
            get; set;
        }

        public EntityType EntityType
        {
            get; set;
        }

        public HealthState HealthState
        {
            get; set;
        }

        public string Metric
        {
            get; set;
        }

        public string NodeName
        {
            get; set;
        }

        public string NodeType
        {
            get; set;
        }

        public string ObserverName
        {
            get; set;
        }

        public string OS
        {
            get { return _os; }
        }

        public Guid? PartitionId
        {
            get; set;
        }

        public long ProcessId
        {
            get; set;
        }

        public string Property
        {
            get; set;
        }

        public long ReplicaId
        {
            get; set;
        }

        public ReplicaRole ReplicaRole
        {
            get; set;
        }

        public ServiceKind ServiceKind
        {
            get; set;
        }

        public string ServiceName
        {
            get; set;
        }

        public ServicePackageActivationMode? ServicePackageActivationMode
        {
            get; set;
        }

        public string Source
        {
            get; set;
        }

        public string SystemServiceProcessName
        {
            get; set;
        }

        public double Value
        {
            get; set;
        }

        [JsonConstructor]
        public TelemetryData()
        {
            _os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
        }
    }
}