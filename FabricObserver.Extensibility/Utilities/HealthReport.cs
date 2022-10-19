// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Health;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers.Utilities
{
    public class HealthReport
    {
        /// <summary>
        /// Service Fabric Application Name.
        /// </summary>
        public Uri AppName
        {
            get; set;
        }

        /// <summary>
        /// Error code.
        /// </summary>
        public string Code
        {
            get; set;
        }

        /// <summary>
        /// TTL for Health Report (Time-to-live).
        /// </summary>
        public TimeSpan HealthReportTimeToLive
        {
            get; set;
        }

        /// <summary>
        /// Description.
        /// </summary>
        public string HealthMessage
        {
            get; set;
        }

        /// <summary>
        /// Whether or not to write a local log entry in addition to generating a health report. Default is true. 
        /// Note that if Verbose logging is not enabled for some observer, then only Warning and Error report data will be written to local logs.
        /// </summary>
        public bool EmitLogEvent 
        { 
            get; set; 
        } = true;

        /// <summary>
        /// Target EntityType (Node, Application, Service, Machine, etc.). This determines what kind of HealthReport FabricObserver will generate. 
        /// Use this instead of the deprecated ReportType.
        /// </summary>
        public EntityType EntityType
        {
            get; set;
        }

        /// <summary>
        /// [Deprecated] Type of health report. Use EntityType instead.
        /// </summary>
        public HealthReportType ReportType
        {
            get; set;
        }

        /// <summary>
        /// HealthState.
        /// </summary>
        public HealthState State
        {
            get; set;
        }

        /// <summary>
        /// Node name.
        /// </summary>
        public string NodeName
        {
            get; set;
        }

        /// <summary>
        /// Observer name.
        /// </summary>
        public string Observer
        {
            get; set;
        }

        /// <summary>
        /// Service Fabric Health Event Property.
        /// </summary>
        public string Property
        {
            get; set;
        }

        /// <summary>
        /// Resource usage data property.
        /// </summary>
        public string ResourceUsageDataProperty
        {
            get; set;
        }

        /// <summary>
        /// Service Fabric Health Event SourceId.
        /// </summary>
        public string SourceId
        {
            get; set;
        }

        /// <summary>
        /// TelemetryData instance (this will be JSON-serialized and used as the Description of the generated health event).
        /// </summary>
        public TelemetryData HealthData
        {
            get; set;
        }

        /// <summary>
        /// Service Fabric Service name.
        /// </summary>
        public Uri ServiceName
        {
            get; set;
        }

        /// <summary>
        /// Service Fabric Partition Id.
        /// </summary>
        public Guid PartitionId
        {
            get; set;
        }

        /// <summary>
        /// Service Fabric Replica or Instance Id.
        /// </summary>
        public long ReplicaOrInstanceId
        {
            get; set;
        }
    }
}