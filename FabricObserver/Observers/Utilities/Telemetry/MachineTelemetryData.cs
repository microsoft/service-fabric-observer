// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities.Telemetry
{
    public class MachineTelemetryData
    {
        public string HealthState
        {
            get;
            set;
        }

        public string Node
        {
            get;
            set;
        }

        public string Observer
        {
            get;
            set;
        }

        public string OS
        {
            get;
            set;
        }

        public string OSVersion
        {
            get;
            set;
        }

        public string OSInstallDate
        {
            get;
            set;
        }

        public string LastBootUpTime
        {
            get;
            set;
        }

        public int TotalMemorySizeGB
        {
            get;
            set;
        }

        public int LogicalProcessorCount
        {
            get;
            set;
        }

        public int LogicalDriveCount
        {
            get;
            set;
        }

        public int NumberOfRunningProcesses
        {
            get;
            set;
        }

        public int ActiveFirewallRules
        {
            get;
            set;
        }

        public int ActivePorts
        {
            get;
            set;
        }

        public int ActiveEphemeralPorts
        {
            get;
            set;
        }

        public string WindowsDynamicPortRange
        {
            get;
            set;
        }

        public string FabricAppPortRange
        {
            get;
            set;
        }

        public string HotFixes
        {
            get;
            set;
        }

        public double AvailablePhysicalMemory
        {
            get;
            internal set;
        }

        public string DriveInfo
        {
            get;
            internal set;
        }

        public double AvailableVirtualMemory
        {
            get;
            internal set;
        }
    }
}