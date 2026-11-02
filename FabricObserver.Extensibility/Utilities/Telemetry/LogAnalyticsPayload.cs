// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using FabricObserver.TelemetryLib;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    internal static class LogAnalyticsPayload
    {
        internal static string Health(
            string propertyName,
            HealthState state,
            string unhealthyEvaluations,
            string source,
            string serviceName,
            string instanceName) =>
            JsonConvert.SerializeObject(new
            {
                ClusterInformation.ClusterInfoTuple.ClusterId,
                source,
                property = propertyName,
                healthState = state.ToString(),
                healthEvaluation = unhealthyEvaluations,
                serviceName = serviceName ?? string.Empty,
                instanceName = instanceName ?? string.Empty,
                osPlatform = OperatingSystem.IsWindows() ? "Windows" : "Linux"
            });

        internal static string Metric<T>(string name, T value, string source) =>
            JsonConvert.SerializeObject(new
            {
                id = $"FO_{Guid.NewGuid()}",
                datetime = DateTime.UtcNow,
                ClusterInformation.ClusterInfoTuple.ClusterId,
                source,
                property = name,
                value
            });

        internal static string ClusterUpgrade(ServiceFabricUpgradeEventData eventData) =>
            JsonConvert.SerializeObject(new
            {
                ClusterId = ServiceFabricUpgradeEventData.ClusterId ?? ClusterInformation.ClusterInfoTuple.ClusterId,
                Timestamp = DateTime.UtcNow,
                eventData.OS,
                CurrentUpgradeDomain = eventData.FabricUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName,
                eventData.FabricUpgradeProgress.NextUpgradeDomain,
                UpgradeTargetCodeVersion = eventData.FabricUpgradeProgress.UpgradeDescription?.TargetCodeVersion,
                UpgradeTargetConfigVersion = eventData.FabricUpgradeProgress.UpgradeDescription?.TargetConfigVersion,
                UpgradeState = Enum.GetName(typeof(FabricUpgradeState), eventData.FabricUpgradeProgress.UpgradeState),
                UpgradeDuration = eventData.FabricUpgradeProgress.CurrentUpgradeDomainDuration,
                FailureReason = eventData.FabricUpgradeProgress.FailureReason.HasValue
                    ? Enum.GetName(typeof(UpgradeFailureReason), eventData.FabricUpgradeProgress.FailureReason.Value)
                    : null
            });

        internal static string ApplicationUpgrade(ServiceFabricUpgradeEventData eventData) =>
            JsonConvert.SerializeObject(new
            {
                ClusterId = ServiceFabricUpgradeEventData.ClusterId ?? ClusterInformation.ClusterInfoTuple.ClusterId,
                Timestamp = DateTime.UtcNow,
                eventData.OS,
                ApplicationName = eventData.ApplicationUpgradeProgress.ApplicationName?.OriginalString,
                CurrentUpgradeDomain = eventData.ApplicationUpgradeProgress.CurrentUpgradeDomainProgress?.UpgradeDomainName,
                eventData.ApplicationUpgradeProgress.NextUpgradeDomain,
                UpgradeTargetAppTypeVersion = eventData.ApplicationUpgradeProgress.UpgradeDescription?.TargetApplicationTypeVersion,
                UpgradeState = Enum.GetName(typeof(FabricUpgradeState), eventData.ApplicationUpgradeProgress.UpgradeState),
                UpgradeDuration = eventData.ApplicationUpgradeProgress.CurrentUpgradeDomainDuration,
                FailureReason = eventData.ApplicationUpgradeProgress.FailureReason.HasValue
                    ? Enum.GetName(typeof(UpgradeFailureReason), eventData.ApplicationUpgradeProgress.FailureReason.Value)
                    : null
            });
    }
}
