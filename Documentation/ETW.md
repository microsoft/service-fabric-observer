## FabricObserver ETW Support

FabricObserver employs EventSource events for ETW. There are two key pieces to this support. 

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

By default, ObserverManager's EnableETWProvider setting (also located in ApplicationManifest.xml) is enabled. If you disable this, then no ETW will be generated regardless of the Observer-specific settings you provide. Note that AppObserver is enabled to emit ETW events by default.

Let's take a look at the construction of one of these events.

``` JSON
{
  "Timestamp": "2022-08-12T14:01:15.4246616-07:00",
  "ProviderName": "FabricObserverETWProvider",
  "Id": 65279,
  "Message": null,
  "ProcessId": 36388,
  "Level": "Verbose",
  "Keywords": "0x0000000000000004",
  "EventName": "FabricObserverDataEvent",
  "ActivityID": "00000072-0001-0000-248e-0000ffdcd7b5",
  "RelatedActivityID": null,
  "Payload": {
    "telemetryData": "{\"ApplicationName\":\"fabric:/Voting\",\"ApplicationType\":\"VotingType\",\"Code\":null,\"ContainerId\":null,\"ClusterId\":\"undefined\",\"Description\":null,\"EntityType\":2,\"HealthState\":0,\"Metric\":\"Thread Count\",\"NodeName\":\"_Node_0\",\"NodeType\":\"NodeType0\",\"ObserverName\":\"AppObserver\",\"OS\":\"Windows\",\"PartitionId\":\"9607e129-beba-4969-93e0-7d96765fa4d0\",\"ProcessId\":23068,\"ProcessName\":\"VotingData\",\"Property\":null,\"ProcessStartTime\":\"2022-08-12T17:16:28.5535412Z\",\"ReplicaId\":133029754548379257,\"ReplicaRole\":2,\"ServiceKind\":2,\"ServiceName\":\"fabric:/Voting/VotingData\",\"ServicePackageActivationMode\":0,\"Source\":\"AppObserver\",\"Value\":77.0}"
  }
}
``` 

Note the Payload property. That is a serialized instance of TelemetryData type, which holds the information (in this case) that AppObserver detected for an service named fabric:/Voting/VotingData for the resource metric Thread Count. Included in the data is everything you need to know about the service like ReplicaId, Partition, NodeName, Metric, Value, ProcessName, ProcessId, ProcessStartTime.

If you have the VM/Machine configured to transmit ETW events to some diagnostics service endpoint, you can readily query the data using KQL. 

For example, using the above event and payload, you could construct a ```KQL``` query like this:

```SQL
// If you want play around with an event in Kusto Data Explorer to experiment with how extract json correctly (efficiently) using parse_json._
//let FabricObserverDataEvent = datatable (Message: string)
//["{\"telemetryData\":{\"ApplicationName\":\"fabric:/Voting\",\"ApplicationType\":\"VotingType\",\"Code\":null,\"ContainerId\":null,\"ClusterId\":\"undefined\",\"Description\":null,\"EntityType\":2,\"HealthState\":0,\"Metric\":\"Thread Count\",\"NodeName\":\"_Node_0\",\"NodeType\":\"NodeType0\",\"ObserverName\":\"AppObserver\",\"OS\":\"Windows\",\"PartitionId\":\"9607e129-beba-4969-93e0-7d96765fa4d0\",\"ProcessId\":23068,\"ProcessName\":\"VotingData\",\"Property\":null,\"ProcessStartTime\":\"2022-08-12T17:16:28.5535412Z\",\"ReplicaId\":133029754548379257,\"ReplicaRole\":2,\"ServiceKind\":2,\"ServiceName\":\"fabric:/Voting/VotingData\",\"ServicePackageActivationMode\":0,\"Source\":\"AppObserver\",\"Value\":71.0}}"];

FabricObserverDataEvent
| extend foData = parse_json(Message)
| extend Metric = foData.telemetryData.Metric, ApplicationName = foData.telemetryData.ApplicationName, ApplicationType = foData.telemetryData.ApplicationType, 
ServiceName = foData.telemetryData.ServiceName, PartitionId = foData.telemetryData.PartitionId, NodeName = foData.telemetryData.NodeName,
ReplicaId = foData.telemetryData.ReplicaId, ProcessId = foData.telemetryData.ProcessId, ProcessName = foData.telemetryData.ProcessName,
Value = foData.telemetryData.Value, ProcessStartTime = foData.telemetryData.ProcessStartTime
// Do meaningful stuff with properties... or just Project to test that the Json is parsable, which it will be, of course.
| project Metric, ApplicationName, ApplicationType, NodeName, ServiceName, PartitionId, ReplicaId, ProcessId, ProcessName, ProcessStartTime, Value

```

FO emits more than Json-serialized TelemetryData events. It also emits Json-serialized ChildProcessTelemetryData events, MachineTelemetryData events (OSObserver emits these), and aData events (Json-serialized anonymous data type which is typically something like an informational or warning event from some observer that is unrelated to some Metric (and therefore a threshold you provided for it)).