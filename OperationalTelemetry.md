FabricObserver operational data is transmitted to Microsoft and contains only basic information about FabricObserver (enabled observers, built-in (non-plugin) observer warnings or errors detected), the type of cluster (SFRP, for example), the type of OS (Windows or Linux). We are only interested in the following information:

- Is FO is working and healthy? - If FO crashes with an unhandled exception that can be caught, related error information will sent to us (this will include the exception stack).
- What is the number of enabled observers?
- Are there any FO plugins running?
- Is FO is finding issues (generating health events)? This data is represented in the total number of Warnings/Errors an observer finds in an 8 hour window.
- This telemetry is sent every 8 hours and internal error/warning counters are reset after each telemetry transmission.

This agnostic information is **very** helpful to your friends on the Service Fabric team working on FO. 

If you do not want to share this information, simply disable it in ApplicationManifest.xml by setting ObserverManagerEnableOperationalTelemetry to false, e.g.,: 

```XML
    <Parameter Name="ObserverManagerEnableOperationalTelemetry" DefaultValue="false" />
```

Here is a full example of exactly what is sent in one of these telemetry events, in this case, from an SFRP cluster:

```JSON
{
    "EventName": "OperationalEvent",
    "TaskName": "FabricObserver",
    "EventRunInterval": "08:00:00",
    "ClusterId": "50bf5602-1611-459c-aed2-45b960e9eb16",
    "ClusterType": "SFRP",
    "NodeNameHash": "1672329571",
    "FOVersion": "3.1.17",
    "HasPlugins": "False",
    "UpTime": "00:00:27.2535830",
    "Timestamp": "2021-08-26T20:51:42.8588118Z",
    "OS": "Windows",
    "EnabledObserverCount": 5,
    "AppObserverTotalMonitoredApps": 4,
    "AppObserverTotalMonitoredServiceProcesses": 6,
    "AppObserverErrorDetections": 0,
    "AppObserverWarningDetections": 0,
    "CertificateObserverErrorDetections": 0,
    "CertificateObserverWarningDetections": 0,
    "DiskObserverErrorDetections": 0,
    "DiskObserverWarningDetections": 0,
    "NodeObserverErrorDetections": 0,
    "NodeObserverWarningDetections": 0,
    "OSObserverErrorDetections": 0,
    "OSObserverWarningDetections": 0
  }
```

This information helps us understand which observers matter in the real world, what type of environment they run in and how many services are being monitored. This information will help us make sure we invest time in the right places. This data does not contain PII or any information about the services running in your cluster. We only care about what FO is doing. We'd really appreciate it if you would share this information with us, but fully understand if you just don't want to. As always, you configure FO to suit your own needs, not ours. 

Let's take a look at the data and why we think it is useful to share with us. We'll go through each object property in the JSON above.

- EventName - this is the name of the telemetry event.
- TaskName - this specifies that the event is from FabricObserver.
- EventRunInterval - this is how often this telemetry is sent from a node in a cluster.
- ClusterId - this is used to both uniquely identify a telemetry event and to correlate data that comes from a cluster.
- ClusterType - this is the type of cluster: Standalone or SFRP.
- NodeNameHash - this is hashed expression of the name of the Fabric node from where the data originates.
- FOVersion - this is the internal version of FO (so, if you have your own version naming, we will know what the FO code version is (not your specific version name)).
- HasPlugins - this inform us about whether or not FO plugins are being used (we would love to know if folks are using the plugin model).
- UpTime - this is the amount of time FO has been running since it last started.
- Timestamp - this is the time, in UTC, when FO sent the telemetry.
- OS - this is the operating system FO is running on (Windows or Linux).
- AppObserverTotalMonitoredApps - this is the total number of deployed applications AppObserver is monitoring.
- AppObserverTotalMonitoredServiceProcesses - this is the total number of processes AppObserver is monitoring.
- AppObserverErrorDetections - this is how many Error level health events AppObserver generated in an 8 hour window.
- AppObserverWarningDetections - this is how many Warning level health events AppObserver generated in an 8 hour window.
- [Some]ObserverErrorDetections - this is how many Error level health events [Some]Observer generated in an 8 hour window.
- [Some]ObserverWarningDetections - this is how many Error level health events [Some]Observer generated in an 8 hour window.

Note that plugin data, besides whether or not plugins are in use, is not captured. If the ClusterType is not SFRP then a TenantId is sent for use in the same way we use ClusterId.

This information will really help us understand how FO is doing out there and we would appreciate the information! 

Observe away!