### ClusterObserver

ClusterObserver (CO) is a stateless singleton Service Fabric service that runs on one node in a cluster. CO observes cluster health (aggregated) 
and sends telemetry when a cluster is in Error or Warning. CO shares a very small subset of FabricObserver's (FO) code. It is designed to be completely independent from FO sources, 
but lives in this repo (and SLN) because it is very useful to have both services deployed, especially for those who want cluster-level health observation and reporting in addition to 
the node-level user-defined resource monitoring, health event creation, and health reporting done by FO. FabricObserver is designed to generate Service Fabric health events based on user-defined resource usage Warning and Error thresholds which ClusterObserver sends to your log analytics and alerting service.

By design, CO will send an Ok health state report when a cluster goes from Warning or Error state to Ok.

CO only sends telemetry when something is wrong or when something that was previously wrong recovers. This limits the amount of data sent to your log analytics service. Like FabricObserver, you can implement whatever analytics backend 
you want by implementing the IObserverTelemetryProvider interface. As stated, this is already implemented for both Azure ApplicationInsights and Azure LogAnalytics. 

The core idea is that you use the aggregated cluster error/warning/Ok health state information from ClusterObserver to fire alerts and/or trigger some other action that gets your attention and/or some SF on-call's enagement via auto-creating a support incident (and an Ok signal would mean auto-mitigate the related incident/ticket).  

You can change ClusterObserver configuration parameters by doing a versionless Application Parameter Upgrade. This means you can change settings for CO without having to redeploy the application or any packages.  

Application Parameter Upgrade Example: 

* Open an Admin Powershell console.

* Connect to your Service Fabric cluster using Connect-ServiceFabricCluster command. 

* Create a variable that contains all the settings you want update:

```Powershell
$appParams = @{ "RunInterval" = "00:10:00"; "MaxTimeNodeStatusNotOk" = "04:00:00"; }
```

Then execute the application upgrade with

```Powershell
Start-ServiceFabricApplicationUpgrade -ApplicationName fabric:/ClusterObserver -ApplicationTypeVersion 2.1.14 -ApplicationParameter $appParams -Monitored -FailureAction rollback
```

Example Configuration:  

```XML
<?xml version="1.0" encoding="utf-8" ?>
<Settings xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.microsoft.com/2011/01/fabric">
  <Section Name="ObserverManagerConfiguration">
    <!-- Required: Amount of time, in seconds, to sleep before the next iteration of clusterobserver run loop. Internally, the run loop will sleep for 15 seconds if this
         setting is not greater than 0. -->
    <Parameter Name="ObserverLoopSleepTimeSeconds" Value="30" />
    <!-- Required: Amount of time, in seconds, ClusterObserver is allowed to complete a run. If this time is exceeded, 
         then the offending observer will be marked as broken and will not run again. 
         Below setting represents 60 minutes. -->
    <Parameter Name="ObserverExecutionTimeout" Value="3600" />
    <!-- Optional: This observer makes async SF Api calls that are cluster-wide operations and can take time in large clusters. -->
    <Parameter Name="AsyncOperationTimeoutSeconds" Value="120" />
    <!-- Required: Location on disk to store observer data, including ObserverManager. 
         ClusterObserver will write to its own directory on this path.
         **NOTE: For Linux runtime target, just supply the name of the directory (not a path with drive letter like you for Windows).** -->
    <Parameter Name="ObserverLogPath" Value="cluster_observer_logs" />
    <!-- Required: Enabling this will generate noisy logs. Disabling it means only Warning and Error information 
         will be locally logged. This is the recommended setting. Note that file logging is generally
         only useful for FabricObserverWebApi, which is an optional log reader service that ships in this repo. -->
    <Parameter Name="EnableVerboseLogging" Value="false" />
    <Parameter Name="EnableETWProvider" Value="true" />
    <!-- Required: Whether the Observer should send all of its monitoring data and Warnings/Errors to configured Telemetry service. This can be overriden by the setting 
         in the ClusterObserverConfiguration section. The idea there is that you can do an application parameter update and turn this feature on and off. -->
    <Parameter Name="EnableTelemetry" Value="true" />
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
    <Parameter Name="ObserverShutdownGracePeriodInSeconds" Value="1" />
  </Section>
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
    <!-- Report on currently executing Repair Jobs in the cluster. -->
    <Parameter Name="MonitorRepairJobs" Value="" MustOverride="true" />
    <!-- CO diagnostic telemetry. -->
    <Parameter Name="EnableOperationalTelemetry" Value="" MustOverride="true" />
  </Section>
</Settings>
``` 

Example LogAnalytics Query  

![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/COQueryFileHandles.png "Example LogAnalytics query") 

You should configure FabricObserver to monitor ClusterObserver, of course. :)

## Operational Telemetry

ClusterObserver operational data is transmitted to Microsoft and contains information about ClusterObserver. 

**This information is only used by the Service Fabric team and will be retained for no more than 90 days.** 

Disabling / Enabling transmission of Operational Data: 

Transmission of operational data is controlled by a setting and can be easily turned off. ```EnableOperationalTelemetry``` setting in ```ApplicationManifest.xml``` controls transmission of Operational data. 

Setting the value to false as below will immediately stop the transmission of operational data: 

**\<Parameter Name="EnableOperationalTelemetry" DefaultValue="false" />** 

#### Questions we want to answer from data: 

- CO started successfully.
-	Health of CO 
       -   If CO crashes with an unhandled exception that can be caught, related error information will be sent to us (this will include the offending FO stack). This will help us improve quality. 
-	This telemetry is sent only once, after the deployed CO instance starts monitoring.

#### Operational data details: 

Here is a full example of exactly what is sent in one of these telemetry events, in this case, from an SFRP cluster: 

```JSON
{
    "EventName": "OperationalEvent",
    "TaskName": "ClusterObserver",
    "ClusterId": "00000000-1111-1111-0000-00f00d000d",
    "ClusterType": "SFRP",
    "COVersion": "2.1.14",
    "Timestamp": "2021-11-22T19:02:04.4287671Z",
    "OS": "Windows"
}
```

Let's take a look at the data and why we think it is useful to share with us. We'll go through each object property in the JSON above.
-	**EventName** - this is the name of the telemetry event.
-	**TaskName** - this specifies that the event is from ClusterObserver.
-	**ClusterId** - this is used to both uniquely identify a telemetry event and to correlate data that comes from a cluster.
-	**ClusterType** - this is the type of cluster: Standalone, SFRP or undefined.
-	**COVersion** - this is the internal version of CO (if you have your own version naming, we will only know what the CO code version is (not your specific CO app version name)).
-	**Timestamp** - this is the time, in UTC, when CO sent the telemetry.
-	**OS** - this is the operating system CO is running on (Windows or Linux).

If the ClusterType is not SFRP then a TenantId (Guid) is sent for use in the same way we use ClusterId. 

We would greatly appreciate you sharing this information with us!



