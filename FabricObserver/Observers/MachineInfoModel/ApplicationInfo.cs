// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Model
{
    internal class ApplicationInfo
    {
        public string Target { get; set; }

        public string ServiceExcludeList { get; set; }

        public string ServiceIncludeList { get; set; }

        public long MemoryWarningLimitMB { get; set; } = 1000;

        public long MemoryErrorLimitMB { get; set; } = 4000;

        public int CpuErrorLimitPct { get; set; } = 0;

        public int CpuWarningLimitPct { get; set; } = 75;

        public int DiskIOErrorReadsPerSecMS { get; set; } = 0;

        public int DiskIOErrorWritesPerSecMS { get; set; } = 0;

        public int DiskIOWarningReadsPerSecMS { get; set; } = 100;

        public int DiskIOWarningWritesPerSecMS { get; set; } = 0;

        public int NetworkErrorActivePorts { get; set; } = 0;

        public int NetworkWarningActivePorts { get; set; } = 40000;

        public int NetworkErrorEphemeralPorts { get; set; } = 0;

        public int NetworkWarningEphemeralPorts { get; set; } = 20000;

        public int NetworkErrorFirewallRules { get; set; } = 0;

        public int NetworkWarningFirewallRules { get; set; } = 2500;

        public bool DumpProcessOnError { get; set; } = false;

        /// <inheritdoc/>
        public override string ToString() => $"ApplicationName: {this.Target ?? string.Empty}\n" +
                                             $"ServiceExcludeList: {this.ServiceExcludeList ?? string.Empty}\n" +
                                             $"ServiceIncludeList: {this.ServiceIncludeList ?? string.Empty}\n" +
                                             $"MemoryWarningLimit: {this.MemoryWarningLimitMB}\n" +
                                             $"MemoryErrorLimit: {this.MemoryErrorLimitMB}\n" +
                                             $"CpuWarningLimit: {this.CpuWarningLimitPct}\n" +
                                             $"CpuErrorLimit: {this.CpuErrorLimitPct}\n" +
                                             $"DiskErrorIOReadsPerSecMS: {this.DiskIOErrorReadsPerSecMS}\n" +
                                             $"DiskIOErrorWritesPerSecMS: {this.DiskIOErrorWritesPerSecMS}\n" +
                                             $"DiskIOWarningReadsPerSecMS: {this.DiskIOWarningReadsPerSecMS}\n" +
                                             $"DiskIOWarningWritesPerSecMS: {this.DiskIOWarningWritesPerSecMS}\n" +
                                             $"NetworkErrorActivePorts: {this.NetworkErrorActivePorts}\n" +
                                             $"NetworkWarningActivePorts: {this.NetworkWarningActivePorts}\n" +
                                             $"NetworkErrorEphemeralPorts: {this.NetworkErrorEphemeralPorts}\n" +
                                             $"NetworkWarningEphemeralPorts: {this.NetworkWarningEphemeralPorts}\n" +
                                             $"NetworkErrorFirewallRules: {this.NetworkErrorFirewallRules}\n" +
                                             $"NetworkWarningFirewallRules: {this.NetworkWarningFirewallRules}\n" +
                                             $"DumpProcessOnError: {this.DumpProcessOnError}\n";
    }
}