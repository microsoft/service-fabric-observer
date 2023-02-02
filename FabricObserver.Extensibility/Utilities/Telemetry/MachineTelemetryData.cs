// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    public class MachineTelemetryData
    {
        public string HealthState
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

        public string OSName
        {
            get; set;
        }

        public string OSVersion
        {
            get; set;
        }

        public string OSInstallDate
        {
            get; set;
        }

        public string LastBootUpTime
        {
            get; set;
        }

        public int TotalMemorySizeGB
        {
            get; set;
        }

        public int LogicalProcessorCount
        {
            get; set;
        }

        public int LogicalDriveCount
        {
            get; set;
        }

        public int NumberOfRunningProcesses
        {
            get; set;
        }

        public int ActiveFirewallRules
        {
            get; set;
        }

        public int ActiveTcpPorts
        {
            get; set;
        }

        public int ActiveEphemeralTcpPorts
        {
            get; set;
        }

        public string EphemeralTcpPortRange
        {
            get; set;
        }

        public string FabricApplicationTcpPortRange
        {
            get; set;
        }

        public string HotFixes
        {
            get; set;
        }

        public double AvailablePhysicalMemoryGB
        {
            get; set;
        }

        public string DriveInfo
        {
            get; set;
        }

        public double FreeVirtualMemoryGB
        {
            get; set;
        }

        public bool WindowsUpdateAutoDownloadEnabled
        {
            get; set;
        }
    }
}