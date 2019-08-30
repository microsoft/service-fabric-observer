**INTRODUCTION**

FabricObserver (FO) is a complete, working example of a fully functioning, easily-configurable stateless Service Fabric (SF) service that monitors both user services and internal fabric services for potential problems related to resource usage across Disk, CPU, Memory, Networking. It employs a simple .NET development model, enabling the quick creation of new observers. You can deploy the [sfpkgs](https://github.com/microsoft/service-fabric-observer/releases) directly as part of an [ARM deployment](https://github.com/Azure-Samples/service-fabric-dotnet-quickstart/blob/master/ARM/UserApp.json), for example, and get benefits from this service without doing anything. However, you will definitely want to take advantage of configuration to better support your specific workloads and app deployments. As Service Fabric developers, you can extend FabricObserver very easily and make it work specifically for your needs. These aren't just watchdogs. They are observers.

FO is composed of Observer objects (instance types) that are designed to observe, record, and report on several machine-level environmental conditions inside a Windows VM (node) of a Service Fabric cluster. An observer, by design, does not communicate over the Internet. In fact, FabricObserver does not listen on any ports. It is an isolated, node-only, service.

In Warning and Error states, an observer will signal status (reports) via a Service Fabric Health Report (e.g., extended, high CPU and Memory usage, extended Disk IO, limited Disk space, Networking issues (connectivity), Firewall Rule leaking, port exhaustion. Since an observer doesn't know what's good or what's bad by simply observing some resource state (in some cases, like disk space monitoring and fabric system service monitoring, there are predefined maxima/minima), a user should provide Warning and Error thresholds that ring the alarm bells that make sense for their workloads. These settings are supplied and packaged in Service Fabric configuration files (both XML and JSON are supported).

FO ships with an AppInsights telemetry implementation, but you can use whatever provider you want as long you implement the [IObserverTelemetryProvider interface](/FabricObserver/Observers/Interfaces/IObserverTelemetryProvider.cs). 

In this iteration of the project, we have designed Observers that can be configured by users to monitor the machine-level side effects of an App (defined as a collection of Service Fabric services). The user-controlled, App-focused functionality is primarily encapsulated in  **AppObserver**, which observes, records and reports on real-time CPU, Memory, Disk, active and ephemeral TCP port counts as defined by the user in a Data configuration file (JSON, App array objects). Likewise, there is the configurable, App-focused **NetworkObserver**.  

For the most part, **we focus on both the state of the system surrounding a Service Fabric app and the specific side effects of 
the app's behavior**. Most observers focus on machine level states: Disk (local storage disk health/availability, space usage, IO), CPU (per process across Apps and Fabric system services), Memory (per process across Apps and Fabric system services as well as system-wide), Networking (general health and monitoring of availability of user-specified, per-app endpoints), basic OS properties (install date, health status, list of hot fixes, hardware configuration, etc., ephemeral port range and real-time status), and Service Fabric infrastructure information and state. The design is decidedly simplistic and easy to understand/implement. C# and .NET make this very easy to do. 

**The uber goal here is to greatly reduce the complexity of host OS, Service Fabric infrastructure, and Service Fabric app health monitoring**. For SF app developers, it's as easy as supplying some simple configuration files (JSON and XML).
Empower cloud developers to understand and learn from inevitable failure conditions by not adding more cognitive complexity to their lives - reliably ***enabling self-mitigation through service health knowledge before correctable problems turn into outages***.  
  
To learn more about Observers and their configuration, please see the [observers readme](./Documentation/Observers.md).  
  
For more information about the design of FabricObserver, please check out the [design readme](./Documentation/Design.md).  
  
**Conclusion**

Observers are designed to be low impact, long-lived objects that perform specific observational and related reporting activities across iteration intervals defined in configuration settings for each observer type. As their name clearly suggests, they do not mitigate in this first version. They observe, record, and report. For Warning and Errors, we will utilize Service Fabric Health store and reporting mechanisms to surface important information in SFX. This release also includes a telemtry provider interface and ships with an AppInsights implementation. So, you can stream events to AppInsights by simply enabling the feature in Settings.xml and providing your AppInsights key.  

We also have an internal telemetry provider that will stream FO-related telemetry to Microsoft so that we can understand how FO is doing in the real world. This facility does not send PII data or anything related to your apps, settings, etc... The only data that we receive is basic information about the SF cluster and FO-specific initialization and internal health states. Just look at that the implementation in the [TelemetryLib](/TelemetryLib) folder. You can turn this off, of course, but we would appreciate it if you didn't. We want to know how FO is doing and we need data to understand what's working and what isn't - again, FO-only data, nothing about your services, your data, your information - you can see exactly what we are sending since you have the code. 

We hope you find FabricObserver useful and that it never adds any burden to your cluster - it should be a silent partner up until it let's you know something is wrong based on what you asked it to observe and report. Please put FO into all of your Service Fabric deployments and help yourself catch issues before they become incidents. Also, we'd love your contributions and partnership. 

Just observe it.

# Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.microsoft.com.

When you submit a pull request, a CLA-bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., label, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.  

Please see [CONTRIBUTING.md](CONTRIBUTING.md) for development process information.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
