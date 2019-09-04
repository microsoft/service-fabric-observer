**INTRODUCTION**

FabricObserver (FO) is a complete, working example of a fully functioning, easily-configurable stateless Service Fabric (SF) service that monitors both user services and internal fabric services for potential user-defined problems related to resource usage across Disk, CPU, Memory, Networking. It employs a simple .NET development model, enabling the quick creation of new observers for developers. It employs simple configuration patterns, enabling OPS people to easily deploy with meaningful warning thresholds for the Service Fabric applications and services they ship. You can deploy the [sfpkgs](https://github.com/microsoft/service-fabric-observer/releases) directly as part of an [ARM deployment](https://github.com/Azure-Samples/service-fabric-dotnet-quickstart/blob/master/ARM/UserApp.json), for example, and get benefits from this service without doing anything. However, you will definitely want to take advantage of configuration to better support your **specific workloads and app deployments**. As Service Fabric developers and ops pros, you can extend FabricObserver very easily and make it work specifically for your needs. 

**FO is not a replacement for, nor is it an alternative to, existing Monitoring and Diagnostics services.**  Think of it only as a highly configurable and extensible watchdog service that is designed to be run in Service Fabric clusters composed of Windows VMs.

FO is composed of Observer objects (instance types) that are designed to observe, record, and report on several machine-level environmental conditions inside a Windows VM (node) of a Service Fabric cluster. An observer, by design, does not communicate over the Internet. In fact, FabricObserver does not listen on any ports. It is an isolated, node-only service.

In Warning and Error states, an observer will signal status (reports) via a Service Fabric Health Report (e.g., extended, high CPU and Memory usage, extended Disk IO, limited Disk space, Networking issues (connectivity), Firewall Rule leaking, port exhaustion. Since an observer doesn't know what's good or what's bad by simply observing some resource state (in some cases, like disk space monitoring and fabric system service monitoring, there are predefined maxima/minima), a user should provide Warning and Error thresholds that ring the alarm bells that make sense for their workloads. These settings are supplied and packaged in Service Fabric configuration files (both XML and JSON are supported).

FO ships with an AppInsights telemetry implementation, but you can use whatever provider you want as long you implement the [IObserverTelemetryProvider interface](/FabricObserver/Observers/Interfaces/IObserverTelemetryProvider.cs). 

In this iteration of the project, we have designed Observers that can be configured by users to monitor the machine-level side effects of a **Service Fabric App - defined as a collection of Service Fabric services**. The user-controlled, App-focused functionality is primarily encapsulated in  **AppObserver**, which observes, records and reports on CPU, Memory, Disk, active and ephemeral TCP port counts as defined by the user in a Data configuration file (JSON, App array objects). Likewise, there is the configurable, App-focused **NetworkObserver**. 

As the author of Service Fabric Apps it is your responsibility to determine what threshold values make sense for your specific workloads. ***It is very important that you spend some time measuring the impact your service code has on the surrounding environment before supplying warning thresholds for FO to report on***. For sure, you do not want to add noise to your life nor to the hard-working Microsoft Support professionals, and especially not the Service Fabric dev team. Please be thoughtful and spend quality learning about your service behavior as it relates to resource use and then map this knowledge to thresholds that actually can help you in times of real Warning.

For the most part, **we focus on both the state of the system surrounding Service Fabric app services and the specific resource side effects of service behavior**. Most observers focus on machine level states: Disk (local storage disk health/availability, space usage, IO), CPU (per process across Apps and Fabric system services), Memory (per process across Apps and Fabric system services as well as system-wide), Networking (general health and monitoring of availability of user-specified, per-app endpoints), basic OS properties (install date, health status, list of hot fixes, hardware configuration, etc., ephemeral port range and real-time OS health status), and Service Fabric infrastructure information and state. The design is decidedly simplistic and easy to understand/extend. C# and .NET make this very easy to do.   

To learn about **Building FO**, please see the [Build readme](Build.md).  
    
To learn more about **Observers and their configuration**, please see the [Observers readme](./Documentation/Observers.md).  
  
For more information about **the design of FabricObserver**, please see the [Design readme](./Documentation/Design.md).  
  

**Conclusion**

Observers are designed to be low impact, long-lived objects that perform specific observational and related reporting activities across iteration intervals defined in configuration settings for each observer type. As their name clearly suggests, they do not mitigate in this first version. They observe, record, and report. For Warning and Errors, we will utilize Service Fabric Health store and reporting mechanisms to surface important information in SFX. This release also includes a telemtry provider interface and ships with an AppInsights implementation. So, you can stream events to AppInsights by simply enabling the feature in Settings.xml and providing your AppInsights key.  

We hope you find FabricObserver useful and that it never adds any burden to your cluster - it should be a silent partner up until it let's you know something is wrong based on what you asked it to observe and report. Please put FO into all of your Service Fabric deployments and help yourself catch issues before they become incidents. Also, we'd love your contributions and partnership. 

Just observe it.

# Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us the rights to use your contribution. For details, visit https://cla.microsoft.com.

When you submit a pull request, a CLA-bot will automatically determine whether you need to provide a CLA and decorate the PR appropriately (e.g., label, comment). Simply follow the instructions provided by the bot. You will only need to do this once across all repos using our CLA.  

Please see [CONTRIBUTING.md](CONTRIBUTING.md) for development process information.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
