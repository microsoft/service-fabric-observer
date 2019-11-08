# Observers

Observers are low-impact, long-lived objects that perform specialied monitoring and reporting activities. Observers monitor and report, but they aren't designed to take action. Observers generally monitor appliations through their side effects on the node, like resource usage, but do not actually communicate with the applications. Observers report to SF Event Store (viewable through SFX) in warning and error states, and can use built-in AppInsights support to report there as well.

> AppInsights can be enabled in `Settings.xml` by providing your AppInsights key

### Logging

Each Observer instance logs to a directory of the same name. You can configure the base directory of the output and log verbosity level (verbose or not). If you enable telemetry and provide an ApplicationInsights key, then you will also see the output in your log analytics queries. Each observer has configuration settings in PackageRoot/Config/Settings.xml. AppObserver and NetworkObserver house their runtime config settings (error/warning thresholds) in json files located in PackageRoot/Observers.Data folder.  

### Emiting Errors

Service Fabric Error Health Events block upgrades and other important Fabric runtime operations. Error thresholds should be set such that putting the cluster in an emergency state incurs less cost than allowing the state to continue. For this reason, Fabric Observer by default ***treats Errors as Warnings***.  However if your cluster health policy is to [ConsiderWarningAsError](https://docs.microsoft.com/en-us/azure/service-fabric/service-fabric-health-introduction#cluster-health-policy), FabricObserver has a ***high risk of putting your cluster in an error state***. Proceed with caution.

## [How to implement a new Observer](#writing-a-new-observer)
## Currently Implemented Observers  

| Observer | Description |
| :--- | :--- |
| [AppObserver](#appobserver) | Monitors CPU usage, Memory use, and Disk space availability for Service Fabric Application services (processes). Alerts when user-supplied thresholds are reached. |
| [CertificateObserver](#certificateobserver) | Monitors the expiration date of the cluster certificate and any other certificates provided by the user. Warns when close to expiration. |
| [DiskObserver](#diskobserver) | Monitors, storage disk information like capacity and IO rates. Alerts when user-supplied thresholds are reached. |
| [FabricSystemObserver](#fabricsystemobserver) | Monitors CPU usage, Memory use, and Disk space availability for Service Fabric System services (compare to AppObserver) |
| [NetworkObserver](#networkobserver) | Monitors outbound connection state for user-supplied endpoints (hostname/port pairs), i.e. it checks that the node can reach specific endpoints. |
| [NodeObserver](#nodeobserver) | This observer monitors VM level resource usage across CPU, Memory, firewall rules, static and dynamic ports (aka ephemeral ports). |
| [OSObserver](#osobserver) | Records basic OS properties across OS version, OS health status, physical/virtual memory use, number of running processes, number of active TCP ports (active/ephemeral), number of enabled firewall rules, list of recent patches/hotfixes. |
| [SFConfigurationObserver](#sfconfigurationobserver) | Records information about the currently installed Service Fabric runtime environment. |

# Fabric Observers - What they do and how to configure them  

You can quickly get started by reading [this](/Documentation/Using.md).  

  
## AppObserver  
Observer that monitors CPU usage, Memory use, and Disk space
availability for Service Fabric Application services (processes). This
observer will alert when user-supplied thresholds are reached.

### Input
JSON config file supplied by user, stored in
PackageRoot/Observers.Data folder. This data contains JSON arrays
objects which constitute Service Fabric Apps (identified by service
URI's). Users supply Error/Warning thresholds for CPU use, Memory use and Disk
IO, ports. Memory values are supplied as number of megabytes... CPU and
Disk Space values are provided as percentages (integers: so, 80 = 80%...)... 
**Please note that you can omit any of these properties. You can also supply 0 as the value, which means that threshold
will be ignored (they are not omitted below so you can see what a fully specified object looks like). 
We recommend you omit all Error thresholds until you become more 
comfortable with the behavior of your services and the side effects they have on machine resources**.

Example JSON config file located in **PackageRoot\\Config**
folder (AppObserver.config.json):
```JSON
[
  {
    "target": "fabric:/MyApp",
    "cpuErrorLimitPct": 0,
    "cpuWarningLimitPct": 30,
    "diskIOErrorReadsPerSecMS": 0,
    "diskIOErrorWritesPerSecMS": 0,
    "diskIOWarningReadsPerSecMS": 0,
    "diskIOWarningWritesPerSecMS": 0,
    "dumpProcessOnError": false,
    "memoryErrorLimitPercent": 0,
    "memoryWarningLimitPercent": 60,
    "networkErrorActivePorts": 0,
    "networkErrorEphemeralPorts": 0,
    "networkWarningActivePorts": 800,
    "networkWarningEphemeralPorts": 400
  },
  {
    "target": "fabric:/MyApp1",
    "serviceIncludeList": "MyService42, MyOtherService42",
    "cpuErrorLimitPct": 0,
    "cpuWarningLimitPct": 8,
    "diskIOErrorReadsPerSecMS": 0,
    "diskIOErrorWritesPerSecMS": 0,
    "diskIOWarningReadsPerSecMS": 0,
    "diskIOWarningWritesPerSecMS": 0,
    "dumpProcessOnError": false,
    "memoryErrorLimitPercent": 0,
    "memoryWarningLimitPercent": 60,
    "networkErrorActivePorts": 0,
    "networkWarningActivePorts": 800,
    "networkErrorEphemeralPorts": 0,
    "networkWarningEphemeralPorts": 400
  },
  {
    "target": "fabric:/FabricObserver",
    "cpuErrorLimitPct": 0,
    "cpuWarningLimitPct": 30,
    "diskIOErrorReadsPerSecMS": 0,
    "diskIOErrorWritesPerSecMS": 0,
    "diskIOWarningReadsPerSecMS": 0,
    "diskIOWarningWritesPerSecMS": 0,
    "dumpProcessOnError": false,
    "memoryErrorLimitPercent": 0,
    "memoryWarningLimitPercent": 30,
    "networkErrorActivePorts": 0,
    "networkWarningActivePorts": 800,
    "networkErrorEphemeralPorts": 0,
    "networkWarningEphemeralPorts": 400
  },
  {
    "target": "fabric:/FabricObserverWebApi",
    "cpuErrorLimitPct": 0,
    "cpuWarningLimitPct": 30,
    "diskIOErrorReadsPerSecMS": 0,
    "diskIOErrorWritesPerSecMS": 0,
    "diskIOWarningReadsPerSecMS": 0,
    "diskIOWarningWritesPerSecMS": 0,
    "dumpProcessOnError": false,
    "memoryErrorLimitPercent": 0,
    "memoryWarningLimitPercent": 30,
    "networkErrorActivePorts": 0,
    "networkWarningActivePorts": 800,
    "networkErrorEphemeralPorts": 0,
    "networkWarningEphemeralPorts": 400
  }
]
```
Settings descriptions: 

All settings are optional, ***except target***, and can be omitted if you don't want to track. Or, you can leave the values blank ("") or set to 0 for numeric values. For process memory use, you can supply either MB values (a la 1024 for 1GB) for Working Set (Private) or percentage of total memory in use by process (as an integer).

| Setting | Description |
| :--- | :--- |
| **target** | App URI string to observe. Required. | 
| **serviceExcludeList** | A comma-separated list of service names (***not URI format***, just the service name as we already know the app name URI) to ***exclude from observation***. Just omit the object or set value to "" to mean ***include all***. (excluding all does not make sense) |
| **serviceIncludeList** | A comma-separated list of service names (***not URI format***, just the service name as we already know the app name URI) to ***include in observation***. Just omit the object or set value to "" to mean ***include all***. |  
| **memoryErrorLimitMB** | Maximum service process private working set in Megabytes that should generate a Fabric Error (SFX and local log) |  
| **memoryWarningLimitMB**| Minimum service process private working set in Megabytes that should generate a Fabric Warning (SFX and local log) |  
| **memoryErrorLimitPercent** | Maximum percentage of memory used by an App's service process (integer) that should generate a Fabric Error (SFX and local log) |  
| **memoryWarningLimitPercent** | Minimum percentage of memory used by an App's service process (integer) that should generate a Fabric Warning (SFX and local log) | 
| **cpuErrorLimitPct** | Maximum CPU percentage that should generate a Fabric Error |
| **cpuWarningLimitPct** | Minimum CPU percentage that should generate a Fabric Warning |
| **diskIOErrorReadsPerSecMS** | Maximum number of milliseconds for average sec/Read IO on system logical disk that will generate a Fabric Error. |
| **diskIOWarningReadsPerSecMS** | Minimum number of milliseconds for average sec/Read IO on system logical disk that will generate a Fabric warning. |
| **diskIOErrorWritesPerSecMS** | Maximum number of milliseconds for average sec/Write IO on system logical disk that will generate a Fabric Error. |
| **diskIOWarningWritesPerSecMS** | Minimum number of milliseconds for average sec/Write IO on system logical disk that will generate a Fabric Warning. |
| **dumpProcessOnError** | Instructs whether or not FabricObserver should   dump your service process when service health is detected to be in an  Error (critical) state... |  
| **networkErrorActivePorts** | Maximum number of established TCP ports in use by app process that will generate a Fabric Error. |
| **networkWarningActivePorts** | Minimum number of established TCP ports in use by app process that will generate a Fabric Warning. |
| **networkErrorEphemeralPorts** | Maximum number of ephemeral TCP ports (within a dynamic port range) in use by app process that will generate a Fabric Error. |
| **networkWarningEphemeralPorts** | Minimum number of established TCP ports (within a dynamic port range) in use by app process that will generate a Fabric Warning. |  
| **Output**| Log text(Error/Warning), Service Fabric Application Health Report |

AppObserver also optionally outputs CSV files for each app containing all resource usage data across iterations for use in analysis. Included are Average and Peak measurements. You can turn this on/off in Settings.xml, where there are comments explaining the feature further.  
  
AppObserver error/warning thresholds are user-supplied-only and bound to specific service instances (processes) as dictated by the user,
as explained above. Like FabricSystemObserver, all data is stored in in-memory data structures for the lifetime of the run (for example, 60 seconds at 5 second intervals). Like all observers, the last thing this observer does is call its *ReportAsync*, which will then determine the health state based on accumulated data across resources, send a Warning if necessary (clear an existing warning if necessary), then clean out the in-memory data structures to limit impact on the system over time. So, each iteration of this observer accumulates *temporary* data for use in health determination.
  
This observer also monitors the FabricObserver service itself across
CPU/Mem/Disk.  

## CertificateObserver
Monitors the expiration date of the cluster certificate and any other certificates provided by the user. 

### Notes
- If the cluster is unsecured or uses Windows security, the observer will not monitor the cluster security but will still monitor user-provided certificates. 
- The user can provide a list of thumbprints and/or common names of certificates they use on the VM, and the Observer will emit warnings if they pass the expiration Warning threshold. 
- In the case of common-name, CertificateObserver will monitor the ***newest*** valid certificate with that common name, whether it is the cluster certificate or another certificate.
- A user should provide either the thumbprint or the commonname of any certifcate they want monitored, not both, as this will lead to redudant alerts.

### Configuration
```xml
<Section Name="CertificateObserverConfiguration">
  <Parameter Name="Enabled" Value="True" />
  <!-- Default is 14 days for each -->
  <Parameter Name="DaysUntilClusterExpiryWarningThreshold" Value="14" />
  <Parameter Name="DaysUntilAppExpiryWarningThreshold" Value="14" />
  <!-- These are JSON-style lists of strings, empty should be "[]", full should be "['thumb1', 'thumb2']" -->
  <Parameter Name="AppCertThumbprintsToObserve" Value="[]"/>
  <Parameter Name="AppCertCommonNamesToObserve" Value="[]"/>
</Section>
```

## DiskObserver
This observer monitors, records and analyzes storage disk information.
Depending upon configuration settings, it signals disk health status
warnings (or OK state) for all logical disks it detects.

After DiskObserver logs basic disk information, it performs 5 seconds of
measurements on all logical disks across space usage and IO. The data collected are averaged and then
used in ReportAsync to determine if a Warning shot should be fired based on user-supplied threshold 
settings housed in Settings.xml. Note that you do not need to specify a threshold parameter that you 
don't plan you using... You can either omit the XML node or leave the value blank (or set to 0).

```xml
  <Section Name="DiskObserverConfiguration">
    <Parameter Name="Enabled" Value="True" />
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <Parameter Name="DiskSpacePercentWarningThreshold" Value="80" />
    <Parameter Name="DiskSpacePercentErrorThreshold" Value="" />
    <Parameter Name="AverageQueueLengthErrorThreshold" Value="" />
    <Parameter Name="AverageQueueLengthWarningThreshold" Value="7" />
    <Parameter Name="IOReadsErrorThreshold" Value="" />
    <Parameter Name="IOReadsWarningThreshold" Value="" />
    <Parameter Name="IOWritesErrorThreshold" Value="" />
    <Parameter Name="IOWritesWarningThreshold" Value="" />
  </Section>
```

**Output**: 

Node Health Report (Error/Warning/Ok)
  
example: 

Disk Info:

Drive Name: C:\  
Drive Type: Fixed \
  Volume Label   : Windows \
  Filesystem     : NTFS \
  Total Disk Size: 126 GB \
  Root Directory : C:\\  
  Free User : 98 GB \
  Free Total: 98 GB \
  % Used    : 22% \
  Avg. Disk sec/Read: 0 ms \
  Avg. Disk sec/Write: 1.36 ms \
  Avg. Disk Queue Length: 0.017 

Drive Name: D:\  
Drive Type: Fixed  
  Volume Label   : Temporary Storage  
  Filesystem     : NTFS  
  Total Disk Size: 99 GB  
  Root Directory : D:\  
  Free User : 52 GB  
  Free Total: 52 GB  
  % Used    : 47%  
  Avg. Disk sec/Read: 0 ms  
  Avg. Disk sec/Write: 0.014 ms  
  Avg. Disk Queue Length: 0   

**This observer also optionally outputs a CSV file containing all resource usage
data across iterations for use in analysis. Included are Average and
Peak measurements. Set in Settings.xml's EnableLongRunningCSVLogging boolean setting.**


## FabricSystemObserver 
This observer monitors Fabric system services for 1 minute per global
observer iteration e.g., Fabric, FabricApplicationGateway, FabricCAS,
FabricDCA, FabricDnsService, FabricGateway, FabricHost,
FileStoreService.  

**NOTE:**
Only enable FabricSystemObserver ***after*** you get a sense of what impact your services have on the SF runtime... 
This is very important because there is no "one threshold fits all" across warning/error thresholds for any of the SF system services. 
That is, we (the SF team) do not have a fixed set of guaranteed problematic warning thresholds for SF infrastructure services. Your code can cause Fabric.exe to eat a lot of CPU, for example, but this is not a Fabric.exe bug. 
 
Again, it is best to ***not*** Enable this observer until you have done some experimentation with monitoring how your service code impacts Service Fabric system services. Otherwise, you may end up generating noise and creating support tickets when there is in fact nothing wrong with SF, but that your service code is just stressing SF services (e.g., Fabric.exe). This is of course useful to know, but FabricSystemObserver can't tell you that your code is the problem and we do not want you to create a support ticket because FSO warned you about something SF engineers can't fix for you...  

**Input**: Settings.xml in PackageRoot\\Observers.Config\
**Output**: Log text(Error/Warning), Service Fabric Health Report
(Error/Warning)

**This observer also optionally outputs a CSV file containing all resource usage
data across iterations for use in analysis. Included are Average and
Peak measurements. Set in Settings.xml's EnableLongRunningCSVLogging boolean setting.**

This observer runs for either a specified configuration setting of time
or default of 30 seconds. Each fabric system process is monitored for
CPU and memory usage, with related values stored in instances of
FabricResourceUsageData object: 
```C#
using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Utilities
{
    internal class FabricResourceUsageData<T>
    {
        public List<T> Data { get; }
        public string Name { get; set; }
        public T MaxDataValue { get; }
        public double AverageDataValue { get; }
        public bool ActiveErrorOrWarning { get; set; }
        public int LifetimeWarningCount { get; set; } = 0;
        public bool IsUnhealthy<U>(U threshold){ ... }
        public double StandardDeviation { get; }
	public FabricResourceUsageData(string property, string id){ ... }
    }
}
```

These instances live across iterations and the usage data they hold
(Data field) is cleared upon successful health checks by sending a
HealthState.Ok health report to fabric. Consumer can choose to keep
MaxDataValue and AverageDataValue values intact across runs and use them
for specific purposes. This is the case for Fabric.exe currently, for
example. Note the design of GetHealthState. We report on Average of
accumulated resource usage values to determine health state. If this
value exceeds specified related threshold, a Warning is issued to Fabric
via ObserverBase instance's ObserverHealthReporter, which takes a
FabricClient instance that ObserverManager creates (and disposes). We
only want to use a single FabricClient for all observers given our own
resource constraints (we want to limit our impact on the system as core
design goal). The number of active ports in use by each fabric service
is logged per iteration.


## NetworkObserver

This observer checks outbound connection state for user-supplied endpoints (hostname/port pairs).

**Input**: NetworkObserver.config.json in PackageRoot\\Config.
Users should supply hostname/port pairs (if they only allow
communication with an allowed list of endpoints, for example, or just
want us to test the endpoints they care about...). If this list is not
provided, the observer will run through a default list of well-known,
reliable internal Internet endpoints: google.com, facebook.com,
azure.microsoft.com, but report on them... The point of this observer is to test YOUR endpoints... 
The implementation allows for either an ICMP or TCP-based test.

Each endpoint test result is stored in a simple data type
(ConnectionState) that lives for either the lifetime of the run or until
the next run assuming failure, in which case if the endpoint is
reachable, SFX will be cleared with an OK health state report and the
ConnectionState object will be removed from the containing
List\<ConnectionState\>. Only Warning data is persisted across
iterations.

Example NetworkObserver.config.json configuration:  

```javascript
[
  {
      "appTarget": "fabric:/MyApp",
      "endpoints": [
        {
          "hostname": "google.com",
          "port": 443
        },
        {
          "hostname": "facebook.com",
          "port": 443
        },
        {
          "hostname": "westusserver001.database.windows.net",
          "port": 1433
        }
     ]
  },
  {
      "appTarget": "fabric:/MyApp2",
      "endpoints": [
        {
          "hostname": "google.com",
          "port": 443
        },
        {
          "hostname": "microsoft.com",
          "port": 443
        },
        {
          "hostname": "eastusserver007.database.windows.net",
          "port": 1433
        }
     ]
  }
]
```

**Output**: Log text(Info/Error/Warning), Service Fabric Health Report
(Error/Warning)  

This observer runs 4 checks per supplied hostname with a 3 second delay
between tests. This is done to help ensure we don't report transient
network failures which will result in Fabric Health warnings that live
until the observer runs again...  


## NodeObserver
 This observer monitors VM level resource usage across CPU, Memory, firewall rules, static and dynamic ports (aka ephemeral ports).
 Thresholds for Erorr and Warning signals are user-supplied in PackageRoot/Config/Settings.xml.

**Input**:
```xml
  <Section Name="NodeObserverConfiguration">
    <Parameter Name="Enabled" Value="True" />
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <Parameter Name="CpuErrorLimitPercent" Value="" />
    <Parameter Name="CpuWarningLimitPercent" Value="90" />
    <Parameter Name="MemoryErrorLimitMB" Value="" />
    <Parameter Name="MemoryWarningLimitMB" Value ="" />
    <Parameter Name="MemoryErrorLimitPercent" Value="" />
    <Parameter Name="MemoryWarningLimitPercent" Value ="90" />
    <Parameter Name="NetworkErrorActivePorts" Value="" />
    <Parameter Name="NetworkWarningActivePorts" Value="55000" />
    <Parameter Name="NetworkErrorFirewallRules" Value="" />
    <Parameter Name="NetworkWarningFirewallRules" Value="2500" />
    <Parameter Name="NetworkErrorEphemeralPorts" Value="" />
    <Parameter Name="NetworkWarningEphemeralPorts" Value="5000" />
  </Section>
```  
| Setting | Description |
| :--- | :--- | 
| **CpuErrorLimitPct** | Maximum CPU percentage that should generate an Error |  
| **CpuWarningLimitPct** | Minimum CPU percentage that should generate a Warning | 
| **MemoryErrorLimitMB** | Maximum amount of committed memory on virtual machine that will generate an Error. | 
| **MemoryWarningLimitMB** | Minimum amount of committed memory that will generate a Warning. |  
| **MemoryErrorLimitPercent** | Maximum percentage of memory in use on virtual machine that will generate an Error. | 
| **MemoryWarningLimitPercent** | Minimum percentage of memory in use on virtual machine that will generate a Warning. |  
| **NetworkErrorFirewallRules** | Number of established Firewall Rules that will generate a Health Warning. |  
| **NetworkWarningFirewallRules** |  Number of established Firewall Rules that will generate a Health Error. |  
| **NetworkErrorActivePorts** | Maximum number of established ports in use by all processes on node that will generate a Fabric Error. |
| **NetworkWarningActivePorts** | Minimum number of established TCP ports in use by all processes on node that will generate a Fabric Warning. |
| **NetworkErrorEphemeralPorts** | Maximum number of established ephemeral TCP ports in use by app process that will generate a Fabric Error. |
| **NetworkWarningEphemeralPorts** | Minimum number of established ephemeral TCP ports in use by all processes on node that will generate a Fabric warning. |

**Output**:\
SFX Warnings when min/max thresholds are reached. CSV file,
CpuMemDiskPorts\_\[nodeName\].csv, containing long-running data (across
all run iterations of the observer).


## OSObserver
This observer records basic OS properties across OS version, OS health status, physical/virtual memory use, number of running processes, number of active TCP ports (active/ephemeral), number of enabled firewall rules, list of recent patches/hotfixes. It submits an infinite OK SF Health Report that is visible in SFX at the node level (Details tab) and by calling http://localhost:5000/api/ObserverManager. It shares a set of static fields that other observers can use (and do), so ***it's best to not disable this observer***. It should be the first observer to run in the observer list (as it is, by default). There is no reason to disable OSObserver (so, don't). **Only for consistency reasons does the enable/disable setting exist for OSObserver**. 
\
\
**Input**: This observer does not take input.\
**Output**: Log text(Error/Warning), Service Fabric Health Report (Ok/Error), HTML output for API service and SFX (node level Details tab). 

The output of OSObserver is stored in its local log file. The only Fabric health reports generated by this observer 
is an Error when OS Status is not "OK" (which means something is wrong at the OS level and
this means trouble...) and long-lived Ok Health Report that contains the information it collected about the node it's running on.  

Example output: 

![alt text](/Documentation/Images/OSObserverOutput.jpg "Logo Title Text 1")  


## SFConfigurationObserver 

This observer doesn't monitor or report health status. 
It provides information about the currently installed Service Fabric runtime environment.
The output (a local file) is used by the FabricObserver API service, rendered as HTML (e.g., http://localhost:5000/api/ObserverManager). You can learn more about API service [here](/FabricObserverWeb/ReadMe.md).

# Writing a New Observer

Writing a new observer consists of extending `ObserverBase`, creating configuration, and writing your logic.

## Create `MyObserver.cs`, extending `ObserverBase`
```csharp
namespace FabricObserver
{
    public class MyObserver : ObserverBase
    {
        // Most observers have an initialize where they read their settings
        private async Task Initialize(CancellationToken token)
        {
            var mythreshold = this.GetSettingParameterValue(
                ObserverConstants.MyObserverConfigurationSectionName,
                ObserverConstants.MyObserverThreshold);
            if (!string.IsNullOrEmpty(mythreshold))
            {
                this.mytheshold = threshold;
            }
            else
            {
                this.mythreshold = "foo";
            }

            // ...
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
          Initialize(token);
          // ...
          ReportAsync(token);
        }

        public override async Task ReportAsync(CancellationToken token)
        {
          // read state recorded by ObserveAsync
          // ...
          healthReport = new Utilities.HealthReport
                {
                    Observer = this.ObserverName,
                    ReportType = "",
                    EmitLogEvent = "",
                    NodeName = "",
                    HealthMessage = "",
                    State = "",
                    HealthReportTimeToLive = ""
                };

                this.HasActiveFabricErrorOrWarning = "";
        }
	
        this.HealthReporter.ReportHealthToServiceFabric(healthReport);
    }
}
```
*Note*: if you are measuring things that involve numeric values, then you should create instances of [FabricResourceUsageData<T>](/FabricObserver/Observers/Utilities/FabricResourceUsageData.cs) 
objects, add to them during your monitoring, then call [ProcessResourceDataReportHealth](/FabricObserver/Observers/ObserverBase.cs#L446), which is already implemented in 
ObserverBase and does the work of generating health reports based on what you provide (the data, related thresholds, etc...). 
This is only useful when T is some type of number. See [NodeObserver's related implementation](https://github.com/microsoft/service-fabric-observer/blob/9e27c33f0ba7c55d90648acd9970d9075fb0c944/FabricObserver/Observers/NodeObserver.cs#L371) as an example. 
	

## Create configuration section in `Settings.xml`
```xml
  <Section Name="MyObserverConfiguration">
    <Parameter Name="Enabled" Value="True" />
    <Parameter Name="MyThreshold" Value="10" />
    <Parameter Name="MyFlag" Value="False" />
```

## Add references to configuration in `ObserverConstants.cs`
```csharp
// My Observer
public const string MyObserverName = "CertificateObserver";
public const string MyObserverConfigurationSectionName = "MyObserverConfiguration";
public const string MyObserverThreshold = "MyThreshold";
public const string MyObserverFlag = "MyFlag";
```

## Add observer to `ObserverManager.GetObservers()`
```csharp
// ...
new CertificateObserver(),
new MyObserver()
// ...
```

That's it, your observer will now be queued and run alongside the other enabled observers.
