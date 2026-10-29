// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.Linq;
using FabricObserver.Observers.Interfaces;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    /// <summary>
    /// Selects which Log Analytics API(s) receive telemetry.
    /// </summary>
    internal enum LogAnalyticsWriteMode
    {
        LegacyOnly,
        DualWrite,
        LogsIngestionOnly
    }

    /// <summary>
    /// Reads and validates the Log Analytics settings for the selected write mode and builds the matching sender.
    /// Only settings for the selected API(s) are validated; settings for an unselected API are ignored.
    /// </summary>
    internal sealed class LogAnalyticsConfiguration
    {
        public const string WriteModeHealthProperty = "LogAnalyticsConfiguration";
        public const string LegacyHealthProperty = "LogAnalyticsLegacyConfiguration";
        public const string IngestionHealthProperty = "LogAnalyticsIngestionConfiguration";

        private static readonly string[] HealthProperties = [WriteModeHealthProperty, LegacyHealthProperty, IngestionHealthProperty];

        private string workspaceId;
        private string sharedKey;
        private string logType;
        private Uri ingestionEndpoint;
        private string dcrImmutableId;
        private string streamName;
        private string managedIdentityClientId;

        private LogAnalyticsConfiguration()
        {
        }

        /// <summary>
        /// The selected write mode, or null when the configured value is not recognized.
        /// </summary>
        public LogAnalyticsWriteMode? WriteMode { get; private set; }

        public string WriteModeProblem { get; private set; }

        public string LegacyProblem { get; private set; }

        public string IngestionProblem { get; private set; }

        private bool UsesLegacyApi => WriteMode is LogAnalyticsWriteMode.LegacyOnly or LogAnalyticsWriteMode.DualWrite;

        private bool UsesIngestionApi => WriteMode is LogAnalyticsWriteMode.LogsIngestionOnly or LogAnalyticsWriteMode.DualWrite;

        /// <param name="getSetting">Returns the value of an ObserverManagerConfiguration parameter, or null when it is not set.</param>
        /// <param name="defaultLogType">Data Collector API log type used when LogAnalyticsLogType is not set.</param>
        public static LogAnalyticsConfiguration Read(Func<string, string> getSetting, string defaultLogType = null)
        {
            ArgumentNullException.ThrowIfNull(getSetting);
            var configuration = new LogAnalyticsConfiguration();
            string writeMode = getSetting(ObserverConstants.LogAnalyticsWriteModeParameter)?.Trim();

            if (string.IsNullOrEmpty(writeMode))
            {
                configuration.WriteMode = LogAnalyticsWriteMode.LegacyOnly;
            }
            else
            {
                // Match names only: Enum.TryParse would also accept numeric values such as "1".
                string name = Enum.GetNames<LogAnalyticsWriteMode>().FirstOrDefault(n => string.Equals(n, writeMode, StringComparison.OrdinalIgnoreCase));

                if (name != null)
                {
                    configuration.WriteMode = Enum.Parse<LogAnalyticsWriteMode>(name);
                }
                else
                {
                    configuration.WriteModeProblem =
                        $"{ObserverConstants.LogAnalyticsWriteModeParameter} value '{writeMode}' is not recognized. " +
                        $"Supported values: {string.Join(", ", Enum.GetNames<LogAnalyticsWriteMode>())}. " +
                        "Nothing is sent to Log Analytics until the value is corrected.";
                    return configuration;
                }
            }

            if (configuration.UsesLegacyApi)
            {
                configuration.ReadLegacySettings(getSetting, defaultLogType);
            }

            if (configuration.UsesIngestionApi)
            {
                configuration.ReadIngestionSettings(getSetting);
            }

            return configuration;
        }

        /// <summary>
        /// Creates a sender for every selected API whose settings are valid.
        /// </summary>
        /// <returns>The sender, a dual-write sender when both APIs are usable, or null when no selected API is usable.</returns>
        public ITelemetryProvider CreateTelemetryProvider()
        {
            ITelemetryProvider legacySender = UsesLegacyApi && LegacyProblem == null
                ? new LogAnalyticsTelemetry(workspaceId, sharedKey, logType)
                : null;

            ITelemetryProvider ingestionSender = UsesIngestionApi && IngestionProblem == null
                ? new LogsIngestionTelemetry(ingestionEndpoint, dcrImmutableId, streamName, managedIdentityClientId)
                : null;

            if (legacySender != null && ingestionSender != null)
            {
                return new DualWriteTelemetry(legacySender, ingestionSender);
            }

            return legacySender ?? ingestionSender;
        }

        /// <summary>
        /// Reports a Service Fabric health event for each Log Analytics configuration property: Warning (and a local warning log)
        /// while a problem exists, otherwise Ok so a warning from an earlier configuration or process instance is cleared.
        /// </summary>
        /// <param name="configuration">The evaluated configuration, or null when Log Analytics is not the active telemetry provider.</param>
        public static void ReportHealth(
            LogAnalyticsConfiguration configuration,
            Action<HealthReport> reportHealth,
            Logger logger,
            Uri serviceName,
            string nodeName,
            string sourceId)
        {
            ArgumentNullException.ThrowIfNull(reportHealth);

            foreach (string property in HealthProperties)
            {
                string problem = configuration?.GetProblem(property);

                if (problem != null)
                {
                    logger?.LogWarning("{0}", problem);
                }

                reportHealth(new HealthReport
                {
                    EntityType = EntityType.Service,
                    ServiceName = serviceName,
                    NodeName = nodeName,
                    SourceId = sourceId,
                    Property = property,
                    State = problem != null ? HealthState.Warning : HealthState.Ok,
                    HealthMessage = problem ?? $"{property}: no Log Analytics configuration problem detected.",

                    // A configuration warning stays until the configuration changes; the Ok report uses the default TTL and is then removed.
                    HealthReportTimeToLive = problem != null ? TimeSpan.MaxValue : default,

                    // Logged above so the local warning does not depend on how the report is delivered.
                    EmitLogEvent = false
                });
            }
        }

        private string GetProblem(string property) => property switch
        {
            WriteModeHealthProperty => WriteModeProblem,
            LegacyHealthProperty => LegacyProblem,
            IngestionHealthProperty => IngestionProblem,
            _ => null
        };

        private void ReadLegacySettings(Func<string, string> getSetting, string defaultLogType)
        {
            workspaceId = getSetting(ObserverConstants.LogAnalyticsWorkspaceIdParameter);
            sharedKey = getSetting(ObserverConstants.LogAnalyticsSharedKeyParameter);
            logType = getSetting(ObserverConstants.LogAnalyticsLogTypeParameter) ?? defaultLogType;
            var missing = new List<string>();

            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                missing.Add(ObserverConstants.LogAnalyticsWorkspaceIdParameter);
            }

            if (string.IsNullOrWhiteSpace(sharedKey))
            {
                missing.Add(ObserverConstants.LogAnalyticsSharedKeyParameter);
            }

            if (missing.Count > 0)
            {
                // Name the settings only. Never include the shared key value.
                LegacyProblem =
                    $"Log Analytics Data Collector API (old API) is selected by {ObserverConstants.LogAnalyticsWriteModeParameter}={WriteMode}, " +
                    $"but these settings are missing: {string.Join(", ", missing)}. Nothing is sent through this API until they are set.";
            }
        }

        private void ReadIngestionSettings(Func<string, string> getSetting)
        {
            string endpoint = getSetting(ObserverConstants.LogAnalyticsIngestionEndpointParameter)?.Trim();
            dcrImmutableId = getSetting(ObserverConstants.LogAnalyticsDcrImmutableIdParameter)?.Trim();
            streamName = getSetting(ObserverConstants.LogAnalyticsStreamNameParameter)?.Trim();
            managedIdentityClientId = getSetting(ObserverConstants.LogAnalyticsManagedIdentityClientIdParameter)?.Trim();
            var invalid = new List<string>();

            // Only check what is needed to create the sender. Azure rejects wrong values, and upload failures are reported per API.
            if (string.IsNullOrEmpty(endpoint))
            {
                invalid.Add($"{ObserverConstants.LogAnalyticsIngestionEndpointParameter} (missing)");
            }
            else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out ingestionEndpoint))
            {
                invalid.Add($"{ObserverConstants.LogAnalyticsIngestionEndpointParameter} (not an absolute URL)");
            }

            if (string.IsNullOrEmpty(dcrImmutableId))
            {
                invalid.Add($"{ObserverConstants.LogAnalyticsDcrImmutableIdParameter} (missing)");
            }

            if (string.IsNullOrEmpty(streamName))
            {
                invalid.Add($"{ObserverConstants.LogAnalyticsStreamNameParameter} (missing)");
            }

            if (string.IsNullOrEmpty(managedIdentityClientId))
            {
                invalid.Add($"{ObserverConstants.LogAnalyticsManagedIdentityClientIdParameter} (missing)");
            }

            if (invalid.Count > 0)
            {
                IngestionProblem =
                    $"Log Analytics Logs Ingestion API (new API) is selected by {ObserverConstants.LogAnalyticsWriteModeParameter}={WriteMode}, " +
                    $"but these settings are missing or invalid: {string.Join(", ", invalid)}. Nothing is sent through this API until they are corrected.";
            }
        }
    }
}
