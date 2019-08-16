// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Fabric;
using Microsoft.Win32;
using FabricObserver.Utilities;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver
{
    // This is important. TODO: Generalize for SF. We need to enable configuration-based validation
    // to understand what a valid certificate means...
    // Test for Validity
    // Test for Expiration proximity
    // Test for...
    // Work with Dragos to get more insights into how best to design this observer for SF certs...

    public class CertificateObserver : ObserverBase
    {
        public override DateTime LastRunDateTime { get; set; } = DateTime.MinValue;
        private string _clusterManifestXml;

        public CertificateObserver(StatelessServiceContext fabricServiceContext, 
                                   FabricClient fabricClient) : base(ObserverConstants.CertificateObserverName, 
                                                                     fabricServiceContext, 
                                                                     fabricClient)
        {

        }

        private DateTime GetExpirationWarningThreshold()
        {
            DateTime threshold;

            try
            {
                threshold = DateTime.UtcNow.Add(
                TimeSpan.Parse(
                GetSettingParameterValue(
                ObserverConstants.CertificateObserverConfigurationSectionName,
                ObserverConstants.CertificateObserverThresholdParameterName),
                CultureInfo.InvariantCulture));
            }
            catch (ArgumentOutOfRangeException)
            {
                threshold = DateTime.UtcNow.AddDays(90);
            }

            return threshold;
        }

        // TODO... This is not a meaningful impl yet... 
        // Waiting on Dragos!
        public override async Task ObserveAsync(CancellationToken token)
        {
            await Task.Factory.StartNew(async () =>
            {
                X509Store store = null;
                var expirationWarningThreshold = GetExpirationWarningThreshold();
                var certificatesNearExpiry = new List<X509Certificate2>();
               
                // Dictionary of thumbprint to logical certificate name.
                var certLogicalName = new Dictionary<string, List<string>>();

                try
                {
                    _clusterManifestXml = await FabricClientInstance.ClusterManager.GetClusterManifestAsync().ConfigureAwait(true);
                    //var cert = await this.FabricClientInstance.QueryManager.GetServiceListAsync(new Uri(this.FabricServiceContext.CodePackageActivationContext.ApplicationName));
                    
                    // The objective here is to find all the logical certificate names that are stored in 
                    // the registry and the thumbprint associated with each logical name.  It is possible for a 
                    // thumbprint to be associated with multiple logical names, but not vice-versa.
                    var hklm = Registry.LocalMachine;
                    using (var softwareKey = hklm.OpenSubKey("SOFTWARE"))
                    using (var msftKey = softwareKey.OpenSubKey("Microsoft"))
                    using (var xdbKey = msftKey.OpenSubKey("Service Fabric"))
                    using (var certGroupKey = xdbKey.OpenSubKey("Certificates"))
                    {
                        foreach (var certKeyName in certGroupKey.GetSubKeyNames())
                        {
                            using (var certKey = certGroupKey.OpenSubKey(certKeyName))
                            {
                                var thumbprint = certKey.GetValue("Thumbprint") as string;

                                if (!string.IsNullOrEmpty(thumbprint))
                                {
                                    thumbprint = thumbprint.ToUpper();

                                    if (certLogicalName.ContainsKey(thumbprint))
                                    {
                                        certLogicalName[thumbprint].Add(certKeyName);
                                    }
                                    else
                                    {
                                        certLogicalName.Add(thumbprint, new List<string>() { certKeyName });
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    // Report error as warning health property (no need to cause deployment failures).
                    string message = string.Format(
                    CultureInfo.InvariantCulture,
                    "LocalCertFailure: {0} : {1}",
                    e.GetBaseException().GetType().ToString(),
                    e.GetBaseException().Message);
                    Logger.LogError(ObserverConstants.CertificateObserverName, message);
                    HealthReporter.ReportWarning(ObserverConstants.CertificateObserverName, FabricServiceContext.NodeContext.NodeName, message);

                    throw;
                }

                // Sort the keys logical certificate names so they are always in alphabetical order.
                foreach (var key in certLogicalName)
                {
                    key.Value.Sort();
                }

                try
                {
                    // Open All the interesting cert store locations and names
                    StoreName[] interestingStoreNames = { StoreName.My, StoreName.Root, StoreName.CertificateAuthority, StoreName.AuthRoot };
                    StoreLocation[] interestingStoreLocations = { StoreLocation.CurrentUser, StoreLocation.LocalMachine };

                    foreach (var location in interestingStoreLocations)
                    {
                        foreach (var name in interestingStoreNames)
                        {
                            store = new X509Store(name, location);
                            store.Open(OpenFlags.ReadOnly);

                            // Enumerate all certs
                            foreach (var certificate in store.Certificates)
                            {
                                string commonName = certificate.GetNameInfo(X509NameType.SimpleName, false);

                                // If the certificate is too close to expiry, we will report below
                                //
                                if (certificate.NotAfter < expirationWarningThreshold)
                                {
                                    certificatesNearExpiry.Add(certificate);
                                }

                                string certThumbprint = certificate.Thumbprint.ToUpper();

                                Logger.LogInfo(
                                "{0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8}",
                                ObserverConstants.CertificateObserverName,
                                "Event",
                                NodeName,
                                certificate.Subject,
                                certificate.Thumbprint,
                                certificate.NotBefore.ToString("yyyy-MM-dd"),
                                certificate.NotAfter.ToString("yyyy-MM-dd"),
                                certLogicalName.ContainsKey(certThumbprint) ? string.Join(",", certLogicalName[certThumbprint]) : string.Empty,
                                string.Format("{0}/{1}", store.Location, store.Name));
                            }
                        }
                    }
                    
                    // All done
                    if (certificatesNearExpiry.Count > 0)
                    {
                        HealthReporter.ReportOk(
                        ObserverConstants.CertificateObserverName,
                        FabricServiceContext.NodeContext.NodeName,
                        string.Format(
                        CultureInfo.CurrentCulture,
                        "Certificates nearing expiry count: {0}",
                        certificatesNearExpiry.Count));
                    }
                    else
                    {
                        HealthReporter.ReportOk(
                        ObserverConstants.CertificateObserverName,
                        FabricServiceContext.NodeContext.NodeName,
                        "Local Certificate Report Successful: Not nearing expiry date.");
                    }

                    // Do not run again until the process restarts or enough time has passed
                    //
                    LastRunDateTime = DateTime.Now;
                }
                catch (Exception e)
                {
                    // Report error as warning health property (no need to cause deployment failures).
                    //
                    string message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Certificate Failure: {0} : {1}",
                    e.GetBaseException().GetType().ToString(),
                    e.GetBaseException().Message);
                    Logger.LogError(ObserverConstants.CertificateObserverName, message);
                    HealthReporter.ReportWarning(ObserverConstants.CertificateObserverName, FabricServiceContext.NodeContext.NodeName, message);

                    throw;
                }
                finally
                {
                    // Clean up
                    if (store != null)
                    {
                        store.Close();
                    }
                }
            }, token).ConfigureAwait(true);

        }

        // TODO...
        public override async Task ReportAsync(CancellationToken token)
        {
            await Task.FromResult(token).ConfigureAwait(true);
        }
    }
}