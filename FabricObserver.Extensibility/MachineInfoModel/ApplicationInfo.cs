// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver.Observers.MachineInfoModel
{
    public class ApplicationInfo
    {
        public string TargetApp
        {
            get; set;
        }

        public string TargetAppType
        {
            get; set;
        }

        public string ServiceExcludeList
        {
            get; set;
        }

        public string ServiceIncludeList
        {
            get; set;
        }

        public long MemoryWarningLimitMb
        {
            get; set;
        }

        public long MemoryErrorLimitMb
        {
            get; set;
        }

        public double MemoryWarningLimitPercent
        {
            get; set;
        }

        public double MemoryErrorLimitPercent
        {
            get; set;
        }

        public double CpuErrorLimitPercent
        {
            get; set;
        }

        public double CpuWarningLimitPercent
        {
            get; set;
        }

        public int NetworkErrorActivePorts
        {
            get; set;
        }

        public int NetworkWarningActivePorts
        {
            get; set;
        }

        public int NetworkErrorEphemeralPorts
        {
            get; set;
        }

        public int NetworkWarningEphemeralPorts
        {
            get; set;
        }

        public int NetworkErrorFirewallRules
        {
            get; set;
        }

        public int NetworkWarningFirewallRules
        {
            get; set;
        }

        public bool DumpProcessOnError
        {
            get; set;
        }

        public int ErrorOpenFileHandles 
        { 
            get; set; 
        }

        public int WarningOpenFileHandles
        {
            get; set;
        }

        public override string ToString() => $"ApplicationName: {TargetApp ?? string.Empty}{Environment.NewLine}" +
                                             $"ApplicationTypeName: {TargetAppType ?? string.Empty}{Environment.NewLine}" +
                                             $"ServiceExcludeList: {ServiceExcludeList ?? string.Empty}{Environment.NewLine}" +
                                             $"ServiceIncludeList: {ServiceIncludeList ?? string.Empty}{Environment.NewLine}" +
                                             $"MemoryWarningLimitMB: {MemoryWarningLimitMb}{Environment.NewLine}" +
                                             $"MemoryErrorLimitMB: {MemoryErrorLimitMb}{Environment.NewLine}" +
                                             $"MemoryWarningLimitPercent: {MemoryWarningLimitPercent}{Environment.NewLine}" +
                                             $"MemoryErrorLimitPercent: {MemoryErrorLimitPercent}{Environment.NewLine}" +
                                             $"CpuWarningLimitPercent: {CpuWarningLimitPercent}{Environment.NewLine}" +
                                             $"CpuErrorLimitPercent: {CpuErrorLimitPercent}{Environment.NewLine}" +
                                             $"NetworkErrorActivePorts: {NetworkErrorActivePorts}{Environment.NewLine}" +
                                             $"NetworkWarningActivePorts: {NetworkWarningActivePorts}{Environment.NewLine}" +
                                             $"NetworkErrorEphemeralPorts: {NetworkErrorEphemeralPorts}{Environment.NewLine}" +
                                             $"NetworkWarningEphemeralPorts: {NetworkWarningEphemeralPorts}{Environment.NewLine}" +
                                             $"NetworkErrorFirewallRules: {NetworkErrorFirewallRules}{Environment.NewLine}" +
                                             $"NetworkWarningFirewallRules: {NetworkWarningFirewallRules}{Environment.NewLine}" +
                                             $"DumpProcessOnError: {DumpProcessOnError}{Environment.NewLine}" +
                                             $"ErrorOpenFileHandles: {ErrorOpenFileHandles}{Environment.NewLine}" +
                                             $"WarningOpenFileHandles: {WarningOpenFileHandles}{Environment.NewLine}";
    }
}