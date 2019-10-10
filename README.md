# Fabric Observer

**FabricObserver (FO)** is an implementation of a Service Fabric watchdog service as a Service Fabric application that 
1. Out-of-the-box monitors a broad range of resources that tend to be important to all service fabric applications, like disk, CPU, memory, networking, and cluster certificates.
2. Provides a simple model in which new observers can be built and configured and run automatically through a .NET development model.

aka FO works today and can be extended to meet the special needs of your Service Fabric applciation.

```
FO is not a replacement for, nor is it an alternative to, existing Monitoring and Diagnostics services.
```

## How it works

Fabric Observer comes with a number of Observers that run as-is. However, most of the observers should not be run until the proper thresholds are set that match your specific needs and application. These settings can be set via [Settings.xml](/FabricObserver/PackageRoot/Config/Settings.xml).

In Warning and Error states, an observer will signal status (reports) via a Service Fabric Health Report. Thresholds and information about what constitues a Warning or Error is configured by the user. These are viewable with other health reports in SFX and event store.

FO also ships with an AppInsights telemetry implementation, but you can use whatever provider you want as long you implement the [IObserverTelemetryProvider interface](/FabricObserver/Observers/Interfaces/IObserverTelemetryProvider.cs). 

For more information about **the design of FabricObserver**, please see the [Design readme](./Documentation/Design.md). 

## Build and run

To learn how to build FabricObserver, please see the [Build readme](Build.md).  

## Observer Model

FO is composed of Observer objects (instance types) that are designed to observe, record, and report on several machine-level environmental conditions inside a Windows VM (node) of a Service Fabric cluster. It is an isolated, node-only service.

Since observers live in their own application, they monitor other applications through the resource side effects of those applications. Here are the current observers and what they monitor:

| Resource | Observer |
| --- | --- |
| Disk (local storage disk health/availability, space usage, IO) | DiskObserver |
| CPU/Memory (per process across Apps and Fabric system services) | Node Observer |
| OS properties (install date, health status, list of hot fixes, hardware configuration, etc., ephemeral port range and real-time OS health status) | OS Observer |
| Networking (general health and monitoring of availability of user-specified, per-app endpoints) | Network Observer |
| Service Fabric Infrastructure | FabricSystemObserver |
| Application certificates | Certificate Observer |
| **Another resource you find important** | **Observer you implement** |

To learn more about the current **Observers and their configuration**, please see the [Observers readme](./Documentation/Observers.md).  
    
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
