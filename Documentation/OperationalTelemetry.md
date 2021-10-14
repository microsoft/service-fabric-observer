## FabricObserver Operational Telemetry

FabricObserver operational data is transmitted to Microsoft and contains information about FabricObserver.  This information helps us understand which observers matter in the real world, what type of environment they run in, and how many services are being monitored. This information will help us make sure we invest time in the right places. This data does not contain PII or any information about the services running in your cluster or the data handled by the applications. Nor do we capture the configurations set for FO. 

**This information is only used by the Service Fabric team and will be stored (data retention) for no more than 90 days.** 

Disabling / Enabling transmission of Operational Data: 

Transmission of operational data is controlled by a setting and can be easily turned off. ObserverManagerEnableOperationalTelemetry setting in ApplicationManifest.xml controls transmission of Operational data. 

Setting the value to false as below will immediately stop the transmission of operational data: 

**\<Parameter Name="ObserverManagerEnableOperationalTelemetry" DefaultValue="false" />** 

#### Questions we want to answer from data: 

-	Health of FO 
       -	If FO crashes with an unhandled exception that can be caught, related error information will be sent to us (this will include the offending FO stack). This will help us improve quality. 
-	Enabled Observers 
    -	Helps us focus effort on the most useful observers.
-	Are there any FO plugins running?
-	Is FO finding issues (generating health events)? This data is represented in the total number of Warnings/Errors an observer finds in an 8 hour window.
-	This telemetry is sent every 8 hours and internal error/warning counters are reset after each telemetry transmission.

#### Operational data details: 

Here is a full example of exactly what is sent in one of these telemetry events, in this case, from an SFRP cluster: 

```JSON
{
    "EventName": "OperationalEvent",
    "TaskName": "FabricObserver",
    "EventRunInterval": "08:00:00",
    "ClusterId": "50bf5602-1611-459c-aed2-45b960e9eb16",
    "ClusterType": "SFRP",
    "NodeNameHash": "1672329571",
    "FOVersion": "3.1.19",
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

Let's take a look at the data and why we think it is useful to share with us. We'll go through each object property in the JSON above.
-	**EventName** - this is the name of the telemetry event.
-	**TaskName** - this specifies that the event is from FabricObserver.
-	**EventRunInterval** - this is how often this telemetry is sent from a node in a cluster.
-	**ClusterId** - this is used to both uniquely identify a telemetry event and to correlate data that comes from a cluster.
-	**ClusterType** - this is the type of cluster: Standalone or SFRP.
-	**NodeNameHash** - this is a hashed expression of the name of the Fabric node from where the data originates. It is used to correlate data from specific nodes in a cluster (the hashed node name will be known to be part of the cluster with a specific cluster id).
-	**FOVersion** - this is the internal version of FO (if you have your own version naming, we will only know what the FO code version is (not your specific FO app version name)).
-	**HasPlugins** - this inform us about whether or not FO plugins are being used (we would love to know if folks are using the plugin model).
-	**UpTime** - this is the amount of time FO has been running since it last started.
-	**Timestamp** - this is the time, in UTC, when FO sent the telemetry.
-	**OS** - this is the operating system FO is running on (Windows or Linux).
-	**AppObserverTotalMonitoredApps** - this is the total number of deployed applications AppObserver is monitoring.
-	**AppObserverTotalMonitoredServiceProcesses** - this is the total number of processes AppObserver is monitoring.
-	**AppObserverErrorDetections** - this is how many Error level health events AppObserver generated in an 8 hour window.
-	**AppObserverWarningDetections** - this is how many Warning level health events AppObserver generated in an 8 hour window.
-	**[Built-in]ObserverErrorDetections** - this is how many Error level health events [Built-in]Observer generated in an 8 hour window.
-	**[Built-in]ObserverWarningDetections** - this is how many Error level health events [Built-in]Observer generated in an 8 hour window. 


Note that specific plugin data, besides whether or not plugins are in use, is not captured. Only agnostic data from built-in (ship with FO) observers is collected. 

If the ClusterType is not SFRP then a TenantId (Guid) is sent for use in the same way we use ClusterId. 

This information will **really** help us understand how FO is doing out there and we would greatly appreciate you sharing it with us!


