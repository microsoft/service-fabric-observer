// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32;

namespace FabricObserver.TelemetryLib
{
    /// <summary>
    /// Helper class to facilitate non-PII identification of cluster.
    /// </summary>
    public sealed class ClusterIdentificationUtility
    {
        private const string FabricRegistryKeyPath = "Software\\Microsoft\\Service Fabric";
        private static string paasClusterId;
        private static string diagnosticsClusterId;

        // No need to set these more than once.
        private static string clusterManifestXml;
        private static string tenantId;
        private static string clusterId;
        private static string clusterType;
  
        /// <summary>
        /// Gets ClusterID, tenantID and ClusterType for current ServiceFabric cluster
        /// The logic to compute these values closely resembles the logic used in SF runtime's telemetry client.
        /// </summary>
        public static async Task<(string ClusterId, string TenantId, string ClusterType)> TupleGetClusterIdAndTypeAsync(FabricClient fabricClient, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(clusterManifestXml))
            {
                clusterManifestXml = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                 () => fabricClient.ClusterManager.GetClusterManifestAsync(
                                                            TimeSpan.FromSeconds(TelemetryConstants.AsyncOperationTimeoutSeconds),
                                                            token), token);
            }

            // Get tenantId for PaasV1 clusters or SFRP.
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                tenantId = GetTenantId() ?? TelemetryConstants.Undefined;
            }

            if (string.IsNullOrWhiteSpace(clusterId) && string.IsNullOrWhiteSpace(clusterType))
            {
                clusterId = TelemetryConstants.Undefined;
                clusterType = TelemetryConstants.Undefined;

                if (string.IsNullOrWhiteSpace(clusterManifestXml))
                {
                    return (TelemetryConstants.Undefined, clusterId, clusterType);
                }

                // Safe XML pattern - *Do not use LoadXml*.
                var clusterManifestXdoc = new XmlDocument { XmlResolver = null };

                using (var sreader = new StringReader(clusterManifestXml))
                {
                    using (var xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null }))
                    {
                        clusterManifestXdoc?.Load(xreader);

                        // Get values from cluster manifest, clusterId if it exists in either Paas or Diagnostics section.
                        GetValuesFromClusterManifest(clusterManifestXdoc);

                        if (!string.IsNullOrWhiteSpace(paasClusterId))
                        {
                            clusterId = paasClusterId;
                            clusterType = TelemetryConstants.ClusterTypeSfrp;
                        }
                        else if (tenantId != TelemetryConstants.Undefined)
                        {
                            clusterId = tenantId;
                            clusterType = TelemetryConstants.ClusterTypePaasV1;
                        }
                        else if (!string.IsNullOrWhiteSpace(diagnosticsClusterId))
                        {
                            clusterId = diagnosticsClusterId;
                            clusterType = TelemetryConstants.ClusterTypeStandalone;
                        }
                    }
                }
            }

            return (clusterId, tenantId, clusterType);
        }

        /// <summary>
        /// Gets the value of a parameter inside a section from the cluster manifest XmlDocument instance (clusterManifestXdoc).
        /// </summary>
        /// <param name="sectionName"></param>
        /// <param name="parameterName"></param>
        /// <returns></returns>
        private static string GetParamValueFromSection(XmlDocument clusterManifestXdoc, string sectionName, string parameterName)
        {
            if (clusterManifestXdoc == null)
            {
                return null;
            }

            XmlNode sectionNode = clusterManifestXdoc.DocumentElement?.SelectSingleNode("//*[local-name()='Section' and @Name='" + sectionName + "']");
            XmlNode parameterNode = sectionNode?.SelectSingleNode("//*[local-name()='Parameter' and @Name='" + parameterName + "']");
            XmlAttribute attr = parameterNode?.Attributes?["Value"];

            return attr?.Value;
        }

        private static string GetClusterIdFromPaasSection(XmlDocument clusterManifestXdoc)
        {
            return GetParamValueFromSection(clusterManifestXdoc, "Paas", "ClusterId");
        }

        private static string GetClusterIdFromDiagnosticsSection(XmlDocument clusterManifestXdoc)
        {
            return GetParamValueFromSection(clusterManifestXdoc, "Diagnostics", "ClusterId");
        }

        private static string GetTenantId()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetTenantIdWindows();
            }

            return GetTenantIdLinux();
        }

        private static string GetTenantIdLinux()
        {
            // Implementation copied from https://github.com/microsoft/service-fabric/blob/master/src/prod/src/managed/DCA/product/host/TelemetryConsumerLinux.cs
            const string TenantIdFile = "/var/lib/waagent/HostingEnvironmentConfig.xml";

            if (!File.Exists(TenantIdFile))
            {
                return null;
            }

            string tenantId;
            var xmlDoc = new XmlDocument { XmlResolver = null };

            using (var xmlReader = XmlReader.Create(TenantIdFile, new XmlReaderSettings { XmlResolver = null }))
            {
                xmlDoc.Load(xmlReader);
            }

            tenantId = xmlDoc.GetElementsByTagName("Deployment").Item(0).Attributes.GetNamedItem("name").Value;
            return tenantId;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string GetTenantIdWindows()
        {
            const string TenantIdValueName = "WATenantID";
            string tenantIdKeyName = string.Format(CultureInfo.InvariantCulture, "{0}\\{1}", Registry.LocalMachine.Name, FabricRegistryKeyPath);

            return (string)Registry.GetValue(tenantIdKeyName, TenantIdValueName, null);
        }

        private static void GetValuesFromClusterManifest(XmlDocument clusterManifestXdoc)
        {
            paasClusterId = GetClusterIdFromPaasSection(clusterManifestXdoc);
            diagnosticsClusterId = GetClusterIdFromDiagnosticsSection(clusterManifestXdoc);
        }
    }
}
