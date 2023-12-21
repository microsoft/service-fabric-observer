### ClusterObserver 2.2.7
#### This version requires SF Runtime >= 9.0 and targets .NET 6. .NET Core 3.1 is no longer supported.

[ClusterObserver (CO)](https://github.com/microsoft/service-fabric-observer/tree/main/ClusterObserver) is a stateless singleton Service Fabric .NET 6 service that runs on one node in a cluster. CO observes cluster health (aggregated) 
and sends telemetry when a cluster is in Error or Warning. CO shares a very small subset of FabricObserver's (FO) code. It is designed to be completely independent from FO sources, 
but lives in this repo (and SLN) because it is very useful to have both services deployed, especially for those who want cluster-level health observation and reporting in addition to 
the node-level user-defined resource monitoring, health event creation, and health reporting done by FO. FabricObserver is designed to generate Service Fabric health events based on user-defined resource usage Warning and Error thresholds which ClusterObserver sends to your log analytics and alerting service.

By design, CO will send an Ok health state report when a cluster goes from Warning or Error state to Ok.

CO only sends telemetry when something is wrong or when something that was previously wrong recovers. This limits the amount of data sent to your log analytics service. Like FabricObserver, you can implement whatever analytics backend 
you want by implementing the IObserverTelemetryProvider interface. As stated, this is already implemented for both Azure ApplicationInsights and Azure LogAnalytics. 

The core idea is that you use the aggregated cluster error/warning/Ok health state information from ClusterObserver to fire alerts and/or trigger some other action that gets your attention and/or some SF on-call's enagement via auto-creating a support incident (and an Ok signal would mean auto-mitigate the related incident/ticket).  

```As of version 2.2.0.831/960, ClusterObserver supports the FabricObserver extensibility model. This means you can extend the behavior of ClusterObserver by writing your own observer plugins just as you can do with FabricObserver.```

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
Start-ServiceFabricApplicationUpgrade -ApplicationName fabric:/ClusterObserver -ApplicationTypeVersion 2.2.1.960 -ApplicationParameter $appParams -Monitored -FailureAction rollback
```

Example Configuration:  

```XML
<?xml version="1.0" encoding="utf-8" ?>
<Settings xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.microsoft.com/2011/01/fabric">
	<Section Name="ObserverManagerConfiguration">
		<!-- Required: Amount of time, in seconds, to sleep before the next iteration of clusterobserver run loop. Internally, the run loop will sleep for 15 seconds if this
         setting is not greater than 0. -->
		<Parameter Name="ObserverLoopSleepTimeSeconds" Value="" MustOverride="true" />
		<!-- Required: Amount of time, in seconds, ClusterObserver is allowed to complete a run. If this time is exceeded, 
         then the offending observer will be marked as broken and will not run again. 
         Below setting represents 60 minutes. -->
		<Parameter Name="ObserverExecutionTimeout" Value="" MustOverride="true" />
		<!-- Required: Location on disk to store observer data, including ObserverManager. 
         ClusterObserver will write to its own directory on this path.
         **NOTE: For Linux runtime target, just supply the name of the directory (not a path with drive letter like you for Windows).** -->
		<Parameter Name="ObserverLogPath" Value="" MustOverride="true" />
		<!-- Required: Enabling this will generate noisy logs. Disabling it means only Warning and Error information 
         will be locally logged. This is the recommended setting. Note that file logging is generally
         only useful for FabricObserverWebApi, which is an optional log reader service that ships in this repo. -->
		<Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
		<Parameter Name="ObserverFailureHealthStateLevel" Value="" MustOverride="true" />
		<Parameter Name="EnableETWProvider" Value="" MustOverride="true" />
		<Parameter Name="ETWProviderName" Value="" MustOverride="true" />
		<Parameter Name="EnableTelemetryProvider" Value="" MustOverride="true" />
		<Parameter Name="EnableOperationalTelemetry" Value="" MustOverride="true" />

		<!-- Non-overridable. -->

		<Parameter Name="AsyncOperationTimeoutSeconds" Value="120" />
		<!-- Required: Supported Values are AzureApplicationInsights or AzureLogAnalytics as these providers are implemented. -->
		<Parameter Name="TelemetryProvider" Value="AzureLogAnalytics" />
		
		<!-- AzureApplicationInsights -->
		<!-- OBSOLETE: Use AppInsightsConnectionString instead. Setting this has no effect. -->
		<Parameter Name="AppInsightsInstrumentationKey" Value="" />
		<!-- Required-If TelemetryProvider is AzureApplicationInsights. Your Connection String. -->
		<Parameter Name="AppInsightsConnectionString" Value="" />
		
		<!-- AzureLogAnalytics -->
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
		<Parameter Name="EnableEtw" Value="" MustOverride="true"/>
		<Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
		<Parameter Name="EnableTelemetry" Value="" MustOverride="true" />
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
		<!-- Monitor Cluster and Application Upgrades. -->
		<Parameter Name="MonitorUpgrades" Value="" MustOverride="true" />
	</Section>
	<!-- Plugin model sample. Just add the configuration info here that your observer needs.
       **NOTE**: You must name these Sections in the following way: [ObserverName]Configuration.
       Example: SampleNewObserverConfiguration, where SampleNewObserver is the type name of the observer plugin.
       See the SampleObserverPlugin project for a complete example of implementing an observer plugin. 
	   
       If you want to enable versionless parameter-only application upgrades, then add MustOverride to the Parameters you want to be 
       able to change without redeploying CO and add them to ApplicationManifest.xml just like for ClusterObserver.
  <Section Name="MyClusterObserverPluginConfiguration">
    <Parameter Name="Enabled" Value="true" />
    <Parameter Name="ClusterOperationTimeoutSeconds" Value="120" />
    <Parameter Name="EnableEtw" Value="false" />
    <Parameter Name="EnableVerboseLogging" Value="false" />
    <Parameter Name="RunInterval" Value=""  />
  </Section> -->
</Settings>
``` 

Example LogAnalytics Query  

![alt text](https://raw.githubusercontent.com/microsoft/service-fabric-observer/main/Documentation/Images/COQueryFileHandles.png "Example LogAnalytics query") 
