// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Newtonsoft.Json;

namespace FabricObserver.TelemetryLib
{
    /// <summary>
    /// Contains common telemetry events
    /// </summary>
    public class TelemetryEvents : IDisposable
    {
        private const string EventName = "InternalObserverData";
        private const string TaskName = "FabricObserver";
        private readonly TelemetryClient telemetryClient;
        private readonly ServiceContext serviceContext;
        private readonly ITelemetryEventSource serviceEventSource;
        private readonly string clusterId, tenantId, clusterType;
        private readonly TimeSpan observerDataEventRunInterval;
        private readonly TelemetryConfiguration appInsightsTelemetryConf;

        public TelemetryEvents(
            FabricClient fabricClient,
            ServiceContext context,
            ITelemetryEventSource eventSource,
            TimeSpan runInterval,
            CancellationToken token)
        {
            serviceEventSource = eventSource;
            serviceContext = context;
            observerDataEventRunInterval = runInterval;
            string config = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "InternalApplicationInsights.config"));
            appInsightsTelemetryConf = TelemetryConfiguration.CreateFromConfiguration(config);
            appInsightsTelemetryConf.TelemetryChannel.EndpointAddress = TelemetryConstants.TelemetryEndpoint;
            telemetryClient = new TelemetryClient(appInsightsTelemetryConf);
            var (ClusterId, TenantId, ClusterType) = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, token).GetAwaiter().GetResult();
            clusterId = ClusterId;
            tenantId = TenantId;
            clusterType = ClusterType;
        }

        public bool EmitFabricObserverTelemetryEvent(FabricObserverInternalTelemetryData foData)
        {
            if (!telemetryClient.IsEnabled())
            {
                return false;
            }

            // This means that the token replacement did not take place and this is not an 
            // SFPKG/NUPKG signed Release build of FO. So, don't do anything.
            if (TelemetryConstants.AppInsightsInstrumentationKey.Contains("Token"))
            {
                return false;
            }

            try
            {
                serviceEventSource.InternalFODataEvent(JsonConvert.SerializeObject(foData));

                string nodeHashString = string.Empty;
                int nodeNameHash = serviceContext?.NodeContext.NodeName.GetHashCode() ?? -1;

                if (nodeNameHash != -1)
                {
                    nodeHashString = ((uint)nodeNameHash).ToString();
                }

                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", EventName},
                    { "TaskName", TaskName},
                    { "EventRunInterval", observerDataEventRunInterval.ToString() },
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "TenantId", tenantId },
                    { "NodeNameHash",  nodeHashString },
                    { "FOVersion", foData.Version },
                    { "UpTime", foData.UpTime.ToString() },
                    { "EnabledObserverCount", foData.EnabledObserverCount.ToString() },
                    { "Timestamp", DateTime.UtcNow.ToString("o") }
                };

                IDictionary<string, double> metrics = new Dictionary<string, double>
                {
                    // Target app/service counts for enabled observers that monitor service processes.
                    { "AppObserverTotalApps", foData.ObserverData.Any(o => o.ObserverName == "AppObserver") ? ((AppServiceObserverData)foData.ObserverData.Find(o => o.ObserverName == "AppObserver")).MonitoredAppCount : 0 },
                    { "AppObserverTotalServices", foData.ObserverData.Any(o => o.ObserverName == "AppObserver") ? ((AppServiceObserverData)foData.ObserverData.Find(o => o.ObserverName == "AppObserver")).MonitoredServiceCount : 0 },
                    { "FabricSystemObserverTotalServices", foData.ObserverData.Any(o => o.ObserverName == "FabricSystemObserver") ? ((AppServiceObserverData)foData.ObserverData.Find(o => o.ObserverName == "FabricSystemObserver")).MonitoredServiceCount : 0 },
                    { "NetworkObserverTotalApps", foData.ObserverData.Any(o => o.ObserverName == "NetworkObserver") ? ((AppServiceObserverData)foData.ObserverData.Find(o => o.ObserverName == "NetworkObserver")).MonitoredAppCount : 0 },
                
                    // Error level health event counts generated by enabled observers.
                    { "AppObserverErrorDetections", foData.ObserverData.Any(o => o.ObserverName == "AppObserver") ? foData.ObserverData.Find(o => o.ObserverName == "AppObserver").ErrorCount : 0  },
                    { "CertificateObserverErrorDetections", foData.ObserverData.Any(o => o.ObserverName == "CertificateObserver") ? foData.ObserverData.Find(o => o.ObserverName == "CertificateObserver").ErrorCount : 0  },
                    { "DiskObserverErrorDetections", foData.ObserverData.Any(o => o.ObserverName == "DiskObserver") ?  foData.ObserverData.Find(o => o.ObserverName == "DiskObserver").ErrorCount : 0  },
                    { "FabricSystemObserverErrorDetections", foData.ObserverData.Any(o => o.ObserverName == "FabricSystemObserver") ? foData.ObserverData.Find(o => o.ObserverName == "FabricSystemObserver").ErrorCount : 0  },
                    { "NetworkObserverErrorDetections", foData.ObserverData.Any(o => o.ObserverName == "NetworkObserver") ? foData.ObserverData.Find(o => o.ObserverName == "NetworkObserver").ErrorCount : 0  },
                    { "NodeObserverErrorDetections", foData.ObserverData.Any(o => o.ObserverName == "NodeObserver") ? foData.ObserverData.Find(o => o.ObserverName == "NodeObserver").ErrorCount : 0  },
                    { "OSObserverErrorDetections", foData.ObserverData.Any(o => o.ObserverName == "OSObserver") ? foData.ObserverData.Find(o => o.ObserverName == "OSObserver").ErrorCount : 0  },
                
                    // Warning level health event counts generated by enabled observers.
                    { "AppObserverWarningDetections", foData.ObserverData.Any(o => o.ObserverName == "AppObserver") ? foData.ObserverData.Find(o => o.ObserverName == "AppObserver").WarningCount : 0  },
                    { "CertificateObserverWarningDetections", foData.ObserverData.Any(o => o.ObserverName == "CertificateObserver") ? foData.ObserverData.Find(o => o.ObserverName == "CertificateObserver").WarningCount : 0  },
                    { "DiskObserverWarningDetections", foData.ObserverData.Any(o => o.ObserverName == "DiskObserver") ? foData.ObserverData.Find(o => o.ObserverName == "DiskObserver").WarningCount : 0  },
                    { "FabricSystemObserverWarningDetections", foData.ObserverData.Any(o => o.ObserverName == "FabricSystemObserver") ? foData.ObserverData.Find(o => o.ObserverName == "FabricSystemObserver").WarningCount : 0  },
                    { "NetworkObserverWarningDetections", foData.ObserverData.Any(o => o.ObserverName == "NetworkObserver") ? foData.ObserverData.Find(o => o.ObserverName == "NetworkObserver").WarningCount : 0  },
                    { "NodeObserverWarningDetections", foData.ObserverData.Any(o => o.ObserverName == "NodeObserver") ? foData.ObserverData.Find(o => o.ObserverName == "NodeObserver").WarningCount : 0  },
                    { "OSObserverWarningDetections", foData.ObserverData.Any(o => o.ObserverName == "OSObserver") ? foData.ObserverData.Find(o => o.ObserverName == "OSObserver").WarningCount : 0  }
                };

                telemetryClient?.TrackEvent($"{TaskName}.{EventName}", eventProperties, metrics);
                telemetryClient?.Flush();

                // allow time for flushing
                Thread.Sleep(1000);
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
            telemetryClient?.Flush();

            // allow time for flushing.
            Thread.Sleep(1000);
            appInsightsTelemetryConf?.Dispose();
        }
    }
}