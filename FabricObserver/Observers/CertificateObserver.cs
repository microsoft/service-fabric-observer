using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using FabricObserver.Utilities;

namespace FabricObserver
{
    public class CertificateObserver : ObserverBase
    {
        // By default, CertificateObserver runs once a day, and its reports last a single day
        private const int SECONDSBETWEENRUNS = 100; //86400 = 1 day

        public int DaysUntilClusterExpireWarningThreshold { get; set; }
        public int DaysUntilAppExpireWarningThreshold { get; set; }
        public List<string> appCertificateThumbprintsToObserve { get; set; }
        public List<string> appCertificateCommonNamesToObserve { get; set; }

        public List<string> notFoundWarnings;
        public List<string> expiredWarnings;
        public List<string> expiringWarnings;

        public SecurityConfiguration securityConfiguration;

        public CertificateObserver() : base(ObserverConstants.CertificateObserverName) { }

        public struct SecurityConfiguration
        {
            public SecurityType SecurityType { get; set; }
            public string clusterCertThumbprintOrCommonName { get; set; }
            public string clusterCertSecondaryThumbprint { get; set; }
        }

        public enum SecurityType
        {
            None,
            Thumbprint,
            CommonName,
            Windows
        }

        private async Task Initialize(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var daysUntilClusterExpireWarningThreshold = this.GetSettingParameterValue(
                ObserverConstants.CertificateObserverConfigurationSectionName,
                ObserverConstants.CertificateObserverDaysUntilClusterExpiryWarningThreshold);
            if (!string.IsNullOrEmpty(daysUntilClusterExpireWarningThreshold))
            {
                this.DaysUntilClusterExpireWarningThreshold = int.Parse(daysUntilClusterExpireWarningThreshold);
            }
            else
            {
                this.DaysUntilClusterExpireWarningThreshold = 14;
            }

            var daysUntilAppExpireWarningClusterThreshold = this.GetSettingParameterValue(
            ObserverConstants.CertificateObserverConfigurationSectionName,
            ObserverConstants.CertificateObserverDaysUntilAppExpiryWarningThreshold);
            if (!string.IsNullOrEmpty(daysUntilAppExpireWarningClusterThreshold))
            {
                this.DaysUntilAppExpireWarningThreshold = int.Parse(daysUntilAppExpireWarningClusterThreshold);
            }
            else
            {
                DaysUntilAppExpireWarningThreshold = 14;
            }

            var appThumbprintsToObserve = this.GetSettingParameterValue(
            ObserverConstants.CertificateObserverConfigurationSectionName,
            ObserverConstants.CertificateObserverAppCertificateThumbprints);
            if (!string.IsNullOrEmpty(appThumbprintsToObserve))
            {
                appCertificateThumbprintsToObserve = JsonHelper.ConvertFromString<List<string>>(appThumbprintsToObserve);
            }
            else
            {
                appCertificateThumbprintsToObserve = new List<string>();
            }

            var appCommonNamesToObserve = this.GetSettingParameterValue(
            ObserverConstants.CertificateObserverConfigurationSectionName,
            ObserverConstants.CertificateObserverAppCertificateCommonNames);
            if (!string.IsNullOrEmpty(appThumbprintsToObserve))
            {
                appCertificateCommonNamesToObserve = JsonHelper.ConvertFromString<List<string>>(appCommonNamesToObserve);
            }
            else
            {
                appCertificateCommonNamesToObserve = new List<string>();
            }

            await GetSecurityTypes(token);
        }

        private async Task GetSecurityTypes(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            this.securityConfiguration = new SecurityConfiguration();

            string clusterManifestXml = await this.FabricClientInstance.ClusterManager.GetClusterManifestAsync().ConfigureAwait(true);

            XmlReader xreader = null;
            StringReader sreader = null;
            string ret = null;

            try
            {
                // Safe XML pattern - *Do not use LoadXml*...
                var xdoc = new XmlDocument { XmlResolver = null };
                sreader = new StringReader(clusterManifestXml);
                xreader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null });
                xdoc.Load(xreader);

                // Cluster Information...
                var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");

                // Failover Manager...
                XmlNodeList certificateNode = xdoc.SelectNodes($"//sf:NodeType[@Name='{this.NodeType}']//sf:Certificates", nsmgr);
                if (certificateNode.Count == 0)
                {
                    this.securityConfiguration.SecurityType = SecurityType.None;
                    return;
                }
                else
                {
                    var clusterCertificateNode = certificateNode.Item(0).ChildNodes.Item(0);

                    var commonNameAttribute = clusterCertificateNode.Attributes.GetNamedItem("X509FindType");
                    if (commonNameAttribute != null)
                    {
                        if(commonNameAttribute.Value == "FindBySubjectName")
                        {
                            this.securityConfiguration.SecurityType = SecurityType.CommonName;
                            this.securityConfiguration.clusterCertThumbprintOrCommonName = clusterCertificateNode.Attributes.GetNamedItem("X509FindValue").Value;
                            return;
                        }
                        else
                        {
                            throw new System.ServiceModel.ActionNotSupportedException("if X509FindTime attribute, value should be FindBySubjectName");
                        }
                    }

                    this.securityConfiguration.SecurityType = SecurityType.Thumbprint;
                    this.securityConfiguration.clusterCertThumbprintOrCommonName = clusterCertificateNode.Attributes.GetNamedItem("X509FindValue").Value;
                    var secondaryThumbprintAttribute = clusterCertificateNode.Attributes.GetNamedItem("X509FindValueSecondary");
                    if (secondaryThumbprintAttribute != null)
                    {
                        this.securityConfiguration.clusterCertSecondaryThumbprint = secondaryThumbprintAttribute.Value;
                    }
                }
            }
            catch(Exception e)
            {
                this.WriteToLogWithLevel(
                this.ObserverName,
                $"There was an issue parsing the cluster manifest. Oberver cannot run.",
                Utilities.LogLevel.Error);
                throw e;
            }
        }

        private void checkLastestBySubjectName(X509Store store, string subjectName, int warningThreshold)
        {
            X509Certificate2Collection certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false);
            X509Certificate2 newestcertificate = null;
            DateTime newestNotAfter = DateTime.MinValue;

            if(certificates.Count == 0)
            {
                this.notFoundWarnings.Add($"Could not find requested certificate with common name: {subjectName} in LocalMachine/My");
            }

            foreach (X509Certificate2 certificate in certificates)
            {
                if (certificate.NotAfter > newestNotAfter)
                {
                    newestcertificate = certificate;
                    newestNotAfter = certificate.NotAfter;
                }
            }


            DateTime expiry = newestcertificate.NotAfter;                               // Expiration time in local time (not UTC)
            System.TimeSpan timeUntilExpiry = expiry.Subtract(System.DateTime.Now);

            if (timeUntilExpiry.TotalMilliseconds < 0)
            {
                expiredWarnings.Add($"Certificate expired on {expiry.ToShortDateString()}: [Thumbprint: {newestcertificate.Thumbprint} Issuer {newestcertificate.Issuer}, Subject: {newestcertificate.Subject}]");
            }
            else if (timeUntilExpiry.TotalDays < warningThreshold)
            {
                expiringWarnings.Add($"Certificate expiring in {timeUntilExpiry.TotalDays} days, on {expiry.ToShortDateString()}: [Thumbprint: {newestcertificate.Thumbprint} Issuer {newestcertificate.Issuer}, Subject: {newestcertificate.Subject}]");
            }
        }

        private void checkByThumbprint(X509Store store, string thumbprint, int warningThreshold)
        {
            X509Certificate2Collection certificates = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);

            if (certificates.Count == 0)
            {
                this.notFoundWarnings.Add($"Could not find requested certificate with thumbprint: {thumbprint} in LocalMachine/My");
            }

            // Return first vaule
            var enumerator = certificates.GetEnumerator();
            enumerator.MoveNext();

            DateTime expiry = enumerator.Current.NotAfter;                               // Expiration time in local time (not UTC)
            System.TimeSpan timeUntilExpiry = expiry.Subtract(System.DateTime.Now);

            if (timeUntilExpiry.TotalMilliseconds < 0)
            {
                expiredWarnings.Add($"Certificate Expired on {expiry.ToShortDateString()}: Thumbprint: {enumerator.Current.Thumbprint} Issuer {enumerator.Current.Issuer}, Subject: {enumerator.Current.Subject}");
            }
            else if (timeUntilExpiry.TotalDays < warningThreshold)
            {
                expiringWarnings.Add($"Certificate Expiring on {expiry.ToShortDateString()}: Thumbprint: {enumerator.Current.Thumbprint} Issuer {enumerator.Current.Issuer}, Subject: {enumerator.Current.Subject}");
            }
        }

        public override async Task ObserveAsync(CancellationToken token)
        {

            // Only run once per SECONDSBETWEENRUNS (default 1 day)
            if(DateTime.Now.Subtract(this.LastRunDateTime).TotalSeconds < SECONDSBETWEENRUNS) {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!this.IsTestRun)
            {
                await Initialize(token);
            }

            this.expiredWarnings = new List<string>();
            this.expiringWarnings = new List<string>();
            this.notFoundWarnings = new List<string>();

            X509Store store = new X509Store(StoreName.My, StoreLocation.LocalMachine);

            try
            {
                store.Open(OpenFlags.ReadOnly);
            }
            catch (SecurityException e)
            {
                this.WriteToLogWithLevel(
                    this.ObserverName,
                    $"Can't access {store.Name} due to {e.Message} - {e.StackTrace}",
                    Utilities.LogLevel.Warning);
                return;
            }

            // Cluster Certificates
            if(this.securityConfiguration.SecurityType == SecurityType.CommonName)
            {
                checkLastestBySubjectName(store, this.securityConfiguration.clusterCertThumbprintOrCommonName, this.DaysUntilClusterExpireWarningThreshold);
            }
            else if(this.securityConfiguration.SecurityType == SecurityType.Thumbprint)
            {
                checkByThumbprint(store, this.securityConfiguration.clusterCertThumbprintOrCommonName, this.DaysUntilClusterExpireWarningThreshold);
            }

            // App certificates
            foreach (string commonname in this.appCertificateCommonNamesToObserve)
            {
                checkLastestBySubjectName(store, commonname, this.DaysUntilAppExpireWarningThreshold);
            }
            foreach (string thumbprint in this.appCertificateThumbprintsToObserve)
            {
                checkByThumbprint(store, thumbprint, this.DaysUntilAppExpireWarningThreshold);
            }

            await ReportAsync(token).ConfigureAwait(true);
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Someone calling without observing first, must be run after a new run of ObserveAsync
            if (expiringWarnings == null || expiredWarnings == null || notFoundWarnings == null)
            {
                return;
            }

            Utilities.HealthReport healthReport;

            if(expiringWarnings.Count == 0 && expiredWarnings.Count == 0 && notFoundWarnings.Count == 0)
            {
                healthReport = new Utilities.HealthReport
                {
                    Observer = this.ObserverName,
                    ReportType = HealthReportType.Node,
                    EmitLogEvent = true,
                    NodeName = this.NodeName,
                    HealthMessage = "All cluster and observed app certificates outside of expiration warning window",
                    State = System.Fabric.Health.HealthState.Ok,
                    HealthReportTimeToLive = TimeSpan.FromDays(1)
                    // RemoveWhenExpired = True; automatically
                };

                this.HasActiveFabricErrorOrWarning = false;
            }
            else
            {

                string healthMessage = (expiredWarnings.Count == 0 ? "" : (expiredWarnings.Aggregate("", (i, j) => i + "\n" + j) + "\n")) +
                                       (notFoundWarnings.Count == 0 ? "" : (notFoundWarnings.Aggregate("", (i, j) => i + "\n" + j) + "\n")) +
                                       (expiringWarnings.Count == 0 ? "" : (expiringWarnings.Aggregate("", (i, j) => i + "\n" + j)));

                healthReport = new Utilities.HealthReport
                {
                    Observer = this.ObserverName,
                    ReportType = HealthReportType.Node,
                    EmitLogEvent = true,
                    NodeName = this.NodeName,
                    HealthMessage = healthMessage,
                    State = System.Fabric.Health.HealthState.Warning,
                    HealthReportTimeToLive = TimeSpan.FromSeconds(SECONDSBETWEENRUNS)
                    // RemoveWhenExpired = True; automatically
                };

                this.HasActiveFabricErrorOrWarning = true;
            }

            this.HealthReporter.ReportHealthToServiceFabric(healthReport);

            this.expiredWarnings = null;
            this.expiringWarnings = null;
            this.notFoundWarnings = null;

            this.LastRunDateTime = DateTime.Now;
        }
    }
}
