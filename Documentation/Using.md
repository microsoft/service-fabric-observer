# Using FabricObserver - Scenarios


> You can learn all about the currently implemeted Observers and their supported resource properties [***here***](/Documentation/Observers.md). 


**CPU Usage - CPU Time**  


***Problem***: I want to know how much CPU my App is using and emit a warning when a specified threshold is reached... 

***Solution***: AppObserver is your friend. You can do exactly this, plus more. :)  

> A Service Fabric Application is a logical "container" of related services, an abstract encapsulation of versioned configuration and code.

For an app named MyApp, you would simply add this to PackageRoot/Config/AppObserver.config.json:  

```JSON 
[
  {
    "targetApp": "fabric:/MyApp",
    "cpuWarningLimitPercent": 65
  }
]
```

Now, let's say you have more then one App deployed (a common scenario) and only want to watch one or more of the services in a specific set of apps. 
You would add this to PackageRoot/Config/AppObserver.config.json:

```JSON 
[
 {
    "targetApp": "fabric:/MyApp",
    "serviceIncludeList": "ILikeCpuService, ILikeCpuTooService",
    "cpuWarningLimitPercent": 45
  },
  {
    "targetApp": "fabric:/MyOtherApp",
    "serviceIncludeList": "ILoveCpuService",
    "cpuWarningLimitPercent": 65
  }
]
```

Example Output in SFX: 


![alt text](/Documentation/Images/AppCpuWarningDescription.jpg "Logo Title Text 1")  


SF Event Store:  


![alt text](/Documentation/Images/CpuWarnEventsClear.jpg "Logo Title Text 1")  

<a name="targetType"></a>You can also supply a targetType parameter instead of a target in AppObserver.config.json. This instructs FO to monitor all applications of a given ApplicationType (this is an advanced SF deployment scenario, generally, but it is very useful for large or complex systems with many apps). All app services of a given type will be observed and reported on with specified Warning thresholds.
When you use this property, you can also use either serviceExcludeList or serviceIncludeList JSON property settings to further filter what you want observed and reported.

```JSON 
[
  {
    "targetAppType": "MyAppType",
    "cpuWarningLimitPercent": 40,
    "memoryWarningLimitPercent": 30,
    "networkWarningActivePorts": 80,
    "networkWarningEphemeralPorts": 40,
    "serviceExcludeList": "someService42,someService53"
  }
]
```

**Disk Usage - Space**  

***Problem:*** I want to know when my local disks are filling up well before my cluster goes down, along with the VM.

***Solution:*** DiskObserver to the rescue. 

DiskObserver's Threshold settings are housed in the usual place: PackageRoot/Config/Settings.xml.

Add this to DiskObserver's configuration section and it will warn you when disk space consumption reaches 80%:

```XML
  <Section Name="DiskObserverConfiguration">
    <Parameter Name="Enabled" Value="True" />
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <Parameter Name="DiskSpacePercentUsageWarningThreshold" Value="80" />
  </Section>
```  

Example Output in SFX: 

![alt text](/Documentation/Images/DiskWarnDescriptionNode.jpg "Logo Title Text 1")  


**Memory Usage** 

***Problem:*** I want to know how much memory some or all of my services are using and warn when they hit some meaningful percent-used thresold.  

***Solution:*** AppObserver is your friend.  

The first two JSON objects below tell AppObserver to warn when any of the services under MyApp app reach 30% memory use (as a percentage of total memory). 
 
The third one scopes to all services _but_ 3 and asks AppObserver to warn when any of them hit 40% memory use on the machine (virtual or not).

```JSON
  {
    "targetApp": "fabric:/MyApp",
    "memoryWarningLimitPercent": 30
  },
  {
    "targetApp": "fabric:/AnotherApp",
    "memoryWarningLimitPercent": 30
  },
  {
    "targetApp": "fabric:/SomeOtherApp",
    "serviceExcludeList": "WhoNeedsMemoryService, NoMemoryNoProblemService, Service42",
    "memoryWarningLimitPercent": 40
  }
```   


**Different thresholds for different services belonging to the same app**  

***Problem:*** I want to monitor and report on different services for different thresholds 
for one app.  

***Solution:*** Easy. You can supply any number of array items in AppObserver's JSON configuration file
regardless of target - there is no requirement for unique target properties in the object array. 

```JSON
  {
    "targetApp": "fabric:/MyApp",
    "serviceIncludeList": "MyCpuEatingService1, MyCpuEatingService2",
    "cpuWarningLimitPercent": 45
  },
  {
    "targetApp": "fabric:/MyApp",
    "serviceIncludeList": "MemoryCrunchingService1, MemoryCrunchingService42",
    "memoryWarningLimitPercent": 30
  }
```


If what you really want to do is monitor for different thresholds (like CPU and Memory) for a set of services, you would
just add the threshold properties to one object: 

```JSON
  {
    "targetApp": "fabric:/MyApp",
    "serviceIncludeList": "MyCpuEatingService1, MyCpuEatingService2, MemoryCrunchingService1, MemoryCrunchingService42",
    "cpuWarningLimitPercent": 45,
    "memoryWarningLimitPercent": 30
  }
```  

The following configuration tells AppObserver to monitor and report Warnings for multiple resources for two services belonging to MyApp:  
 

```JSON
{
    "targetApp": "fabric:/MyApp",
    "serviceIncludeList": "MyService42, MyOtherService42",
    "cpuErrorLimitPercent": 90,
    "cpuWarningLimitPercent": 80,
    "memoryWarningLimitPercent": 70,
    "networkWarningActivePorts": 800,
    "networkWarningEphemeralPorts": 400
  }
``` 

> You can learn all about the currently implemeted Observers and their supported resource properties [***here***](/Documentation/Observers.md). 



**What about the state of the Machine, as a whole?** 

***Problem:*** I want to know when Total CPU Time and Memory Consumption on the VM (or real machine)
reaches certain points and then emit a Warning.  

***Solution:*** Enter NodeObserver.  

NodeObserver doesn't speak JSON (can you believe it!!??....). So, you simply set the desired warning
thresholds in PackageRoot/Config/Settings.xml:  

```XML
  <Section Name="NodeObserverConfiguration">
    <Parameter Name="Enabled" Value="True" />
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <Parameter Name="CpuWarningLimitPercent" Value="90" />
    <Parameter Name="MemoryWarningLimitPercent" Value="90" />
  </Section>
```  

Example Output in SFX: 

![alt text](/Documentation/Images/MemoryWarnDescriptionNode.jpg "Logo Title Text 1") 



**Networking: Endpoint Availability**  

***Problem:*** I want to know when the endpoints I care about are not reachable.  

***Solution:*** NetworkObserver at your service. 

Let's say you have 3 critical endpoints that you want to monitor for availability and emit warnings when they are unreachable. 

In NetworkObserver's configuration file (PackageRoot/Config/NetworkObserver.config.json), add this:  


```JSON
[
  {
    "targetApp": "fabric:/MyApp",
    "endpoints": [
      {
        "hostname": "critical.endpoint.com",
        "port": 443
      },
      {
        "hostname": "another.critical.endpoint.net",
        "port": 443
      },
      {
        "hostname": "eastusserver0042.database.windows.net",
        "port": 1433
      }
    ]
  },
  {
    "targetApp": "fabric:/AnotherApp",
    "endpoints": [
      {
        "hostname": "critical.endpoint42.com",
        "port": 443
      },
      {
        "hostname": "another.critical.endpoint.net",
        "port": 443
      },
      {
        "hostname": "westusserver0007.database.windows.net",
        "port": 1433
      }
    ]
  }
]
```  

Example Output in SFX: 


![alt text](/Documentation/Images/NetworkEndpointWarningDesc.jpg "Logo Title Text 1")   

***Problem***: I want to get telemetry that includes aggregated cluster health for use in alerting.  

***Solution***: [ClusterObserver](/ClusterObserver) is your friend.  

ClusterObserver is a stateless singleton service that runs on one node in your cluster. It can be
configured to emit telemetry to your Azure ApplicationInsights or Azure LogAnalytics workspace out of the box. 
All you have to do is provide either your instrumentation key in two files: Settings.xml and ApplicationInsights.config or 
supply your Azure LogAnalytics WorkspaceId and SharedKey in Settings.xml. You can 
configure CO to emit Warning state signals in addition to the default Error signalling. It's up to you.  

Telemetry config sample:

```XML
    <!-- Optional: Diagnostic Telemetry. Azure ApplicationInsights and Azure LogAnalytics support is already implemented, 
         but you can implement whatever provider you want. See IObserverTelemetry interface. -->
    <Parameter Name="EnableTelemetryProvider" Value="True" />
    <!-- Required: Supported Values are AzureApplicationInsights or AzureLogAnalytics as these providers are implemented. -->
    <Parameter Name="TelemetryProvider" Value="AzureLogAnalytics" />
    <!-- Required-If TelemetryProvider is AzureApplicationInsights. -->
    <Parameter Name="AppInsightsInstrumentationKey" Value="" />
    <!-- Required-If TelemetryProvider is AzureLogAnalytics. Your Workspace Id. -->
    <Parameter Name="LogAnalyticsWorkspaceId" Value="[LogAnalytics Workspace Id]" />
    <!-- Required-If TelemetryProvider is AzureLogAnalytics. Your Shared Key. -->
    <Parameter Name="LogAnalyticsSharedKey" Value="[Log Analytics Shared Key]" />
    <!-- Required-If TelemetryProvider is AzureLogAnalytics. Log scope. Default is Application. -->
    <Parameter Name="LogAnalyticsLogType" Value="Application" />
```

By design, CO will send an Ok health state report when a cluster goes from Warning or Error state to Ok.

Example Configuration:  

```XML
  <Section Name="ClusterObserverConfiguration">
    <!-- Required Parameter for all Observers: To enable or not enable, that is the question.-->
    <Parameter Name="Enabled" Value="True" />
    <!-- Optional: Enabling this will generate noisy logs. Disabling it means only Warning and Error information 
         will be locally logged. This is the recommended setting. Note that file logging is generally
         only useful for FabricObserverWebApi, which is an optional log reader service that ships in this repo. -->
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <!-- Optional: This observer makes async SF Api calls that are cluster-wide operations and can take time in large deployments. -->
    <Parameter Name="ClusterOperationTimeoutSeconds" Value="120" />
    <!-- Emit health details for both Warning and Error for aggregated cluster health? Error details will
    always be transmitted.-->
    <Parameter Name="EmitHealthWarningEvaluationDetails" Value="True" />
    <!-- Emit Ok aggregated health state telemetry when cluster health goes from Warning or Error to Ok. -->
    <Parameter Name="EmitOkHealthStateTelemetry" Value="True" />
    <!-- Maximum amount of time a node can be in disabling/disabled/down state before
         emitting a Warning signal.-->
    <Parameter Name="MaxTimeNodeStatusNotOk" Value="02:00:00"/>
  </Section>
``` 

You deploy CO into your cluster just as you would any other Service Fabric service.
