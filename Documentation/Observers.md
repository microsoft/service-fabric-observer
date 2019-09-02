**Currently Implemented Observers**  

***AppObserver***  
***DiskObserver***  
***FabricSystemObserver***  
***NetworkObserver***  
***NodeObserver***  
***OSObserver*** (Not configurable beyond Enable/Disable)  
***SFConfigurationObserver***  (Not configurable beyond Enable/Disable)  

Each Observer instance logs to a directory of the same name. You can configure the base directory of the output and log verbosity level (verbose or not). If you enable telemetry and provide an ApplicationInsights key, then you will also see the output in your log analytics queries. Each observer has configuration settings in PackageRoot/Config/Settings.xml. AppObserver and NetworkObserver house their runtime config settings (error/warning thresholds), which are more verbose and do not lend themselves very well to the Settings.xml to schema, in json files located in PackageRoot/Observers.Data folder.  

**A note on Error thresholds**  
A Service Fabric Error Health Event is a critical event. You should be thoughtful about when to emit Errors versus Warnings as Errors
will block service upgrades and other Fabric runtime operations from taking place. The default configuration settings for all configurable observers do not have error thresholds set to prevent blocking runtime operations like upgrade. Only set error thresholds when you are comfortable with the blocking nature of these critical health events. There is nothing wrong with setting error thresholds, but please be thoughtful and spend some time understanding when to emit health errors versus warnings, depending upon your scenario. This is just a note of caution, not a proclamation to never emit Error events. It's very reasonable to emit an Error health event when some threshold is reached that can lead to critical failure in your service. In fact, it's the right thing to do. Again, just be cautious and judicious in how you rely on Errors.  

**Fabric Observers - What they do and how to configure them**  
  

**AppObserver**  
Observer that monitors CPU usage, Memory use, and Disk space
availability for Service Fabric Application services (processes). This
observer will alert when user-supplied thresholds are reached.

**Input**: JSON config file supplied by user, stored in
PackageRoot/Observers.Data folder. This data contains JSON arrays
objects which constitute Service Fabric Apps (identified by service
URI's). Users supply Error/Warning thresholds for CPU use, Memory use and Disk
IO, ports. Memory values are supplied as number of megabytes... CPU and
Disk Space values are provided as percentages (integers: so, 80 = 80%...)... 
**Please note that you can supply 0 for any of these setting. It just means that the threshold
will be ignored. We recommend you do this for all Error thresholds until you become more 
comfortable with the behavior of your services and the machine-level side effects produced by them**.

Example JSON config file located in **PackageRoot\\Observers.Data**
folder (AppObserver.config.json):
```JSON
[
  {
    "target": "fabric:/MyApp",
    "serviceExcludeList" : "MyService13,MyService17",
    "cpuErrorLimitPct": 0,
    "cpuWarningLimitPct": 30,
    "diskIOErrorReadsPerSecMS": 0,
    "diskIOErrorWritesPerSecMS": 0,
    "diskIOWarningReadsPerSecMS": 0,
    "diskIOWarningWritesPerSecMS": 0,
    "dumpProcessOnError": false,
    "memoryErrorLimitMB": 0,
    "memoryWarningLimitMB": 1024,
    "networkErrorActivePorts": 0,
    "networkWarningActivePorts": 800,
    "networkErrorEphemeralPorts": 0,
    "networkWarningEphemeralPorts": 400
  },
  {
    "target": "fabric:/MyApp2",
    "serviceIncludeList" : "MyService3",
    "cpuErrorLimitPct": 0,
    "cpuWarningLimitPct": 30,
    "diskIOErrorReadsPerSecMS": 0,
    "diskIOErrorWritesPerSecMS": 0,
    "diskIOWarningReadsPerSecMS": 0,
    "diskIOWarningWritesPerSecMS": 0,
    "dumpProcessOnError": false,
    "memoryErrorLimitMB": 0,
    "memoryWarningLimitMB": 1024,
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
    "memoryErrorLimitMB": 0,
    "memoryWarningLimitMB": 250,
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
    "memoryErrorLimitMB": 0,
    "memoryWarningLimitMB": 1024,
    "networkErrorActivePorts": 0,
    "networkWarningActivePorts": 800,
    "networkErrorEphemeralPorts": 0,
    "networkWarningEphemeralPorts": 400
  }
]
```
Settings descriptions: 

Note that all of these are optional, ***except target***, and can be omitted if you don't want to track. Or, you can leave the values blank ("") or set to 0 for numeric values.

**target**: App URI to observe (or "system" for node-level resource
monitoring...)\
**serviceExcludeList**: A comma-separated list of service names (***not URI format***, just the service name as we already know the app name URI) to ***exclude from observation***. Just omit the object or set value to "" to mean ***include all***. (excluding all does not make sense)  
**serviceIncludeList**: A comma-separated list of service names (***not URI format***, just the service name as we already know the app name URI) to ***include in observation***. Just omit the object or set value to "" to mean ***include all***.  
**memoryErrorLimitMB**: Maximum service process private working set,
in Megabytes, that should generate a Fabric Error (SFX and local log) \[Note:
this shouldn't have to be supplied in bytes...\]\
**memoryWarningLimitMB**: Minimum service process private working set,
in Megabytes, that should generate a Fabric Warning (SFX and local log)
\[Note: this shouldn't have to be supplied in bytes...\]\
**cpuErrorLimitPct**: Maximum CPU percentage that should generate a
Fabric Error \
**cpuWarningLimitPct**: Minimum CPU percentage that should generate a
Fabric Warning \
**diskIOErrorReadsPerSecMS:** Maximum number of milliseconds for average
sec/Read IO on system logical disk that will generate a Fabric
Error.\
**diskIOWarningReadsPerSecMS**: Minimum number of milliseconds for average
sec/Read IO on system logical disk that will generate a Fabric warning.\
**diskIOErrorWritesPerSecMS:** Maximum number of milliseconds for average
sec/Write IO on system logical disk that will generate a Fabric
Error.\
**diskIOWarningWritesPerSecMS**: Minimum number of milliseconds for average
sec/Write IO on system logical disk that will generate a Fabric
Warning.\
**dumpProcessOnError**: Instructs whether or not FabricObserver should  
dump your service process when service health is detected to be in an 
Error (critical) state...  
**networkErrorActivePorts:** Maximum number of established TCP ports in use by
app process that will generate a Fabric Error.\
**networkWarningActivePorts:** Minimum number of established TCP ports in use by
app process that will generate a Fabric Warning.\
**networkErrorEphemeralPorts:** Maximum number of ephemeral TCP ports (within a dynamic port range) in use by
app process that will generate a Fabric Error.\
**networkWarningEphemeralPorts:** Minimum number of established TCP ports (within a dynamic port range) in use by
app process that will generate a Fabric Warning.\  

**Output**: Log text(Error/Warning), Service Fabric Application Health Report
(Error/Warning/ok), telemetry data.

AppObserver also optionally outputs CSV files for each app containing all resource usage data across iterations for use in analysis. Included are Average and Peak measurements. You can turn this on/off in Settings.xml, where there are comments explaining the feature further.  
  
AppObserver error/warning thresholds are user-supplied-only and
bound to specific service instances (processes) as dictated by the user,
as explained above. Like FabricSystemObserver, all data is stored in
in-memory data structures for the lifetime of the run (for example, 60
seconds at 5 second intervals). Like all observers, the last thing this
observer does is call its *ReportAsync*, which will then determine the
health state based on accumulated data across resources, send a Warning
if necessary (clear an existing warning if necessary), then clean out
the in-memory data structures to limit impact on the system over time.
So, each iteration of this observer accumulates *temporary* data for use
in health determination.

This observer also monitors the FabricObserver service itself across
CPU/Mem/Disk.  


**DiskObserver**  
This observer monitors, records and analyzes storage disk information.
Depending upon configuration settings, it signals disk health status
warnings (or OK state) for all logical disks it detects.

After DiskObserver logs basic disk information, it performs 5 seconds of
measurements on all logical disks across space usage and IO. The data collected are averaged and then
used in ReportAsync to determine if a Warning shot should be fired based on user-supplied threshold 
settings housed in Settings.xml.

```xml
  <Section Name="DiskObserverConfiguration">
    <Parameter Name="Enabled" Value="True" />
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <Parameter Name="DiskSpaceWarningThreshold" Value="80" />
    <Parameter Name="DiskSpaceErrorThreshold" Value="" />
    <Parameter Name="AverageQueueLengthErrorThreshold" Value ="" />
    <Parameter Name="AverageQueueLengthWarningThreshold" Value ="5" />
    <!-- These may or may not be useful to you. Depends on your IO-bound workload... -->
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


**FabricSystemObserver**  
This observer monitors Fabric system services for 1 minute per global
observer iteration e.g., Fabric, FabricApplicationGateway, FabricCAS,
FabricDCA, FabricDnsService, FabricGateway, FabricHost,
FileStoreService.

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


**NetworkObserver**

This observer checks outbound connection state for user-supplied endpoints (hostname/port pairs).

**Input**: NetworkObserver.config.json in PackageRoot\\Observers.Data.
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


**NodeObserver**
 This observer monitors VM level resource usage across CPU, Memory, firewall rules, static and dynamic ports (aka ephemeral ports).
 Thresholds for Erorr and Warning signals are user-supplied in PackageRoot/Config/Settings.xml.

**Input**:
```xml
 <Section Name="NodeObserverConfiguration">
    <Parameter Name="Enabled" Value="True" />
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <Parameter Name="CpuErrorLimitPct" Value="0" />
    <Parameter Name="CpuWarningLimitPct" Value="80" />
    <Parameter Name="MemoryErrorLimitMB" Value="0" />
    <Parameter Name="MemoryWarningLimitMB" Value ="28000" />
    <Parameter Name="NetworkErrorActivePorts" Value="0" />
    <Parameter Name="NetworkWarningActivePorts" Value="45000" />
    <Parameter Name="NetworkErrorFirewallRules" Value="0" />
    <Parameter Name="NetworkWarningFirewallRules" Value="2500" />
    <Parameter Name="NetworkErrorEphemeralPorts" Value="0" />
    <Parameter Name="NetworkWarningEphemeralPorts" Value="5000" />
  </Section>
```  
Settings descriptions:  

**CpuErrorLimitPct**: Maximum CPU percentage that should generate an
Error (SFX and local log)\
**CpuWarningLimitPct**: Minimum CPU percentage that should generate a
Warning (SFX and local log)\
**MemoryErrorLimitMB**: Maximum service process private working set,
in Megabytes, that should generate an Error (SFX and local log) \[Note:
this shouldn't have to be supplied in bytes...\]\
**MemoryWarningLimitMB**: Minimum service process private working set,
in Megabytes, that should generate an Warning (SFX and local log)
\[Note: this shouldn't have to be supplied in bytes...\]\
**NetworkErrorFirewallRules**: Number of established Firewall Rules that will generate a Health Warning  
**NetworkWarningFirewallRules**:  Number of established Firewall Rules that will generate a Health Error  
**NetworkErrorActivePorts:** Maximum number of established ports in use by
all processes on node that will generate a Fabric Error.\
**NetworkWarningActivePorts:** Minimum number of established TCP ports in use by
all processes on node that will generate a Fabric Warning.\
**NetworkErrorEphemeralPorts:** Maximum number of established ephemeral TCP ports in use by
app process that will generate a Fabric Error.\
**NetworkWarningEphemeralPorts:** Minimum number of established ephemeral TCP ports in use by
all processes on node that will generate a Fabric warning.\

**Output**:\
SFX Warnings when min/max thresholds are reached. CSV file,
CpuMemDiskPorts\_\[nodeName\].csv, containing long-running data (across
all run iterations of the observer).


**OSObserver**\
This observer records basic OS properties for use during mitigations,
RCAs. It submits an Infinite OK SF Health Report that is visible in SFX at the node level. 
\
\
**Input**: This observer does not take input.\
**Output**: Log text(Error/Warning), Service Fabric Health Report
(Ok/Error/Warning)

The output of OSObserver is stored in the local log file. The only Fabric health reports generated by this observer 
is an Error when OS Status is not "OK" (which means something is wrong at the OS level and
this means trouble...) and long-lived Ok Health Report that contains the information it collected.  

Example output: 

Last updated on 8/29/2019 17:26:18 UTC

OS Info:  
  
OS: Microsoft Windows Server 2016 Datacenter  
FreePhysicalMemory: 12 GB  
FreeVirtualMemory: 17 GB  
InstallDate: 5/17/2019 9:46:40 PM  
LastBootUpTime: 5/28/2019 8:26:12 PM  
NumberOfProcesses: 101  
OSLanguage: 1033  
Status: OK  
TotalVirtualMemorySize: 22 GB  
TotalVisibleMemorySize: 15 GB  
Version: 10.0.14393  
Total number of enabled Firewall rules: 367  
Total number of active TCP ports: 150  
Windows ephemeral TCP port range: 49152 - 65535  
Fabric Application TCP port range: 20000 - 30000  
Total number of active ephemeral TCP ports: 88  
  
OS Patches/Hot Fixes:  
  
KB4509091  7/11/2019  
KB4503537  6/13/2019  
KB4494440  5/22/2019  
KB4498947  5/19/2019  
KB4054590  4/9/2019  
KB4132216  4/9/2019  
KB4485447  4/9/2019  


**SFConfigurationObserver**  

This observer doesn't monitor or report health status. 
It provides information about the currently installed Service Fabric runtime environment.
The output (a local file) is used by the FabricObserver API service, rendered as HTML (e.g., http://localhost:5000/api/ObserverManager). You can learn more about API service [here](/FabricObserverWeb/ReadMe.md).

