[![Build Status](https://msazure.visualstudio.com/One/_apis/build/status/cdptriggers/azure/compute/master/service-fabric-observer?branchName=Release)](https://msazure.visualstudio.com/One/_build/latest?definitionId=89044&branchName=Release)

***INTRODUCTION***

FabricObserver (FO) is a user-configurable stateless Service Fabric service that monitors both user services and internal fabric services for potential problems related to resource usage, networking, and certificates/security. It employs a simple-to-understand Observer development model, enabling the creation of new observers very quickly with little cognitive complexity... 

FO is implemented using Service Fabric’s external/public API surface only. It does not ship with SF. It is independent of the SF runtime engineering schedule…

FO is composed of Observer objects (instance classes) that are designed to observe, record, and report on several machine-level environmental conditions inside a VM, so at the OS node level of a Service Fabric cluster. 

In Warning and Error states, an observer signals status (reports) via a Service Fabric Health Report (e.g., extended, high CPU and Memory usage, extended Disk IO, limited Disk space, Networking issues (latency, unavailability, unusually high or low network IO). Since an observer doesn't know what's good and what's bad by simply observing some resource state (in some cases, like disk space monitoring and fabric system service monitoring, there are predefined maxima/minima), a user must provide Warning and Error thresholds that ring the alarm bells. These settings are supplied and packaged in Service Fabric configuration files (both XML and JSON are supported).

Finally, FO logs to disk, logs to long-running resource usage data in CSV files, and signals SF Health Reports under Warning and Error conditions. All of these are configurable across on/off and verbosity (in the case of file logging). For external users, it will be trivial to hook up output to some diagnostics/log analytics service.

FO does not mitigate, by design, yet...

Currently, FabricObserver is implemented as a .NET Desktop Application, which means it is Windows-only. There is a ToDo to convert to .NET Core 2.2. This should be relatively painless. It's going to happen...

FabricObserver needs to be generic enough to be useful across scenarios and not bound to a specific
set of workloads.  Our goal is to continue to build this out in a general-purpose way, targeting all
users (not just internal SF customers). To begin, we have designed and
implemented a handful of Observers that we will employ in production 
in the near term to better understand what we need to build for
the long term.

In this iteration of the project, we have designed Observers that can be configured by users to
monitor the machine-level side effects of an App (defined as a
collection of Service Fabric services). The user-controlled,
App-focused functionality is primarily encapsulated in the
**AppObserver**, which observes, records and reports real-time CPU,
Memory, Disk IO, and active port count as defined by the user in a Data
configuration file (JSON, App array objects). Likewise, there are the easily-configurable, App-focused **NetworkObserver** and  **DiskObserver**. You can learn a lot more about the set of existing observers further down this relatively long readme...\
\
For the most part, **we focus on both the state of the system surrounding a Service Fabric app and the specific side effects of 
the app's (mis)behavior**. Most observers focus on machine level states: 
Disk (local storage disk health/availability, space usage, IO), CPU (per
process across Apps and Fabric system services), Memory (per process
across Apps and Fabric system services as well as system-wide), Networking (general health and
monitoring of availability of user-specified, per-app endpoints), basic OS
properties (install date, health status, list of hot fixes, hardware configuration,
etc.), and Service Fabric infrastructure information and state. The design is decidely simplistic and easy to understand/implement. C# and .NET make this very easy to do. Since the product is OSS, anybody can author scenario-specific
observers themselves very quickly (or extend currently implemented ones) to meet
their monitoring and reporting needs. The idea is that an Observer observes user-specified conditions, reports, and logs. Observers do not mitigate. That should be done in a different service that works in concert, but is not coupled to FabricObserver. Basic microservice design thinking...

***GOAL***

**The uber goal here is to greatly reduce the complexity of real-time host OS, Service Fabric infrastructure, and app health monitoring**. For SF app developers, it's as easy as supplying some simple configuration files (JSON and XML).
Empower cloud developers to understand and learn from inevitable failure conditions by not adding more cognitive complexity to their lives - reliably ***enabling self-mitigation before problems turn into incidents***. 

***SCOPE***

As stated, the goal of this project, in line with the team's Supportability initiative, is to help
enable Service Fabric customers to self-mitigate issues versus creating
ICM incidents, by default. With knowledge comes power. For Phase 1, the scope is improving a developer's knowledge about the actual health state of the environment in which her services are running. As we
move to Phase 2 we will include Policy-driven Action that is owned by
the user (not by SF). This will reside in a FabricHealer service.

Simply put, \*Phase 1 is about collection, recording, and alerting of the
right metrics\*, where "right" is defined by data from real-world incidents. We
will then be armed with the requisite health data to begin
exploring safe auto-mitigation strategies.

***DEFINITIONS AND ACRONYMS***

**Observer** -- conceptually equivalent to "Watchdog", however, we want
to make it even more explicit that the nature of these objects is
non-reactive, meaning they do not mitigate, they observe, record, and
report.

***DESIGN***

**FabricObserver** is a lightweight *Stateless Service Fabric service*
which is designed to be highly decoupled from the underlying Fabric
system services it observes. We do not rely on interaction with Service
Fabric subsystems beyond what we can access through a internal API, like
all Service Fabric Service implementations in the internal domain\... This
is a design decision to limit runtime dependencies and enable agile
delivery of bug fixes and new features for Observers (and in time,
Healers or Mitigators). We do not need to align with the delivery plans
for the underlying system. There is no need for FabricObserver to
maintain replicated internal state, so we don't need to implement Fabric
Observer as a Stateful Service Fabric service today.

**ObserverManager** serves as the entry point for all machine local
Observers. Its RunObservers method calls Start on all the Observers in a
loop, which runs through them sequentially. After each iteration through
all observers, it logs FabricObserver's health state (Warning or Error,
always logging to local disk Error conditions, and signaling to SFX via
Health Reporting as Warnings. Ok state is not recorded...). When an
observer reports a warning, the ObserverManager will wait 30 seconds and
then run that observer again (retry). If it is still in a warning state
after the retry, the loop continues through to the next observer (and
the retry information is logged...). The reason for the delayed retry is
to clear any transient health issues from the FabricHealth queue sooner
than waiting for the next "natural" time the offending observer
runs...

The iteration interval defaults to 300 seconds (5 minutes): after
running all observers, it sleeps for the allotted time. This setting is
user-configurable in Settings. ObserverManager manages lifetime of
observers and will dispose of them in addition to stopping their
execution when shutdown or task cancellation is requested. 


**Abstract class ObserverBase**  

> This is the abstract base class for all Observers. It provides several
> concrete method and property implementations that are widely used by
> deriving types. It explicitly implements one of the members of
> IObserver (RunInterval), while requiring derived types to implement
> the rest of IObserver's members.  

***Design*** 

IObserver and IObserverBase interfaces (implemented/abstracted by ObserverBase, which is
implemented by all Observers)  
```C#
internal interface IObserver : IDisposable
{
        DateTime LastRunDateTime { get; set; } 
        TimeSpan RunInterval { get; set; }
        bool IsEnabled { get; set; }
        bool HasActiveFabricErrorOrWarning { get; set; }
        bool IsUnhealthy { get; set; }
        Task ObserveAsync(CancellationToken token);
        Task ReportAsync(CancellationToken token);
}
    
internal interface IObserverBase<T,TU> : IObserver
{
        string ObserverName { get; set; }
        string NodeName { get; set; }
        ObserverHealthReporter HealthReporter { get; }
        // StatefulServiceContext or StatelessServiceContext...
        T FabricServiceContext { get; }
        Logger Logger { get; set; }
        DataLogger DataLogger { get; set; }
        void EmitLogEvent(string observerName, string description, LogEventLevel level);
        TimeSpan GetObserverRunInterval(string configSectionName, string configParamName, TimeSpan? defaultTo = null);
        string GetSettingParameterValue(string sectionName, string parameterName, string defaultValue = null);
        // Whatever key-value/named-pair data structure suits your fancy, default is IDictionary...
        TU GetConfigSettingSectionParameters(string sectionName);
}
    

internal abstract class ObserverBase: IObserverBase<StatelessServiceContext, 
                                                    IDictionary<string, string>>  
{
	internal string ObserverName { get; set; }
        internal string NodeName { get; set; }
        internal ObserverHealthReporter HealthReporter { get; }
        internal StatelessServiceContext FabricServiceContext { get; }
        internal abstract DateTime LastRunDateTime { get; set; }
        internal bool IsEnabled { get; set; } 
        internal bool IsUnhealthy { get; set; }
        internal Logger Logger { get; set; }
        internal DataLogger DataLogger { get; set; }

        // Each derived Observer can set this to maintain health status (Warning state true/false) across iterations.
        // This information is used by ObserverManager. 
        internal bool HasActiveFabricErrorOrWarning { get; set; } = false;
        internal TimeSpan RunInterval { get; set; } = TimeSpan.FromMinutes(10);
        internal List<string> Settings { get; }
        internal abstract Task ObserveAsync(CancellationToken token);
        internal abstract Task ReportAsync(CancellationToken token);
	protected FabricClient FabricClientInstance { get; }
	internal IDictionary<string, string> GetConfigSettingSectionParameters(string sectionName) { ... }
	internal TimeSpan GetObserverRunInterval(string configSectionName,
                                                 string configParamName,
                                                 TimeSpan? defaultTo = null) { ... }
	// Windows only process dumping...
        internal bool DumpServiceProcess(int processId, DumpType dumpType = DumpType.Full) { ... }
	
	// Dispose pattern, made virtual so Observers aren't required to implement if they don't need to...
	protected virtual void Dispose(bool disposing) { ... }
	internal virtual void Dispose() { ... }
	
}
```

An ObserverBase derived type, as are all Observers, must implement:

-   A internal ctor of shape: \[Some\]Observer() **:
    base(\[ObserverName\])** { }

-   A internal Task ObserveAsync(CancellationToken token) method

-   A internal Task ReportAsync(CancellationToken token) method

***The Observer***

An **Observer** is a C\# object that is instantiated inside a Stateless
Service Fabric Service process. An Observer is designed to monitor
specific machine level environmental conditions (thus the notion of a
"machine local" monitoring mechanism), the metrics of which are stored
in in-memory data structures, \[statistics...\] .... that people (app
developers and SF team devs on ICM duty) can use to make informed
mitigation decisions. It will also support an extensibility model which
will be kept very simple to encourage usage. The project has 4 stages of
design and development. We are already executing on Phase 1, which
includes research and x-org collaboration planning...

An Observer must support the following properties and behaviors:

-   Easily extensible monitoring "framework": developers should be able
    to readily add new types of data collectors to an observer...

-   Ability to observe both Machine and App health (the latter is a bit
    tricky, but with xPing, for example, at least we could attain some
    useful insights at the App level...).

-   Observers must be low cost and highly effective: Process of
    observation does not add significant resource pressure to nodes
    (VMs).

-   Health data collected by Observers must be easily queried with
    results that are immediately useful to users such that they can make
    informed mitigation decisions, should they choose to do so.

-   Data is stored in formats readily consumable by data analysis
    services

Each Observer can create a Warning if some metric exceeds a supplied
threshold. **Note: we do not generate Fabric Health Errors by default as this will block
runtime upgrades, etc -- and further, this breaks the guarantee that
FabricObserver will have no side effects on Service Fabric's systemic
behavior... Generating Health Errors is YOUR decision and it's fine to do it as long as you understand what it means...**. 
A Health report lives for a calculated duration: the
current date time -- last run date time +
ObserverManager.ObserverExecutionLoopSleepSeconds \[+ observer runtime (for example,
FabricSystemObserver runs for 60 seconds, AppObserver runs
for 60 seconds)\]. This ensures that the Fabric Health report remains
active until the next time the related observer runs, which will either
clear the warning by sending an OK health report or sends another
warning/error report to fabric that lives for the calculated TTL. Each observer can call ObserverBase's SetTimeToLiveWarning function,
optionally providing a known number that represents some timeout or running time the observer self-manages...

```C#
public TimeSpan SetTimeToLiveWarning(int runDuration = 0)
{
    // Set TTL...
    if (this.LastRunDateTime == DateTime.MinValue) // First run...
    {
	return TimeSpan.FromSeconds(ObserverManager.ObserverExecutionLoopSleepSeconds)
			 .Add(TimeSpan.FromMinutes(TTLAddMinutes));
    }
    else
    {
	return DateTime.Now.Subtract(this.LastRunDateTime)
	       	.Add(TimeSpan.FromSeconds(runDuration))
	       	.Add(TimeSpan.FromSeconds(ObserverManager.ObserverExecutionLoopSleepSeconds));
    }
} 
```

Let's look at the simple design of an Observer:
```c#
using System;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using FabricObserver.Utilities;

namespace FabricObserver
{
	class SomeObserver : ObserverBase
	{
		 internal SomeObserver() : base(ObserverConstants.SomeObserverName) { }
		
		 internal override DateTime LastRunDateTime { get; set; } = DateTime.MinValue;
		 
		 internal override async Task ObserveAsync(CancellationToken token)
	 	 {
			 // Observe then call ReportAsync...
		 }
		
		 internal override async Task ReportAsync(CancellationToken token)
		 {
			 // Prepare observational data to send to ObserverBase's ProcessDataReportHealth function...
		 }
	 }	
 }
 ```

**Currently Implemented Observers**  

***AppObserver***  
***DiskObserver***  
***FabricSystemObserver***  
***NetworkObserver***  
***NodeObserver***  
***OSObserver***  
***SFConfigurationObserver***  

Each Observer instance logs to a directory of the same name. You can configure the base directory of the output and log verbosity level (verbose or not).

You can see examples of specific observer output by calling a REST endpoint a la: 

Observer Logs -> http://winlrc-ctorre-10.westus2.cloudapp.azure.com:8080/api/ObserverLog/DiskObserver/_SFRole0_0
ObserverManager Log -> http://winlrc-ctorre-10.westus2.cloudapp.azure.com:8080/api/ObserverManager

**AppObserver**  
Observer that monitors CPU usage, Memory use, and Disk space
availability for Service Fabric Application services (processes). This
observer will alert when user-supplied thresholds are reached.

**Input**: JSON config file supplied by user, stored in
PackageRoot/Observers.Data folder. This data contains JSON arrays
objects which constitute Service Fabric Apps (identified by service
URI's). Users supply Min/Max thresholds for CPU use, Memory use and Disk
space. Memory values are supplied as (long) number of bytes... CPU and
Disk Space values are provided as percentages (integers)...

Example JSON config file located in **PackageRoot\\Observers.Data**
folder (AppObserver.config.json):
```javascript
[
  {
    "target": "fabric:/BadApp",
    "cpuErrorLimitPct": 0,
    "cpuWarningLimitPct": 30,
    "diskIOErrorReadsPerSecMS": 0,
    "diskIOErrorWritesPerSecMS": 0,
    "diskIOWarningReadsPerSecMS": 45,
    "diskIOWarningWritesPerSecMS": 45,
    "dumpProcessOnError": true,
    "memoryErrorLimitMB": 15000,
    "memoryWarningLimitMB": 12000,
    "networkErrorActivePorts": 1000,
    "networkWarningActivePorts": 800,
    "networkErrorEphemeralPorts": 500,
    "networkWarningEphemeralPorts": 400
  },
  {
    "target": "fabric:/CpuStress",
    "cpuErrorLimitPct": 0,
    "cpuWarningLimitPct": 30,
    "diskIOErrorReadsPerSecMS": 0,
    "diskIOErrorWritesPerSecMS": 0,
    "diskIOWarningReadsPerSecMS": 45,
    "diskIOWarningWritesPerSecMS": 45,
    "dumpProcessOnError": true,
    "memoryErrorLimitMB": 0,
    "memoryWarningLimitMB": 12000,
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
    "diskIOWarningReadsPerSecMS": 45,
    "diskIOWarningWritesPerSecMS": 45,
    "dumpProcessOnError": true,
    "memoryErrorLimitMB": 0,
    "memoryWarningLimitMB": 100,
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
    "diskIOWarningReadsPerSecMS": 45,
    "diskIOWarningWritesPerSecMS": 45,
    "dumpProcessOnError": true,
    "memoryErrorLimitMB": 0,
    "memoryWarningLimitMB": 12000,
    "networkErrorActivePorts": 0,
    "networkWarningActivePorts": 800,
    "networkErrorEphemeralPorts": 0,
    "networkWarningEphemeralPorts": 400
  }
]
```

**target**: App URI to observe (or "system" for node-level resource
monitoring...)\
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
(Error/Warning/ok)

This observer also outputs CSV files for each app containing all
resource usage data across iterations for use in analysis. Included are
Average and Peak measurements.

This observer runs for 60 seconds by default (this is user
configurable in Settings.xml). The thresholds are user-supplied-only and
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

This observer also monitors the FabricObserver service itself for
CPU/Mem/Disk.

**DiskObserver**
This observer monitors, records and analyzes storage disk information.
Depending upon configuration settings, it signals disk health status
warnings (or OK state) for all logical disks it detects.

After DiskObserver logs basic information, it performs 60 seconds of
Disk measurements on all logical disks. This ensures we don't warn on
transient disk states. The data values collected are averaged and then
used in ReportAsync to determine if a Warning shot should be sent to
SFX, based on user-supplied threshold setting for Space (we internally
warn on IO, but we can enable this as a user setting, too...).
Currently, the data collected are disk space usage (percent of disk
space used). % Disk Read Time, % Disk Write Time. This can grow as
needed by customers... More to do here, but this fine for V1 since it
covers space and basic IOPs. Note that currently we Warn when Disk Read
Time or Disk Write Time exceeds, on average, 25% over the course of
sampling. This is the typical behavior of monitors. We can change this
based on real world data...

**Output**: 

Node Health Report (Error/Warning/Ok)
  
example: 

Disk Info:

Drive Name: C:\\
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
Peak measurements.**

**FabricSystemObserver**  
This observer monitors Fabric system services for 1 minute per global
observer iteration e.g., Fabric, FabricApplicationGateway, FabricCAS,
FabricDCA, FabricDnsService, FabricGateway, FabricHost,
FileStoreService.

**Input**: Settings.xml in PackageRoot\\Observers.Config\
**Output**: Log text(Error/Warning), Service Fabric Health Report
(Error/Warning)

This observer also outputs a CSV file containing all resource usage
data for each Fabric service across iterations for use in analysis.
Included are Average and Peak measurements.

This observer runs for either a specified configuration setting of time
or default of 60 seconds. Each fabric system process is monitored for
CPU and memory usage, with related values stored in instances of
FabricResourceUsageData: 
```C#
using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Utilities
{
    internal class FabricResourceUsageData<T>
    {
        public List<T> Data { get; private set; }
        public string Name { get; set; }
        public T MaxDataValue { get; }
        public double AverageDataValue { get; }
        public bool ActiveErrorOrWarning { get; set; }
        public int LifetimeWarningCount { get; set; } = 0;
        public bool IsUnhealthy<U>(U threshold){ ... }
        public double StandardDeviation { get; }
	public FabricResourceUsageData(string name){ ... }
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

Observer that checks networking conditions across outbound and
inbound connection state, Firewalls (monitors for leaks), Ports
(exhaustion), etc\...

**Input**: NetworkObserver.config.json in PackageRoot\\Observers.Data.
Users should supply hostname/port pairs (if they only allow
communication with an allowed list of endpoints, for example, or just
want us to test the endpoints they care about...). If this list is not
provided, the observer will run through a default list of well-known,
reliable internal Internet endpoints: google.com, facebook.com,
azure.microsoft.com. The implementation allows for either an ICMP or
TCP-based test. Is this overkill? If not, then we can add another
property to the json object, "protocol", that the user can supply. We
default to TCP today...

Each endpoint test result is stored in a simple data type
(ConnectionState) that lives for either the lifetime of the run or until
the next run assuming failure, in which case if the endpoint is
reachable, SFX will be cleared with an OK health state report and the
ConnectionState object will be removed from the containing
List\<ConnectionState\>. Only Warning data is persisted across
iterations.
```C#
internal class ConnectionState
{
	internal string HostName { get; set; }
	internal bool Connected { get; set; }
	internal HealthState Health { get; set; }
}
```
**Output**: Log text(Info/Error/Warning), Service Fabric Health Report
(Error/Warning)  

Example NetworkObserver.config.json configuration:  

```javascript
[
  {
      "appTarget": "fabric:/CpuStress",
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
      "appTarget": "fabric:/FabricObserver",
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

This observer runs 4 checks per supplied hostname with a 3 second delay
between tests. This is done to help ensure we don't report transient
network failures which will result in Fabric Health warnings that live
until the observer runs again.

**Output:**  
Application Health Report (Error/Warning/Ok) and logging.

Network observer logs and monitors port use (Active). Log will simply
contain output like this per non-warning (Ok) cycle (in Release build,
which is necessarily less noisy than Debug...):

2018-12-19
15:54:23.9806\|INFO\|FabricObserver.Utilities.Logger\|fabric:/FabricObserverImpl\|Starting
Observer: NetworkObserver\
2018-12-19
15:54:24.0495\|INFO\|FabricObserver.Utilities.Logger\|NetworkObserver\|Current
number of **ports in use: 74**\
2018-12-19
15:54:24.6448\|INFO\|FabricObserver.Utilities.Logger\|fabric:/FabricObserverImpl\|Completed
Observer: NetworkObserver\
\
NetworkObserver keeps track of and records Firewall rules count for use
in Firewall leak detection. (NOTE: This should probably be put inside
NodeObserver...) 

**NodeObserver**
This observer records CPU (%CPU time), Disk (% space used), Memory
(private working set in MBs), and Port use count (int) of a Fabric node.
It will warn on min/max thresholds supplied in a Json configuration
file, NodeObserver.config.json  

**Input**:
```javascript
{ 
  "target": "node", 
  "cpuErrorLimitPct": 0, 
  "cpuWarningLimitPct": 80, 
  "memoryErrorLimitMB": 0, 
  "memoryWarningLimitMB": 28000, 
  "networkErrorActivePorts": 0, 
  "networkErrorEphemeralPorts": 0, 
  "networkErrorFirewallRules": 0, 
  "networkWarningActivePorts": 45000, 
  "networkWarningEphemeralPorts": 20000, 
  "networkWarningFirewallRules": 2500 
} 
```
**target**: always "node"...    
**cpuErrorLimitPct**: Maximum CPU percentage that should generate an
Error (SFX and local log)\
**cpuWarningLimitPct**: Minimum CPU percentage that should generate a
Warning (SFX and local log)\
**memoryErrorLimitMB**: Maximum service process private working set,
in Megabytes, that should generate an Error (SFX and local log) \[Note:
this shouldn't have to be supplied in bytes...\]\
**memoryWarningLimitMB**: Minimum service process private working set,
in Megabytes, that should generate an Warning (SFX and local log)
\[Note: this shouldn't have to be supplied in bytes...\]\
**networkErrorActivePorts:** Maximum number of established ports in use by
all processes on node that will generate a Fabric Error.\
**networkWarningActivePorts:** Minimum number of established TCP ports in use by
all processes on node that will generate a Fabric Warning.\
**networkErrorEphemeralPorts:** Maximum number of established ephemeral TCP ports in use by
app process that will generate a Fabric Error.\
**networkWarningEphemeralPorts:** Minimum number of established ephemeral TCP ports in use by
all processes on node that will generate a Fabric warning.\

**Output**:\
SFX Warnings when min/max thresholds are reached. CSV file,
CpuMemDiskPorts\_\[nodeName\].csv, containing long-running data (across
all run iterations of the observer).

**OSObserver**\
This observer records basic OS properties for use during mitigations,
RCAs. It will only run occasionally given the metrics are generally
static.\
\
**Input**: This observer does not take input.\
**Output**: Log text(Error/Warning), Service Fabric Health Report
(Error/Warning)

The output of OSObserver is stored in the local log file,
fabric\_observers.log. The only Fabric health report generated by this observer 
is an Error when OS Status is not "OK" (which means something is wrong at the OS level and
this means trouble...). As stated above, only OS status, last boot time,
and patches/hotfixes are reported (logged) during each run iteration. 

Example output: 

Last updated on 8/5/2019 17:26:18 UTC

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

**DESIGN RATIONALE**

This project will have multiple phases across design and execution,
beginning with phase 1, which is where we will determine the metrics
that make sense to collect along with where they map into what is
already being monitored by a subsystem component (a la plb). we will
also be researching what our internal customer are doing in this domain
(cosmosdb, sql azure, intune, ust...). we will not rewrite the same
stuff repeatedly. instead, we will focus on design and reuse as much
existing code assets as we can. further, we will not do this in a
vacuum: we will partner with existing projects (a la ust healing
service) and ensure we collaborate in an effective and efficient way: we
all want the same thing here =\> limit the number of preventable icm
incidents by analyzing, understanding, and reacting to real-time
systemic misbehavior that is directly correlated to the misbehavior of
service fabric services through an automated observation and analysis
system comprised of service fabric observers.

We will design and implement phase 1 of the project in 30 days. this
will include the development of stateless service that observes and
collects data on and detects the following types of per-process
(service) conditions:

-   high cpu and memory usage (for app process as well as fabric system
    services...)

-   critical local disk state (available space, queue size, throughput,
    etc.... tbd)

-   networking (outbound communication state, latency, availability,
    etc.... tbd)

-   port exhaustion

-   firewall leaks

-   os basics: last reboot time, os version, os sku, os install datetime

-   service fabric basics: last fabric.exe crash datetime (via eventlog
    on windows, \[?\] on linux...)

The key here is that we will not spend time on observing things that
have not shown up at some point as part of an icm rc. we also need time
to meet with other teams doing similar work, sharing knowledge (and
code) and collaborating. we will not be working in our own little
private sandbox...

**Data Design**

Each observer is tasked with recording one or more metrics that are
relevant to local Machine health. Each metric will be stored in an
in-memory data structure (List\<T\> or some other generic collection
type) on the observer type, specified as a private instance field. These
values will be used to decide actionable (useful for mitigation)
alerting strategy (time based, standard deviation, min and max
thresholds).\
\
**Basic Output**

Log files containing time-based INFO, DEBUG, ERROR, and WARNING
information. INFO and DEBUG are written only in DEBUG builds for
development purposes. Only ERROR and WARNING states are signaled to
Fabric and logged locally. Some observers also output CSV files
containing related resource usage data across all iterations for use in
analysis, a long-running view of their behavior as it pertains to
resource consumption across CPU Time, Workingset, DiskIO. Peak and
Average usage is computed and stored. Each CSV is archived after 24
hours to limit file sizes.\
\
Fabric Health Reports for surfacing WARNING and ERROR states to users in
SFX. We will be diligent in not abusing Service Fabric Health store and
reporting. These states are preserved in memory across observation
iterations and if the state has settled back to Ok (normal), we will
fire off a health report with a *HealthState.Ok* to clear SFX
Error/Warning assuming expiration time has not already been reached. We
could maintain this in memory as well, and only send an Ok report if we
need to.

**Conclusion**

Observers are designed to be low impact, long-lived objects that perform
specific observational and related reporting activities across iteration 
intervals defined in configuration settings for each observer type. 
As their name clearly suggests, they do not mitigate in this first version. 
They observe, record, and report. We will use the data to understand the usefulness 
of this approach before embarking on automatic safe mitigation design and implementation. 
For Warning and Errors, we will utilize Fabric Health store and reporting mechanisms to surface
important information in SFX. This release also includes a telemtry provider interface and
ships with an AppInsights implementation. So, you can stream events to AppInsigths in addition
to file logging and SFX health reporting.


# Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.microsoft.com.

When you submit a pull request, a CLA-bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., label, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
