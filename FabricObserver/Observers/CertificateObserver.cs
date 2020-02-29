using System;
using System.Collections.Generic;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FabricObserver.Observers.Utilities;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    public class CertificateObserver : ObserverBase
    {
        private const string HowToUpdateCnCertsSfLinkHtml =
            "<a href=\"https://aka.ms/AA69ai7\" target=\"_blank\">Click here to learn how to update expiring/expired certificates.</a>";

        private const string HowToUpdateSelfSignedCertSfLinkHtml =
           "<a href=\"https://aka.ms/AA6cicw\" target=\"_blank\">Click here to learn how to fix expired self-signed certificates.</a>";

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

        public TimeSpan HealthReportTimeToLive { get; set; } = TimeSpan.FromDays(1);

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // Only run once per specified time in Settings.xml. (default is already set to 1 day for CertificateObserver)
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter.
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
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

                // Cluster Certificates
                if (this.SecurityConfiguration.SecurityType == SecurityType.CommonName)
                {
                    this.CheckLatestBySubjectName(store, this.SecurityConfiguration.ClusterCertThumbprintOrCommonName, this.DaysUntilClusterExpireWarningThreshold);
                }
                else if (this.SecurityConfiguration.SecurityType == SecurityType.Thumbprint)
                {
                    this.CheckByThumbprint(store, this.SecurityConfiguration.ClusterCertThumbprintOrCommonName, this.DaysUntilClusterExpireWarningThreshold);
                }

                // App certificates
                foreach (string commonName in this.AppCertificateCommonNamesToObserve)
                {
                    this.CheckLatestBySubjectName(store, commonName, this.DaysUntilAppExpireWarningThreshold);
                }

                foreach (string thumbprint in this.AppCertificateThumbprintsToObserve)
                {
                    this.CheckByThumbprint(store, thumbprint, this.DaysUntilAppExpireWarningThreshold);
                }

                await this.ReportAsync(token).ConfigureAwait(true);
            }
            catch (SecurityException e)
            {
                this.WriteToLogWithLevel(
                    this.ObserverName,
                    $"Can't access {store.Name} due to {e.Message} - {e.StackTrace}",
                    LogLevel.Warning);
            }
            finally
            {
                store.Dispose();
            }
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

            if (this.ExpiringWarnings.Count == 0
                && this.ExpiredWarnings.Count == 0
                && this.NotFoundWarnings.Count == 0)
            {
                healthReport = new HealthReport
                {
                    Observer = this.ObserverName,
                    ReportType = HealthReportType.Node,
                    EmitLogEvent = true,
                    NodeName = this.NodeName,
                    HealthMessage = $"All cluster and monitored app certificates are healthy.",
                    State = HealthState.Ok,
                    HealthReportTimeToLive = this.RunInterval > TimeSpan.MinValue ? this.RunInterval : this.HealthReportTimeToLive,
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
                    Code = FoErrorWarningCodes.WarningCertificateExpiration,
                    Observer = this.ObserverName,
                    ReportType = HealthReportType.Node,
                    EmitLogEvent = true,
                    NodeName = this.NodeName,
                    HealthMessage = healthMessage,
                    State = HealthState.Warning,
                    HealthReportTimeToLive = this.RunInterval > TimeSpan.MinValue ? this.RunInterval : this.HealthReportTimeToLive,
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

        private static bool IsSelfSignedCertificate(X509Certificate2 certificate)
        {
            X509Chain ch = null;

            // This function is only passed well-formed certs, so no need error handling.
            try
            {
                ch = new X509Chain();
                _ = ch.Build(certificate);

                if (ch.ChainElements.Count == 1)
                {
                    return true;
                }
            }
            finally
            {
                ch?.Dispose();
            }

            return false;
        }

        private async Task Initialize(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var daysUntilClusterExpireWarningThreshold = this.GetSettingParameterValue(
                ObserverConstants.CertificateObserverConfigurationSectionName,
                ObserverConstants.CertificateObserverDaysUntilClusterExpiryWarningThreshold);

            this.DaysUntilClusterExpireWarningThreshold = !string.IsNullOrEmpty(daysUntilClusterExpireWarningThreshold) ? int.Parse(daysUntilClusterExpireWarningThreshold) : 14;

            var daysUntilAppExpireWarningClusterThreshold = this.GetSettingParameterValue(
            ObserverConstants.CertificateObserverConfigurationSectionName,
            ObserverConstants.CertificateObserverDaysUntilAppExpiryWarningThreshold);

            this.DaysUntilAppExpireWarningThreshold = !string.IsNullOrEmpty(daysUntilAppExpireWarningClusterThreshold) ? int.Parse(daysUntilAppExpireWarningClusterThreshold) : 14;

            var appThumbprintsToObserve = this.GetSettingParameterValue(
                    ObserverConstants.CertificateObserverConfigurationSectionName,
                    ObserverConstants.CertificateObserverAppCertificateThumbprints);

            this.AppCertificateThumbprintsToObserve = !string.IsNullOrEmpty(appThumbprintsToObserve) ? JsonHelper.ConvertFromString<List<string>>(appThumbprintsToObserve) : new List<string>();

            var appCommonNamesToObserve = this.GetSettingParameterValue(
                    ObserverConstants.CertificateObserverConfigurationSectionName,
                    ObserverConstants.CertificateObserverAppCertificateCommonNames);

            this.AppCertificateCommonNamesToObserve = !string.IsNullOrEmpty(appThumbprintsToObserve) ? JsonHelper.ConvertFromString<List<string>>(appCommonNamesToObserve) : new List<string>();

            await this.GetSecurityTypes(token).ConfigureAwait(true);
        }

        private async Task GetSecurityTypes(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            this.SecurityConfiguration = new SecurityConfiguration();

            string clusterManifestXml = await this.FabricClientInstance.ClusterManager.GetClusterManifestAsync(
                                                this.AsyncClusterOperationTimeoutSeconds,
                                                this.Token).ConfigureAwait(true);

            XmlReader xreader = null;
            StringReader sreader = null;

            try
            {
                // Safe XML pattern - *Do not use LoadXml*.
                var xdoc = new XmlDocument { XmlResolver = null };
                sreader = new StringReader(clusterManifestXml);
                xreader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null });
                xdoc.Load(xreader);

                var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");

                var certificateNode = xdoc.SelectNodes($"//sf:NodeType[@Name='{this.NodeType}']//sf:Certificates", nsmgr);

                if (certificateNode?.Count == 0)
                {
                    this.SecurityConfiguration.SecurityType = SecurityType.None;
                }
                else
                {
                    var clusterCertificateNode = certificateNode?.Item(0)?.ChildNodes.Item(0);

                    var commonNameAttribute = clusterCertificateNode?.Attributes?.GetNamedItem("X509FindType");
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
                    this.SecurityConfiguration.ClusterCertThumbprintOrCommonName = clusterCertificateNode?.Attributes?.GetNamedItem("X509FindValue").Value;
                    var secondaryThumbprintAttribute = clusterCertificateNode?.Attributes?.GetNamedItem("X509FindValueSecondary");

                    if (secondaryThumbprintAttribute != null)
                    {
                        this.SecurityConfiguration.ClusterCertSecondaryThumbprint = secondaryThumbprintAttribute.Value;
                    }
                }
            }
            catch (Exception e)
            {
                this.WriteToLogWithLevel(
                this.ObserverName,
                $"There was an issue parsing the cluster manifest. Observer cannot run.\nError Details:\n{e}",
                LogLevel.Error);

                throw;
            }
            finally
            {
                sreader?.Dispose();
                xreader?.Dispose();
            }
        }

        private void CheckLatestBySubjectName(X509Store store, string subjectName, int warningThreshold)
        {
            var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false);
            X509Certificate2 newestCertificate = null;
            var newestNotAfter = DateTime.MinValue;

            if (certificates.Count == 0)
            {
                this.NotFoundWarnings.Add($"Could not find requested certificate with common name: {subjectName} in LocalMachine/My");
                return;
            }

            var message = HowToUpdateCnCertsSfLinkHtml;

            foreach (var certificate in certificates)
            {
                if (certificate.NotAfter > newestNotAfter)
                {
                    newestCertificate = certificate;
                    newestNotAfter = certificate.NotAfter;
                }

                if (IsSelfSignedCertificate(certificate))
                {
                    message = HowToUpdateSelfSignedCertSfLinkHtml;
                }
            }

            DateTime? expiry = newestCertificate?.NotAfter; // Expiration time in local time (not UTC)
            var timeUntilExpiry = expiry?.Subtract(DateTime.Now);

            if (timeUntilExpiry?.TotalMilliseconds < 0)
            {
                this.ExpiredWarnings.Add(
                    $"Certificate expired on {expiry?.ToShortDateString()}: " +
                    $"[Thumbprint: {newestCertificate?.Thumbprint} " +
                    $"" +
                    $"Issuer {newestCertificate.Issuer}, " +
                    $"Subject: {newestCertificate.Subject}]{Environment.NewLine}{message}");
            }
            else if (timeUntilExpiry?.TotalDays < warningThreshold)
            {
                this.ExpiringWarnings.Add(
                    $"Certificate expiring in {timeUntilExpiry?.TotalDays} days, on {expiry?.ToShortDateString()}: " +
                    $"[Thumbprint: {newestCertificate.Thumbprint} " +
                    $"Issuer {newestCertificate.Issuer}, " +
                    $"Subject: {newestCertificate.Subject}]{Environment.NewLine}{message}");
            }
        }

        private void CheckByThumbprint(X509Store store, string thumbprint, int warningThreshold)
        {
            var certificates = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                thumbprint,
                false);

            if (certificates.Count == 0)
            {
                this.NotFoundWarnings.Add(
                    $"Could not find requested certificate with thumbprint: {thumbprint} in LocalMachine/My");

                return;
            }

            // Return first value
            var enumerator = certificates.GetEnumerator();
            _ = enumerator.MoveNext();

            var expiry = enumerator?.Current?.NotAfter; // Expiration time in local time (not UTC)
            var timeUntilExpiry = expiry?.Subtract(DateTime.Now);
            var message = HowToUpdateCnCertsSfLinkHtml;

            if (IsSelfSignedCertificate(enumerator.Current))
            {
                message = HowToUpdateSelfSignedCertSfLinkHtml;
            }

            if (timeUntilExpiry?.TotalMilliseconds < 0)
            {
                this.ExpiredWarnings.Add($"Certificate Expired on {expiry?.ToShortDateString()}: " +
                                         $"Thumbprint: {enumerator.Current.Thumbprint} " +
                                         $"Issuer {enumerator.Current.Issuer}, " +
                                         $"Subject: {enumerator.Current.Subject}{Environment.NewLine}{message}");
            }
            else if (timeUntilExpiry?.TotalDays < warningThreshold)
            {
                this.ExpiringWarnings.Add($"Certificate Expiring on {expiry?.ToShortDateString()}: " +
                                          $"Thumbprint: {enumerator.Current.Thumbprint} " +
                                          $"Issuer {enumerator.Current.Issuer}, " +
                                          $"Subject: {enumerator.Current.Subject}{Environment.NewLine}{message}");
            }
        }
    }
}