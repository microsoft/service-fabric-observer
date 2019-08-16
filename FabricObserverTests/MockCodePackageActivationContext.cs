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
        public string ApplicationName { get; private set; }
        public string ApplicationTypeName { get; private set; }
        public string CodePackageName { get; private set; }
        public string CodePackageVersion { get; private set; }
        public string ContextId { get; private set; }
        public string LogDirectory { get; private set; }
        public string TempDirectory { get; private set; }
        public string WorkDirectory { get; private set; }

        // Stub events... These are never used. Ignore the Warnings(CS0067).
        public event EventHandler<PackageAddedEventArgs<CodePackage>> CodePackageAddedEvent;
        public event EventHandler<PackageModifiedEventArgs<CodePackage>> CodePackageModifiedEvent;
        public event EventHandler<PackageRemovedEventArgs<CodePackage>> CodePackageRemovedEvent;
        public event EventHandler<PackageAddedEventArgs<ConfigurationPackage>> ConfigurationPackageAddedEvent;
        public event EventHandler<PackageModifiedEventArgs<ConfigurationPackage>> ConfigurationPackageModifiedEvent;
        public event EventHandler<PackageRemovedEventArgs<ConfigurationPackage>> ConfigurationPackageRemovedEvent;
        public event EventHandler<PackageAddedEventArgs<DataPackage>> DataPackageAddedEvent;
        public event EventHandler<PackageModifiedEventArgs<DataPackage>> DataPackageModifiedEvent;
        public event EventHandler<PackageRemovedEventArgs<DataPackage>> DataPackageRemovedEvent;

        public ApplicationPrincipalsDescription GetApplicationPrincipals()
        {
            return default(ApplicationPrincipalsDescription);
        }

        public IList<string> GetCodePackageNames()
        {
            return new List<string>() { this.CodePackageName };
        }

        public CodePackage GetCodePackageObject(string packageName)
        {
            return default(CodePackage);
        }

        public IList<string> GetConfigurationPackageNames()
        {
            return new List<string>() { "" };
        }

        public ConfigurationPackage GetConfigurationPackageObject(string packageName)
        {
            return default(ConfigurationPackage);
        }

        public IList<string> GetDataPackageNames()
        {
            return new List<string>() { "" };
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
            return this.ServiceManifestName;
        }

        public string GetServiceManifestVersion()
        {
            return this.ServiceManifestVersion;
        }

        public KeyedCollection<string, ServiceTypeDescription> GetServiceTypes()
        {
            return null;
        }

        public void ReportApplicationHealth(HealthInformation healthInformation)
        {
            return;
        }

        public void ReportDeployedServicePackageHealth(HealthInformation healthInformation)
        {
            return;
        }

        public void ReportDeployedApplicationHealth(HealthInformation healthInformation)
        {
            return;
        }

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                this.disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            this.Dispose(true);
        }

        public void ReportApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
            return;
        }

        public void ReportDeployedApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
            return;
        }

        public void ReportDeployedServicePackageHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions)
        {
            return;
        }

        #endregion
    }
}