// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
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
        private readonly ServiceContext serviceContext;
        private readonly string clusterId, tenantId, clusterType;
        private readonly TelemetryConfiguration appInsightsTelemetryConf;

        public TelemetryEvents(FabricClient fabricClient, ServiceContext context, CancellationToken token)
        {
            serviceContext = context;
            appInsightsTelemetryConf = TelemetryConfiguration.CreateDefault();
            appInsightsTelemetryConf.ConnectionString = TelemetryConstants.ConnectionString;

            // Attempt to filter and block transmission from restricted regions/clouds. Note, it is really the user's responsibilty to prevent internal diagnostics from being
            // sent from restricted regions (China) and clouds (Gov) by simply disabling the feature (ObserverManagerEnableOperationalFOTelemetry setting) before deploying FO to the data-restricted location.
            var telemetryProcessorChainBuilder = appInsightsTelemetryConf.DefaultTelemetrySink.TelemetryProcessorChainBuilder.Use((next) => new RestrictedCloudFilter(next));
            telemetryProcessorChainBuilder.Build();
            telemetryClient = new TelemetryClient(appInsightsTelemetryConf);

            var (ClusterId, TenantId, ClusterType) = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, token).GetAwaiter().GetResult();
            clusterId = ClusterId;
            tenantId = TenantId;
            clusterType = ClusterType;
        }

        public bool EmitFabricObserverOperationalEvent(FabricObserverOperationalEventData foData, TimeSpan runInterval, string logFilePath)
        {
            if (telemetryClient == null || !telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                _ = TryGetHashStringSha256(serviceContext?.NodeContext.NodeName, out string nodeHashString);

                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", OperationalEventName},
                    { "TaskName", FOTaskName},
                    { "EventRunInterval", runInterval.ToString() },
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "NodeNameHash", nodeHashString },
                    { "FOVersion", foData.Version },
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
                    if (obData.Key == appobs || obData.Key == fsobs || obData.Key == contobs)
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

                // allow time for flushing
                Thread.Sleep(1000);

                // Write a local log file containing the data that was sent to MS.
                // This file is also used to prevent redundant service data from being transmitted more than once.
                if (RestrictedCloudFilter.IsRestricted == false)
                {
                    _ = TryWriteLogFile(logFilePath, JsonConvert.SerializeObject(foData));
                }

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
                    { "FOVersion", errorData.Version },
                    { "CrashTime", errorData.CrashTime },
                    { "ErrorMessage", errorData.ErrorMessage },
                    { "CrashData", errorData.ErrorStack },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", errorData.OS }
                };

                string nodeHashString = string.Empty;

                if (source == FOTaskName)
                {
                    _ = TryGetHashStringSha256(serviceContext.NodeContext.NodeName, out nodeHashString);
                    eventProperties.Add("NodeNameHash", nodeHashString);
                }

                telemetryClient.TrackEvent($"{FOTaskName}.{CriticalErrorEventName}", eventProperties);
                telemetryClient.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                // write a local log file containing the exact information sent to MS \\
                if (RestrictedCloudFilter.IsRestricted == false)
                {
                    _ = TryWriteLogFile(logFilePath, JsonConvert.SerializeObject(errorData));
                }

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

                // write a local log file containing the exact information sent to MS \\
                if (RestrictedCloudFilter.IsRestricted == false)
                {
                    string telemetryData = "{" + string.Join(",", eventProperties.Select(kv => $"\"{kv.Key}\":" + $"\"{kv.Value}\"").ToArray()) + "}";
                    _ = TryWriteLogFile(logFilePath, telemetryData);
                }

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

    internal class RestrictedCloudFilter : ITelemetryProcessor
    {
        private ITelemetryProcessor Next { get; set; }

        public static bool IsRestricted { get; set; } = false;

        // next will point to the next TelemetryProcessor in the chain.
        public RestrictedCloudFilter(ITelemetryProcessor next)
        {
            Next = next;
        }

        public void Process(ITelemetry item)
        {
            // To filter out an item, return without calling the next processor.
            if (!SafetoSend(item)) 
            {
                IsRestricted = true;
                return; 
            }

            Next.Process(item);
        }

#pragma warning disable IDE0060 // Remove unused parameter: This parameter is required by interface definition.
        private bool SafetoSend(ITelemetry item)
#pragma warning restore IDE0060 // Remove unused parameter
        {
            IPHostEntry hostEntry;
            int resolvedHostCount = 0;

            try
            {
                var localMachine = Dns.GetHostName();
                hostEntry = Dns.GetHostEntry(localMachine);
            }
            catch (SocketException)
            {
                // Can't figure this out, so don't send telemetry.
                return false;
            }

            foreach (IPAddress address in hostEntry.AddressList)
            {
                try
                {
                    // IPV4-only...
                    if (address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var host = Dns.GetHostEntry(address.ToString());

                    if (host == null)
                    {
                        continue;
                    }

                    resolvedHostCount++;

                    if (host.HostName.Contains(".cn") || host.HostName.Contains(".gov"))
                    {
                        // Do not process the telemetry event. This is a restricted cloud.
                        return false;
                    }
                }
                catch (SocketException)
                {
                    // We get here if the host is not known, which is fine. Try the next address in the list.
                }
            }

            // We couldn't answer the question, so the answer is do not send the telemetry item.
            if (resolvedHostCount == 0)
            {
                return false;
            }

            // One of the resolved IPV4 addresses had to be an actual public IP with a hostname (and without a restricted domain suffix of interest),
            // so if we get here, then we "know" we are probably not running in a restricted cloud.
            return true;
        }
    }
}