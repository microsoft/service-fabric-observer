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

Example Output in SFX (Warning): 


![alt text](/Documentation/Images/AppObsWarn.png "AppObserver Warning Output example.")  


SF Event Store (Warning and Clear (Ok)):  


![alt text](/Documentation/Images/CpuWarnEventsClear.jpg "AppObserver Health Event example.")  

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

**Active TCP Ports**  

***Problem***: I want to know when any of the services belonging to application fabric:/MyApp have opened more than 2000 TCP ports and generate a Health Warning.  

***Solution***: AppObserver is your friend. This is simple.  

```JSON
[
  {
    "targetApp": "fabric:/MyApp",
    "networkWarningActivePorts": 2000
  }
]
```

**Active Ephemeral TCP Ports**  

***Problem***: I want to know when any of the services belonging to application fabric:/MyApp have opened more than 1500 Ephemeral (dynamic range) TCP ports and generate a Health Warning.  

***Solution***: AppObserver is your friend. This is simple.  

```JSON
[
  {
    "targetApp": "fabric:/MyApp",
    "networkWarningEphemeralPorts": 1500
  }
]
```  

You should of course combine these into one:  

```JSON
[
  {
    "targetApp": "fabric:/MyApp",
    "networkWarningActivePorts": 2000,
    "networkWarningEphemeralPorts": 1500
  }
]
```


**File Handles**  

***Problem***: I want to know how many file handles (total allocated/open) my App services are using and emit a warning when a specified threshold is reached.

***Solution***: AppObserver is your friend. You can do exactly this.  

For an app named MyApp, you would simply add this to PackageRoot/Config/AppObserver.config.json:  

```JSON 
[
  {
    "targetApp": "fabric:/MyApp",
    "warningOpenFileHandles": 2000
  }
]
``` 

So, based on the above configuration, when the number of open file handles held by any of the designated app's services reaches 2000 or higher, FO will emit a Health Warning.  

**Threads**  

***Problem***: I want to know how many threads an App service is using and emit a warning when a specified threshold is reached.

***Solution***: AppObserver is your friend. You can do exactly this. 

```JSON
[
  {
    "targetApp": "fabric:/MyApp42",
    "warningThreadCount": 500
  }
]
```

**Disk Usage - Space**  

***Problem:*** I want to know when my local disks are filling up well before my cluster goes down, along with the VM.

***Solution:*** DiskObserver to the rescue. 

DiskObserver's Threshold setting values are required to be overriden and are set in FO's ApplicationManifest.xml file.

Set the DiskSpacePercentUsageWarningThreshold parameter in DiskObserver's configuration section located in FO's ***ApplicationManifest.xml*** file and it will warn you when disk space consumption reaches 80%:

```XML
    <!-- Disk Observer Warning/Error Thresholds -->
    <Parameter Name="DiskSpacePercentUsageWarningThreshold" DefaultValue="80" />
    <Parameter Name="DiskSpacePercentUsageErrorThreshold" DefaultValue="" />
    <Parameter Name="AverageQueueLengthErrorThreshold" DefaultValue="" />
    <Parameter Name="AverageQueueLengthWarningThreshold" DefaultValue="15" />
```  

Example Output in SFX (Warning): 

![alt text](/Documentation/Images/DiskObsWarn.png "DiskObserver Warning output example.")  


**Memory Usage - Private Working Set as Percentage of Total Physical Memory** 

***Problem:*** I want to know how much memory some or all of my services are using and warn when they hit some meaningful percent-used thresold.  

***Solution:*** AppObserver is your friend.  

The first two JSON objects below tell AppObserver to warn when any of the services under MyApp app reach 30% memory use (as a percentage of total memory). 
 
The third one scopes to all services _but_ 3 and asks AppObserver to warn when any of them hit 40% memory use on the machine (virtual or not).

```JSON
[
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
]
```   

**Memory Usage - Private Working Set - MB in Use** 

***Problem:*** I want to know how much memory some or all of my services are using and warn when they hit some meaningful Megabytes in use thresold.  

***Solution:*** AppObserver is your friend.  

The first two JSON objects below tell AppObserver to warn when any of the services under MyApp app reach 30% memory use (as a percentage of total memory). 
 
The third one scopes to all services _but_ 3 and asks AppObserver to warn when any of them hit 40% memory use on the machine (virtual or not).

```JSON
[
  {
    "targetApp": "fabric:/MyApp",
    "memoryWarningLimitMb": 300
  },
  {
    "targetApp": "fabric:/AnotherApp",
    "memoryWarningLimitMb": 500
  },
  {
    "targetApp": "fabric:/SomeOtherApp",
    "serviceExcludeList": "WhoNeedsMemoryService, NoMemoryNoProblemService, Service42",
    "memoryWarningLimitMb": 600
  }
]
```   


**Different thresholds for different services belonging to the same app**  

***Problem:*** I want to monitor and report on different services for different thresholds 
for one app.  

***Solution:*** Easy. You can supply any number of array items in AppObserver's JSON configuration file
regardless of target - there is no requirement for unique target properties in the object array. 

```JSON
[
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
]
```


If what you really want to do is monitor for different thresholds (like CPU and Memory) for a set of services, you would
just add the threshold properties to one object: 

```JSON
[  
  {
    "targetApp": "fabric:/MyApp",
    "serviceIncludeList": "MyCpuEatingService1, MyCpuEatingService2, MemoryCrunchingService1, MemoryCrunchingService42",
    "cpuWarningLimitPercent": 45,
    "memoryWarningLimitPercent": 30
  }
]
```  

The following configuration tells AppObserver to monitor and report Warnings for multiple resources for two services belonging to MyApp:  
 

```JSON
[
  {
    "targetApp": "fabric:/MyApp",
    "serviceIncludeList": "MyService42, MyOtherService42",
    "cpuErrorLimitPercent": 90,
    "cpuWarningLimitPercent": 80,
    "memoryWarningLimitPercent": 70,
    "networkWarningActivePorts": 800,
    "networkWarningEphemeralPorts": 400
  }
]
``` 

**All App Monitoring** 

***Problem:*** I don't care what the app is, I just want to monitor all app services deployed to any node.  

***Solution:*** AppObserver is your friend.  Note, you can specify all app targets using either "*" or "All" (case doesn't matter). 

The configuration below specifies that AppObserver is to monitor and report thresholds breaches for a collection of metrics on all services that belong to any app that is deployed to the node.  

Note that AppObserver does not (and will not) monitor fabric:/System app services. Also, individual targetApp configuration items will override the global configuration when the same thresholds are supplied. 

In the example below, the setting for cpuWarningLimitPercent for fabric:/MyApp will override the same setting specified in the all inclusive config item. fabric:/MyApp will still be monitored for the other global metrics.

```JSON
[
  {
    "targetApp": "*",
    "cpuWarningLimitPercent": 75,
    "memoryWarningLimitMb" : 500,
    "networkWarningActivePorts": 2000,
    "networkWarningEphemeralPorts": 1500,
    "warningThreadCount": 500
  },
  {
    "targetApp": "fabric:/MyApp",
    "cpuWarningLimitPercent": 50,
    "warningThreadCount": 300
  }
]
```   
***Problem:*** I don't care what the app is, I just want to monitor all app services deployed to any node, except for fabric:/MyApp, where I only care about raw memory use (MB) by any of its services. 

***Solution:*** AppObserver is your friend.  Note, you can specify all app targets using either "*" or "All" (case doesn't matter). 

The configuration below specifies that AppObserver is to monitor and report threshold breaches for a collection of metrics on all services that belong to any app that is deployed to the node, except for fabric:/MyApp.  

```JSON
[
  {
    "targetApp": "*",
    "appExcludeList": "fabric:/MyApp",
    "cpuWarningLimitPercent": 75,
    "memoryWarningLimitPerceent" : 40,
    "networkWarningActivePorts": 2000,
    "networkWarningEphemeralPorts": 1500
  },
  {
    "targetApp": "fabric:/MyApp",
    "memoryWarningLimitMb": 600
  }
]
```   
***Problem:*** I want to monitor the same resource metrics used by 3 apps and I don't like writing JSON.

***Solution:*** AppObserver is your friend.  Note, you can specify all app targets using either "*" or "All"(case doesn't matter). 

The configuration below specifies that AppObserver is to monitor and report threshold breaches for a collection of metrics on all services that belong to the apps supplied in appIncludeList.  

```JSON
[
  {
    "targetApp": "*",
    "appIncludeList": "fabric:/MyApp, fabric:/MyApp2, fabric:/MyApp3",
    "cpuWarningLimitPercent": 75,
    "memoryWarningLimitPerceent" : 40,
    "networkWarningActivePorts": 2000,
    "networkWarningEphemeralPorts": 1500
  }
]
``` 

**Advanced Debugging - Windows Process Dumps**  

***Problem:*** I want to dump any of my SF service processes that are eating too much memory on Windows. (This is not supported on Linux yet).

***Solution:*** AppObserver is your friend.  Note, you can specify all app targets using either "*" or "All"(case doesn't matter). 
In this case, AppObserver will initiate a mini dump (MiniPlus by default) of an offending process running on Windows. You can configure [AzureStorageUploadObserver](/Documentation/Observers.md#azurestorageuploadobserver) to ship the dmp (compressed to zip file) to a blob in your Azure storage account.
Please see [Observers documentation](/Documentation/Observers.md), specifically App and AzureStorageUpload observer sections for details on this process dump and upload feature.

```JSON
{
    "targetApp": "*",
    "appExcludeList": "fabric:/SomeApp, fabric:/SomeOtherApp",
    "cpuWarningLimitPercent": 85,
    "memoryErrorLimitMb": 1048,
    "dumpProcessOnError": true,
    "networkWarningActivePorts": 8000,
    "networkWarningEphemeralPorts": 7500
  }
```

> You can learn all about the currently implemeted Observers and their supported resource properties [***here***](/Documentation/Observers.md). 



**What about the state of the Machine, as a whole?** 

***Problem:*** I want to know when Total CPU Time and Memory Consumption on the VM (or real machine)
reaches certain points and then emit a Warning.  

***Solution:*** Enter NodeObserver.  

NodeObserver doesn't speak JSON (can you believe it!!??....). So, you simply set the desired warning thresholds in FO's ApplicationManifest.xml file:  

```XML
    <!-- NodeObserver Warning/Error Thresholds -->
    <Parameter Name="NodeObserverCpuErrorLimitPercent" DefaultValue="" />
    <Parameter Name="NodeObserverCpuWarningLimitPercent" DefaultValue="90" />
    <Parameter Name="NodeObserverMemoryErrorLimitMb" DefaultValue="" />
    <Parameter Name="NodeObserverMemoryWarningLimitMb" DefaultValue="" />
    <Parameter Name="NodeObserverMemoryErrorLimitPercent" DefaultValue="" />
    <Parameter Name="NodeObserverMemoryWarningLimitPercent" DefaultValue="85" />
```  

Example Output in SFX (Warning): 

![alt text](/Documentation/Images/FODiskNodeObs.png "NodeObserver Warning output example.") 



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
        "port": 443,
        "protocol": "http"
      },
      {
        "hostname": "another.critical.endpoint.net",
        "port": 443,
        "protocol": "http"
      },
      {
        "hostname": "eastusserver0042.database.windows.net",
        "port": 1433,
        "protocol": "tcp"
      }
    ]
  },
  {
    "targetApp": "fabric:/AnotherApp",
    "endpoints": [
      {
        "hostname": "critical.endpoint42.com",
        "port": 443,
        "protocol": "http"
      },
      {
        "hostname": "another.critical.endpoint.net",
        "port": 443,
        "protocol": "http"
      },
      {
        "hostname": "westusserver0007.database.windows.net",
        "port": 1433,
        "protocol": "tcp"
      }
    ]
  }
]
```  

Example Output in SFX (Warning) : 


![alt text](/Documentation/Images/NetworkEndpointWarningDesc.jpg "Logo Title Text 1")   

***Problem***: I want to get telemetry that includes aggregated cluster health for use in alerting.  

***Solution***: [ClusterObserver](/ClusterObserver) is your friend.  

ClusterObserver is a stateless singleton service that runs on one node in your cluster. It can be
configured to emit telemetry to your Azure ApplicationInsights or Azure LogAnalytics workspace out of the box. 
All you have to do is provide either your instrumentation key in two files: Settings.xml and ApplicationInsights.config or 
supply your Azure LogAnalytics WorkspaceId and SharedKey in Settings.xml. You can 
configure CO to emit Warning state signals in addition to the default Error signalling. It's up to you.  

ClusterObserver's ObserverManager config (Settings.xml). These are not overridable:

```XML
  <Section Name="ObserverManagerConfiguration">
    <!-- Required: Amount of time, in seconds, to sleep before the next iteration of observers run loop. 
         0 means run continuously with no pausing. We recommend at least 60 seconds for ClusterObserver. -->
    <Parameter Name="ObserverLoopSleepTimeSeconds" Value="60" />
    <!-- Required: Amount of time, in seconds, ClusterObserver is allowed to complete a run. If this time is exceeded, 
         then the offending observer will be marked as broken and will not run again. 
         Below setting represents 30 minutes. -->
    <Parameter Name="ObserverExecutionTimeout" Value="1800" />
    <!-- Optional: This observer makes async SF Api calls that are cluster-wide operations and can take time in large clusters. -->
    <Parameter Name="AsyncOperationTimeoutSeconds" Value="120" />
    <!-- Required: Location on disk to store observer data, including ObserverManager. 
         ClusterObserver will write to its own directory on this path.
         **NOTE: For Linux runtime target, just supply the name of the directory (not a path with drive letter like you for Windows).** -->
    <Parameter Name="ObserverLogPath" Value="clusterobserver_logs" />
    <!-- Required: Enabling this will generate noisy logs. Disabling it means only Warning and Error information 
         will be locally logged. This is the recommended setting. Note that file logging is generally
         only useful for FabricObserverWebApi, which is an optional log reader service that ships in this repo. -->
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <Parameter Name="EnableEventSourceProvider" Value="True" />
    <!-- Required: Whether the Observer should send all of its monitoring data and Warnings/Errors to configured Telemetry service. This can be overriden by the setting 
         in the ClusterObserverConfiguration section. The idea there is that you can do an application parameter update and turn this feature on and off. -->
    <Parameter Name="EnableTelemetry" Value="True" />
    <!-- Required: Supported Values are AzureApplicationInsights or AzureLogAnalytics as these providers are implemented. -->
    <Parameter Name="TelemetryProvider" Value="AzureLogAnalytics" />
    <!-- Required-If TelemetryProvider is AzureApplicationInsights. -->
    <Parameter Name="AppInsightsInstrumentationKey" Value="" />
    <!-- Required-If TelemetryProvider is AzureLogAnalytics. Your Workspace Id. -->
    <Parameter Name="LogAnalyticsWorkspaceId" Value="" />
    <!-- Required-If TelemetryProvider is AzureLogAnalytics. Your Shared Key. -->
    <Parameter Name="LogAnalyticsSharedKey" Value="" />
    <!-- Required-If TelemetryProvider is AzureLogAnalytics. Log scope. Default is Application. -->
    <Parameter Name="LogAnalyticsLogType" Value="ClusterObserver" />
    <!-- Optional: Amount of time in seconds to wait before ObserverManager signals shutdown. -->
    <Parameter Name="ObserverShutdownGracePeriodInSeconds" Value="3" />
  </Section>
```

By design, CO will send an Ok health state report when a cluster goes from Warning or Error state to Ok.

Example Configuration:  

```XML
  <!-- ClusterObserver Configuration Settings. NOTE: These are overridable settings, see ApplicationManifest.xml. 
       The Values for these will be overriden by ApplicationManifest Parameter settings. Set DefaultValue for each
       overridable parameter in that file, not here, as the parameter DefaultValues in ApplicationManifest.xml will be used, by default. 
       This design is to enable unversioned application-parameter-only updates. This means you will be able to change
       any of the MustOverride parameters below at runtime by doing an ApplicationUpdate with ApplicationParameters flag. 
       See: https://docs.microsoft.com/en-us/azure/service-fabric/service-fabric-application-upgrade-advanced#upgrade-application-parameters-independently-of-version -->
  <Section Name="ClusterObserverConfiguration">
    <!-- Maximum amount of time to wait for an async operation to complete (e.g., any of the SF API calls..) -->
    <Parameter Name="AsyncOperationTimeoutSeconds" Value="" MustOverride="true" />
    <!-- Required: To enable or not enable, that is the question.-->
    <Parameter Name="Enabled" Value="" MustOverride="true" />
    <!-- Optional: Enabling this will generate noisy logs. Disabling it means only Warning and Error information 
         will be locally logged. This is the recommended setting. Note that file logging is generally
         only useful for FabricObserverWebApi, which is an optional log reader service that ships in this repo. -->
    <Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
    <!-- Optional: Emit health details for both Warning and Error for aggregated cluster health? 
         Aggregated Error evaluations will always be transmitted regardless of this setting. -->
    <Parameter Name="EmitHealthWarningEvaluationDetails" Value="" MustOverride="true" />
    <!-- Maximum amount of time a node can be in disabling/disabled/down state before
         emitting a Warning signal.-->
    <Parameter Name="MaxTimeNodeStatusNotOk" Value="" MustOverride="true" />
    <!-- How often to run ClusterObserver. This is a Timespan value, e.g., 00:10:00 means every 10 minutes, for example. -->
    <Parameter Name="RunInterval" Value="" MustOverride="true" />
  </Section>

ApplicationManifest.xml is where you set Overridable settings.  

  <Parameters>
    <!-- ClusterObserver settings. -->
    <Parameter Name="ClusterObserver_InstanceCount" DefaultValue="1" />
    <Parameter Name="Enabled" DefaultValue="true" />
    <Parameter Name="EnableVerboseLogging" DefaultValue="false" />
    <Parameter Name="MaxTimeNodeStatusNotOk" DefaultValue="02:00:00" />
    <Parameter Name="EmitHealthWarningEvaluationDetails" DefaultValue="true" />
    <Parameter Name="RunInterval" DefaultValue="" />
    <Parameter Name="AsyncOperationTimeoutSeconds" DefaultValue="120" />
  </Parameters>
``` 
You deploy CO into your cluster just as you would any other Service Fabric service.
### Application Parameter Updates
<a name="parameterUpdates"></a>
***Problem***: I want to update an Observer's settings without having to redeploy the application.

***Solution***: [Application Upgrade with Configuration-Parameters-only update](https://docs.microsoft.com/en-us/azure/service-fabric/service-fabric-application-upgrade-advanced#upgrade-application-parameters-independently-of-version) to the rescue.

Example: 

Open an Admin Powershell console.

Connect to your Service Fabric cluster using Connect-ServiceFabricCluster command. 

Create a variable that contains all the settings you want update:

```Powershell
$appParams = @{ "FabricSystemObserverEnabled" = "true"; "FabricSystemObserverMemoryWarningLimitMb" = "4096"; }
```

Then execute the application upgrade with

```Powershell
Start-ServiceFabricApplicationUpgrade -ApplicationName fabric:/FabricObserver -ApplicationTypeVersion 3.1.22 -ApplicationParameter $appParams -Monitored -FailureAction rollback
```  

Note: On *Linux*, this will restart FO processes (one at a time, UD Walk with safety checks) due to the way Linux Capabilites work. In a nutshell, for any kind of application upgrade, we have to re-run the FO setup script to get the Capabilities in place. For Windows, FO processes will NOT be restarted.
