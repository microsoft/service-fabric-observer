## FabricObserver 3.2.10

[**FabricObserver (FO)**](https://github.com/microsoft/service-fabric-observer) is a production-ready watchdog service with an easy-to-use extensibility model, written as a stateless, singleton Service Fabric **.NET 6** application that by default  

1. Monitors a broad range of physical machine resources that tend to be very important to all Service Fabric services and maps these metrics to the related Service Fabric entities.
2. Runs on multiple versions of Windows Server and Ubuntu.
3. Provides [an easy-to-use extensibility model](https://github.com/microsoft/service-fabric-observer/blob/main/Documentation/Plugins.md) for creating [custom Observers](https://github.com/microsoft/service-fabric-observer/blob/main/SampleObserverPlugin) out of band (so, you don't need to clone the repo to build an Observer). In this way, FabricObserver is also an "Observer" platform. 
4. Supports [Configuration Setting Application Updates](/Documentation/Using.md#parameterUpdates) for any observer for any supported setting. 
5. Is actively developed in the open. 

> FabricObserver targets SF runtime versions 9 and higher. 

FO is a Stateless Service Fabric Application composed of a single service that runs on every node in your cluster, so it can be deployed and run alongside your applications without any changes to them. Each FO service instance knows nothing about other FO instances in the cluster, by design. 

```If you run your apps on Service Fabric, then you should definitely consider deploying FabricObserver to all of your clusters (Test, Staging, Production).```

## Using FabricObserver  

To quickly learn how to use FO, please see the **[simple scenario-based examples](https://github.com/microsoft/service-fabric-observer/blob/main/Documentation/Using.md)**.  

![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/FOClusterView.png "Cluster View App Warning UI")  

## How it works 

Application Level Warnings: 

![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/AppWarnClusterView.png "Cluster View App Warning UI")  
![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/AppObsWarn.png "AppObserver Warning UI")  
![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/ContainerObserver.png "ContainerObserver Warning UI")  

Node Level Warnings: 

![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/DiskObsWarn.png "DiskObserver Warning UI")  
![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/FODiskNodeObs.png "Multiple Observers Warning UI")  
![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/FODiskNodeOkClears.png "Multiple Health Event OK Clearing UI")  

Node Level Machine Info:  

![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/FONodeDetails.png "Node Details UI")  

When FabricObserver gracefully exits or updates, it will clear all of the health events it created.  

![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/EventClearOnUpdateExit.png "All Health Event Clearing UI")  

FabricObserver comes with a number of Observers that run out-of-the-box. Observers are specialized objects that monitor, point in time, specific resources in use by user service processes, SF system service processes, containers, virtual/physical machines. They emit Service Fabric health reports, diagnostic telemetry and ETW events, then go away until the next round of monitoring. The resource metric thresholds supplied in the configurations of the built-in observers must be set to match your specific monitoring and alerting needs. These settings are housed in [Settings.xml](/FabricObserver/PackageRoot/Config/Settings.xml) and [ApplicationManifest.xml](/FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml). The default settings are useful without any modifications, but you should design your resource usage thresholds according to your specific needs.

When a Warning threshold is reached or exceeded, an observer will send a Health Report to Service Fabric's Health management system (either as a Node or Application Health Report, depending on the observer). This Warning state and related reports are viewable in SFX, the Service Fabric EventStore, and Azure's Application Insights/LogAnalytics/ETW, if enabled and configured.

Most observers will remove the Warning state in cases where the issue is transient, but others will maintain a long-running Warning for applications/services/nodes/security problems observed in the cluster. For example, high CPU usage above the user-assigned threshold for a VM or App/Service will put a Node into Warning State (NodeObserver) or Application Warning state (AppObserver), for example, but will soon go back to Healthy if it is a transient spike or after you mitigate the specific problem :-). An expiring certificate Warning from CertificateObsever, however, will remain until you update your application's certificates (Cluster certificates are already monitored by the SF runtime. This is not the case for Application certificates, so use CertificateObserver for this, if necessary).

[Read more about Service Fabric Health Reports](https://docs.microsoft.com/azure/service-fabric/service-fabric-report-health)

FO ships with both an Azure ApplicationInsights and Azure LogAnalytics telemetry implementation. Other providers can be used by implementing the [ITelemetryProvider interface](https://github.com/microsoft/service-fabric-observer/blob/main/FabricObserver.Extensibility/Interfaces/ITelemetryProvider.cs). 

For more information about **the design of FabricObserver**, please see the [Design readme](https://github.com/microsoft/service-fabric-observer/blob/main/Documentation/Design.md). 

***Note: By default, FO runs as NetworkUser on Windows and sfappsuser on Linux. If you want to monitor SF service processes that run as elevated (System) on Windows, then you must also run FO as System on Windows. There is no reason to run as root on Linux under any circumstances (see the Capabilities binaries implementations, which allow for FO to run as sfappsuser and successfully execute specific commands that require elevated privilege).*** 

For Linux deployments, we have ensured that FO will work as expected as normal user (non-root user). In order for us to do this, we had to implement a setup script that sets [Capabilities](https://man7.org/linux/man-pages/man7/capabilities.7.html) on three proxy binaries which can only run specific commands as root. 

## Configuration Change Support

When a new version of FabricObserver ships, often (not always) there will be new configuration settings, which requires customers to manually update the latest ApplicationManifest.xml and Settings.xml files with their preferred/established settings (current). In order
to remove this manual step when upgrading, we wrote a simple tool that will diff/patch FO config (XML-only) automatically, which will be quite useful in devops workflows. Please try out [XmlDiffPatchSF](https://github.com/GitTorre/XmlDiffPatchSF) and use it in your pipelines or other build automation systems. It should save you some time.

## Observer Model

FO is composed of Observer objects (instance types) that are designed to observe, record, and report on several machine-level environmental conditions inside a Windows or Linux (Ubuntu) VM hosting a Service Fabric node.

Here are the current observers and what they monitor:  

| Resource | Observer |
| --- | --- |
| Application (services) resource usage health monitoring across CPU, File Handles, Memory, Ports (TCP), Threads | AppObserver |
| Looks for dmp and zip files in AppObserver's MemoryDumps folder, compresses (if necessary) and uploads them to your specified Azure storage account (blob only, AppObserver only, and still Windows only in this version of FO) | AzureStorageUploadObserver |
| Application (user) and cluster certificate health monitoring | CertificateObserver |
| Container resource usage health monitoring across CPU and Memory | ContainerObserver |
| Disk (local storage disk health/availability, space usage, IO) | DiskObserver |
| SF System Services resource usage health monitoring across CPU, File Handles, Memory, Ports (TCP), Threads | FabricSystemObserver |
| Networking - general health and monitoring of availability of user-specified, per-app endpoints | NetworkObserver |
| CPU/Memory/File Handles(Linux)/Firewalls(Windows)/TCP Ports usage at machine level | NodeObserver |
| OS/Hardware - OS install date, OS health status, list of hot fixes, hardware configuration, AutoUpdate configuration, Ephemeral TCP port range, TCP ports in use, memory and disk space usage | OSObserver |
| Service Fabric Configuration information | SFConfigurationObserver |
| **Another resource you find important** | **Observer [that you implement](https://github.com/microsoft/service-fabric-observer/blob/main/Documentation/Plugins.md)** |

To learn more about the current Observers and their configuration, please see the [Observers readme](https://github.com/microsoft/service-fabric-observer/blob/main/Documentation/Observers.md).  
    
```
Just observe it.
```