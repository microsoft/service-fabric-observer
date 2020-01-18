ClusterObserver (CO) is a standalone SF singleton stateless service that runs on one node (1) and is 
independent from FabricObserver, which runs on all nodes (-1). CO observes cluster health (aggregated) 
and sends telemetry when cluster is in Error (and optionally in Warning). 
CO shares a very small subset of FabricObserver's (FO) code. It is designed to be completely independent from FO sources, 
but lives in this repo (and SLN) because it is very useful to have both services deployed, 
especially for those who want cluster-level health observation and reporting in addition to 
the node-level user-defined resource monitoring, health event creation, and health reporting done by FO. FabricObserver is designed to generate Service Fabric health events based on user-defined resource usage Warning and Error thresholds which ClusterObserver sends to your log analytics and alerting service.

By design, CO will send an Ok health state report when a cluster goes from Warning or Error state to Ok.

CO only sends telemetry when something is wrong or when something that was previously wrong recovers. This limits 
the amount of data sent to your log analytics service. Like FabricObserver, you can implement whatever analytics backend 
you want by implementing the IObserverTelemetryProvider interface. As stated, this is already implemented for ApplicationInsights. 

The core idea is that you use the aggregated cluster error/warning/Ok health state information from ClusterObserver to fire alerts and/or trigger some other action that gets your attention and/or some SF on-call's enagement via auto-creating a support incident (and an Ok signal would mean auto-mitigate the related incident/ticket).

Example Configuration:  

```XML
  <Section Name="ClusterObserverConfiguration">
    <!-- Required Parameter for all Observers: To enable or not enable, that is the question.-->
    <Parameter Name="Enabled" Value="True" />
    <!-- Optional: Enabling this will generate noisy logs. Disabling it means only Warning and Error information 
         will be locally logged. This is the recommended setting. Note that file logging is generally
         only useful for FabricObserverWebApi, which is an optional log reader service that ships in this repo. -->
    <Parameter Name="EnableVerboseLogging" Value="False" />
    <!-- Optional: This observer makes async SF Api calls that are cluster-wide operations and can take time in large deployments. -->
    <Parameter Name="ClusterOperationTimeoutSeconds" Value="120" />
    <!-- Emit health details for both Warning and Error for aggregated cluster health? Error details will
    always be transmitted.-->
    <Parameter Name="EmitHealthWarningEvaluationDetails" Value="True" />
    <!-- Emit Ok aggregated health state telemetry when cluster health goes from Warning or Error to Ok. -->
    <Parameter Name="EmitOkHealthStateTelemetry" Value="True" />
  </Section>
``` 

You should configure FabricObserver to monitor ClusterObserver, of course. :)
