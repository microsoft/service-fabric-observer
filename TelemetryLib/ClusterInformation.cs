using System;
using System.Fabric;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32;

namespace Microsoft.ServiceFabric.TelemetryLib
{
    /// <summary>
    /// Helper class to facilitate non-PII identification of cluster...
    /// </summary>
    public class ClusterIdentificationUtility : IDisposable
    {
        private const string FabricRegistryKeyPath = "Software\\Microsoft\\Service Fabric";
        private string paasClusterId;
        private string diagnosticsClusterId;
        private StringReader sreader = null;
        private readonly XmlDocument xdoc = null;
        private XmlReader xreader = null;

        public ClusterIdentificationUtility(FabricClient fabricClient)
        {
            Task<string> task = fabricClient.ClusterManager.GetClusterManifestAsync();
            task.Wait();
            string clusterManifest = task.Result;

            // Safe XML pattern - *Do not use LoadXml*...
            this.xdoc = new XmlDocument { XmlResolver = null };
            this.sreader = new StringReader(clusterManifest);
            this.xreader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null });

            xdoc?.Load(xreader);
        }

        public void GetValuesFromClusterManifest()
        {
            this.paasClusterId = this.GetClusterIdFromPaasSection();
            this.diagnosticsClusterId = this.GetClusterIdFromDiagnosticsSection();
        }

        /// <summary>
        /// Get the value of a parameter inside a section from cluster manifest
        /// </summary>
        /// <param name="sectionName"></param>
        /// <param name="parameterName"></param>
        /// <returns></returns>
        public string GetParamValueFromSection(string sectionName, string parameterName)
        {
            XmlNode sectionNode = this.xdoc.DocumentElement?.SelectSingleNode("//*[local-name()='Section' and @Name='" + sectionName + "']");
            XmlNode parameterNode = sectionNode?.SelectSingleNode("//*[local-name()='Parameter' and @Name='" + parameterName + "']");
            XmlAttribute attr = parameterNode?.Attributes?["Value"];
            
            return attr?.Value;
        }

        private static string GetTenantId()
        {
            const string TenantIdValueName = "WATenantID";
            string tenantIdKeyName = string.Format(CultureInfo.InvariantCulture, "{0}\\{1}", Registry.LocalMachine.Name, FabricRegistryKeyPath);

            return (string)Registry.GetValue(tenantIdKeyName, TenantIdValueName, null);
        }

        /// <summary>
        /// Gets ClusterID, tenantID and ClusterType for current ServiceFabric cluster
        /// The logic to compute these values closely resembles the logic used in SF runtime's telemetry client.
        /// </summary>
        /// <param name="clusterId">Cluster ID for current SF cluster</param>
        /// <param name="tenantId">Tenant ID for current SF cluster</param>
        /// <param name="clusterType">Type of SF cluster viz. standalone, SFRP, PaasV1</param>
        public void GetClusterIdAndType(
            out string clusterId, 
            out string tenantId, 
            out string clusterType)
        {
            // Get values from cluster manifest, viz. clusterId if it exists in either Paas or Diagnostics section
            this.GetValuesFromClusterManifest();

            // Get tenantId for PaasV1 clusters or SFRP
            tenantId = GetTenantId() ?? TelemetryConstants.Undefined;

            if (null != this.paasClusterId)
            {
                clusterId = this.paasClusterId;
                clusterType = TelemetryConstants.ClusterTypeSfrp;
                return;
            }

            // try to find TenantId in Registry for PaasV1 clusters
            if (TelemetryConstants.Undefined != tenantId)
            {
                clusterId = tenantId;
                clusterType = TelemetryConstants.ClusterTypePaasV1;
                return;
            }

            if (null != this.diagnosticsClusterId)
            {
                clusterId = this.diagnosticsClusterId;
                clusterType = TelemetryConstants.ClusterTypeStandalone;
                return;
            }

            // Cluster id and type undefined in case it does not match any of the conditions above
            clusterId = TelemetryConstants.Undefined;
            clusterType = TelemetryConstants.Undefined;
        }

        private string GetClusterIdFromPaasSection()
        {
            return this.GetParamValueFromSection("Paas", "ClusterId");
        }

        private string GetClusterIdFromDiagnosticsSection()
        {
            return this.GetParamValueFromSection("Diagnostics", "ClusterId");
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (this.sreader != null)
                    {
                        this.sreader.Dispose();
                        this.sreader = null;
                    }

                    if (this.xreader != null)
                    {
                        this.xreader.Dispose();
                        this.xreader = null;
                    }
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion

    }
}
