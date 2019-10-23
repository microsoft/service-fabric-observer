# FabricObserver

**FabricObserver (FO)** is a working implementation of a Service Fabric watchdog service as a Service Fabric application that 
1. Monitors a broad range of resources that tend to be important to all service fabric applications, like disk, CPU, memory, networking, and cluster certificates out-of-the-box.
2. Provides a simple model in which new observers can be built and configured and run automatically through a .NET development model.

FO is a standalone Service Fabric Application, so it can be deployed and run alongside your applications without any change to them.


> FO is not an alternative to existing Monitoring and Diagnostics services. Running side-by-side with existing monitoring services, FO provides useful and timely health information for the nodes (VMs), apps, and services that make up your Service Fabric deployment. 

[Read more about Service Fabric health monitoring](https://docs.microsoft.com/azure/service-fabric/service-fabric-health-introduction)

## How it works

Fabric Observer comes with a number of Observers that run out-of-the-box. Observers are specialized objects which wake up, monitor a specific set of resources, emit a health report, and sleep again. However, the thresholds and configurations of the included observers must be set to match the specific needs of your cluster. These settings can be set via [Settings.xml](/FabricObserver/PackageRoot/Config/Settings.xml).

> It is not recommended to run FO with the default thresholds. It is recommended to first enable observers with ignored thresholds (by setting the threshold to 0), then run FO to monitor over a learning period the baseline behavior of your cluster along the measured metrics. After the learning period, the observers should be enabled with thresholds that make sense for the cluster.

In Warning and Error states, an observer will signal `Warning` Service Fabric Health Reports. This warning state and related reports are viewable in SFX, the EventStore, and AppInsights, if enabled. Most observers will clean the Warning state in the case the issue is transient, but others will indicate a long-running problem with applications in the cluster. For example, high CPU usage above the user-assigned threshold will put a cluster in Warning State if the NodeObserver is enabled, but will soon go back to Healthy if it is a transient spike. An expiring certificate Warning however will remain until the user takes manual intervention to update their application's certificates. 

[Read more about Service Fabric Health Reports](https://docs.microsoft.com/azure/service-fabric/service-fabric-report-health)

FO ships with an AppInsights telemetry implementation, other providers can be used by implementing the [IObserverTelemetryProvider interface](/FabricObserver/Observers/Interfaces/IObserverTelemetryProvider.cs). 

For more information about **the design of FabricObserver**, please see the [Design readme](./Documentation/Design.md). 

## Build and run

1. Clone the repo
2. Install the [.NET Core 2.2 SDK](https://dotnet.microsoft.com/download/dotnet-core/2.2) (if you want to build FabricObserverWebApi)*
   Please note that if you are using VS2017, you will have to install .NET Core 2.1, as VS2017 does not support 2.2 for ASPNETCORE.
3. FabricObserverApp can be run and deployed through Visual Studio or Powershell, like any SF app

## Observer Model

FO is composed of Observer objects (instance types) that are designed to observe, record, and report on several machine-level environmental conditions inside a Windows VM (node) of a Service Fabric cluster. It is an isolated, node-only service. 

Since observers live in their own application, they monitor other applications through the resource side effects of those applications. Here are the current observers and what they monitor:

| Resource | Observer |
| --- | --- |
| Disk (local storage disk health/availability, space usage, IO) | DiskObserver |
| CPU/Memory (per process across Apps and Fabric system services) | NodeObserver |
| OS properties (install date, health status, list of hot fixes, hardware configuration, etc., ephemeral port range and real-time OS health status) | OSObserver |
| Networking (general health and monitoring of availability of user-specified, per-app endpoints) | NetworkObserver |
| Service Fabric Infrastructure | FabricSystemObserver |
| Application certificates | CertificateObserver |
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
