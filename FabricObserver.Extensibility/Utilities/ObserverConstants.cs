// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    public sealed class ObserverConstants
    {
        public const string ObserverManagerName = "ObserverManager";
        public const string ObserverManagerConfigurationSectionName = "ObserverManagerConfiguration";
        public const string ObserverWebApiEnabled = "ObserverWebApiEnabled";
        public const string EnableCSVDataLogging = "EnableCSVDataLogging";
        public const string Fqdn = "FQDN";
        public const string EnableETWProvider = "EnableETWProvider";
        public const string EventSourceProviderName = "FabricObserverETWProvider";
        public const string FabricObserverTelemetryEnabled = "EnableFabricObserverDiagnosticTelemetry";
        public const string AsyncClusterOperationTimeoutSeconds = "ClusterOperationTimeoutSeconds";
        public const string FabricObserverName = "FabricObserver";
        public const string FabricObserverETWEventName = "FabricObserverDataEvent";

        // The name of the package that contains this Observer's configuration
        public const string ObserverConfigurationPackageName = "Config";

        // Setting name for Runtime frequency of the Observer
        public const string ObserverLoopSleepTimeSeconds = "ObserverLoopSleepTimeSeconds";

        // Default to 1 minute if frequency is not supplied in config
        public const int ObserverRunLoopSleepTimeSeconds = 60;

        public const string AllObserversExecutedMessage = "All Observers have been executed.";

        // Setting name for Grace period of shutdown in seconds.
        public const string ObserverShutdownGracePeriodInSeconds = "ObserverShutdownGracePeriodInSeconds";

        // Setting name for Maximum time an observer should run before being considered hung or in some failure state.
        public const string ObserverExecutionTimeout = "ObserverExecutionTimeout";

        // Common Observer Settings Parameters.
        public const string ObserverLogPathParameter = "ObserverLogPath";
        public const string DataLogPathParameter = "DataLogPath";
        public const string ObserverRunIntervalParameter = "RunInterval";
        public const string ObserverEnabledParameter = "Enabled";
        public const string ObserverTelemetryEnabledParameter = "EnableTelemetry";
        public const string ObserverEtwEnabledParameter = "EnableEtw";
        public const string EnableVerboseLoggingParameter = "EnableVerboseLogging";
        public const string DataCapacityParameter = "ResourceUsageDataCapacity";
        public const string UseCircularBufferParameter = "UseCircularBuffer";
        public const string MonitorDurationParameter = "MonitorDuration";
        public const string MaxArchivedCsvFileLifetimeDays = "MaxArchivedCsvFileLifetimeDays";
        public const string MaxArchivedLogFileLifetimeDays = "MaxArchivedLogFileLifetimeDays";
        public const string CsvFileWriteFormat = "CsvFileWriteFormat";

        // AppObserver.
        public const string AppObserverName = "AppObserver";
        public const string AppObserverConfigurationSectionName = "AppObserverConfiguration";

        // Certificate Observer
        public const string CertificateObserverName = "CertificateObserver";
        public const string CertificateObserverConfigurationSectionName = "CertificateObserverConfiguration";
        public const string CertificateObserverDaysUntilClusterExpiryWarningThreshold = "DaysUntilClusterExpiryWarningThreshold";
        public const string CertificateObserverDaysUntilAppExpiryWarningThreshold = "DaysUntilAppExpiryWarningThreshold";
        public const string CertificateObserverAppCertificateThumbprints = "AppCertThumbprintsToObserve";
        public const string CertificateObserverAppCertificateCommonNames = "AppCertCommonNamesToObserve";

        // DiskObserver.
        public const string DiskObserverName = "DiskObserver";
        public const string DiskObserverConfigurationSectionName = "DiskObserverConfiguration";
        public const string DiskObserverIntervalParameterName = "RunInterval";
        public const string DiskObserverDiskSpacePercentError = "DiskSpacePercentUsageErrorThreshold";
        public const string DiskObserverDiskSpacePercentWarning = "DiskSpacePercentUsageWarningThreshold";
        public const string DiskObserverAverageQueueLengthError = "AverageQueueLengthErrorThreshold";
        public const string DiskObserverAverageQueueLengthWarning = "AverageQueueLengthWarningThreshold";

        // FabricSystemObserver.
        public const string FabricSystemObserverName = "FabricSystemObserver";
        public const string FabricSystemObserverConfigurationSectionName = "FabricSystemObserverConfiguration";
        public const string FabricSystemObserverCpuErrorLimitPct = "CpuErrorLimitPercent";
        public const string FabricSystemObserverCpuWarningLimitPct = "CpuWarningLimitPercent";
        public const string FabricSystemObserverMemoryErrorLimitMb = "MemoryErrorLimitMb";
        public const string FabricSystemObserverMemoryWarningLimitMb = "MemoryWarningLimitMb";
        public const string FabricSystemObserverMemoryUsePercentError = "MemoryErrorLimitPercent";
        public const string FabricSystemObserverMemoryUsePercentWarning = "MemoryWarningLimitPercent";
        public const string FabricSystemObserverNetworkErrorActivePorts = "NetworkErrorActivePorts";
        public const string FabricSystemObserverNetworkWarningActivePorts = "NetworkWarningActivePorts";
        public const string FabricSystemObserverNetworkErrorEphemeralPorts = "NetworkErrorEphemeralPorts";
        public const string FabricSystemObserverNetworkWarningEphemeralPorts = "NetworkWarningEphemeralPorts";
        public const string FabricSystemObserverMonitorWindowsEventLog = "MonitorWindowsEventLog";
        public const string FabricSystemObserverErrorHandles = "AllocatedHandlesErrorLimit";
        public const string FabricSystemObserverWarningHandles = "AllocatedHandlesWarningLimit";

        // NetworkObserver.
        public const string NetworkObserverName = "NetworkObserver";
        public const string NetworkObserverConfigurationSectionName = "NetworkObserverConfiguration";

        // NodeObserver.
        public const string NodeObserverName = "NodeObserver";
        public const string NodeObserverConfigurationSectionName = "NodeObserverConfiguration";
        public const string NodeObserverCpuErrorLimitPct = "CpuErrorLimitPercent";
        public const string NodeObserverCpuWarningLimitPct = "CpuWarningLimitPercent";
        public const string NodeObserverMemoryErrorLimitMb = "MemoryErrorLimitMb";
        public const string NodeObserverMemoryWarningLimitMb = "MemoryWarningLimitMb";
        public const string NodeObserverMemoryUsePercentError = "MemoryErrorLimitPercent";
        public const string NodeObserverMemoryUsePercentWarning = "MemoryWarningLimitPercent";
        public const string NodeObserverNetworkErrorActivePorts = "NetworkErrorActivePorts";
        public const string NodeObserverNetworkWarningActivePorts = "NetworkWarningActivePorts";
        public const string NodeObserverNetworkErrorEphemeralPorts = "NetworkErrorEphemeralPorts";
        public const string NodeObserverNetworkWarningEphemeralPorts = "NetworkWarningEphemeralPorts";
        public const string NodeObserverNetworkErrorFirewallRules = "NetworkErrorFirewallRules";
        public const string NodeObserverNetworkWarningFirewallRules = "NetworkWarningFirewallRules";
        
        // For use by Linux File Descriptors monitor.
        public const string NodeObserverLinuxFileHandlesErrorLimitPct = "LinuxFileHandlesErrorLimitPercent";
        public const string NodeObserverLinuxFileHandlesWarningLimitPct = "LinuxFileHandlesWarningLimitPercent";
        public const string NodeObserverLinuxFileHandlesErrorTotalAllocated = "LinuxFileHandlesErrorLimitTotal";
        public const string NodeObserverLinuxFileHandlesWarningTotalAllocated = "LinuxFileHandlesWarningLimitTotal";

        // OSObserver.
        public const string OSObserverName = "OSObserver";
        public const string OSObserverConfigurationSectionName = "OSObserverConfiguration";
        public const string EnableWindowsAutoUpdateCheck = "EnableWindowsAutoUpdateCheck";

        // SFConfigurationObserver.
        public const string SFConfigurationObserverName = "SFConfigurationObserver";
        public const string SFConfigurationObserverVersionName = "InfrastructureConfigurationVersion";
        public const string SFConfigurationObserverConfigurationSectionName = "SFConfigurationObserverConfiguration";
        public const string SFConfigurationObserverRunIntervalParameterName = "RunInterval";

        // Telemetry Settings Parameters.
        public const string AiKey = "AppInsightsInstrumentationKey";
        public const string TelemetryEnabled = "EnableTelemetryProvider";
        public const string TelemetryProviderType = "TelemetryProvider";
        public const string LogAnalyticsLogTypeParameter = "LogAnalyticsLogType";
        public const string LogAnalyticsSharedKeyParameter = "LogAnalyticsSharedKey";
        public const string LogAnalyticsWorkspaceIdParameter = "LogAnalyticsWorkspaceId";
        public const string InfrastructureServiceType = "InfrastructureServiceType";
        public const string ClusterTypeSfrp = "SFRP";
        public const string Undefined = "Undefined";
        public const string ClusterTypePaasV1 = "PaasV1";
        public const string ClusterTypeStandalone = "Standalone";
    }
}
