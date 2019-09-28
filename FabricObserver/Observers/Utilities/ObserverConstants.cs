// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Utilities
{
    public sealed class ObserverConstants
    {
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

        // Setting name for Maximum time an observer should run before being considered hung or in some failure state...
        public const string ObserverExecutionTimeout = "ObserverExecutionTimeout";

        public const string DiskObserverName = "DiskObserver";
        public const string DiskObserverConfigurationSectionName = "DiskObserverConfiguration";
        public const string DiskObserverIntervalParameterName = "RunInterval";
        public const string DiskObserverDiskSpacePercentError = "DiskSpacePercentErrorThreshold";
        public const string DiskObserverDiskSpacePercentWarning = "DiskSpacePercentWarningThreshold";
        public const string DiskObserverIOReadsError = "IOReadsErrorThreshold";
        public const string DiskObserverIOReadsWarning = "IOReadsWarningThreshold";
        public const string DiskObserverIOWritesError = "IOWritesErrorThreshold";
        public const string DiskObserverIOWritesWarning = "IOWritesWarningThreshold";
        public const string DiskObserverAverageQueueLengthError = "AverageQueueLengthErrorThreshold";
        public const string DiskObserverAverageQueueLengthWarning = "AverageQueueLengthWarningThreshold";
        public const string SFConfigurationObserverName = "SFConfigurationObserver";
        public const string InfrastructureConfigurationVersionName = "InfrastructureConfigurationVersion";
        public const string InfrastructureObserverConfigurationSectionName = "SFConfigurationObserverConfiguration";
        public const string InfrastructureObserverIntervalParameterName = "RunInterval";
        public const string OSObserverName = "OSObserver";
        public const string OSObserverConfigurationSectionName = "OSObserverConfiguration";
        public const string OSObserverObserverIntervalParameterName = "RunInterval";

        // Observer configuration parameters
        public const string CertificateObserverIntervalParameterName = "ObserverRunInterval";
        public const string CertificateObserverThresholdParameterName = "ObserverExpiryThreshold";
        public const string ContainerObserverName = "ContainerObserver";
        public const string ContainerObserverConfigurationSectionName = "ContainerObserverConfiguration";
        public const string AppObserverName = "AppObserver";
        public const string AppObserverConfigurationSectionName = "AppObserverConfiguration";
        public const string NodeObserverName = "NodeObserver";
        public const string NodeObserverConfigurationSectionName = "NodeObserverConfiguration";
        public const string NodeObserverCpuErrorLimitPct = "CpuErrorLimitPercent";
        public const string NodeObserverCpuWarningLimitPct = "CpuWarningLimitPercent";
        public const string NodeObserverMemoryErrorLimitMB = "MemoryErrorLimitMB";
        public const string NodeObserverMemoryWarningLimitMB = "MemoryWarningLimitMB";
        public const string NodeObserverNetworkErrorActivePorts = "NetworkErrorActivePorts";
        public const string NodeObserverNetworkWarningActivePorts = "NetworkWarningActivePorts";
        public const string NodeObserverNetworkErrorEphemeralPorts = "NetworkErrorEphemeralPorts";
        public const string NodeObserverNetworkWarningEphemeralPorts = "NetworkWarningEphemeralPorts";
        public const string NodeObserverNetworkErrorFirewallRules = "NetworkErrorFirewallRules";
        public const string NodeObserverNetworkWarningFirewallRules = "NetworkWarningFirewallRules";
        public const string NodeObserverMemoryUsePercentError = "MemoryErrorLimitPercent";
        public const string NodeObserverMemoryUsePercentWarning = "MemoryWarningLimitPercent";
        public const string NetworkObserverName = "NetworkObserver";
        public const string NetworkObserverConfigurationSectionName = "NetworkObserverConfiguration";
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
        public const string ObserverEnabled = "Enabled";
        public const string AIKey = "AppInsightsInstrumentationKey";
        public const string TelemetryEnabled = "EnableTelemetryProvider";
        public const string EnableEventSourceProvider = "EnableEventSourceProvider";
        public const string EventSourceProviderName = "EventSourceProviderName";
        public const string FabricObserverTelemetryEnabled = "EnableFabricObserverDiagnosticTelemetry";
        public const string ConfigPackageName = "Config";
        public const string ObserversDataPackageName = "Observers.Data";
        public const string AppObserverConfiguration = "AppObserverConfiguration";
        public const string NetworkObserverConfiguration = "NetworkObserverConfiguration";
    }
}
