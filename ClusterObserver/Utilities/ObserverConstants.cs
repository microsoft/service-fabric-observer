// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace ClusterObserver.Utilities
{
    public static class ObserverConstants
    {
        public const string ClusterObserverManagerName = "ClusterObserverManager";
        public const string ObserverManagerConfigurationSectionName = "ObserverManagerConfiguration";
        public const string EnableVerboseLoggingParameter = "EnableVerboseLogging";
        public const string ObserverLogPathParameter = "ObserverLogPath";
        public const string ObserverRunIntervalParameter = "RunInterval";
        public const string ObserverEnabledParameter = "Enabled";
        public const string AiKey = "AppInsightsInstrumentationKey";
        public const string AsyncOperationTimeoutSeconds = "AsyncOperationTimeoutSeconds";
        public const string ClusterObserverETWEventName = "ClusterObserverDataEvent";
        public const string EventSourceProviderName = "ClusterObserverETWProvider";
        public const string FabricObserverName = "FabricObserver";

        // The name of the package that contains this Observer's configuration.
        public const string ObserverConfigurationPackageName = "Config";

        // Setting name for Runtime frequency of the Observer
        public const string ObserverLoopSleepTimeSecondsParameter = "ObserverLoopSleepTimeSeconds";

        // Setting name for Grace period of shutdown in seconds.
        public const string ObserverShutdownGracePeriodInSecondsParameter = "ObserverShutdownGracePeriodInSeconds";

        // Setting name for Maximum time an observer should run before being considered hung or in some failure state.
        public const string ObserverExecutionTimeoutParameter = "ObserverExecutionTimeout";

        // EmitHealthWarningEvaluationDetails.
        public const string EmitHealthWarningEvaluationConfigurationSetting = "EmitHealthWarningEvaluationDetails";

        // Emit Repair Job information
        public const string MonitorRepairJobsConfigurationSetting = "MonitorRepairJobs";

        // Emit Application and Cluster upgrade information.
        public const string MonitorUpgradesConfigurationSetting = "MonitorUpgrades";

        // Settings.
        public const string ClusterObserverConfigurationSectionName = "ClusterObserverConfiguration";
        public const string MaxTimeNodeStatusNotOkSettingParameter = "MaxTimeNodeStatusNotOk";

        // Telemetry Settings Parameters.
        public const string EnableTelemetryParameter = "EnableTelemetry";
        public const string TelemetryProviderTypeParameter = "TelemetryProvider";
        public const string LogAnalyticsLogTypeParameter = "LogAnalyticsLogType";
        public const string LogAnalyticsSharedKeyParameter = "LogAnalyticsSharedKey";
        public const string LogAnalyticsWorkspaceIdParameter = "LogAnalyticsWorkspaceId";
        public const string EnableETWProviderParameter = "EnableETWProvider";
        public const string OperationalTelemetryEnabledParameter = "EnableOperationalTelemetry";

        // General consts.
        public const string ClusterObserverName = "ClusterObserver";
        public const int ObserverRunLoopSleepTimeSeconds = 60;
        public const string InfrastructureServiceType= "InfrastructureServiceType";
        public const string ClusterTypeSfrp = "SFRP";
        public const string Undefined = "Undefined";
        public const string ClusterTypePaasV1 = "PaasV1";
        public const string ClusterTypeStandalone = "Standalone";
        public const string SystemAppName = "fabric:/System";
    }
}
