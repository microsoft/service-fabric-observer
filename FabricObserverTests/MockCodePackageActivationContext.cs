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
        /// <param name="ApplicationName"></param>
        /// <param name="ApplicationTypeName"></param>
        /// <param name="CodePackageName"></param>
        /// <param name="CodePackageVersion"></param>
        /// <param name="Context"></param>
        /// <param name="LogDirectory"></param>
        /// <param name="TempDirectory"></param>
        /// <param name="WorkDirectory"></param>
        /// <param name="ServiceManifestName"></param>
        /// <param name="ServiceManifestVersion"></param>
        public MockCodePackageActivationContext(
            string ApplicationName,
            string ApplicationTypeName,
            string CodePackageName,
            string CodePackageVersion,
            string Context,
            string LogDirectory,
            string TempDirectory,
            string WorkDirectory,
            string ServiceManifestName,
            string ServiceManifestVersion)
        {
            this.ApplicationName = ApplicationName;
            this.ApplicationTypeName = ApplicationTypeName;
            this.CodePackageName = CodePackageName;
            this.CodePackageVersion = CodePackageVersion;
            this.ContextId = Context;
            this.LogDirectory = LogDirectory;
            this.TempDirectory = TempDirectory;
            this.WorkDirectory = WorkDirectory;
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

        // Interface required events... These are never used. Ignore the Warnings(CS0067).

        /// <inheritdoc/>
        public event EventHandler<PackageAddedEventArgs<CodePackage>> CodePackageAddedEvent = null;

        /// <inheritdoc/>
        public event EventHandler<PackageModifiedEventArgs<CodePackage>> CodePackageModifiedEvent = null;

        /// <inheritdoc/>
        public event EventHandler<PackageRemovedEventArgs<CodePackage>> CodePackageRemovedEvent = null;

        /// <inheritdoc/>
        public event EventHandler<PackageAddedEventArgs<ConfigurationPackage>> ConfigurationPackageAddedEvent = null;

        /// <inheritdoc/>
        public event EventHandler<PackageModifiedEventArgs<ConfigurationPackage>> ConfigurationPackageModifiedEvent = null;

        /// <inheritdoc/>
        public event EventHandler<PackageRemovedEventArgs<ConfigurationPackage>> ConfigurationPackageRemovedEvent = null;

        /// <inheritdoc/>
        public event EventHandler<PackageAddedEventArgs<DataPackage>> DataPackageAddedEvent = null;

        /// <inheritdoc/>
        public event EventHandler<PackageModifiedEventArgs<DataPackage>> DataPackageModifiedEvent = null;

        /// <inheritdoc/>
        public event EventHandler<PackageRemovedEventArgs<DataPackage>> DataPackageRemovedEvent = null;

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
            return;
        }

        /// <inheritdoc/>
        public void ReportDeployedServicePackageHealth(HealthInformation healthInformation)
        {
            return;
        }

        /// <inheritdoc/>
        public void ReportDeployedApplicationHealth(HealthInformation healthInformation)
        {
            return;
        }

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                }

                this.disposedValue = true;
            }
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
            return;
        }

        /// <inheritdoc/>
        public void ReportDeployedApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
            return;
        }

        /// <inheritdoc/>
        public void ReportDeployedServicePackageHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
            return;
        }
    }
}