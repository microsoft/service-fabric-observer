## FabricObserver ETW Support

FabricObserver employs EventSource events for ETW. There are two key pieces to this support. This is a feature typically used by internal Microsoft teams. Generally, you should disable this feature unless
you have a way to push EventSource ETW to Azure Kusto.

- FabricObserverETWProvider is the default name of the EventSource provider. You can customize this name by changing
the value of the Application parameter ObserverManagerETWProviderName. Doing so is unnecessary unless you have a more advanced scenario (like multiple instance of FO running on the same node).
- FabricObserverDataEvent is the name of each EventSource event that FabricObserver emits. 

You have to enable ETW for each observer that you want to receive ETW from. You do this in ApplicationManifest.xml: 

```XML
    <!-- ETW - Custom EventSource Tracing -->
    <Parameter Name="AppObserverEnableEtw" DefaultValue="true" />
    <Parameter Name="AzureStorageUploadObserverEnableEtw" DefaultValue="false" />
    <Parameter Name="CertificateObserverEnableEtw" DefaultValue="false" />
    <Parameter Name="ContainerObserverEnableEtw" DefaultValue="false" />
    <Parameter Name="DiskObserverEnableEtw" DefaultValue="false" />
    <Parameter Name="FabricSystemObserverEnableEtw" DefaultValue="false" />
    <Parameter Name="NetworkObserverEnableEtw" DefaultValue="false" />
    <Parameter Name="NodeObserverEnableEtw" DefaultValue="false" />
    <Parameter Name="OSObserverEnableEtw" DefaultValue="false" />
    <Parameter Name="SFConfigurationObserverEnableEtw" DefaultValue="false" />
```

By default, ObserverManager's EnableETWProvider setting (also located in ApplicationManifest.xml) is enabled. If you disable this, then no ETW will be generated regardless of the Observer-specific settings you provide. Note that AppObserver is enabled to emit ETW events by default for historical reasons. As mentioned above, unless this feature is useful to you, disable it.

Let's take a look at an example of an event that is ingested into the FabricObserverDataEvent Kusto table.

``` JSON
"Message": data="{"ApplicationName":"fabric:/SomeApplication","ApplicationType":"ResourceCentralType","Code":null,"ContainerId":null,"ClusterId":"undefined","Description":null,"EntityType":2,"HealthState":0,"Metric":"Active Ephemeral Ports","NodeName":"MW2PPF7D8279821","NodeType":"AZSM","ObserverName":"AppObserver","OS":"Windows","PartitionId":"a56a62d7-69fd-4f5f-a5fb-caf8b84b537f","ProcessId":24564,"ProcessName":"SomeService","Property":null,"ProcessStartTime":"2022-08-18T15:45:27.2901800Z","ReplicaId":133053111176036935,"ReplicaRole":1,"ServiceKind":1,"ServiceName":"fabric:/SomeApplication/SomeService","ServicePackageActivationMode":0,"Source":"AppObserver","Value":133.0}"
``` 
As you can see, this is not correctly formatted Json. Note the data="" value. 

data is a serialized instance of TelemetryData type in this case, which holds the information that AppObserver (in this case) detected for a service named fabric:/SomeApplication/SomeService for the resource metric Active Ephemeral Ports. Included in the data is everything you need to know about the service like ReplicaId, PartitionId, NodeName, Metric, Value, ProcessName, ProcessId, ProcessStartTime, etc..

In order to parse out the Json-serialized instance of some supported FO data type from the Payload (Message, in the above example), you need to reformat the Message string into well-structured Json:

```KQL
// TelemetryData type. Json is a single object representation.
// data is the payload name. You must remove it.
FabricObserverDataEvent
| where PreciseTimeStamp >= ago(1h) and Tenant == "uswest2-test-42"
// Look for payload that is a Json object.
| where Message startswith "data=\"{"
// remove opening payload name and opening quote
| extend reData = replace_string(Message, "data=\"", "")
// remove closing quote
| extend reData = replace_string(reData, "}\"", "}")
// Now, we have Json..
| extend data = parse_json(reData)
| extend AppName = data.ApplicationName, ServiceName = data.ServiceName, Metric = data.Metric, Result = data.Value, ReplicaId = data.ReplicaId, PartitionId = data.PartitionId,
ProcessId = data.ProcessId, ProcessName = data.ProcessName, ProcessStartTime = data.ProcessStartTime, ServicePackageActivationMode = data.ServicePackageActivationMode, ReplicaRole = data.ReplicaRole,
ServiceKind = data.ServiceKind, Observer = data.ObserverName, NodeName = data.NodeName, NodeType = data.NodeType, Property = data.Property
//| where Observer == "NodeObserver"
//| where Metric == "CPU Time (Percent)" and Result > 0
| project PreciseTimeStamp, ServiceName, NodeName, NodeType, Metric, Result, ReplicaId, PartitionId, ProcessId, ProcessName, ProcessStartTime, ReplicaRole, Observer, Property
| sort by PreciseTimeStamp desc;

// ChildProcessTelemetryData/ChildProcessInfo are Json arrays.
FabricObserverDataEvent
| where PreciseTimeStamp >= ago(1h) and Tenant == "uswest2-test-42"
// Look for payload that is a Json array.
| where Message startswith "data=\"["
| extend reData = replace_string(Message, "data=\"", "")
| extend reData = replace_string(reData, "]\"", "]")
| extend data = parse_json(reData)
// data is a collection (array). Expand into rows.
| mv-expand data
| extend ServiceName = data.ServiceName
| extend Metric = data.Metric
// Parent + child processes culmulative usage value for some metric
| extend CulmulativeValue = data.Value
| extend ChildProcInfo = data.ChildProcessInfo
| project PreciseTimeStamp, ServiceName, Metric, CulmulativeValue, ChildProcInfo
| sort by PreciseTimeStamp desc
```
For information events like above (raw metrics), HealthState is always 0 (Invalid). When some metric crosses the line for a threshold you supplied, HealthState will be 2 (Warning) or 3 (Error), depending upon your related threshold configuration settings.
FO emits more than Json-serialized TelemetryData ETW events. It also emits Json-serialized ChildProcessTelemetryData events (see above), MachineTelemetryData events (OSObserver emits these), and anonymously typed events (Json-serialized anonymous data type which is typically something like an informational or warning event from some observer or ObserverManager that is not a custom FO data type (class) related resource usage monitoring). 

**NOTE** (for internal customers): You should configure MA to format the FabricObserverETWProvider event data as Json. This will result in a Kusto column named data that holds an actual Json string.
So, you would only need to run parse_json(data) in your KQL query versus employing the extra string manipulation steps. This will increase query efficiency
when querying over very large amounts of data from FabricObserver.

### API

For [observer plugin](Plugins.md) authors, you can use your own event name and generate your own ETW using ```LogEtw<T>(string eventName, T data)``` which is a member of ObserverBase.ObserverLogger: 

```C# 
ObserverLogger.LogEtw("MyEventName", myObj);
```
