# FabricObserver 3.1.24

[**FabricObserver (FO)**](https://github.com/microsoft/service-fabric-observer/releases) is a complete implementation of a production-ready, generic resource usage watchdog service written as a stateless, singleton Service Fabric .NET Core 3.1 application that 
1. Monitors a broad range of machine resources that tend to be very important to all Service Fabric applications, like disk space consumption, CPU use, memory use, endpoint availability, ephemeral TCP port use, and app/cluster certificate health out-of-the-box.
2. Runs on multiple versions of Windows Server and Ubuntu 16.04 and 18.04
3. Provides [an easy-to-use extensibility model](/Documentation/Plugins.md) for creating [custom Observers](/SampleObserverPlugin) out of band (so, you don't need to clone the repo to build an Observer). See [ContainerObserver](https://github.com/GitTorre/ContainerObserver) for a complete plugin impl that extends FO with SF container app resource monitoring and alerting (note that this observer is built into FO as of version 3.1.17).
4. Supports [Configuration Setting Application Updates](/Documentation/Using.md#parameterUpdates) for any observer for any supported setting. 
5. Is actively developed completely in the open. The latest code (generally in flight and not meant for production) lives in the develop branch. It is highly recommended that you only deploy code built from the main branch into your production clusters.

FO is a Stateless Service Fabric Application composed of a single service that runs on every node in your cluster, so it can be deployed and run alongside your applications without any changes to them. Each FO service instance knows nothing about other FO instances in the cluster, by design. 

Note that in version 3.1.18 and higher, AppObserver, ContainerObserver and FabricSystemObserver can run their monitoring loops in parallel on capable hardware (logical CPU processors >= 4).
This feature is enabled by default. You can disable/enable with parameter-only, versionless application upgrades with the related settings found in ApplicationManifest.xml under the AppObserver, ContainerObserver and FabricSystemObserver settings sections.
Enabling this feature greatly decreases the time it takes for an observer running on capable hardware to complete monitoring. This is especially important if you monitor a large number of services.


> FO is not an alternative to existing Monitoring and Diagnostics services. Running side-by-side with existing monitoring services, FO provides useful and timely health information for the nodes (VMs), apps, and services that make up your Service Fabric deployment. 


[Read more about Service Fabric health monitoring](https://docs.microsoft.com/azure/service-fabric/service-fabric-health-introduction) 

FabricObserver is one member of a growing family of open source Service Fabric observability services. The latest member of the family is [FabricHealer](https://github.com/microsoft/service-fabric-healer), which works in conjunction with FabricObserver to auto-mitigate service, node and VM level issues reported by FO.

```If you run your apps on Service Fabric, then you should definitely consider deploying FabricObserver to all of your clusters (Test, Staging, Production).```

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fmicrosoft%2Fservice-fabric-observer%2Fmain%2FDocumentation%2FDeployment%2Fservice-fabric-observer.json)  

## Using FabricObserver  

To quickly learn how to use FO, please see the **[simple scenario-based examples](./Documentation/Using.md)**.  
You can clone the repo, build, and deploy or simply grab latest tested [SFPKG with Microsoft signed binaries](https://github.com/microsoft/service-fabric-observer/releases/latest) from Releases section, modify configs, and deploy.

![alt text](/Documentation/Images/FOClusterView.png "Cluster View App Warning UI")  

## How it works 

Application Level Warnings: 

![alt text](/Documentation/Images/AppWarnClusterView.png "Cluster View App Warning UI")  
![alt text](/Documentation/Images/AppObsWarn.png "AppObserver Warning UI")  
![alt text](/Documentation/Images/ContainerObserver.png "ContainerObserver Warning UI")  

Node Level Warnings: 

![alt text](/Documentation/Images/DiskObsWarn.png "DiskObserver Warning UI")  
![alt text](/Documentation/Images/FODiskNodeObs.png "Multiple Observers Warning UI")  
![alt text](/Documentation/Images/FODiskNodeOkClears.png "Multiple Health Event OK Clearing UI")  

Node Level Machine Info:  

![alt text](/Documentation/Images/FONodeDetails.png "Node Details UI")  

When FabricObserver gracefully exits or updates, it will clear all of the health events it created.  

![alt text](/Documentation/Images/EventClearOnUpdateExit.png "All Health Event Clearing UI")  


FabricObserver comes with a number of Observers that run out-of-the-box. Observers are specialized objects that monitor, point in time, specific resources in use by user service processes, SF system service processes, containers, virtual/physical machines. They emit Service Fabric health reports, diagnostic telemetry and ETW events, then go away until the next round of monitoring. The resource metric thresholds supplied in the configurations of the built-in observers must be set to match your specific monitoring and alerting needs. These settings are housed in [Settings.xml](/FabricObserver/PackageRoot/Config/Settings.xml) and [ApplicationManifest.xml](/FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml). The default settings are useful without any modifications, but you should design your resource usage thresholds according to your specific needs.

When a Warning threshold is reached or exceeded, an observer will send a Health Report to Service Fabric's Health management system (either as a Node or App Health Report, depending on the observer). This Warning state and related reports are viewable in SFX, the Service Fabric EventStore, and Azure's Application Insights/LogAnalytics/ETW, if enabled.

Most observers will remove the Warning state in cases where the issue is transient, but others will maintain a long-running Warning for applications/services/nodes/security problems observed in the cluster. For example, high CPU usage above the user-assigned threshold for a VM or App/Service will put a Node into Warning State (NodeObserver) or Application Warning state (AppObserver), for example, but will soon go back to Healthy if it is a transient spike or after you mitigate the specific problem :-). An expiring certificate Warning from CertificateObsever, however, will remain until you update your application's certificates (Cluster certificates are already monitored by the SF runtime. This is not the case for Application certificates, so use CertificateObserver for this, if necessary).

[Read more about Service Fabric Health Reports](https://docs.microsoft.com/azure/service-fabric/service-fabric-report-health)

FO ships with both an Azure ApplicationInsights and Azure LogAnalytics telemetry implementation. Other providers can be used by implementing the [ITelemetryProvider interface](https://github.com/microsoft/service-fabric-observer/blob/main/FabricObserver.Extensibility/Interfaces/ITelemetryProvider.cs). 

For more information about **the design of FabricObserver**, please see the [Design readme](./Documentation/Design.md). 

## Build and run  

1. Clone the repo.
2. Install [.NET Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1)
3. Build. 

***Note: By default, FO runs as NetworkUser on Windows and sfappsuser on Linux. If you want to monitor SF service processes that run as elevated (System) on Windows, then you must also run FO as System on Windows. There is no reason to run as root on Linux under any circumstances (see the Capabilities binaries implementations, which allow for FO to run as sfappsuser and successfully execute specific commands that require elevated privilege).*** 

For Linux deployments, we have ensured that FO will work as expected as normal user (non-root user). In order for us to do this, we had to implement a setup script that sets [Capabilities](https://man7.org/linux/man-pages/man7/capabilities.7.html) on three proxy binaries which can only run specific commands as root. 
If you deploy from VS, then you will need to use FabricObserver/PackageRoot/ServiceManifest.linux.xml (just copy its contents into ServiceManifest.xml or add the new piece which is simply a SetupEntryPoint section).  

You will also need to modify ApplicationManifest.xml to run Setup as LocalSystem, which maps to root on Linux:

```XML
    </ConfigOverrides>
    <Policies>
      <RunAsPolicy CodePackageRef="Code" UserRef="SystemUser" EntryPointType="Setup" />
    </Policies>  

...

  </DefaultServices>
  <Principals>
    <Users>
      <User Name="SystemUser" AccountType="LocalSystem" />
    </Users>
  </Principals>
```

If you use our build scripts, they will take care of these modifications automatically for linux build output.

You can also run the build scripts from a Powershell console. These include code build, sfpkg generation, and nupkg generation. They are all located in the top level directory of this repo.

FabricObserver can be run and deployed through Visual Studio or Powershell, like any SF app. If you want to add this to your Azure Pipelines CI, 
see [FOAzurePipeline.yaml](/FOAzurePipeline.yaml) for msazure devops build tasks. <strong>Please keep in mind that if your target servers do not already have
.NET Core 3.1 installed (if you deploy VM images from Azure gallery, then they will not have .NET Core 3.1 installed), then you must deploy the SelfContained package.</strong>

If you deploy via ARM, then simply add a path to the FO SFPKG you generate (create pkg folder (in VS, right-click FabricObserverApp project, select Package), zip it, rename the file to whatever you want, replace .zip with .sfpkg file extension...) from your build after updating
the configs to make sense for your applications/services/nodes (store the sfpkg in some blob store you trust): 

```JSON
    "appPackageUrl": {
      "type": "string",
      "metadata": {
        "description": "The URL to the FO sfpkg file you generated."
      }
    },
```  

### Deploy FO using PowerShell  

After you adjust configuration settings to meet to your needs (this means changing settings in Settings.xml for ObserverManager (ObserverManagerConfiguration section) and in ApplicationManifest.xml for observers).

```PowerShell

#cd to the top level repo directory where you cloned FO sources.

cd C:\Users\me\source\repos\service-fabric-observer

#Build FO (Release)

./Build-FabricObserver

#create a $path variable that points to the build output:
#E.g., for Windows deployments:

$path = "C:\Users\me\source\repos\service-fabric-observer\bin\release\FabricObserver\win-x64\self-contained\FabricObserverType"

#For Linux deployments:

#$path = "C:\Users\me\source\repos\service-fabric-observer\bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType"

#Connect to target cluster, for example:

Connect-ServiceFabricCluster -ConnectionEndpoint @('sf-win-cluster.westus2.cloudapp.azure.com:19000') -X509Credential -FindType FindByThumbprint -FindValue '[thumbprint]' -StoreLocation LocalMachine -StoreName 'My'

#Copy $path contents (FO app package) to server:

Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FO3122 -TimeoutSec 1800

#Register FO ApplicationType:

Register-ServiceFabricApplicationType -ApplicationPathInImageStore FO3122

#Create FO application (if not already deployed at lesser version):

New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.1.24   

#OR if updating existing version:  

Start-ServiceFabricApplicationUpgrade -ApplicationName fabric:/FabricObserver -ApplicationTypeVersion 3.1.24 -Monitored -FailureAction rollback
```  

## Configuration Change Support

When a new version of FabricObserver ships, often (not always) there will be new configuration settings, which requires customers to manually update the latest ApplicationManifest.xml and Settings.xml files with their preferred/established settings (current). In order
to remove this manual step when upgrading, we wrote a simple tool that will diff/patch FO config (XML-only) automatically, which will be quite useful in devops workflows. Please try out [XmlDiffPatchSF](https://github.com/GitTorre/XmlDiffPatchSF) and use it in your pipelines or other build automation systems. It should save you some time.

## Observer Model

FO is composed of Observer objects (instance types) that are designed to observe, record, and report on several machine-level environmental conditions inside a Windows or Linux (Ubuntu) VM hosting a Service Fabric node.

Here are the current observers and what they monitor:  

| Resource | Observer |
| --- | --- |
| Application (services) resource usage health monitoring across CPU, File Handles, Memory, Ports (TCP), Threads  | AppObserver |
| Looks for dmp and zip files in AppObserver's MemoryDumps folder, compresses (if necessary) and uploads them to your specified Azure storage account (blob only, AppObserver only, and still Windows only in this version of FO) | AzureStorageUploadObserver |
| Application (user) and cluster certificate health monitoring | CertificateObserver |
| Container resource usage health monitoring across CPU and Memory | ContainerObserver |
| Disk (local storage disk health/availability, space usage, IO, Folder size monitoring) | DiskObserver |
| SF System Services resource usage health monitoring across CPU, File Handles, Memory, Ports (TCP), Threads | FabricSystemObserver |
| Networking - general health and monitoring of availability of user-specified, per-app endpoints | NetworkObserver |
| CPU/Memory/File Handles(Linux)/Firewalls(Windows)/TCP Ports usage at machine level | NodeObserver |
| OS/Hardware - OS install date, OS health status, list of hot fixes, hardware configuration, AutoUpdate configuration, Ephemeral TCP port range, TCP ports in use, memory and disk space usage | OSObserver |
| Service Fabric Configuration information | SFConfigurationObserver |
| **Another resource you find important** | **Observer [that you implement](./Documentation/Plugins.md)** |

To learn more about the current Observers and their configuration, please see the [Observers readme](./Documentation/Observers.md).  
    
```
Just observe it.
```

# Operational Telemetry 
Please see [FabricObserver Operational Telemetry](/Documentation/OperationalTelemetry.md) for detailed information on the user agnostic (Non-PII) data FabricObserver sends to Microsoft (opt out with a simple configuration parameter change).
Please consider leaving this enabled so your friendly neighborhood Service Fabric devs can understand how FabricObserver is doing in the real world. We would really appreciate it!

# Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit https://cla.microsoft.com.

When you submit a pull request, a CLA-bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., label, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.  

Please see [CONTRIBUTING.md](CONTRIBUTING.md) for development process information.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
