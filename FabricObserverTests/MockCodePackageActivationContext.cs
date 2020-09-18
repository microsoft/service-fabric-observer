// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;

namespace FabricObserverTests
{
    public class MockCodePackageActivationContext : ICodePackageActivationContext
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MockCodePackageActivationContext"/> class.
        /// </summary>
        /// <param name="applicationName">applicationName.</param>
        /// <param name="applicationTypeName">applicationTypeName.</param>
        /// <param name="codePackageName">codePackageName.</param>
        /// <param name="codePackageVersion">codePackageVersion.</param>
        /// <param name="context">context.</param>
        /// <param name="logDirectory">logDirectory.</param>
        /// <param name="tempDirectory">tempDirectory.</param>
        /// <param name="workDirectory">workDirectory.</param>
        /// <param name="serviceManifestName">serviceManifestName.</param>
        /// <param name="serviceManifestVersion">serviceManifestVersion.</param>
        public MockCodePackageActivationContext(
            string applicationName,
            string applicationTypeName,
            string codePackageName,
            string codePackageVersion,
            string context,
            string logDirectory,
            string tempDirectory,
            string workDirectory,
            string serviceManifestName,
            string serviceManifestVersion)
        {
            ApplicationName = applicationName;
            ApplicationTypeName = applicationTypeName;
            CodePackageName = codePackageName;
            CodePackageVersion = codePackageVersion;
            ContextId = context;
            LogDirectory = logDirectory;
            TempDirectory = tempDirectory;
            WorkDirectory = workDirectory;
            ServiceManifestName = serviceManifestName;
            ServiceManifestVersion = serviceManifestVersion;
        }

        private string ServiceManifestName { get; set; }

        private string ServiceManifestVersion { get; set; }

        public string ApplicationName { get; private set; }

        public string ApplicationTypeName { get; private set; }

        public string CodePackageName { get; private set; }

        public string CodePackageVersion { get; private set; }

        public string ContextId { get; private set; }

        public string LogDirectory { get; private set; }

        public string TempDirectory { get; private set; }

        public string WorkDirectory { get; private set; }

        // Interface required events. These are never used. Ignore the Warnings(CS0067) The event 'MockCodePackageActivationContext.CodePackageRemovedEvent' is never used
#pragma warning disable CS0067
        
        public event EventHandler<PackageAddedEventArgs<CodePackage>> CodePackageAddedEvent;
        public event EventHandler<PackageModifiedEventArgs<CodePackage>> CodePackageModifiedEvent;
        public event EventHandler<PackageRemovedEventArgs<CodePackage>> CodePackageRemovedEvent;
        public event EventHandler<PackageAddedEventArgs<ConfigurationPackage>> ConfigurationPackageAddedEvent;
        public event EventHandler<PackageModifiedEventArgs<ConfigurationPackage>> ConfigurationPackageModifiedEvent;
        public event EventHandler<PackageRemovedEventArgs<ConfigurationPackage>> ConfigurationPackageRemovedEvent;
        public event EventHandler<PackageAddedEventArgs<DataPackage>> DataPackageAddedEvent;
        public event EventHandler<PackageModifiedEventArgs<DataPackage>> DataPackageModifiedEvent;
        public event EventHandler<PackageRemovedEventArgs<DataPackage>> DataPackageRemovedEvent;
#pragma warning restore

        public ApplicationPrincipalsDescription GetApplicationPrincipals()
        {
            return default(ApplicationPrincipalsDescription);
        }

        public IList<string> GetCodePackageNames()
        {
            return new List<string>() { CodePackageName };
        }

        public CodePackage GetCodePackageObject(string packageName)
        {
            return default(CodePackage);
        }

        public IList<string> GetConfigurationPackageNames()
        {
            return new List<string>() { string.Empty };
        }

        public ConfigurationPackage GetConfigurationPackageObject(string packageName)
        {
            return default(ConfigurationPackage);
        }

        public IList<string> GetDataPackageNames()
        {
            return new List<string>() { string.Empty };
        }

        public DataPackage GetDataPackageObject(string packageName)
        {
            return default(DataPackage);
        }

        public EndpointResourceDescription GetEndpoint(string endpointName)
        {
            return default(EndpointResourceDescription);
        }

        public KeyedCollection<string, EndpointResourceDescription> GetEndpoints()
        {
            return null;
        }

        public KeyedCollection<string, ServiceGroupTypeDescription> GetServiceGroupTypes()
        {
            return null;
        }

        public string GetServiceManifestName()
        {
            return ServiceManifestName;
        }

        public string GetServiceManifestVersion()
        {
            return ServiceManifestVersion;
        }

        public KeyedCollection<string, ServiceTypeDescription> GetServiceTypes()
        {
            return null;
        }

        public void ReportApplicationHealth(HealthInformation healthInformation)
        {
        }

        public void ReportDeployedServicePackageHealth(HealthInformation healthInformation)
        {
        }

        public void ReportDeployedApplicationHealth(HealthInformation healthInformation)
        {
        }

        private bool disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (this.disposedValue)
            {
                return;
            }

            if (disposing)
            {
                // TODO: dispose managed state (managed objects).
            }

            this.disposedValue = true;
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }

        public void ReportApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
        }

        public void ReportDeployedApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
        }

        public void ReportDeployedServicePackageHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
        }
    }
}