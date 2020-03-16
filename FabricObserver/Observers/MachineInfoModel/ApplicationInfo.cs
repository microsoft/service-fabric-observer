// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.MachineInfoModel
{
    public class ApplicationInfo
    {
        public string TargetApp { get; set; }

        public string TargetAppType { get; set; }

        public string ServiceExcludeList { get; set; }

        public string ServiceIncludeList { get; set; }

        public long MemoryWarningLimitMb { get; set; }

        public long MemoryErrorLimitMb { get; set; }

        public int MemoryWarningLimitPercent { get; set; }

        public int MemoryErrorLimitPercent { get; set; }

        public int CpuErrorLimitPercent { get; set; }

        public int CpuWarningLimitPercent { get; set; }

        public int NetworkErrorActivePorts { get; set; }

        public int NetworkWarningActivePorts { get; set; }

        public int NetworkErrorEphemeralPorts { get; set; }

        public int NetworkWarningEphemeralPorts { get; set; }

        public int NetworkErrorFirewallRules { get; set; }

        public int NetworkWarningFirewallRules { get; set; }

        public bool DumpProcessOnError { get; set; }

        /// <inheritdoc/>
        public override string ToString() => $"ApplicationName: {this.TargetApp ?? string.Empty}\n" +
                                             $"ApplicationTypeName: {this.TargetAppType ?? string.Empty}\n" +
                                             $"ServiceExcludeList: {this.ServiceExcludeList ?? string.Empty}\n" +
                                             $"ServiceIncludeList: {this.ServiceIncludeList ?? string.Empty}\n" +
                                             $"MemoryWarningLimitMB: {this.MemoryWarningLimitMb}\n" +
                                             $"MemoryErrorLimitMB: {this.MemoryErrorLimitMb}\n" +
                                             $"MemoryWarningLimitPercent: {this.MemoryWarningLimitPercent}\n" +
                                             $"MemoryErrorLimitPercent: {this.MemoryErrorLimitPercent}\n" +
                                             $"CpuWarningLimitPercent: {this.CpuWarningLimitPercent}\n" +
                                             $"CpuErrorLimitPercent: {this.CpuErrorLimitPercent}\n" +
                                             $"NetworkErrorActivePorts: {this.NetworkErrorActivePorts}\n" +
                                             $"NetworkWarningActivePorts: {this.NetworkWarningActivePorts}\n" +
                                             $"NetworkErrorEphemeralPorts: {this.NetworkErrorEphemeralPorts}\n" +
                                             $"NetworkWarningEphemeralPorts: {this.NetworkWarningEphemeralPorts}\n" +
                                             $"NetworkErrorFirewallRules: {this.NetworkErrorFirewallRules}\n" +
                                             $"NetworkWarningFirewallRules: {this.NetworkWarningFirewallRules}\n" +
                                             $"DumpProcessOnError: {this.DumpProcessOnError}\n";
    }
}