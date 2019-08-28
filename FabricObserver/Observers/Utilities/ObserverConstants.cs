// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Utilities
{
    public static class ObserverConstants
    {
        #region ObserverManager
        public const string ObserverManangerName = "ObserverManager";
        public const string ObserverManagerConfigurationSectionName = "ObserverManagerConfiguration";
        public const string EnableVerboseLoggingParameter = "EnableVerboseLogging";
        public const string EnableLongRunningCSVLogging = "EnableLongRunningCSVLogging";
        public const string ObserverLogPath = "ObserverLogPath";
        public const string DataLogPath = "DataLogPath";
        public const string FQDN = "FQDN";

        // The service Manifest name for Observers in the configuration file
        public const string ObserversSectionName = "Observers";

        // The name of the package that contains this Observer's data
        public const string ObserverDataPackageName = "Observers.Data";

        // The name of the package that contains this Observer's configuration
        public const string ObserverConfigurationPackageName = "Config";

        // Setting name for Runtime frequency of the Observer
        public const string ObserverLoopSleepTimeSeconds = "ObserverLoopSleepTimeSeconds";

        // Default to 1 minute if frequency is not supplied in config
        public const int ObserverRunLoopSleepTimeSeconds = 60;

        public const string AllObserversExecutedMessage = "All Observers have been executed.";

        // Setting name for Grace period of shutdown in seconds.
        public const string ObserverShutdownGracePeriodInSeconds = "ObserverShutdownGracePeriodInSeconds";

        // Setting name for  Maximum time an observer should run before being considered hung or in some failure state...
        public const string ObserverExecutionTimeout = "ObserverExecutionTimeout";

        #endregion Observer Manager

        #region DiskObserver
        public const string DiskObserverName = "DiskObserver";
        public const string DiskObserverConfigurationSectionName = "DiskObserverConfiguration";
        public const string DiskObserverIntervalParameterName = "RunInterval";
        public const string DiskObserverDiskSpaceError = "DiskSpaceErrorThreshold";
        public const string DiskObserverDiskSpaceWarning = "DiskSpaceWarningThreshold";
        public const string DiskObserverIOReadsError = "IOReadsErrorThreshold";
        public const string DiskObserverIOReadsWarning = "IOReadsWarningThreshold";
        public const string DiskObserverIOWritesError = "IOWritesErrorThreshold";
        public const string DiskObserverIOWritesWarning = "IOWritesWarningThreshold";
        public const string DiskObserverAverageQueueLengthError = "AverageQueueLengthErrorThreshold";
        public const string DiskObserverAverageQueueLengthWarning = "AverageQueueLengthWarningThreshold";
        
        // WMI queries... 
        public const string DiskDriveWMIQuery = "SELECT Caption, DeviceID from Win32_DiskDrive";
        public const string PartitionsWMIQueryTemplate = "ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{0}'}} " +
                                                         "WHERE AssocClass = Win32_DiskDriveToDiskPartition";
        public const string LogicalDisksWMIQueryTemplate = "ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{0}'}} " +
                                                           "WHERE AssocClass = Win32_LogicalDiskToPartition";
        #endregion

        #region SFConfigurationObserver
        public const string SFConfigurationObserverName = "SFConfigurationObserver";
        public const string InfrastructureConfigurationVersionName = "InfrastructureConfigurationVersion";
        public const string InfrastructureObserverConfigurationSectionName = "SFConfigurationObserverConfiguration";
        public const string InfrastructureObserverIntervalParameterName = "RunInterval";
        #endregion Infrastructure Configuration Observer

        #region OSObserver
        public const string OSObserverName = "OSObserver";
        internal const string OSObserverConfigurationSectionName = "OSObserverConfiguration";
        internal const string OSObserverObserverIntervalParameterName = "RunInterval";
        #endregion

        #region CertificateObserver
        // Observer name
        internal const string CertificateObserverName = "CertificateObserver";
        // Observer configuration parameters
        internal const string CertificateObserverConfigurationSectionName = "CertificateObserverConfiguration";
        internal const string CertificateObserverExclusionsSectionName = "CertificateObserverExclusions";
        internal const string CertificateObserverIntervalParameterName = "ObserverRunInterval";
        internal const string CertificateObserverThresholdParameterName = "ObserverExpiryThreshold";
        #endregion Certificate Observer

        #region ContainerObserver
        public const string ContainerObserverName = "ContainerObserver";
        public const string ContainerObserverConfigurationSectionName = "ContainerObserverConfiguration";
        #endregion

        #region AppObserver
        public const string AppObserverName = "AppObserver";
        public const string AppObserverConfigurationSectionName = "AppObserverConfiguration";
        #endregion

        #region NodeObserver
        public const string NodeObserverName = "NodeObserver";
        public const string NodeObserverConfigurationSectionName = "NodeObserverConfiguration";
        public const string NodeObserverCpuErrorLimitPct = "CpuErrorLimitPct";
        public const string NodeObserverCpuWarningLimitPct = "CpuWarningLimitPct"; 
        public const string NodeObserverMemoryErrorLimitMB = "MemoryErrorLimitMB";
        public const string NodeObserverMemoryWarningLimitMB = "MemoryWarningLimitMB";
        public const string NodeObserverNetworkErrorActivePorts = "NetworkErrorActivePorts";
        public const string NodeObserverNetworkWarningActivePorts = "NetworkWarningActivePorts";
        public const string NodeObserverNetworkErrorEphemeralPorts = "NetworkErrorEphemeralPorts";
        public const string NodeObserverNetworkWarningEphemeralPorts = "NetworkWarningEphemeralPorts";
        public const string NodeObserverNetworkErrorFirewallRules = "NetworkErrorFirewallRules";
        public const string NodeObserverNetworkWarningFirewallRules = "NetworkWarningFirewallRules";
        #endregion

        #region NetworkObserver
        public const string NetworkObserverName = "NetworkObserver";
        public const string NetworkObserverConfigurationSectionName = "NetworkObserverConfiguration";
        #endregion

        #region FabricSystemObserver
        public const string FabricSystemObserverName = "FabricSystemObserver";
        public const string FabricSystemObserverConfigurationSectionName = "FabricSystemObserverConfiguration";
        public const string FabricSystemObserverErrorCpu = "ErrorCpuThresholdPercent";
        public const string FabricSystemObserverWarnCpu = "WarnCpuThresholdPercent";
        public const string FabricSystemObserverErrorMemory = "ErrorMemoryThresholdMB";
        public const string FabricSystemObserverWarnMemory = "WarnMemoryThresholdMB";
        public const string FabricSystemObserverErrorDiskIOReads = "ErrorDiskIOReadsThreshold";
        public const string FabricSystemObserverWarnDiskIOReads = "WarnDiskIOReadsThreshold";
        public const string FabricSystemObserverErrorDiskIOWrites = "ErrorDiskIOWritesThreshold";
        public const string FabricSystemObserverWarnDiskIOWrites = "WarnDiskIOWritesThreshold";
        public const string FabricSystemObserverErrorPercentUnhealthyNodes = "PercentUnhealthyNodesErrorThreshold";
        public const string FabricSystemObserverWarnPercentUnhealthyNodes = "PercentUnhealthyNodesWarnThreshold";
        public const string FabricSystemObserverMonitorWindowsEventLog = "MonitorWindowsEventLog";
        #endregion

        #region Misc
        internal const string ObserverEnabled = "Enabled";
        internal const string AIKey = "AppInsightsInstrumentationKey";
        internal const string TelemetryEnabled = "EnableTelemetryProvider";
        internal const string EnableEventSourceProvider = "EnableEventSourceProvider";
        internal const string EventSourceProviderName = "EventSourceProviderName";
        internal const string FabricObserverTelemetryEnabled = "EnableFabricObserverDiagnosticTelemetry";
        public const string ConfigPackageName = "Config";
        public const string ObserversDataPackageName = "Observers.Data";
        public const string AppObserverConfiguration = "AppObserverConfiguration";
        public const string NetworkObserverConfiguration = "NetworkObserverConfiguration";
        #endregion
    }
}
