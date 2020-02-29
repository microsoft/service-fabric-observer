// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32;

namespace Microsoft.ServiceFabric.TelemetryLib
{
    /// <summary>
    /// Helper class to facilitate non-PII identification of cluster.
    /// </summary>
    public sealed class ClusterIdentificationUtility
    {
        private const string FabricRegistryKeyPath = "Software\\Microsoft\\Service Fabric";
        private static string paasClusterId;
        private static string diagnosticsClusterId;
        private static XmlDocument clusterManifestXdoc;
  
        /// <summary>
        /// Gets ClusterID, tenantID and ClusterType for current ServiceFabric cluster
        /// The logic to compute these values closely resembles the logic used in SF runtime's telemetry client.
        /// </summary>
        public static async Task<Tuple<string, string, string>> TupleGetClusterIdAndTypeAsync(FabricClient fabricClient, CancellationToken token)
        {
            string clusterManifest = await fabricClient.ClusterManager.GetClusterManifestAsync(
                                        TimeSpan.FromSeconds(TelemetryConstants.AsyncOperationTimeoutSeconds),
                                        token);

            // Get tenantId for PaasV1 clusters or SFRP.
            string tenantId = GetTenantId() ?? TelemetryConstants.Undefined;
            string clusterId = TelemetryConstants.Undefined;
            string clusterType = TelemetryConstants.Undefined;

            if (!string.IsNullOrEmpty(clusterManifest))
            {
                // Safe XML pattern - *Do not use LoadXml*.
                clusterManifestXdoc = new XmlDocument { XmlResolver = null };

                using (var sreader = new StringReader(clusterManifest))
                {
                    using (var xreader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null }))
                    {
                        clusterManifestXdoc?.Load(xreader);

                        // Get values from cluster manifest, clusterId if it exists in either Paas or Diagnostics section.
                        GetValuesFromClusterManifest();

                        if (paasClusterId != null)
                        {
                            clusterId = paasClusterId;
                            clusterType = TelemetryConstants.ClusterTypeSfrp;
                        }
                        else if (tenantId != TelemetryConstants.Undefined)
                        {
                            clusterId = tenantId;
                            clusterType = TelemetryConstants.ClusterTypePaasV1;
                        }
                        else if (diagnosticsClusterId != null)
                        {
                            clusterId = diagnosticsClusterId;
                            clusterType = TelemetryConstants.ClusterTypeStandalone;
                        }
                    }
                }
            }

            return Tuple.Create(clusterId, tenantId, clusterType);
        }

        /// <summary>
        /// Gets the value of a parameter inside a section from the cluster manifest XmlDocument instance (clusterManifestXdoc).
        /// </summary>
        /// <param name="sectionName"></param>
        /// <param name="parameterName"></param>
        /// <returns></returns>
        private static string GetParamValueFromSection(string sectionName, string parameterName)
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

        private static string GetClusterIdFromPaasSection()
        {
            return GetParamValueFromSection("Paas", "ClusterId");
        }

        private static string GetClusterIdFromDiagnosticsSection()
        {
            return GetParamValueFromSection("Diagnostics", "ClusterId");
        }

        private static string GetTenantId()
        {
            const string TenantIdValueName = "WATenantID";
            string tenantIdKeyName = string.Format(CultureInfo.InvariantCulture, "{0}\\{1}", Registry.LocalMachine.Name, FabricRegistryKeyPath);

            return (string)Registry.GetValue(tenantIdKeyName, TenantIdValueName, null);
        }

        private static void GetValuesFromClusterManifest()
        {
            paasClusterId = GetClusterIdFromPaasSection();
            diagnosticsClusterId = GetClusterIdFromDiagnosticsSection();
        }
    }
}
