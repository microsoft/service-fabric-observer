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

        public override string ToString() => $"ApplicationName: {Target ?? ""}\n" +
                                             $"ServiceExcludeList: {ServiceExcludeList ?? ""}\n" +
                                             $"ServiceIncludeList: {ServiceIncludeList ?? ""}\n" +
                                             $"MemoryWarningLimit: {MemoryWarningLimitMB}\n" +
                                             $"MemoryErrorLimit: {MemoryErrorLimitMB}\n" +
                                             $"CpuWarningLimit: {CpuWarningLimitPct}\n" +
                                             $"CpuErrorLimit: {CpuErrorLimitPct}\n" +
                                             $"DiskErrorIOReadsPerSecMS: {DiskIOErrorReadsPerSecMS}\n" +
                                             $"DiskIOErrorWritesPerSecMS: {DiskIOErrorWritesPerSecMS}\n" +
                                             $"DiskIOWarningReadsPerSecMS: {DiskIOWarningReadsPerSecMS}\n" +
                                             $"DiskIOWarningWritesPerSecMS: {DiskIOWarningWritesPerSecMS}\n" +
                                             $"NetworkErrorActivePorts: {NetworkErrorActivePorts}\n" +
                                             $"NetworkWarningActivePorts: {NetworkWarningActivePorts}\n" +
                                             $"NetworkErrorEphemeralPorts: {NetworkErrorEphemeralPorts}\n" +
                                             $"NetworkWarningEphemeralPorts: {NetworkWarningEphemeralPorts}\n" +
                                             $"NetworkErrorFirewallRules: {NetworkErrorFirewallRules}\n" +
                                             $"NetworkWarningFirewallRules: {NetworkWarningFirewallRules}\n" +
                                             $"DumpProcessOnError: {DumpProcessOnError}\n";
    }
}