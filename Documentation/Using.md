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
    "target": "fabric:/MyApp",
    "cpuWarningLimitPct": 65
  }
]
```

Now, let's say you have more then one App deployed (a common scenario) and only want to watch one or more of the services in a specific set of apps. 
You would add this to PackageRoot/Config/AppObserver.config.json:

```JSON 
[
 {
    "target": "fabric:/MyApp",
    "serviceIncludeList": "ILikeCpuService, ILikeCpuTooService",
    "cpuWarningLimitPct": 45
  },
  {
    "target": "fabric:/MyOtherApp",
    "serviceIncludeList": "ILoveCpuService",
    "cpuWarningLimitPct": 65
  }
]
```

Example Output in SFX: 


![alt text](/Documentation/Images/AppCpuWarningDescription.jpg "Logo Title Text 1")  


SF Event Store:  


![alt text](/Documentation/Images/CpuWarnEventsClear.jpg "Logo Title Text 1")  



**Disk Usage - Space**  

***Problem:*** I want to know when my local disks are filling up well before my cluster goes down, along with the VM.

***Solution:*** DiskObserver to the rescue. 

DiskObserver's Threshold settings are housed in the usual place: PackageRoot/Config/Settings.xml.

Add this to DiskObserver's configuration section and it will warn you when disk space consumption reaches 80%:

```XML
  <Section Name="DiskObserverConfiguration">
    <Parameter Name="Enabled" Value="True" />
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <Parameter Name="DiskSpacePercentWarningThreshold" Value="80" />
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
    "target": "fabric:/MyApp",
    "memoryWarningLimitPercent": 30
  },
  {
    "target": "fabric:/AnotherApp",
    "memoryWarningLimitPercent": 30
  },
  {
    "target": "fabric:/SomeOtherApp",
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
    "target": "fabric:/MyApp",
    "serviceIncludeList": "MyCpuEatingService1, MyCpuEatingService2",
    "cpuWarningLimitPct": 45
  },
  {
    "target": "fabric:/MyApp",
    "serviceIncludeList": "MemoryCrunchingService1, MemoryCrunchingService42",
    "memoryWarningLimitPercent": 30
  }
```


If what you really want to do is monitor for different thresholds (like CPU and Memory) for a set of services, you would
just add the threshold properties to one object: 

```JSON
  {
    "target": "fabric:/MyApp",
    "serviceIncludeList": "MyCpuEatingService1, MyCpuEatingService2, MemoryCrunchingService1, MemoryCrunchingService42",
    "cpuWarningLimitPct": 45,
    "memoryWarningLimitPercent": 30
  }
```  

The following configuration tells AppObserver to monitor and report Warnings for multiple resources for two services belonging to MyApp:  
 

```JSON
{
    "target": "fabric:/MyApp",
    "serviceIncludeList": "MyService42, MyOtherService42",
    "cpuErrorLimitPct": 90,
    "cpuWarningLimitPct": 80,
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
    "appTarget": "fabric:/MyApp",
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
    "appTarget": "fabric:/AnotherApp",
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
