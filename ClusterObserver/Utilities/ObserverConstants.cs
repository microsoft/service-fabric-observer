// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricClusterObserver.Utilities
{
    public sealed class ObserverConstants
    {
        public const string ObserverManangerName = "ClusterObserverManager";
        public const string ObserverManagerConfigurationSectionName = "ObserverManagerConfiguration";
        public const string ObserverWebApiAppDeployed = "ObserverWebApiEnabled";
        public const string EnableVerboseLoggingParameter = "EnableVerboseLogging";
        public const string EnableLongRunningCSVLogging = "EnableLongRunningCSVLogging";
        public const string ObserverLogPath = "ObserverLogPath";
        public const string ObserverRunIntervalParameterName = "RunInterval";
        public const string ObserverEnabled = "Enabled";
        public const string AIKey = "AppInsightsInstrumentationKey";
        public const string TelemetryEnabled = "EnableTelemetryProvider";
        public const string AsyncClusterOperationTimeoutSeconds = "ClusterOperationTimeoutSeconds";

        // The name of the package that contains this Observer's configuration.
        public const string ObserverConfigurationPackageName = "Config";

        // Setting name for Runtime frequency of the Observer
        public const string ObserverLoopSleepTimeSeconds = "ObserverLoopSleepTimeSeconds";

        // Default to 1 minute if frequency is not supplied in config.
        public const int ObserverRunLoopSleepTimeSeconds = 60;

        public const string AllObserversExecutedMessage = "All Observers have been executed.";

        // Setting name for Grace period of shutdown in seconds.
        public const string ObserverShutdownGracePeriodInSeconds = "ObserverShutdownGracePeriodInSeconds";

        // Setting name for Maximum time an observer should run before being considered hung or in some failure state.
        public const string ObserverExecutionTimeout = "ObserverExecutionTimeout";

        // EmitHealthWarningEvaluationDetails.
        public const string EmitHealthWarningEvaluationConfigurationSetting = "EmitHealthWarningEvaluationDetails";

        // ClusterObserver.
        public const string ClusterObserverName = "ClusterObserver";

        // Settings.
        public const string ClusterObserverConfigurationSectionName = "ClusterObserverConfiguration";
        public const string EmitOkHealthState = "EmitOkHealthStateTelemetry";
        public const string IgnoreSystemAppWarnings = "IgnoreFabricSystemAppWarnings";
        public const string EmitHealthStatistics = "EmitHealthStatistics";
    }
}
