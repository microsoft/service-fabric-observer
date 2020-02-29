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
            this.ApplicationName = applicationName;
            this.ApplicationTypeName = applicationTypeName;
            this.CodePackageName = codePackageName;
            this.CodePackageVersion = codePackageVersion;
            this.ContextId = context;
            this.LogDirectory = logDirectory;
            this.TempDirectory = tempDirectory;
            this.WorkDirectory = workDirectory;
            this.ServiceManifestName = serviceManifestName;
            this.ServiceManifestVersion = serviceManifestVersion;
        }

        private string ServiceManifestName { get; set; }

        private string ServiceManifestVersion { get; set; }

        /// <inheritdoc/>
        public string ApplicationName { get; private set; }

        /// <inheritdoc/>
        public string ApplicationTypeName { get; private set; }

        /// <inheritdoc/>
        public string CodePackageName { get; private set; }

        /// <inheritdoc/>
        public string CodePackageVersion { get; private set; }

        /// <inheritdoc/>
        public string ContextId { get; private set; }

        /// <inheritdoc/>
        public string LogDirectory { get; private set; }

        /// <inheritdoc/>
        public string TempDirectory { get; private set; }

        /// <inheritdoc/>
        public string WorkDirectory { get; private set; }

        // Interface required events. These are never used. Ignore the Warnings(CS0414).

        /// <inheritdoc/>
        public event EventHandler<PackageAddedEventArgs<CodePackage>> CodePackageAddedEvent;

        /// <inheritdoc/>
        public event EventHandler<PackageModifiedEventArgs<CodePackage>> CodePackageModifiedEvent;

        /// <inheritdoc/>
        public event EventHandler<PackageRemovedEventArgs<CodePackage>> CodePackageRemovedEvent;

        /// <inheritdoc/>
        public event EventHandler<PackageAddedEventArgs<ConfigurationPackage>> ConfigurationPackageAddedEvent;

        /// <inheritdoc/>
        public event EventHandler<PackageModifiedEventArgs<ConfigurationPackage>> ConfigurationPackageModifiedEvent;

        /// <inheritdoc/>
        public event EventHandler<PackageRemovedEventArgs<ConfigurationPackage>> ConfigurationPackageRemovedEvent;

        /// <inheritdoc/>
        public event EventHandler<PackageAddedEventArgs<DataPackage>> DataPackageAddedEvent;

        /// <inheritdoc/>
        public event EventHandler<PackageModifiedEventArgs<DataPackage>> DataPackageModifiedEvent;

        /// <inheritdoc/>
        public event EventHandler<PackageRemovedEventArgs<DataPackage>> DataPackageRemovedEvent;

        /// <inheritdoc/>
        public ApplicationPrincipalsDescription GetApplicationPrincipals()
        {
            return default(ApplicationPrincipalsDescription);
        }

        /// <inheritdoc/>
        public IList<string> GetCodePackageNames()
        {
            return new List<string>() { this.CodePackageName };
        }

        /// <inheritdoc/>
        public CodePackage GetCodePackageObject(string packageName)
        {
            return default(CodePackage);
        }

        /// <inheritdoc/>
        public IList<string> GetConfigurationPackageNames()
        {
            return new List<string>() { string.Empty };
        }

        /// <inheritdoc/>
        public ConfigurationPackage GetConfigurationPackageObject(string packageName)
        {
            return default(ConfigurationPackage);
        }

        /// <inheritdoc/>
        public IList<string> GetDataPackageNames()
        {
            return new List<string>() { string.Empty };
        }

        /// <inheritdoc/>
        public DataPackage GetDataPackageObject(string packageName)
        {
            return default(DataPackage);
        }

        /// <inheritdoc/>
        public EndpointResourceDescription GetEndpoint(string endpointName)
        {
            return default(EndpointResourceDescription);
        }

        /// <inheritdoc/>
        public KeyedCollection<string, EndpointResourceDescription> GetEndpoints()
        {
            return null;
        }

        /// <inheritdoc/>
        public KeyedCollection<string, ServiceGroupTypeDescription> GetServiceGroupTypes()
        {
            return null;
        }

        /// <inheritdoc/>
        public string GetServiceManifestName()
        {
            return this.ServiceManifestName;
        }

        /// <inheritdoc/>
        public string GetServiceManifestVersion()
        {
            return this.ServiceManifestVersion;
        }

        /// <inheritdoc/>
        public KeyedCollection<string, ServiceTypeDescription> GetServiceTypes()
        {
            return null;
        }

        /// <inheritdoc/>
        public void ReportApplicationHealth(HealthInformation healthInformation)
        {
        }

        /// <inheritdoc/>
        public void ReportDeployedServicePackageHealth(HealthInformation healthInformation)
        {
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            this.Dispose(true);
        }

        /// <inheritdoc/>
        public void ReportApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
        }

        /// <inheritdoc/>
        public void ReportDeployedApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
        }

        /// <inheritdoc/>
        public void ReportDeployedServicePackageHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
        }
    }
}