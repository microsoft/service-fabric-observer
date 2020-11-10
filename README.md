# FabricObserver 3.0.10

[**FabricObserver (FO)**](https://github.com/microsoft/service-fabric-observer/releases) is a complete implementation of a generic resource usage watchdog service written as a stateless, singleton Service Fabric .NET Core 3.1 application that 
1. Monitors a broad range of resources that tend to be important to all Service Fabric applications, like disk, CPU, memory, networking, and cluster certificates out-of-the-box.
2. Runs on multiple versions of Windows Server and Ubuntu 16.04 and 18.04
3. Provides [an easy-to-use extensibility model](/Documentation/Plugins.md) for creating [custom Observers](/SampleObserverPlugin) out of band (so, you don't need to clone the repo to build an Observer). See [ContainerObserver](https://github.com/GitTorre/ContainerObserver) for a complete plugin impl that extends FO with SF container app resource monitoring and alerting.
4. Supports [Configuration Setting Application Updates](/Documentation/Using.md#parameterUpdates) for any observer for any supported setting. 

FO is a Stateless Service Fabric Application composed of a single service that runs on every node in your cluster, so it can be deployed and run alongside your applications without any changes to them. Each FO service instance knows nothing about other FO instances in the cluster, by design.  


> FO is not an alternative to existing Monitoring and Diagnostics services. Running side-by-side with existing monitoring services, FO provides useful and timely health information for the nodes (VMs), apps, and services that make up your Service Fabric deployment. 


[Read more about Service Fabric health monitoring](https://docs.microsoft.com/azure/service-fabric/service-fabric-health-introduction)

## Using FabricObserver  

To quickly learn how to use FO, please see the **[simple scenario-based examples](./Documentation/Using.md)**.  
You can clone the repo, build, and deploy or simply grab latest tested [SFPKG with Microsoft signed binaries](https://github.com/microsoft/service-fabric-observer/releases/latest) from Releases section, modify configs, and deploy.


## How it works 

Application Level Warnings: 

![alt text](/Documentation/Images/AppCpuWarnCluster.jpg "")  
![alt text](/Documentation/Images/AppDetailsWarning.jpg "")  

Node Level Warnings: 

![alt text](/Documentation/Images/Chaos3.jpg "")  
![alt text](/Documentation/Images/MultipleWarningsSFX.jpg "")  
![alt text](/Documentation/Images/MultipleWarningsOKClearSFX.jpg "")  

Node Level Machine Info:  

![alt text](/Documentation/Images/OSObsKBLinks.jpg "")  


FabricObserver comes with a number of Observers that run out-of-the-box. Observers are specialized objects which wake up, monitor a specific set of resources, emit a health report, and sleep again. However, the thresholds and configurations of the included observers must be set to match the specific needs of your cluster. These settings can be set via [Settings.xml](/FabricObserver/PackageRoot/Config/Settings.xml).

When a Warning threshold is reached or exceeded, an observer will send a Health Report to Service Fabric's Health management system (either as a Node or App Health Report, depending on the observer). This Warning state and related reports are viewable in SFX, the Service Fabric EventStore, and Azure's Application Insights, if enabled.

Most observers will remove the Warning state in cases where the issue is transient, but others will maintain a long-running Warning for applications/services/nodes/security problems observed in the cluster. For example, high CPU usage above the user-assigned threshold for a VM or App/Service will put a Node into Warning State (NodeObserver) or Application Warning state (AppObserver), for example, but will soon go back to Healthy if it is a transient spike or after you mitigate the specific problem :-). An expiring certificate Warning from CertificateObsever, however, will remain until you update your application's certificates (Cluster certificates are already monitored by the SF runtime. This is not the case for Application certificates, so use CertificateObserver for this, if necessary).

[Read more about Service Fabric Health Reports](https://docs.microsoft.com/azure/service-fabric/service-fabric-report-health)

FO ships with both an Azure ApplicationInsights and Azure LogAnalytics telemetry implementation. Other providers can be used by implementing the [ITelemetryProvider interface](/FabricObserver/Observers/Interfaces/ITelemetryProvider.cs). 

For more information about **the design of FabricObserver**, please see the [Design readme](./Documentation/Design.md). 

## Build and run  

1. Clone the repo.
2. Install [.NET Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1)
3. Build. 

Note: There is no need to run FO as system on Windows or root on Linux. 

For Linux deployments, we have ensured that FO will work as expected as normal user. In order for us to do this, we had to implement a setup script that sets [Capabilities](https://man7.org/linux/man-pages/man7/capabilities.7.html) on a proxy binary which can run netstat -tnap elevated. 
If you deploy from VS, then you will need to use FabricObserver/PackageRoot/ServiceManifest.linux.xml (just copy its contents into ServiceManifest.xml or add the new piece which is simply a SetupEntryPoint section). You 
will also need to do the same with ApplicationManifest.xml (see FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.linux.xml for required changes). If you use our build scripts, they will take care of these modifications automatically for linux build output.
Just run Build-FabricObserver.ps1

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


## Observer Model

FO is composed of Observer objects (instance types) that are designed to observe, record, and report on several machine-level environmental conditions inside a Windows VM (node) of a Service Fabric cluster. It is an isolated, node-only service. 

Since observers live in their own application, they monitor other applications through the resource side effects of those applications. Here are the current observers and what they monitor:

| Resource | Observer |
| --- | --- |
| Application (services) resource usage health monitoring across CPU, Memory, Ports (TCP) | AppObserver |
| Application (user) and cluster certificate health monitoring | CertificateObserver |
| Disk (local storage disk health/availability, space usage, IO) | DiskObserver |
| Service Fabric Sysytem Services resource usage health monitoring across CPU, Memory, Ports (TCP) | FabricSystemObserver |
| Networking (general health and monitoring of availability of user-specified, per-app endpoints) | NetworkObserver |
| CPU/Memory/Firewalls/Ports resource usage at machine level | NodeObserver |
| OS properties (install date, health status, list of hot fixes, hardware configuration, etc., ephemeral port range and real-time OS health status) | OSObserver |
| Service Fabric Configuration information | SFConfigurationObserver |
| **Another resource you find important** | **Observer you implement** |

To learn more about the current Observers and their configuration, please see the [Observers readme](./Documentation/Observers.md).  
    
```
Just observe it.
```

# Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit https://cla.microsoft.com.

When you submit a pull request, a CLA-bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., label, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.  

Please see [CONTRIBUTING.md](CONTRIBUTING.md) for development process information.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
