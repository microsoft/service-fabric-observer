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

        public long MemoryWarningLimitMB { get; set; }

        public long MemoryErrorLimitMB { get; set; }

        public int MemoryWarningLimitPercent { get; set; }

        public int MemoryErrorLimitPercent { get; set; }

        public int CpuErrorLimitPct { get; set; }

        public int CpuWarningLimitPct { get; set; }

        public int NetworkErrorActivePorts { get; set; }

        public int NetworkWarningActivePorts { get; set; }

        public int NetworkErrorEphemeralPorts { get; set; }

        public int NetworkWarningEphemeralPorts { get; set; }

        public int NetworkErrorFirewallRules { get; set; }

        public int NetworkWarningFirewallRules { get; set; }

        public bool DumpProcessOnError { get; set; }

        /// <inheritdoc/>
        public override string ToString() => $"ApplicationName: {this.Target ?? string.Empty}\n" +
                                             $"ServiceExcludeList: {this.ServiceExcludeList ?? string.Empty}\n" +
                                             $"ServiceIncludeList: {this.ServiceIncludeList ?? string.Empty}\n" +
                                             $"MemoryWarningLimitMB: {this.MemoryWarningLimitMB}\n" +
                                             $"MemoryErrorLimitMB: {this.MemoryErrorLimitMB}\n" +
                                             $"MemoryWarningLimitPercent: {this.MemoryWarningLimitPercent}\n" +
                                             $"MemoryErrorLimitPercent: {this.MemoryErrorLimitPercent}\n" +
                                             $"CpuWarningLimit: {this.CpuWarningLimitPct}\n" +
                                             $"CpuErrorLimit: {this.CpuErrorLimitPct}\n" +
                                             $"NetworkErrorActivePorts: {this.NetworkErrorActivePorts}\n" +
                                             $"NetworkWarningActivePorts: {this.NetworkWarningActivePorts}\n" +
                                             $"NetworkErrorEphemeralPorts: {this.NetworkErrorEphemeralPorts}\n" +
                                             $"NetworkWarningEphemeralPorts: {this.NetworkWarningEphemeralPorts}\n" +
                                             $"NetworkErrorFirewallRules: {this.NetworkErrorFirewallRules}\n" +
                                             $"NetworkWarningFirewallRules: {this.NetworkWarningFirewallRules}\n" +
                                             $"DumpProcessOnError: {this.DumpProcessOnError}\n";
    }
}