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

        public string AppExcludeList
        {
            get; set;
        }

        public string AppIncludeList
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

        /// <summary>
        /// Working Set
        /// </summary>
        public long MemoryWarningLimitMb
        {
            get; set;
        }

        /// <summary>
        /// Working Set
        /// </summary>
        public long MemoryErrorLimitMb
        {
            get; set;
        }

        /// <summary>
        /// Working Set
        /// </summary>
        public double MemoryWarningLimitPercent
        {
            get; set;
        }

        /// <summary>
        /// Working Set
        /// </summary>
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

        public double NetworkErrorEphemeralPortsPercent
        {
            get; set;
        }

        public double NetworkWarningEphemeralPortsPercent
        {
            get; set;
        }

        public bool DumpProcessOnError
        {
            get; set;
        }

        public bool DumpProcessOnWarning
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

        public int ErrorThreadCount
        {
            get; set;
        }

        public int WarningThreadCount
        {
            get; set;
        }

        // Private Bytes \\

        public long WarningPrivateBytesMb
        {
            get; set;
        }

        public long ErrorPrivateBytesMb
        {
            get; set;
        }

        public double WarningPrivateBytesPercent
        {
            get; set;
        }

        public double ErrorPrivateBytesPercent
        {
            get; set;
        }

        // RG monitoring.
        public double WarningRGMemoryLimitPercent
        {
            get; set;
        }

        public override string ToString() => $"ApplicationName: {TargetApp ?? string.Empty}{Environment.NewLine}" +
                                             $"ApplicationTypeName: {TargetAppType ?? string.Empty}{Environment.NewLine}" +
                                             $"AppExcludeList: {AppExcludeList ?? string.Empty}{Environment.NewLine}" +
                                             $"AppIncludeList: {AppIncludeList ?? string.Empty}{Environment.NewLine}" +
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
                                             $"NetworkErrorEphemeralPortsPercent: {NetworkErrorEphemeralPortsPercent}{Environment.NewLine}" +
                                             $"NetworkWarningEphemeralPortsPercent: {NetworkWarningEphemeralPortsPercent}{Environment.NewLine}" +
                                             $"DumpProcessOnError: {DumpProcessOnError}{Environment.NewLine}" +
                                             $"DumpProcessOnWarning: {DumpProcessOnWarning}{Environment.NewLine}" +
                                             $"ErrorOpenFileHandles: {ErrorOpenFileHandles}{Environment.NewLine}" +
                                             $"WarningOpenFileHandles: {WarningOpenFileHandles}{Environment.NewLine}" +
                                             $"ErrorThreadCount: {ErrorThreadCount}{Environment.NewLine}" +
                                             $"WarningThreadCount: {WarningThreadCount}{Environment.NewLine}" +
                                             $"ErrorPrivateBytesMb: {ErrorPrivateBytesMb}{Environment.NewLine}" +
                                             $"WarningPrivateBytesMb: {WarningPrivateBytesMb}{Environment.NewLine}" +
                                             $"ErrorPrivateBytesPercent: {ErrorPrivateBytesPercent}{Environment.NewLine}" +
                                             $"WarningPrivateBytesPercent: {WarningPrivateBytesPercent}{Environment.NewLine}" +
                                             $"WarningRGMemoryLimitPercent: {WarningRGMemoryLimitPercent}{Environment.NewLine}";
    }
}