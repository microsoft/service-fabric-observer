using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;

namespace FabricObserverTests
{
    public interface ICodePackageActivationContext : IDisposable
    {
        string ApplicationTypeName { get; }
        string ApplicationName { get; }
        string CodePackageVersion { get; }
        string CodePackageName { get; }
        string ContextId { get; }
        string TempDirectory { get; }
        string LogDirectory { get; }
        string WorkDirectory { get; }

        event EventHandler<PackageModifiedEventArgs<DataPackage>> DataPackageModifiedEvent;
        event EventHandler<PackageModifiedEventArgs<CodePackage>> CodePackageModifiedEvent;
        event EventHandler<PackageRemovedEventArgs<DataPackage>> DataPackageRemovedEvent;
        event EventHandler<PackageAddedEventArgs<ConfigurationPackage>> ConfigurationPackageAddedEvent;
        event EventHandler<PackageRemovedEventArgs<ConfigurationPackage>> ConfigurationPackageRemovedEvent;
        event EventHandler<PackageAddedEventArgs<CodePackage>> CodePackageAddedEvent;
        event EventHandler<PackageModifiedEventArgs<ConfigurationPackage>> ConfigurationPackageModifiedEvent;
        event EventHandler<PackageAddedEventArgs<DataPackage>> DataPackageAddedEvent;
        event EventHandler<PackageRemovedEventArgs<CodePackage>> CodePackageRemovedEvent;

        ApplicationPrincipalsDescription GetApplicationPrincipals();
        IList<string> GetCodePackageNames();
        CodePackage GetCodePackageObject(string packageName);
        IList<string> GetConfigurationPackageNames();
        ConfigurationPackage GetConfigurationPackageObject(string packageName);
        IList<string> GetDataPackageNames();
        DataPackage GetDataPackageObject(string packageName);
        EndpointResourceDescription GetEndpoint(string endpointName);
        KeyedCollection<string, EndpointResourceDescription> GetEndpoints();
        KeyedCollection<string, ServiceGroupTypeDescription> GetServiceGroupTypes();
        string GetServiceManifestName();
        string GetServiceManifestVersion();
        KeyedCollection<string, ServiceTypeDescription> GetServiceTypes();
        void ReportApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions);
        void ReportApplicationHealth(HealthInformation healthInfo);
        void ReportDeployedApplicationHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions);
        void ReportDeployedApplicationHealth(HealthInformation healthInfo);
        void ReportDeployedServicePackageHealth(HealthInformation healthInfo, HealthReportSendOptions sendOptions);
        void ReportDeployedServicePackageHealth(HealthInformation healthInfo);
    }
}

