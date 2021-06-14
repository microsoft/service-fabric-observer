# Observers

Observers are low-impact, long-lived objects that perform specialied monitoring and reporting activities. Observers monitor and report, but they aren't designed to take action. Observers generally monitor appliations through their side effects on the node, like resource usage, but do not actually communicate with the applications. Observers report to SF Event Store (viewable through SFX) in warning and error states, and can use built-in AppInsights support to report there as well.  

### Note: All of the observers that collect resource usage data can also emit telemetry: EventSource ETW and either LogAnalytics or ApplicationInsights diagnostic service calls. 

> AppInsights or LogAnalytics telemetry can be enabled in `Settings.xml` by providing your related authorization/identity information (keys).

### Logging

Each Observer instance logs to a directory of the same name. You can configure the base directory of the output and log verbosity level (verbose or not). If you enable telemetry and provide ApplicationInsights/LogAnalytics settings, then you will also see the output in your Azure analytics queries. Each observer has configuration settings in PackageRoot/Config/Settings.xml. AppObserver and NetworkObserver house their runtime config settings (error/warning thresholds) in json files located in PackageRoot/Observers.Data folder.  

### Emiting Errors

Service Fabric Error Health Events can block upgrades and other important Fabric runtime operations. Error thresholds should be set such that putting the cluster in an emergency state incurs less cost than allowing the state to continue. For this reason, Fabric Observer by default ***treats Errors as Warnings***.  However if your cluster health policy is to [ConsiderWarningAsError](https://docs.microsoft.com/en-us/azure/service-fabric/service-fabric-health-introduction#cluster-health-policy), FabricObserver has a ***high risk of putting your cluster in an error state***. Proceed with caution.

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

# Observers - What they do and how to configure them  

You can quickly get started by reading [this](/Documentation/Using.md).  

***Note: All observers that monitor various resources output serialized instances of TelemetryData type (JSON). This JSON string is set as the Description property of a Health Event. This is done for a few reasons: Telemetry support and for consuming services that need to deserialize the data to inform some related workflow. In later versions of SF, SFX will display only the textual pieces of this serialized object instance, making it easier to read in SFX's Details view.***

```
The vast majority of settings for any observer are provided in ApplicationManifest.xml 
as required overridden parameters. For AppObserver and NetworkObserver, 
all thresholds/settings are housed in json files, not XML.
For every other observer, it's XML as per usual.
```  

## AppObserver  
Observer that monitors CPU usage, Memory use, and Port use for Service Fabric Application services (processes). This
observer will alert (SF Health event) when user-supplied thresholds are reached. **Please note that this observer should not be used to monitor docker container applications. It is not designed for this task. Instead, please consider employing [ContainerObserver](https://github.com/GitTorre/ContainerObserver), which is designed specifically for container monitoring**.

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

Example JSON config file located in **PackageRoot\\Config** folder (AppObserver.config.json). This is an example of a configuration that applies
to all Service Fabric user (non-System) application service processes running on the virtual machine.
```JSON
[
  {
    "targetApp": "*",
    "cpuWarningLimitPercent": 80,
    "memoryWarningLimitMb": 1048,
    "networkWarningEphemeralPorts": 7000
  }
]
```
Settings descriptions: 

All settings are optional, ***except target OR targetType***, and can be omitted if you don't want to track. For process memory use, you can supply either MB values (a la 1024 for 1GB) for Working Set (Private) or percentage of total memory in use by process (as an integer, 1 - 100).

| Setting | Description |
| :--- | :--- |
| **targetApp** | App URI string to observe. Optional (Required if targetType not specified). | 
| **targetAppType** | ApplicationType name (this is not a Uri format). FO will observe **all** app services belonging to it. Optional (Required if target not specified). | 
| **serviceExcludeList** | A comma-separated list of service names (***not URI format***, just the service name as we already know the app name URI) to ***exclude from observation***. Just omit the object or set value to "" to mean ***include all***. (excluding all does not make sense) |
| **serviceIncludeList** | A comma-separated list of service names (***not URI format***, just the service name as we already know the app name URI) to ***include in observation***. Just omit the object or set value to "" to mean ***include all***. |  
| **memoryErrorLimitMb** | Maximum service process private working set in Megabytes that should generate a Fabric Error (SFX and local log) |  
| **memoryWarningLimitMb**| Minimum service process private working set in Megabytes that should generate a Fabric Warning (SFX and local log) |  
| **memoryErrorLimitPercent** | Maximum percentage of memory used by an App's service process (integer) that should generate a Fabric Error (SFX and local log) |  
| **memoryWarningLimitPercent** | Minimum percentage of memory used by an App's service process (integer) that should generate a Fabric Warning (SFX and local log) | 
| **cpuErrorLimitPercent** | Maximum CPU percentage that should generate a Fabric Error |
| **cpuWarningLimitPercent** | Minimum CPU percentage that should generate a Fabric Warning |
| **dumpProcessOnError** | Instructs whether or not FabricObserver should   dump your service process when service health is detected to be in an  Error (critical) state... |  
| **networkErrorActivePorts** | Maximum number of established TCP ports in use by app process that will generate a Fabric Error. |
| **networkWarningActivePorts** | Minimum number of established TCP ports in use by app process that will generate a Fabric Warning. |
| **networkErrorEphemeralPorts** | Maximum number of ephemeral TCP ports (within a dynamic port range) in use by app process that will generate a Fabric Error. |
| **networkWarningEphemeralPorts** | Minimum number of established TCP ports (within a dynamic port range) in use by app process that will generate a Fabric Warning. |  
| **errorOpenFileHandles** | Maximum number of open file handles in use by an app process that will generate a Fabric Error. |  
| **warningOpenFileHandles** | Minimum number of open file handles in use by app process that will generate a Fabric Warning. |  

**Output** Log text(Error/Warning), Service Fabric Application Health Report (Error/Warning/Ok), ETW (EventSource), Telemetry (AppInsights/LogAnalytics)

Example SFX Output (Warning - Ephemeral Ports Usage):  

![alt text](/Documentation/Images/AppObsWarn.png "AppObserver Warning output example.")  

AppObserver also optionally outputs CSV files for each app containing all resource usage data across iterations for use in analysis. Included are Average and Peak measurements. You can turn this on/off in ApplicationManifest.xml. See Settings.xml where there are comments explaining the feature further.  
  
AppObserver error/warning thresholds are user-supplied-only and bound to specific service instances (processes) as dictated by the user,
as explained above. Like FabricSystemObserver, all data is stored in in-memory data structures for the lifetime of the run (for example, 60 seconds at 5 second intervals). Like all observers, the last thing this observer does is call its *ReportAsync*, which will then determine the health state based on accumulated data across resources, send a Warning if necessary (clear an existing warning if necessary), then clean out the in-memory data structures to limit impact on the system over time. So, each iteration of this observer accumulates *temporary* data for use in health determination.
  
This observer also monitors the FabricObserver service itself across CPU/Mem/FileHandles/Ports.  

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
    <Parameter Name="Enabled" Value="" MustOverride="true" />
    <Parameter Name="EnableTelemetry" Value="" MustOverride="true" />
    <Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
    <!-- Optional: How often does the observer run? For example, CertificateObserver's RunInterval is set to 1 day 
         below, which means it won't run more than once a day (where day = 24 hours.). All observers support a RunInterval parameter.
         Just add a Parameter like below to any of the observers' config sections when you want this level of run control.
         Format: Day(s).Hours:Minutes:Seconds e.g., 1.00:00:00 = 1 day. -->
    <Parameter Name="RunInterval" Value="" MustOverride="true" />
    <!-- Cluster and App Certs Warning Start (Days) -> DefaultValue is 42 -->
    <Parameter Name="DaysUntilClusterExpiryWarningThreshold" Value="" MustOverride="true" />
    <Parameter Name="DaysUntilAppExpiryWarningThreshold" Value="" MustOverride="true" />
    <!-- Required: These are JSON-style lists of strings, empty should be "[]", full should be "['thumb1', 'thumb2']" -->
    <Parameter Name="AppCertThumbprintsToObserve" Value="" MustOverride="true" />
    <Parameter
```

## DiskObserver
This observer monitors, records and analyzes storage disk information.
Depending upon configuration settings, it signals disk health status
warnings (or OK state) for all logical disks it detects.

After DiskObserver logs basic disk information, it performs measurements on all logical disks across space usage (Consumption) and IO (Average Queue Length). The data collected are used in ReportAsync to determine if a Warning shot should be fired based on user-supplied threshold settings housed in Settings.xml. Note that you do not need to specify a threshold parameter that you don't plan you using. You can either omit the XML node or leave the value blank (or set to 0).

```xml
  <Section Name="DiskObserverConfiguration">
    <Parameter Name="Enabled" Value="" MustOverride="true" />
    <Parameter Name="EnableTelemetry" Value="" MustOverride="true" />
    <Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
    <Parameter Name="MonitorDuration" Value="" MustOverride="true" />
    <Parameter Name="RunInterval" Value="" MustOverride="true" />
    <Parameter Name="DiskSpacePercentUsageWarningThreshold" Value="" MustOverride="true" />
    <Parameter Name="DiskSpacePercentUsageErrorThreshold" Value="" MustOverride="true" />
    <Parameter Name="AverageQueueLengthErrorThreshold" Value ="" MustOverride="true" />
    <Parameter Name="AverageQueueLengthWarningThreshold" Value ="" MustOverride="true" />
  </Section>
```

**Output**: 

Node Health Report (Error/Warning/Ok), structured telemetry.
  
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
  Avg. Disk Queue Length: 0   

**This observer also optionally outputs a CSV file containing all resource usage
data across iterations for use in analysis. Included are Average and
Peak measurements. Set in Settings.xml's EnableLongRunningCSVLogging boolean setting.**  

Example SFX Output (Warning - Disk Space Consumption):  

![alt text](/Documentation/Images/DiskObsWarn.png "DiskObserver output example.")  


## FabricSystemObserver 
This observer monitors Fabric system service processes e.g., Fabric, FabricApplicationGateway, FabricCAS,
FabricDCA, FabricDnsService, FabricFAS, FabricGateway, FabricHost, etc...

**NOTE:**
Only enable FabricSystemObserver ***after*** you get a sense of what impact your services have on the SF runtime, in particular Fabric.exe. 
This is very important because there is no "one threshold fits all" across warning/error thresholds for any of the SF system services. 
By default, FabricObserver runs as NetworkUser on Windows and sfappsuser on Linux. These are non-privileged accounts and therefore for any service
running as System or root, default FabricObserver can't monitor process behavior (this is always true on Windows). That said, there are only a few system
services you would care about: Fabric.exe and FabricGateway.exe. Fabric.exe is generally the system service that your code can directly impact with respect to machine resource usage.

**Input - Settings.xml**: Only ClusterOperationTimeoutSeconds is set in Settings.xml.

```xml
  <Section Name="FabricSystemObserverConfiguration">
    ...
    <Parameter Name="ClusterOperationTimeoutSeconds" Value="120" />
  </Section>
```

**Input - ApplicationManifest.xml**: Threshold settings are defined (overriden) in ApplicationManifest.xml.

```xml
<!-- FabricSystemObserver -->
<Parameter Name="FabricSystemObserverUseCircularBuffer" DefaultValue="false" />
<!-- Required-If UseCircularBuffer = True -->
<Parameter Name="FabricSystemObserverResourceUsageDataCapacity" DefaultValue="" />
<!-- FabricSystemObserver Warning/Error Thresholds -->
<Parameter Name="FabricSystemObserverCpuErrorLimitPercent" DefaultValue="" />
<Parameter Name="FabricSystemObserverCpuWarningLimitPercent" DefaultValue="" />
<Parameter Name="FabricSystemObserverMemoryErrorLimitMb" DefaultValue="" />
<Parameter Name="FabricSystemObserverMemoryWarningLimitMb" DefaultValue="4096" />
<Parameter Name="FabricSystemObserverNetworkErrorActivePorts" DefaultValue="" />
<Parameter Name="FabricSystemObserverNetworkWarningActivePorts" DefaultValue="" />
<Parameter Name="FabricSystemObserverNetworkErrorEphemeralPorts" DefaultValue="4000" />
<Parameter Name="FabricSystemObserverNetworkWarningEphemeralPorts" DefaultValue="" />
<Parameter Name="FabricSystemObserverAllocatedHandlesErrorLimit" DefaultValue="" />
<Parameter Name="FabricSystemObserverAllocatedHandlesWarningLimit" DefaultValue="5000" />
<!-- Whether to monitor Windows Event Log. -->
<Parameter Name="FabricSystemObserverMonitorWindowsEventLog" DefaultValue="false" />
```

**Output**: Log text(Error/Warning), Service Fabric Health Report (Error/Warning/Ok), ETW, Telemetry

Example SFX output (Informational):  

![alt text](/Documentation/Images/FOFSOInfo.png "FabricSystemObserver output example.")  

**This observer also optionally outputs a CSV file containing all resource usage
data across iterations for use in analysis. Included are Average and
Peak measurements. See Settings.xml's EnableCSVDataLogging setting.**

This observer runs for either a specified configuration setting of time
or default of 30 seconds. Each fabric system process is monitored for
CPU, memory, ports, and allocated handle usage, with related values stored in instances of
FabricResourceUsageData object.

## NetworkObserver

This observer checks outbound connection state for user-supplied endpoints (hostname/port pairs).

**Input**: NetworkObserver.config.json in PackageRoot\\Config.
Users must supply target application Uri (targetApp), endpoint hostname/port pairs, and protocol. 

The point of this observer is to simply test reachability of an external endpoint. There is no support for verifying successful authentication with remote endpoints. That is, the tests will not authenticate with credentials, but will pass if the server responds since the response means the server/service is up and running (reachable). The implementation allows for TCP/IP-based tests (HTTP or direct TCP), which is the most common protocol for use in service to service communication (which will undoubtedly involve passing data back and forth).

Each endpoint test result is stored in a simple data type
(ConnectionState) that lives for either the lifetime of the run or until
the next run assuming failure, in which case if the endpoint is
reachable, SFX will be cleared with an OK health state report and the
ConnectionState object will be removed from the containing
List\<ConnectionState\>. Only Warning data is persisted across
iterations. If targetApp entries share hostname/port pairs, only one test will
be conducted to limit network traffic. However, each app that is configured with the same
hostname/port pairs will go into warning if the reachability test fails.

| Setting | Description |
| :--- | :--- | 
| **targetApp**|SF application name (Uri format) to target. | 
| **endpoints** | Array of hostname/port pairs and protocol to be used for the reachability tests. | 
| **hostname**| The remote hostname to connect to. You don't have to supply a prefix like http or https if you are using port 443. However, if the endpoint requires a secure SSL/TLS channel using a custom port (so, *not* 443, for example), then you must provide the https prefix (like for a CosmosDB REST endpoint) in the hostname setting string. If you want to test local endpoints (same local network/cluster, some port number), then use the local IP and *not* the domain name for hostname. | 
| **port**| The remote port to connect to. |
| **protocol** | These are "direct" protocols. So, http means the connection should be over HTTP (like REST calls), and a WebRequest will be made. tcp is for database endpoints like SQL Azure, which require a direct TCP connection (TcpClient socket will be used). Note: the default is http, so you can omit this setting for your REST endpoints, etc. You must use tcp when you want to monitor database server endpoints like SQL Azure.|  


Example NetworkObserver.config.json configuration:  

```javascript
[
  {
    "targetApp": "fabric:/MyApp0",
    "endpoints": [
      {
        "hostname": "https://myazuresrvice42.westus2.cloudapp.azure.com",
        "port": 443,
        "protocol": "http"
      },
      {
        "hostname": "somesqlservername.database.windows.net",
        "port": 1433,
        "protocol": "tcp"
      }
    ]
  },
  {
    "targetApp": "fabric:/MyApp1",
    "endpoints": [
      {
        "hostname": "somesqlservername.database.windows.net",
        "port": 1433,
        "protocol": "tcp"
      }
    ]
  }
]
```

**Output**: Log text(Error/Warning), Service Fabric Health Report (Error/Warning/Ok), structured telemetry.  

This observer runs 4 checks per supplied hostname with a 3 second delay between tests. This is done to help ensure we don't report transient
network failures which will result in Fabric Health warnings that live until the observer runs again.  


## NodeObserver
 This observer monitors VM level resource usage across CPU, Memory, firewall rules, static and dynamic ports (aka ephemeral ports).
 Thresholds for Erorr and Warning signals are user-supplied in ApplicationManifest.xml.  

**Input - ApplicationManifest.xml**:
```xml
<!-- NodeObserver -->
<Parameter Name="NodeObserverUseCircularBuffer" DefaultValue="false" />
<!-- Required-If UseCircularBuffer = True -->
<Parameter Name="NodeObserverResourceUsageDataCapacity" DefaultValue="" />
<!-- NodeObserver Warning/Error Thresholds -->
<Parameter Name="NodeObserverCpuErrorLimitPercent" DefaultValue="" />
<Parameter Name="NodeObserverCpuWarningLimitPercent" DefaultValue="90" />
<Parameter Name="NodeObserverMemoryErrorLimitMb" DefaultValue="" />
<Parameter Name="NodeObserverMemoryWarningLimitMb" DefaultValue="" />
<Parameter Name="NodeObserverMemoryErrorLimitPercent" DefaultValue="" />
<Parameter Name="NodeObserverMemoryWarningLimitPercent" DefaultValue="95" />
<Parameter Name="NodeObserverNetworkErrorActivePorts" DefaultValue="" />
<Parameter Name="NodeObserverNetworkWarningActivePorts" DefaultValue="50000" />
<Parameter Name="NodeObserverNetworkErrorFirewallRules" DefaultValue="" />
<Parameter Name="NodeObserverNetworkWarningFirewallRules" DefaultValue="2500" />
<Parameter Name="NodeObserverNetworkErrorEphemeralPorts" DefaultValue="" />
<Parameter Name="NodeObserverNetworkWarningEphemeralPorts" DefaultValue="20000" />
<!-- The below settings only make sense for Linux. -->
<Parameter Name="NodeObserverLinuxFileHandlesErrorLimitPercent" DefaultValue="" />
<Parameter Name="NodeObserverLinuxFileHandlesWarningLimitPercent" DefaultValue="90" />
<Parameter Name="NodeObserverLinuxFileHandlesErrorLimitTotal" DefaultValue="" />
<Parameter Name="NodeObserverLinuxFileHandlesWarningLimitTotal" DefaultValue="" />
```  

| Setting | Description |
| :--- | :--- | 
| **CpuErrorLimitPercent** | Maximum CPU percentage that should generate an Error |  
| **CpuWarningLimitPercent** | Minimum CPU percentage that should generate a Warning | 
| **EnableTelemetry** | Whether or not to send Observer data to diagnostics/log analytics service. |  
| **MemoryErrorLimitMb** | Maximum amount of committed memory on virtual machine that will generate an Error. | 
| **MemoryWarningLimitMb** | Minimum amount of committed memory that will generate a Warning. |  
| **MemoryErrorLimitPercent** | Maximum percentage of memory in use on virtual machine that will generate an Error. | 
| **MemoryWarningLimitPercent** | Minimum percentage of memory in use on virtual machine that will generate a Warning. |  
| **MonitorDuration** | The amount of time this observer conducts resource usage probing. | 
| **NetworkErrorFirewallRules** | Number of established Firewall Rules that will generate a Health Warning. |  
| **NetworkWarningFirewallRules** |  Number of established Firewall Rules that will generate a Health Error. |  
| **NetworkErrorActivePorts** | Maximum number of established ports in use by all processes on node that will generate a Fabric Error. |
| **NetworkWarningActivePorts** | Minimum number of established TCP ports in use by all processes on node that will generate a Fabric Warning. |
| **NetworkErrorEphemeralPorts** | Maximum number of established ephemeral TCP ports in use by app process that will generate a Fabric Error. |
| **NetworkWarningEphemeralPorts** | Minimum number of established ephemeral TCP ports in use by all processes on node that will generate a Fabric warning. |
| **UseCircularBuffer** | You can choose between of `List<T>` or a `CircularBufferCollection<T>` for observer data storage. | 
| **ResourceUsageDataCapacity** | Required-If UseCircularBuffer = True: This represents the number of items to hold in the data collection instance for the observer. | 
| **LinuxFileHandlesErrorLimitPercent** | Maximum percentage of allocated file handles (as a percentage of maximum FDs configured) in use on Linux virtual machine that will generate an Error. | 
| **LinuxFileHandlesWarningLimitPercent** | Minumum percentage of allocated file handles (as a percentage of maximum FDs configured) in use on Linux virtual machine that will generate a Warning. |
| **LinuxFileHandlesErrorLimitTotal** | Total number of allocated file handles in use on Linux virtual machine that will generate an Error. | 
| **LinuxFileHandlesWarningLimitTotal** | Total number of allocated file handles in use on Linux virtual machine that will generate a Warning. |

**Output**:
SFX Warnings when min/max thresholds are reached. CSV file,
CpuMemDiskPorts\_\[nodeName\].csv, containing long-running data (across
all run iterations of the observer) if csv output is enabled, structured telemetry.  

Example SFX Output (Warning - Memory Consumption):  

![alt text](/Documentation/Images/FODiskNodeObs.png "NodeObserver output example.")  


## OSObserver
This observer records basic OS properties across OS version, OS health status, physical/virtual memory use, number of running processes, number of active TCP ports (active/ephemeral), number of enabled firewall rules, list of recent patches/hotfixes. It creates an OK Health State SF Health Report that is visible in SFX at the node level (Details tab) and by calling http://localhost:5000/api/ObserverManager if you have deployed the FabricObserver Web Api App. It's best to enable this observer in all deployments of FO. OSObserver will check the VM's Windows Update AutoUpdate settings and Warn if Windows AutoUpdate Downloads setting is enabled. It is critical to not install Windows Updates in an unregulated (non-rolling) manner is this can take down multiple VMs concurrently, which can lead to seed node quorum loss in your cluster. Please do not enable Automatic Windows Update downloads. **It is highly recommended that you enable [Azure virtual machine scale set automatic OS image upgrades](https://docs.microsoft.com/azure/virtual-machine-scale-sets/virtual-machine-scale-sets-automatic-upgrade).**

**Input**: For Windows, you can set OSObserverEnableWindowsAutoUpdateCheck setting to true of false. This will let you know if your OS is misconfigured with respect to how Windows Update manages update downloads and installation. In general, you should not configure Windows to automatically download Windows Update binaries. Instead, use VMSS Automatic Image Upgrade service.  
**Output**: Log text(Error/Warning), Service Fabric Health Report (Ok/Error), structured telemetry, HTML output for API service and SFX (node level Details tab). 

The output of OSObserver is stored in its local log file when the FabricObserverWebApi service is deployed/enabled. The only Fabric health reports generated by this observer 
is an Error when OS Status is not "OK" (which means something is wrong at the OS level and
this means trouble), a Warning if Windows Update Automatic Update service is configured to automatically download updates, and long-lived Ok Health Report that contains the information it collected about the VM it's running on.  

Example SFX output (Informational): 

![alt text](/Documentation/Images/FONodeDetails.png "OSObserver output example.")  


## SFConfigurationObserver 

This observer doesn't monitor or report health status. 
It provides information about the currently installed Service Fabric runtime environment.
The output (a local file) is used by the FabricObserver API service, rendered as HTML (e.g., http://localhost:5000/api/ObserverManager). You can learn more about API service [here](/FabricObserverWeb/ReadMe.md).

## Writing a New Observer Outside of the FabricObserver project sources (Recommended) - Observer Plugin
Please see the [SampleObserver project](/SampleObserverPlugin) for a complete sample observer plugin implementation with code comments and readme.
Also, see [How to implement an observer plugin using our extensibility model](/Documentation/Plugins.md)
