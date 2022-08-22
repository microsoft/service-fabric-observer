// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using FabricObserver.TelemetryLib;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    public sealed class CertificateObserver : ObserverBase
    {
        private const string HowToUpdateCnCertsSfLinkHtml =
            "<a href=\"https://aka.ms/AA69ai7\" target=\"_blank\">Click here to learn how to update expiring/expired certificates.</a>";

        private const string HowToUpdateSelfSignedCertSfLinkHtml =
           "<a href=\"https://aka.ms/AA6cicw\" target=\"_blank\">Click here to learn how to fix expired self-signed certificates.</a>";

        private TimeSpan HealthReportTimeToLive
        {
            get;
        } = TimeSpan.FromDays(1);

        private List<string> NotFoundWarnings
        {
            get; set;
        }

        private List<string> ExpiredWarnings
        {
            get; set;
        }

        private List<string> ExpiringWarnings
        {
            get; set;
        }

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public CertificateObserver(StatelessServiceContext context) : base (null, context)
        {

        }

        public int DaysUntilClusterExpireWarningThreshold
        {
            get; set;
        }

        public int DaysUntilAppExpireWarningThreshold
        {
            get; set;
        }

        public List<string> AppCertificateThumbprintsToObserve
        {
            get; set;
        }

        public List<string> AppCertificateCommonNamesToObserve
        {
            get; set;
        }

        public SecurityConfiguration SecurityConfiguration
        {
            get; set;
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // Only run once per specified time in Settings.xml. (default is already set to 1 day for CertificateObserver)
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            Token = token;

            await Initialize(token);
            
            ExpiredWarnings = new List<string>();
            ExpiringWarnings = new List<string>();
            NotFoundWarnings = new List<string>();

            // Unix LocalMachine X509Store is limited to the Root and CertificateAuthority stores.
            var store = new X509Store(IsWindows ? StoreName.My : StoreName.Root, StoreLocation.LocalMachine);

            try
            {
                store.Open(OpenFlags.ReadOnly);

                if (SecurityConfiguration.SecurityType == SecurityType.CommonName)
                {
                    CheckLatestBySubjectName(store, SecurityConfiguration.ClusterCertThumbprintOrCommonName, DaysUntilClusterExpireWarningThreshold);

                }
                else if (SecurityConfiguration.SecurityType == SecurityType.Thumbprint)
                {
                    CheckByThumbprint(store, SecurityConfiguration.ClusterCertThumbprintOrCommonName, DaysUntilClusterExpireWarningThreshold);
                }

                if (AppCertificateCommonNamesToObserve != null)
                {
                    // App certificates
                    foreach (string commonName in AppCertificateCommonNamesToObserve)
                    {
                        token.ThrowIfCancellationRequested();
                        CheckLatestBySubjectName(store, commonName, DaysUntilAppExpireWarningThreshold);
                    }
                }

                if (AppCertificateThumbprintsToObserve != null)
                {
                    foreach (string thumbprint in AppCertificateThumbprintsToObserve)
                    {
                        token.ThrowIfCancellationRequested();
                        CheckByThumbprint(store, thumbprint, DaysUntilAppExpireWarningThreshold);
                    }
                }

                await ReportAsync(token);
            }
            catch (SecurityException e)
            {
                ObserverLogger.LogWarning($"Can't access {store.Name} due to {e.Message} - {e.StackTrace}");
            }
            finally
            {
                store.Dispose();
            }

            ExpiredWarnings?.Clear();
            ExpiredWarnings = null;

            ExpiringWarnings?.Clear();
            ExpiringWarnings = null;

            NotFoundWarnings?.Clear();
            NotFoundWarnings = null;

            AppCertificateCommonNamesToObserve?.Clear();
            AppCertificateCommonNamesToObserve = null;

            AppCertificateThumbprintsToObserve?.Clear();
            AppCertificateThumbprintsToObserve = null;

            LastRunDateTime = DateTime.Now;
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            // Someone calling without observing first, must be run after a new run of ObserveAsync
            if (ExpiringWarnings == null || ExpiredWarnings == null || NotFoundWarnings == null)
            {
                return;
            }

            HealthReport healthReport;

            if (ExpiringWarnings.Count == 0 && ExpiredWarnings.Count == 0 && NotFoundWarnings.Count == 0)
            {
                healthReport = new HealthReport
                {
                    Observer = ObserverName,
                    EntityType = EntityType.Node,
                    EmitLogEvent = true,
                    NodeName = NodeName,
                    HealthMessage = "All cluster and monitored app certificates are healthy.",
                    State = HealthState.Ok,
                    HealthReportTimeToLive = RunInterval > TimeSpan.MinValue ? RunInterval : HealthReportTimeToLive
                };

                HasActiveFabricErrorOrWarning = false;
                var telemetryData = new TelemetryData()
                {
                    ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                    Description = "All cluster and monitored app certificates are healthy.",
                    HealthState = HealthState.Ok,
                    NodeName = NodeName,
                    NodeType = NodeType,
                    ObserverName = ObserverName,
                    Source = ObserverConstants.FabricObserverName
                };

                if (IsTelemetryEnabled)
                {
                    await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                }

                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                }
            }
            else
            {
                string healthMessage = (ExpiredWarnings.Count == 0 ? string.Empty : (ExpiredWarnings.Aggregate(string.Empty, (i, j) => i + "\n" + j) + "\n")) +
                                       (NotFoundWarnings.Count == 0 ? string.Empty : (NotFoundWarnings.Aggregate(string.Empty, (i, j) => i + "\n" + j) + "\n")) +
                                       (ExpiringWarnings.Count == 0 ? string.Empty : ExpiringWarnings.Aggregate(string.Empty, (i, j) => i + "\n" + j));

                healthReport = new HealthReport
                {
                    Code = FOErrorWarningCodes.WarningCertificateExpiration,
                    Observer = ObserverName,
                    EntityType = EntityType.Node,
                    EmitLogEvent = true,
                    NodeName = NodeName,
                    HealthMessage = healthMessage,
                    State = HealthState.Warning,
                    HealthReportTimeToLive = RunInterval > TimeSpan.MinValue ? RunInterval : HealthReportTimeToLive
                };

                HasActiveFabricErrorOrWarning = true;
                CurrentWarningCount++;
                var telemetryData = new TelemetryData()
                {
                    ClusterId = ClusterInformation.ClusterInfoTuple.ClusterId,
                    Code = FOErrorWarningCodes.WarningCertificateExpiration,
                    Description = healthMessage,
                    HealthState = HealthState.Warning,
                    Metric = ErrorWarningProperty.CertificateExpiration,
                    NodeName = NodeName,
                    NodeType = NodeType,
                    ObserverName = ObserverName,
                    Source = ObserverConstants.FabricObserverName,
                };

                if (IsTelemetryEnabled)
                {
                    await TelemetryClient.ReportHealthAsync(telemetryData, Token);
                }

                if (IsEtwEnabled)
                {
                    ObserverLogger.LogEtw(ObserverConstants.FabricObserverETWEventName, telemetryData);
                }
            }

            HealthReporter?.ReportHealthToServiceFabric(healthReport);
        }

        private static bool IsSelfSignedCertificate(X509Certificate2 certificate)
        {
            X509Chain ch = null;

            // This function is only passed well-formed certs, so there is no need for error handling.
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

        /// <summary>
        /// This method is used on Linux to load certs from /var/lib/sfcerts or /var/lib/waagent directory.
        /// </summary>
        private static bool TryFindCertificate(string storePath, string thumbprint, out X509Certificate2 certificate)
        {
            string fileName = Path.Combine(storePath, thumbprint.ToUpperInvariant() + ".crt");

            if (File.Exists(fileName))
            {
                certificate = new X509Certificate2(fileName);
                return true;
            }

            certificate = null;
            return false;
        }

        private async Task Initialize(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var daysUntilClusterExpireWarningThreshold = GetSettingParameterValue(
                                                            ConfigurationSectionName,
                                                            ObserverConstants.CertificateObserverDaysUntilClusterExpiryWarningThreshold);

            DaysUntilClusterExpireWarningThreshold = !string.IsNullOrEmpty(daysUntilClusterExpireWarningThreshold) ? int.Parse(daysUntilClusterExpireWarningThreshold) : 14;

            var daysUntilAppExpireWarningClusterThreshold = GetSettingParameterValue(
                                                                ConfigurationSectionName,
                                                                ObserverConstants.CertificateObserverDaysUntilAppExpiryWarningThreshold);

            DaysUntilAppExpireWarningThreshold = !string.IsNullOrEmpty(daysUntilAppExpireWarningClusterThreshold) ? int.Parse(daysUntilAppExpireWarningClusterThreshold) : 14;

            if (AppCertificateThumbprintsToObserve == null)
            {
                var appThumbprintsToObserve = GetSettingParameterValue(
                                                ConfigurationSectionName,
                                                ObserverConstants.CertificateObserverAppCertificateThumbprints);

                AppCertificateThumbprintsToObserve = !string.IsNullOrEmpty(appThumbprintsToObserve) ? JsonHelper.ConvertFromString<List<string>>(appThumbprintsToObserve) : new List<string>();
            }

            if (AppCertificateCommonNamesToObserve == null)
            {
                var appCommonNamesToObserve = GetSettingParameterValue(
                                                ConfigurationSectionName,
                                                ObserverConstants.CertificateObserverAppCertificateCommonNames);

                AppCertificateCommonNamesToObserve = !string.IsNullOrEmpty(appCommonNamesToObserve) ? JsonHelper.ConvertFromString<List<string>>(appCommonNamesToObserve) : new List<string>();
            }

            await GetSecurityTypes(token);
        }

        private async Task GetSecurityTypes(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            SecurityConfiguration = new SecurityConfiguration();
            string clusterManifestXml = await FabricClientInstance.ClusterManager.GetClusterManifestAsync(AsyncClusterOperationTimeoutSeconds, Token);
            XmlReader xreader = null;
            StringReader sreader = null;

            try
            {
                // Safe XML pattern - *Do not use LoadXml*.
                var xdoc = new XmlDocument { XmlResolver = null };
                sreader = new StringReader(clusterManifestXml);
                xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null });
                xdoc.Load(xreader);

                var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");

                var certificateNode = xdoc.SelectNodes($"//sf:NodeType[@Name='{NodeType}']//sf:Certificates", nsmgr);

                if (certificateNode != null ? certificateNode.Count == 0 : false)
                {
                    SecurityConfiguration.SecurityType = SecurityType.None;
                }
                else
                {
                    var clusterCertificateNode = certificateNode != null ? certificateNode.Item(0) != null ? certificateNode.Item(0).ChildNodes.Item(0) : null : null;

                    var commonNameAttribute = clusterCertificateNode != null ? clusterCertificateNode.Attributes != null ? clusterCertificateNode.Attributes.GetNamedItem("X509FindType") : null : null;
                    if (commonNameAttribute != null)
                    {
                        if (commonNameAttribute.Value == "FindBySubjectName")
                        {
                            SecurityConfiguration.SecurityType = SecurityType.CommonName;
                            SecurityConfiguration.ClusterCertThumbprintOrCommonName = clusterCertificateNode.Attributes.GetNamedItem("X509FindValue").Value;
                            return;
                        }
                        else
                        {
                            throw new Exception("if X509FindTime attribute, value should be FindBySubjectName");
                        }
                    }

                    SecurityConfiguration.SecurityType = SecurityType.Thumbprint;
                    SecurityConfiguration.ClusterCertThumbprintOrCommonName = clusterCertificateNode != null ? clusterCertificateNode.Attributes != null ? clusterCertificateNode.Attributes.GetNamedItem("X509FindValue").Value : null : null;
                    var secondaryThumbprintAttribute = clusterCertificateNode != null ? clusterCertificateNode.Attributes != null ? clusterCertificateNode.Attributes.GetNamedItem("X509FindValueSecondary") : null : null;

                    if (secondaryThumbprintAttribute != null)
                    {
                        SecurityConfiguration.ClusterCertSecondaryThumbprint = secondaryThumbprintAttribute.Value;
                    }
                }
            }
            catch (Exception e) when (!(e is TaskCanceledException || e is OperationCanceledException))
            {
                ObserverLogger.LogError($"There was an issue parsing the cluster manifest. Observer cannot run. Error Details:{Environment.NewLine}{e}");
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
                NotFoundWarnings.Add($"Could not find requested certificate with common name: {subjectName} in LocalMachine/My");
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
            TimeSpan? timeUntilExpiry = expiry?.Subtract(DateTime.Now);

            if (timeUntilExpiry != null && timeUntilExpiry.Value.TotalMilliseconds < 0)
            {
                ExpiredWarnings.Add(
                    $"Certificate expired on {expiry.Value.ToShortDateString()}: " +
                    $"[Thumbprint: {newestCertificate.Thumbprint} " +
                    $"Issuer {newestCertificate.Issuer}, " +
                    $"Subject: {newestCertificate.Subject}]{Environment.NewLine}{message}");
            }
            else if (timeUntilExpiry != null && timeUntilExpiry.Value.TotalDays < warningThreshold)
            {
                ExpiringWarnings.Add(
                    $"Certificate expiring in {timeUntilExpiry?.TotalDays} days, on {expiry?.ToShortDateString()}: " +
                    $"[Thumbprint: {newestCertificate.Thumbprint} " +
                    $"Issuer {newestCertificate.Issuer}, " +
                    $"Subject: {newestCertificate.Subject}]{Environment.NewLine}{message}");
            }
        }

        private void CheckByThumbprint(X509Store store, string thumbprint, int warningThreshold)
        {
            X509Certificate2Collection certificates = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            X509Certificate2 certificate;

            if (certificates.Count == 0)
            {
                if (!IsWindows)
                {
                    if (!TryFindCertificate("/var/lib/sfcerts", thumbprint, out certificate) &&
                        !TryFindCertificate("/var/lib/waagent", thumbprint, out certificate))
                    {
                        NotFoundWarnings.Add($"Could not find requested certificate with thumbprint: {thumbprint} in /var/lib/sfcerts, /var/lib/waagent, and LocalMachine/Root");
                        return;
                    }
                }
                else
                {
                    NotFoundWarnings.Add($"Could not find requested certificate with thumbprint: {thumbprint} in LocalMachine/My");
                    return;
                }
            }
            else
            {
                certificate = certificates[0];
            }

            DateTime expiry = certificate.NotAfter; // Expiration time in local time (not UTC)
            TimeSpan timeUntilExpiry = expiry.Subtract(DateTime.Now);
            var message = HowToUpdateCnCertsSfLinkHtml;

            if (IsSelfSignedCertificate(certificate))
            {
                message = HowToUpdateSelfSignedCertSfLinkHtml;
            }

            if (timeUntilExpiry.TotalMilliseconds < 0)
            {
                ExpiredWarnings.Add($"Certificate Expired on {expiry.ToShortDateString()}: " +
                                    $"Thumbprint: {certificate.Thumbprint} " +
                                    $"Issuer {certificate.Issuer}, " +
                                    $"Subject: {certificate.Subject}{Environment.NewLine}{message}");
            }
            else if (timeUntilExpiry.TotalDays < warningThreshold)
            {
                ExpiringWarnings.Add($"Certificate Expiring on {expiry.ToShortDateString()}: " +
                                     $"Thumbprint: {certificate.Thumbprint} " +
                                     $"Issuer {certificate.Issuer}, " +
                                     $"Subject: {certificate.Subject}{Environment.NewLine}{message}");
            }
        }
    }
}