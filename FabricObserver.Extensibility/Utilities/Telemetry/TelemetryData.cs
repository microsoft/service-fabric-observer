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

        [EventField]
        public string ApplicationName
        {
            get; set;
        }

        [EventField]
        public string ApplicationType
        {
            get; set;
        }

        [EventField]
        public string ApplicationTypeVersion
        {
            get; set;
        }

        [EventField]
        public string Code
        {
            get; set;
        }

        [EventField]
        public string ContainerId
        {
            get; set;
        }

        [EventField]
        public string ClusterId
        {
            get; set;
        }

        [EventField]
        public string Description
        {
            get; set;
        }

        [EventField]
        public EntityType EntityType
        {
            get; set;
        }

        [EventField]
        public HealthState HealthState
        {
            get; set;
        }

        [EventField]
        public string Metric
        {
            get; set;
        }

        [EventField]
        public string NodeName
        {
            get; set;
        }

        [EventField]
        public string NodeType
        {
            get; set;
        }

        /// <summary>
        /// The name of the observer that generated the health information.
        /// </summary>
        [EventField]
        public string ObserverName 
        { 
            get; set; 
        }

        [EventField]
        public string OS
        {
            get { return _os; }
        }

        [EventField]
        public Guid? PartitionId
        {
            get; set;
        }

        [EventField]
        public long ProcessId
        {
            get; set;
        }

        [EventField]
        public string ProcessName
        {
            get; set;
        }

        [EventField]
        public string Property
        {
            get; set;
        }

        [EventField]
        public string ProcessStartTime
        {
            get; set;
        }

        [EventField]
        public long ReplicaId
        {
            get; set;
        }

        [EventField]
        public string ReplicaRole
        {
            get; set;
        }

        [EventField]
        public bool RGMemoryEnabled
        {
            get; set;
        }

        /* TODO..
        [EventField]
        public bool RGCpuEnabled
        {
            get; set;
        }
        */

        [EventField]
        public double RGAppliedMemoryLimitMb
        {
            get; set;
        }

        [EventField]
        public string ServiceKind
        {
            get; set;
        }

        [EventField]
        public string ServiceName
        {
            get; set;
        }

        [EventField]
        public string ServiceTypeName
        {
            get; set;
        }

        [EventField]
        public string ServiceTypeVersion
        {
            get; set;
        }

        [EventField]
        public string ServicePackageActivationMode
        {
            get; set;
        }

        [EventField]
        public string Source
        {
            get; set;
        }

        [EventField]
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