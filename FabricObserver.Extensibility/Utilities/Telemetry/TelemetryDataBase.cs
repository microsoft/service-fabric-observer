// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;
using System.Fabric.Health;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    /// <summary>
    /// Base class of telemetry data types.
    /// </summary>
    [EventData]
    [Serializable]
    public class TelemetryDataBase
    {
        private readonly string _os;

        /// <summary>
        /// The Cluster Id.
        /// </summary>
        [EventField]
        public string ClusterId { get; set; }
        /// <summary>
        /// The supported FO error code.
        /// </summary>
        [EventField]
        public string Code { get; set; }
        /// <summary>
        /// The description of the problem being reported.
        /// </summary>
        [EventField]
        public string Description { get; set; }
        /// <summary>
        /// The target Service Fabric entity type.
        /// </summary>
        [EventField]
        public EntityType EntityType { get; set; }
        /// <summary>
        /// The HealthState of the entity.
        /// </summary>
        [EventField]
        public HealthState HealthState { get; set; }
        /// <summary>
        /// The supported resource usage metric name.
        /// </summary>
        [EventField]
        public string Metric { get; set; }
        /// <summary>
        /// Node name.
        /// </summary>
        [EventField]
        public string NodeName
        {
            get; set;
        }
        /// <summary>
        /// Node type.
        /// </summary>
        [EventField]
        public string NodeType
        {
            get; set;
        }
        /// <summary>
        /// Name of observer that emitted this information.
        /// </summary>
        [EventField]
        public string ObserverName { get; set; }
        /// <summary>
        /// The OS hosting Service Fabric. This is read-only.
        /// </summary>
        [EventField]
        public string OS => _os;
        /// <summary>
        /// This is the name of the entity that is generating the health report that houses this TelemetryData instance.
        /// </summary>
        [EventField]
        public string Source { get; set; }
        /// <summary>
        /// The supported resource usage metric value.
        /// </summary>
        [EventField]
        public double Value { get; set; }
        /// <summary>
        /// This will be used as the Health Event Property.
        /// </summary>
        [EventField]
        public string Property { get; set; }

        [JsonConstructor]
        public TelemetryDataBase()
        {
            _os = OperatingSystem.IsWindows() ? "Windows" : "Linux";
        }
    }
}