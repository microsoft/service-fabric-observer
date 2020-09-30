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

        public double MemoryWarningLimitPercent { get; set; }

        public double MemoryErrorLimitPercent { get; set; }

        public double CpuErrorLimitPercent { get; set; }

        public double CpuWarningLimitPercent { get; set; }

        public int NetworkErrorActivePorts { get; set; }

        public int NetworkWarningActivePorts { get; set; }

        public int NetworkErrorEphemeralPorts { get; set; }

        public int NetworkWarningEphemeralPorts { get; set; }

        public int NetworkErrorFirewallRules { get; set; }

        public int NetworkWarningFirewallRules { get; set; }

        public bool DumpProcessOnError { get; set; }

        public override string ToString() => $"ApplicationName: {TargetApp ?? string.Empty}\n" +
                                             $"ApplicationTypeName: {TargetAppType ?? string.Empty}\n" +
                                             $"ServiceExcludeList: {ServiceExcludeList ?? string.Empty}\n" +
                                             $"ServiceIncludeList: {ServiceIncludeList ?? string.Empty}\n" +
                                             $"MemoryWarningLimitMB: {MemoryWarningLimitMb}\n" +
                                             $"MemoryErrorLimitMB: {MemoryErrorLimitMb}\n" +
                                             $"MemoryWarningLimitPercent: {MemoryWarningLimitPercent}\n" +
                                             $"MemoryErrorLimitPercent: {MemoryErrorLimitPercent}\n" +
                                             $"CpuWarningLimitPercent: {CpuWarningLimitPercent}\n" +
                                             $"CpuErrorLimitPercent: {CpuErrorLimitPercent}\n" +
                                             $"NetworkErrorActivePorts: {NetworkErrorActivePorts}\n" +
                                             $"NetworkWarningActivePorts: {NetworkWarningActivePorts}\n" +
                                             $"NetworkErrorEphemeralPorts: {NetworkErrorEphemeralPorts}\n" +
                                             $"NetworkWarningEphemeralPorts: {NetworkWarningEphemeralPorts}\n" +
                                             $"NetworkErrorFirewallRules: {NetworkErrorFirewallRules}\n" +
                                             $"NetworkWarningFirewallRules: {NetworkWarningFirewallRules}\n" +
                                             $"DumpProcessOnError: {DumpProcessOnError}\n";
    }
}