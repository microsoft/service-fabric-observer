// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [Serializable]
    public class MachineTelemetryData
    {
        [EventField]
        public string HealthState
        {
            get; set;
        }

        [EventField]
        public string NodeName
        {
            get; set;
        }

        [EventField]
        public string ObserverName
        {
            get; set;
        }

        [EventField]
        public string OSName
        {
            get; set;
        }

        [EventField]
        public string OSVersion
        {
            get; set;
        }

        [EventField]
        public string OSInstallDate
        {
            get; set;
        }

        [EventField]
        public string LastBootUpTime
        {
            get; set;
        }

        [EventField]
        public int TotalMemorySizeGB
        {
            get; set;
        }

        [EventField]
        public int LogicalProcessorCount
        {
            get; set;
        }

        [EventField]
        public int LogicalDriveCount
        {
            get; set;
        }

        [EventField]
        public int NumberOfRunningProcesses
        {
            get; set;
        }

        [EventField]
        public int ActiveFirewallRules
        {
            get; set;
        }

        [EventField]
        public int ActiveTcpPorts
        {
            get; set;
        }

        [EventField]
        public int ActiveEphemeralTcpPorts
        {
            get; set;
        }

        [EventField]
        public string EphemeralTcpPortRange
        {
            get; set;
        }

        [EventField]
        public string FabricApplicationTcpPortRange
        {
            get; set;
        }

        [EventField]
        public string HotFixes
        {
            get; set;
        }

        [EventField]
        public double AvailablePhysicalMemoryGB
        {
            get; set;
        }

        [EventField]
        public string DriveInfo
        {
            get; set;
        }

        [EventField]
        public double FreeVirtualMemoryGB
        {
            get; set;
        }

        [EventField]
        public bool WindowsUpdateAutoDownloadEnabled
        {
            get; set;
        }
    }
}