
**FabricObserver** is a lightweight *Stateless Service Fabric service* which is designed to be highly decoupled from the underlying Fabric system services it observes. We do not rely on interaction with internal-only Service Fabric subsystems beyond what we can access through a public API, like all Service Fabric Service implementations in the public domain\... This is a design decision to limit runtime dependencies and enable agile delivery of bug fixes and new features for Observers (and in time, Healers or Mitigators). We do not need to align with the delivery plans for the underlying system. There is no need for FabricObserver to maintain replicated internal state, so we don't need to implement Fabric Observer as a Stateful Service Fabric service today. 

FO is implemented using Service Fabric’s public API surface only. It does not ship with SF. It is independent of the SF runtime engineering schedule.

FabricObserver does not need nor use much CPU, Working Set or Disk space (depending on configuration - CSV files can add up if you choose to store long running data locally). FabricObserver does not listen on any ports.

***FabricObserver Components***  

**ObserverManager** serves as the entry point for creating and managing all Observers.
Its RunObservers method calls ObserveAsync on all the Observer instances in a
sequential loop with a configurable sleep between cycles.

The iteration interval defaults to 0 seconds (no sleep). The sleep setting is
user-configurable in Settings.xml. ObserverManager manages the lifetime of
observers and will dispose of them in addition to stopping their
execution when shutdown or task cancellation is requested. You can stop an observer
by calling ObserverManager's StopObservers() function.


**ObserverBase**  

This is the abstract base class for all Observers. It provides several concrete method and property implementations 
that are widely used by deriving types, not the least of which is ProcessResourceDataReportHealth(), which is called from all
built-in observers that generate numeric data as part of their resource usage observations. Any public member of ObserverBase is available
to any plugin implementation. Please see [Plugins readme](/Documentation/Plugins.md) for detailed information about building FabricObserver plugins,
which are implemented as .NET 6 libraries and consume FabricObserver's API surface (just as built-in observers do), which is housed in a NET 6 library, FabricObserver.Extensibility.dll.

***Design*** 

An ObserverBase-derived type must implement:

-   Task ObserveAsync(CancellationToken token) method
-   Task ReportAsync(CancellationToken token) method 


***Observer***

An **Observer** is a C\# object that is instantiated inside a Stateless
Service Fabric Service process. An Observer is designed to monitor
specific machine level resource conditions, SF App properties, SF service properties, SF config and runtime properties.
For resource usage, the metrics are stored in in-memory data structures for use in reporting.

An Observer must support the following properties and behaviors:

-   Easily extensible monitoring "framework": developers should be able
    to readily add new types of data collectors to an observer

-   Ability to observe Machine, SF System Services, and user service health

-   Observers must be low cost and highly effective: Process of
    observation **does not add significant resource pressure** to nodes
    (VMs).

-   Health data collected by Observers must be easily queried with
    results that are immediately useful to users such that they can make
    informed mitigation decisions, should they choose to do so.


Each Observer can create a Warning if some metric exceeds a supplied
threshold. **Note: Fabric Health Errors will not be generated by default as this will block
runtime upgrades, etc -- and further, this breaks the guarantee that FabricObserver will have no side effects on Service Fabric's systemic
behavior. Generating Health Errors is your decision to make and it's fine to do it as long as you really understand what it means**. 
A Health report lives for a calculated duration. This ensures that the Fabric Health report remains
active until the next time the related observer runs, which will either
clear the warning by sending an OK health report to Fabric or sending another
warning/error report to Fabric that lives for the calculated TTL. Each observer can call ObserverBase's SetTimeToLiveWarning function,
optionally providing an integer value that represents some timeout or computed run time, in seconds, the observer self-manages.


Let's look at the simple design of an Observer:
``` C#

using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers
{
    public class SomeObserver : ObserverBase
    {
        public SomeObserver(FabricClient fabricClient, StatelessServiceContext context)
          : base(fabricClient, context)
        {
            //... Your impl.
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            //... Your impl.
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            //... Your impl.
        }
    }
 }
``` 


**Data Design**  

Each observer is tasked with recording one or more metrics that are relevant to local Machine or application service health. Each metric will be stored in an
in-memory data structure on the observer type, specified as a private instance field. These values will be used to decide actionable (useful for mitigation)
alerting strategy (time based, standard deviation, min and max thresholds).

**Basic Output**

Local log files containing time-based INFO, DEBUG, ERROR, and WARNING
information. INFO and DEBUG are written only in DEBUG builds for
development purposes. Only ERROR and WARNING states are signaled to
Fabric and logged locally. Some observers can also output CSV files
containing related resource usage data across all iterations for use in
analysis, a long-running view of their behavior as it pertains to
resource consumption across CPU Time, Workingset, DiskIO. Peak and
Average usage is computed and stored. Each CSV is archived after 24
hours to limit file sizes.

FabricObserver implements ApplicationInsights and LogAnalytics telemetry providers.
It also implements EventSource tracing. You can extend this behavior by implementing 
ITelemetryProvider for your target diagnostics service. 

FabricObserver emits raw usage data and Service Fabric Health Reports for surfacing WARNING and ERROR states. FO can be configured to send this information to an implemented and configured telemetry service. 
These health states are preserved in memory across observation iterations by each emitting observer instance, and if the state has settled back to Ok (normal), we will
fire off a health report with a *HealthState.Ok* to clear SFX Error/Warning assuming expiration time has not already been reached. Note that FabricObserver will generate health reports with 
infinite TTLs to protect against overusing SF's HealthManager system. To protect again orphaned health reports, FO cleans up all existing health reports that it has created when
it gracefully closes (as part of an upgrade or some other event where SF takes down the service gracefully) and when it starts up. In the start up case, this is to ensure that any reports that we not cleared at shutdown will
be cleaned up. 
