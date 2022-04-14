// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using Newtonsoft.Json;
using ClusterObserver.Interfaces;
using System.Fabric.Health;
using System;

namespace ClusterObserver.Utilities.Telemetry
{
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

        public string ObserverName
        {
            get; set;
        }

        public string OS
        {
            get { return _os; }
        }

        public Guid PartitionId
        {
            get; set;
        }

        public long ProcessId
        {
            get; set;
        }

        public long ReplicaId
        {
            get; set;
        }

        public string ServiceName
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

        public string NodeType
        {
            get; set;
        }

        public string RepairId
        {
            get; set;
        }

        public string Property
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

