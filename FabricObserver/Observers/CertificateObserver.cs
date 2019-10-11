using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FabricObserver.Utilities;

namespace FabricObserver
{
    public class CertificateObserver : ObserverBase
    {
        // By default, CertificateObserver runs once a day, and its reports last a single day
        private const int SECONDSBETWEENRUNS = 86400; // 86400 = 1 day

        public CertificateObserver()
        : base(ObserverConstants.CertificateObserverName)
        {
        }

        public int DaysUntilClusterExpireWarningThreshold { get; set; }

        public int DaysUntilAppExpireWarningThreshold { get; set; }

        public List<string> AppCertificateThumbprintsToObserve { get; set; }

        public List<string> AppCertificateCommonNamesToObserve { get; set; }

        public List<string> NotFoundWarnings { get; set; }

        public List<string> ExpiredWarnings { get; set; }

        public List<string> ExpiringWarnings { get; set; }

        public SecurityConfiguration SecurityConfiguration { get; set; }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // Only run once per SECONDSBETWEENRUNS (default 1 day)
            if (DateTime.Now.Subtract(this.LastRunDateTime).TotalSeconds < SECONDSBETWEENRUNS)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!this.IsTestRun)
            {
                await this.Initialize(token).ConfigureAwait(true);
            }

            this.ExpiredWarnings = new List<string>();
            this.ExpiringWarnings = new List<string>();
            this.NotFoundWarnings = new List<string>();

            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);

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
            if (this.SecurityConfiguration.SecurityType == SecurityType.CommonName)
            {
                this.CheckLastestBySubjectName(store, this.SecurityConfiguration.ClusterCertThumbprintOrCommonName, this.DaysUntilClusterExpireWarningThreshold);
            }
            else if (this.SecurityConfiguration.SecurityType == SecurityType.Thumbprint)
            {
                this.CheckByThumbprint(store, this.SecurityConfiguration.ClusterCertThumbprintOrCommonName, this.DaysUntilClusterExpireWarningThreshold);
            }

            // App certificates
            foreach (string commonname in this.AppCertificateCommonNamesToObserve)
            {
                this.CheckLastestBySubjectName(store, commonname, this.DaysUntilAppExpireWarningThreshold);
            }

            foreach (string thumbprint in this.AppCertificateThumbprintsToObserve)
            {
                this.CheckByThumbprint(store, thumbprint, this.DaysUntilAppExpireWarningThreshold);
            }

            await this.ReportAsync(token).ConfigureAwait(true);
        }

        /// <inheritdoc/>
        public override Task ReportAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                 return Task.CompletedTask;
            }

            // Someone calling without observing first, must be run after a new run of ObserveAsync
            if (this.ExpiringWarnings == null ||
                this.ExpiredWarnings == null ||
                this.NotFoundWarnings == null)
            {
                return Task.CompletedTask;
            }

            HealthReport healthReport;

            if (this.ExpiringWarnings.Count == 0 && this.ExpiredWarnings.Count == 0 && this.NotFoundWarnings.Count == 0)
            {
                healthReport = new HealthReport
                {
                    Observer = this.ObserverName,
                    ReportType = HealthReportType.Node,
                    EmitLogEvent = true,
                    NodeName = this.NodeName,
                    HealthMessage = "All cluster and observed app certificates outside of expiration warning window",
                    State = System.Fabric.Health.HealthState.Ok,
                    HealthReportTimeToLive = TimeSpan.FromDays(1),

                    // RemoveWhenExpired = True; automatically
                };

                this.HasActiveFabricErrorOrWarning = false;
            }
            else
            {
                string healthMessage = (this.ExpiredWarnings.Count == 0 ? string.Empty : (this.ExpiredWarnings.Aggregate(string.Empty, (i, j) => i + "\n" + j) + "\n")) +
                                       (this.NotFoundWarnings.Count == 0 ? string.Empty : (this.NotFoundWarnings.Aggregate(string.Empty, (i, j) => i + "\n" + j) + "\n")) +
                                       (this.ExpiringWarnings.Count == 0 ? string.Empty : this.ExpiringWarnings.Aggregate(string.Empty, (i, j) => i + "\n" + j));

                healthReport = new HealthReport
                {
                    Observer = this.ObserverName,
                    ReportType = HealthReportType.Node,
                    EmitLogEvent = true,
                    NodeName = this.NodeName,
                    HealthMessage = healthMessage,
                    State = System.Fabric.Health.HealthState.Warning,
                    HealthReportTimeToLive = TimeSpan.FromSeconds(SECONDSBETWEENRUNS),

                    // RemoveWhenExpired = True; automatically
                };

                this.HasActiveFabricErrorOrWarning = true;
            }

            this.HealthReporter.ReportHealthToServiceFabric(healthReport);

            this.ExpiredWarnings = null;
            this.ExpiringWarnings = null;
            this.NotFoundWarnings = null;

            this.LastRunDateTime = DateTime.Now;

            return Task.CompletedTask;
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
                this.DaysUntilAppExpireWarningThreshold = 14;
            }

            var appThumbprintsToObserve = this.GetSettingParameterValue(
            ObserverConstants.CertificateObserverConfigurationSectionName,
            ObserverConstants.CertificateObserverAppCertificateThumbprints);

            if (!string.IsNullOrEmpty(appThumbprintsToObserve))
            {
                this.AppCertificateThumbprintsToObserve = JsonHelper.ConvertFromString<List<string>>(appThumbprintsToObserve);
            }
            else
            {
                this.AppCertificateThumbprintsToObserve = new List<string>();
            }

            var appCommonNamesToObserve = this.GetSettingParameterValue(
            ObserverConstants.CertificateObserverConfigurationSectionName,
            ObserverConstants.CertificateObserverAppCertificateCommonNames);

            if (!string.IsNullOrEmpty(appThumbprintsToObserve))
            {
                this.AppCertificateCommonNamesToObserve = JsonHelper.ConvertFromString<List<string>>(appCommonNamesToObserve);
            }
            else
            {
                this.AppCertificateCommonNamesToObserve = new List<string>();
            }

            await this.GetSecurityTypes(token).ConfigureAwait(true);
        }

        private async Task GetSecurityTypes(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            this.SecurityConfiguration = new SecurityConfiguration();

            string clusterManifestXml = await this.FabricClientInstance.ClusterManager.GetClusterManifestAsync().ConfigureAwait(true);

            XmlReader xreader = null;
            StringReader sreader = null;

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
                var certificateNode = xdoc.SelectNodes($"//sf:NodeType[@Name='{this.NodeType}']//sf:Certificates", nsmgr);
                if (certificateNode.Count == 0)
                {
                    this.SecurityConfiguration.SecurityType = SecurityType.None;
                    return;
                }
                else
                {
                    var clusterCertificateNode = certificateNode.Item(0).ChildNodes.Item(0);

                    var commonNameAttribute = clusterCertificateNode.Attributes.GetNamedItem("X509FindType");
                    if (commonNameAttribute != null)
                    {
                        if (commonNameAttribute.Value == "FindBySubjectName")
                        {
                            this.SecurityConfiguration.SecurityType = SecurityType.CommonName;
                            this.SecurityConfiguration.ClusterCertThumbprintOrCommonName = clusterCertificateNode.Attributes.GetNamedItem("X509FindValue").Value;
                            return;
                        }
                        else
                        {
                            throw new System.ServiceModel.ActionNotSupportedException("if X509FindTime attribute, value should be FindBySubjectName");
                        }
                    }

                    this.SecurityConfiguration.SecurityType = SecurityType.Thumbprint;
                    this.SecurityConfiguration.ClusterCertThumbprintOrCommonName = clusterCertificateNode.Attributes.GetNamedItem("X509FindValue").Value;
                    var secondaryThumbprintAttribute = clusterCertificateNode.Attributes.GetNamedItem("X509FindValueSecondary");

                    if (secondaryThumbprintAttribute != null)
                    {
                        this.SecurityConfiguration.ClusterCertSecondaryThumbprint = secondaryThumbprintAttribute.Value;
                    }
                }
            }
            catch
            {
                this.WriteToLogWithLevel(
                this.ObserverName,
                $"There was an issue parsing the cluster manifest. Observer cannot run.",
                LogLevel.Error);

                throw;
            }
            finally
            {
                sreader?.Dispose();
                xreader?.Dispose();
            }
        }

        private void CheckLastestBySubjectName(X509Store store, string subjectName, int warningThreshold)
        {
            var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false);
            X509Certificate2 newestcertificate = null;
            var newestNotAfter = DateTime.MinValue;

            if (certificates.Count == 0)
            {
                this.NotFoundWarnings.Add($"Could not find requested certificate with common name: {subjectName} in LocalMachine/My");
            }

            foreach (var certificate in certificates)
            {
                if (certificate.NotAfter > newestNotAfter)
                {
                    newestcertificate = certificate;
                    newestNotAfter = certificate.NotAfter;
                }
            }

            var expiry = newestcertificate.NotAfter;                               // Expiration time in local time (not UTC)
            var timeUntilExpiry = expiry.Subtract(System.DateTime.Now);

            if (timeUntilExpiry.TotalMilliseconds < 0)
            {
                this.ExpiredWarnings.Add($"Certificate expired on {expiry.ToShortDateString()}: [Thumbprint: {newestcertificate.Thumbprint} Issuer {newestcertificate.Issuer}, Subject: {newestcertificate.Subject}]");
            }
            else if (timeUntilExpiry.TotalDays < warningThreshold)
            {
                this.ExpiringWarnings.Add($"Certificate expiring in {timeUntilExpiry.TotalDays} days, on {expiry.ToShortDateString()}: [Thumbprint: {newestcertificate.Thumbprint} Issuer {newestcertificate.Issuer}, Subject: {newestcertificate.Subject}]");
            }
        }

        private void CheckByThumbprint(X509Store store, string thumbprint, int warningThreshold)
        {
            var certificates = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);

            if (certificates.Count == 0)
            {
                this.NotFoundWarnings.Add($"Could not find requested certificate with thumbprint: {thumbprint} in LocalMachine/My");
            }

            // Return first vaule
            var enumerator = certificates.GetEnumerator();
            enumerator.MoveNext();

            var expiry = enumerator.Current.NotAfter;                               // Expiration time in local time (not UTC)
            var timeUntilExpiry = expiry.Subtract(System.DateTime.Now);

            if (timeUntilExpiry.TotalMilliseconds < 0)
            {
                this.ExpiredWarnings.Add($"Certificate Expired on {expiry.ToShortDateString()}: Thumbprint: {enumerator.Current.Thumbprint} Issuer {enumerator.Current.Issuer}, Subject: {enumerator.Current.Subject}");
            }
            else if (timeUntilExpiry.TotalDays < warningThreshold)
            {
                this.ExpiringWarnings.Add($"Certificate Expiring on {expiry.ToShortDateString()}: Thumbprint: {enumerator.Current.Thumbprint} Issuer {enumerator.Current.Issuer}, Subject: {enumerator.Current.Subject}");
            }
        }
    }
}