// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Newtonsoft.Json;

namespace FabricObserver.TelemetryLib
{
    /// <summary>
    /// Contains common FabricObserver telemetry events
    /// </summary>
    public class TelemetryEvents : IDisposable
    {
        private const string OperationalEventName = "OperationalEvent";
        private const string CriticalErrorEventName = "CriticalErrorEvent";
        private const string COTaskName = "ClusterObserver";
        private const string FOTaskName = "FabricObserver";
        private readonly TelemetryClient telemetryClient;
        private readonly string clusterId, tenantId, clusterType;
        private readonly TelemetryConfiguration appInsightsTelemetryConf;
        private readonly string nodeName;

        /// <summary>
        /// Creates an instance of the TelemetryEvents class which is used to emit AppInsights telemetry events from a source Fabric node.
        /// </summary>
        /// <param name="sourceNodeName">The name of the node that is the origin of the telemetry.</param>
        public TelemetryEvents(string sourceNodeName)
        {
            nodeName= sourceNodeName;
            appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            appInsightsTelemetryConf.ConnectionString = TelemetryConstants.ConnectionString;
            telemetryClient = new TelemetryClient(appInsightsTelemetryConf);

            // Set instance fields.
            clusterId = ClusterInformation.ClusterInfoTuple.ClusterId;
            tenantId = ClusterInformation.ClusterInfoTuple.TenantId;
            clusterType = ClusterInformation.ClusterInfoTuple.ClusterType;
        }

        public bool EmitFabricObserverOperationalEvent(FabricObserverOperationalEventData foData, TimeSpan runInterval, string logFilePath)
        {
            if (telemetryClient == null || !telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                _ = TryGetHashStringSha256(nodeName, out string nodeHashString);

                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", OperationalEventName},
                    { "TaskName", FOTaskName},
                    { "EventRunInterval", runInterval.ToString() },
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "NodeNameHash", nodeHashString },
                    { "FOVersion", foData.Version },
                    { "SFRuntimeVersion", foData.SFRuntimeVersion },
                    { "HasPlugins", foData.HasPlugins.ToString() },
                    { "ParallelCapable", foData.ParallelExecutionCapable.ToString() },
                    { "UpTime", foData.UpTime },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", foData.OS }
                };

                if (eventProperties.TryGetValue("ClusterType", out string clustType))
                {
                    if (clustType != TelemetryConstants.ClusterTypeSfrp)
                    {
                        eventProperties.Add("TenantId", tenantId);
                    }
                }

                IDictionary<string, double> metrics = new Dictionary<string, double>
                {
                    { "EnabledObserverCount", foData.EnabledObserverCount }
                };

                const string err = "ErrorDetections";
                const string warn = "WarningDetections";
                const string apps = "TotalMonitoredApps";
                const string procs = "TotalMonitoredServiceProcesses";
                const string conts = "TotalMonitoredContainers";
                const string parallel = "ConcurrencyEnabled";
                const string appobs = "AppObserver";
                const string contobs = "ContainerObserver";
                const string fsobs = "FabricSystemObserver";
                const string netobs = "NetworkObserver";
                const string azobs = "AzureStorageUploadObserver";
                const string sfobs = "SFConfigurationObserver";

                foreach (var obData in foData.ObserverData)
                {
                    double data = 0;
                    string key;

                    // These observers monitor app services/containers and therefore the ServiceData property will not be null.
                    if (obData.Key == appobs || obData.Key == fsobs || obData.Key == netobs || obData.Key == contobs)
                    {
                        var serviceData = obData.Value.ServiceData;

                        if (serviceData == null)
                        {
                            continue;
                        }

                        // App count.
                        data = serviceData.MonitoredAppCount;

                        // if this value is 0, then this data has already been transmitted. Don't send it again.
                        if (data > 0)
                        {
                            bool addMetric = true;

                            // Since we log the telemetry data to disk, check to make sure we don't send the same data again across FO restarts if the data has not changed.
                            if (File.Exists(logFilePath) && TryDeserializeFOEventData(File.ReadAllText(logFilePath), out FabricObserverOperationalEventData foEventDataFromLogFile))
                            {
                                if (foEventDataFromLogFile.ObserverData != null && foEventDataFromLogFile.ObserverData.ContainsKey(obData.Key))
                                {
                                    if (foEventDataFromLogFile?.ObserverData != null && foEventDataFromLogFile?.ObserverData[obData.Key]?.ServiceData?.MonitoredAppCount == data)
                                    {
                                        addMetric = false;
                                    }
                                }
                            }

                            if (addMetric)
                            {
                                key = $"{obData.Key}{apps}";
                                metrics.Add(key, data);
                            }
                        }

                        // Process (service instance/primary replica/container) count.
                        data = serviceData.MonitoredServiceProcessCount;

                        // if this value is 0, then this data has already been transmitted. Don't send it again.
                        if (data > 0)
                        {
                            bool addMetric = true;

                            // Since we log the telemetry data to disk, check to make sure we don't send the same data again across FO restarts if the data has not changed.
                            if (File.Exists(logFilePath) && TryDeserializeFOEventData(File.ReadAllText(logFilePath), out FabricObserverOperationalEventData foEventDataFromLogFile))
                            {
                                if (foEventDataFromLogFile.ObserverData != null && foEventDataFromLogFile.ObserverData.ContainsKey(obData.Key))
                                {
                                    if (foEventDataFromLogFile.ObserverData[obData.Key].ServiceData.MonitoredServiceProcessCount == data)
                                    {
                                        addMetric = false;
                                    }
                                }
                            }

                            if (addMetric)
                            {
                                key = $"{obData.Key}{procs}";

                                if (obData.Key == contobs)
                                {
                                    key = $"{obData.Key}{conts}";
                                }

                                metrics.Add(key, data);
                            }
                        }
                    }

                    // Concurrency
                    if (obData.Key == appobs || obData.Key == contobs)
                    {
                        data = obData.Value.ServiceData.ConcurrencyEnabled ? 1 : 0;
                        key = $"{obData.Key}{parallel}";
                        metrics.Add(key, data);
                    }

                    // AzureStorage and SFConfig observers do not generate health events.
                    if (obData.Key == azobs || obData.Key == sfobs)
                    {
                        key = $"{obData.Key}Enabled";
                        data = 1; // Enabled.
                        metrics.Add(key, data);
                    }
                    else
                    {
                        // Observer-created Error count
                        key = $"{obData.Key}{err}";
                        data = obData.Value.ErrorCount;
                        metrics.Add(key, data);

                        // Observer-created Warning count
                        key = $"{obData.Key}{warn}";
                        data = obData.Value.WarningCount;
                        metrics.Add(key, data);
                    }
                }

                telemetryClient.TrackEvent($"{FOTaskName}.{OperationalEventName}", eventProperties, metrics);
                telemetryClient.Flush();

                // Allow time for flushing
                Thread.Sleep(1000);

                // Audit log.
                _ = TryWriteLogFile(logFilePath, JsonConvert.SerializeObject(foData));
                
                eventProperties.Clear();
                eventProperties = null;
                metrics.Clear();
                metrics = null;

                return true;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FO.
                _ = TryWriteLogFile(logFilePath, $"{e}");
            }

            return false;
        }

        public bool EmitCriticalErrorEvent(CriticalErrorEventData errorData, string source, string logFilePath)
        {
            if (telemetryClient == null || !telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", CriticalErrorEventName},
                    { "TaskName", source},
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "TenantId", tenantId },
                    { source == FOTaskName ? "FOVersion" : "COVersion", errorData.Version },
                    { "SFRuntimeVersion", errorData.SFRuntimeVersion },
                    { "CrashTime", errorData.CrashTime },
                    { "ErrorMessage", errorData.ErrorMessage },
                    { "Stack", errorData.ErrorStack },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", errorData.OS }
                };

                string nodeHashString = string.Empty;

                if (source == FOTaskName)
                {
                    _ = TryGetHashStringSha256(nodeName, out nodeHashString);
                    eventProperties.Add("NodeNameHash", nodeHashString);
                }

                telemetryClient.TrackEvent($"{source}.{CriticalErrorEventName}", eventProperties);
                telemetryClient.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                _ = TryWriteLogFile(logFilePath, JsonConvert.SerializeObject(errorData));

                return true;
            }
            catch
            {
                // Telemetry is non-critical and should not take down FO.
            }

            return false;
        }

        public void Dispose()
        {
            telemetryClient.Flush();

            // allow time for flushing.
            Thread.Sleep(1000);
            appInsightsTelemetryConf?.Dispose();
        }

        const int Retries = 4;

        private bool TryWriteLogFile(string path, string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            for (var i = 0; i < Retries; i++)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);

                    if (!Directory.Exists(directory))
                    {
                        if (directory != null)
                        {
                            _ = Directory.CreateDirectory(directory);
                        }
                    }

                    File.WriteAllText(path, content);
                    return true;
                }
                catch
                {

                }

                Thread.Sleep(1000);
            }

            return false;
        }

        public bool EmitClusterObserverOperationalEvent(ClusterObserverOperationalEventData eventData, string logFilePath)
        {
            if (telemetryClient == null || !telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", OperationalEventName},
                    { "TaskName", COTaskName},
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "COVersion", eventData.Version },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", eventData.OS }
                };

                if (eventProperties.TryGetValue("ClusterType", out string clustType))
                {
                    if (clustType != TelemetryConstants.ClusterTypeSfrp)
                    {
                        eventProperties.Add("TenantId", tenantId);
                    }
                }

                telemetryClient.TrackEvent($"{COTaskName}.{OperationalEventName}", eventProperties);
                telemetryClient.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                string telemetryData = "{" + string.Join(",", eventProperties.Select(kv => $"\"{kv.Key}\":" + $"\"{kv.Value}\"").ToArray()) + "}";
                _ = TryWriteLogFile(logFilePath, telemetryData);

                eventProperties.Clear();
                eventProperties = null;
                return true;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FH.
                _ = TryWriteLogFile(logFilePath, $"{e}");
            }

            return false;
        }


        /// <summary>
        /// Tries to compute sha256 hash of a supplied string and converts the hashed bytes to a string supplied in result.
        /// </summary>
        /// <param name="source">The string to be hashed.</param>
        /// <param name="result">The resulting Sha256 hash string. This will be null if the function returns false.</param>
        /// <returns>true if it can compute supplied string to a Sha256 hash and convert result to a string. false if it can't.</returns>
        public static bool TryGetHashStringSha256(string source, out string result)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                result = null;
                return false;
            }

            try
            {
                StringBuilder Sb = new StringBuilder();

                using (var hash = SHA256.Create())
                {
                    Encoding enc = Encoding.UTF8;
                    byte[] byteVal = hash.ComputeHash(enc.GetBytes(source));

                    foreach (byte b in byteVal)
                    {
                        Sb.Append(b.ToString("x2"));
                    }
                }

                result = Sb.ToString();
                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is EncoderFallbackException || e is FormatException || e is ObjectDisposedException)
            {
                result = null;
                return false;
            }
        }

        private bool TryDeserializeFOEventData(string json, out FabricObserverOperationalEventData obj)
        {
            try
            {
                obj = JsonConvert.DeserializeObject<FabricObserverOperationalEventData>(json);
                return true;
            }
            catch
            {
                obj = null;
                return false;
            }
        }
    }
}