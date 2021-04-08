// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric.Health;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using System.Linq;
using System.Fabric;

namespace FabricObserver.Observers
{
    /// <summary>
    /// Over all idea of how ASCRPObserver works:
    /// It scans for the service instance  of serviceType "CapsServiceType" under each ASCRP applicationType in the ObserverAsync function
    /// After the scanning is done we use reportAsync() to report to SFX and Generate ETW traces to our logs
    /// </summary>

    public class ASCRPObserver : ObserverBase
    {

        private readonly StringBuilder message;       // The message that will go into our ETW traces
        private readonly IDictionary<string, int> numberOfAscServiceInstancesRecorded = new Dictionary<string, int>();  // This Dict is used to hold the number of services from the previous scan
        private readonly int mintuesToWarn = 10; //Minutes to put the warning on the SFX
        private readonly string appName = "fabric:/Ascrp"; //ApplicationName
        private readonly string appType = "ASCRP"; //ApplicationType
        private readonly string serviceName = "RPP_CapsServiceType_"; //ServiceName
        private readonly string serviceType = "CapsServiceType";//ServiceType
        private static int num_of_Instances_To_Warn; // This is the number of instances variable that will tell us after which number we want to send an alert
        private bool serviceInstancesUpdated; //Flag variable that indicates if the serviceInstances got increased in a scan 
        private bool should_warn = false; //Another flag variable that indicates if the warning has to be pushed        
        private int totalNumberofAscServices = 0; //Temp variable that will hold the value of services in every loop
        private const int warn_Threshold_lessThanOrEqual2 = 75;
        private const int warn_Threshold_greaterThan2 = 30;

        public ASCRPObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
            this.message = new StringBuilder();
            serviceInstancesUpdated = false;
            // Key is our ServiceType  which is "CapsServiceType" and 2 because I think there will be a minimum of two instances under each app on SF
            numberOfAscServiceInstancesRecorded.Add(serviceType, 0);
        }

        /// <summary>
        /// ObserveAsync(): How it works:
        /// Mainly counts the serviceInstances under each ASCRP application in each scan
        /// Proactively checks with the recorded value in our dict, if its increased we trigger the flag variable serviceInstancesUpdated to true  
        /// if that number crosses our alert threshold (num_of_Instances_To_Warn) we switch the should_warn variable to true
        /// All this is reported to the ReportAsync() to take action on the observations.
        /// </summary>
        public override async Task ObserveAsync(CancellationToken token)
        {
            this.message.AppendLine($"ASCRPObserver: Entering observerAsync: updated 1106");
            try
            {
                if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
                {
                    return;
                }
                Stopwatch stopwatch = Stopwatch.StartNew();
                var apps = await FabricClientInstance.QueryManager.GetApplicationListAsync(
                    null,
                    AsyncClusterOperationTimeoutSeconds,
                    token).ConfigureAwait(false);
                // This is where we decide the threshold number for the alerts
                var appsOfType = apps.Where(app => app.ApplicationTypeName == appType).ToList();
                int count_appTypes = appsOfType.Count;
                if (count_appTypes > 0)
                {
                    if (count_appTypes <= 2)
                    {
                        num_of_Instances_To_Warn = warn_Threshold_lessThanOrEqual2;
                    }
                    else if (count_appTypes > 2)
                    {
                        num_of_Instances_To_Warn = warn_Threshold_greaterThan2;
                    }
                }
                this.message.AppendLine($"ASCRPObserver:Slices count: {count_appTypes} ");
                this.message.AppendLine($"ASCRPObserver: Alert therhsold : {num_of_Instances_To_Warn} ");
                this.message.AppendLine($"ASCRPObserver:Going to start scanning for Apps with type : {appType} ");
                //Starting to scan for the services
                foreach (var app in apps)
                {
                    string app_applicationName = app.ApplicationName.OriginalString;
                    bool hasAscrpApp = app_applicationName.Contains(appName);
                    string appTypeName = app.ApplicationTypeName;
                    bool hasAscrpTypeApp = appTypeName.Equals(appTypeName);
                    this.message.AppendLine($"ASCRPObserver: Application type name: {appTypeName}, flag contains value : {hasAscrpTypeApp} ");
                    if (hasAscrpTypeApp)
                    {
                        var services = await FabricClientInstance.QueryManager.GetServiceListAsync(
                        app.ApplicationName,
                        null,
                        AsyncClusterOperationTimeoutSeconds,
                        token).ConfigureAwait(false);
                        foreach (var service in services)
                        {
                            string service_name = service.ServiceName.OriginalString;
                            bool hasAscrpServiceName = service_name.Contains(serviceName);
                            string serviceTypeName = service.ServiceTypeName;
                            bool hasSerivceType = serviceTypeName.Equals(serviceType);
                            this.message.AppendLine($"ASCRPObserver: Service type name: {serviceTypeName}, flag contains value : {hasAscrpTypeApp} ");
                            if (hasSerivceType)
                            {
                                totalNumberofAscServices += 1;
                            }
                            if (numberOfAscServiceInstancesRecorded != null && numberOfAscServiceInstancesRecorded.Count > 0)
                            {
                                for (int i = 0; i < numberOfAscServiceInstancesRecorded.Count; i++)
                                {
                                    KeyValuePair<string, int> ascrpServiceInstances = numberOfAscServiceInstancesRecorded.ElementAt(i);
                                    this.message.AppendLine($"ASCRPObserver: Number of service instances before scan:{ascrpServiceInstances.Value}");
                                    if (ascrpServiceInstances.Key.Contains(serviceType))
                                    {
                                        if (ascrpServiceInstances.Value < totalNumberofAscServices)
                                        {
                                            this.message.AppendLine($"ASCRPObserver: Looks like the service instances have increased since last scan.");
                                            this.message.AppendLine($"ASCRPObserver: Updating the record.");
                                            numberOfAscServiceInstancesRecorded[ascrpServiceInstances.Key] = totalNumberofAscServices;
                                            serviceInstancesUpdated = true;
                                            if (totalNumberofAscServices >= num_of_Instances_To_Warn)
                                            {
                                                this.message.AppendLine($"ASCRPObserver: Number of Services now:{totalNumberofAscServices}, Global threshold: {num_of_Instances_To_Warn}.");
                                                this.message.AppendLine($"ASCRPObserver: Looks like we crossed the global maximum, hence initaiting warnings and hence alerts.");
                                                should_warn = true;
                                            }

                                        }
                                        else
                                        {
                                            this.message.AppendLine($"ASCRPObserver: No change in the no of service instances since our last scan");
                                            serviceInstancesUpdated = false;

                                        }
                                        this.message.AppendLine($"ASCRPObserver: Now starting to report.");
                                        await ReportAsync(token);
                                    }
                                    this.message.AppendLine($"ASCRPObserver: Number of service instances after scan:{ascrpServiceInstances.Value}");
                                }
                            }
                            else
                            {
                                this.message.AppendLine($"ASCRPObserver: Dict is null, can't proceed.");
                            }
                        }
                    }
                    //Clearing the temp variables
                    this.message.AppendLine($"ASCRPObserver: Clearing the temp variables totalNumberofAscServices");
                    totalNumberofAscServices = 0;
                    serviceInstancesUpdated = false;
                    should_warn = false;
                }
                stopwatch.Stop();
                RunDuration = stopwatch.Elapsed;
                this.message.AppendLine($"ASCRPObserver: After loop report: total number of ASC Services: {totalNumberofAscServices}");
                this.message.AppendLine($"ASCRPObserver: Time it took to run {ObserverName}.ObserveAsync: {RunDuration}");
                await ReportAsync(token);
                LastRunDateTime = DateTime.Now;
            }
            catch (Exception e) when
             (e is FabricException ||
              e is OperationCanceledException ||
              e is TaskCanceledException ||
              e is TimeoutException)
            {
                // These can happen, transiently. Ignore them.              
            }
            catch (Exception exception)
            {
                string msg = $"ASCRPObserver : Report Async : Exception occured :{Environment.NewLine}{exception}";
                ObserverLogger.LogEtw(
                       ObserverConstants.FabricObserverETWEventName,
                       new
                       {
                           Code = AscFOErrorAndWarningCodes.AscrpObserverError, //This error code shows that ASCRPObserver just crashed due to some unexecpted error
                           Level = "Error",
                           Description = msg,
                           Source = ObserverName,
                           Node = NodeName,
                       });
                throw;

            }

        }

        /// <summary>
        /// ReportAsync() how it works
        /// After the scanning is done we land here: based on the two state variables should_warn and serviceInstancesUpdated, we decide what has to be done next.
        /// if  serviceInstancesUpdated is switched on, we add a trace with warning code "FOASC002".
        /// In addition to serviceInstancesUpdated, if  should_warn is switched on,  we put the SFX into warning mode for "mintuesToWarn" mintues 
        /// This also adds the trace with warning code "FOASC003" that will eventually trigger the monitor to the send the alert (IcM).
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public override Task ReportAsync(CancellationToken token)
        {

            // Local log. This should only be used while debugging. In general, don't enable verbose logging for observer.
#if DEBUG
            ObserverLogger.LogInfo(message.ToString());
#endif
            try
            {
                /* Report to Fabric */
                var healthReporter = new ObserverHealthReporter(ObserverLogger, FabricClientInstance);
                Utilities.HealthReport healthReport = null;
                if (totalNumberofAscServices >= 0)
                {
                    if (serviceInstancesUpdated)
                    {
                        if (should_warn) // Create the Warning.
                        {
                            this.message.AppendLine("The TTL for the warning is:" + mintuesToWarn.ToString());

                            // Provide a SourceId and Property for use in the health event clearing.
                            // This means the related health event will go from Warning -> Ok, with the Warning state removed from the entity
                            // (the fabric Node will be green in SFX, in this case since the ReportType is Node)
                            healthReport = new Utilities.HealthReport
                            {
                                Code = AscFOErrorAndWarningCodes.AscServiceInstanceSendAlert,
                                HealthMessage = this.message.ToString(),
                                NodeName = NodeName,
                                Observer = ObserverName,
                                Property = $"{NodeName}_ServiceInstancesUpdated",
                                ReportType = HealthReportType.Node,
                                State = HealthState.Warning,
                                HealthReportTimeToLive = TimeSpan.FromMinutes(mintuesToWarn),
                                SourceId = $"{ObserverName}({AscFOErrorAndWarningCodes.AscServiceInstanceSendAlert})",
                            };

                            // This property is used to keep track of this observer's latest known warning state.
                            // If it was in warning from the last time it ran and then it is no longer detects the issue the next time it runs, 
                            // then this property would be used to clear the existing warning, which requires:
                            // 1. Same SourceID for each event (Warning and Ok)
                            // 2. Same Property for each event (Warning and Ok)
                            HasActiveFabricErrorOrWarning = true;
                        }
                        else // Create a new Information Event (HealthState = Ok).
                        {
                            healthReport = new Utilities.HealthReport
                            {
                                Code = AscFOErrorAndWarningCodes.AscServiceInstanceWarningInstanceIncreased,
                                HealthMessage = this.message.ToString(),
                                NodeName = NodeName,
                                Observer = ObserverName,
                                Property = $"{NodeName}_ServiceInstancesUpdated",
                                ReportType = HealthReportType.Node,
                                State = HealthState.Ok,
                                SourceId = $"{ObserverName}({AscFOErrorAndWarningCodes.AscServiceInstanceWarningInstanceIncreased})",
                            };
                            HasActiveFabricErrorOrWarning = false;
                        }
                    }
                    else // Clear the Warning with an Ok clear.
                    {
                        if (HasActiveFabricErrorOrWarning)
                        {
                            healthReport = new Utilities.HealthReport
                            {
                                Code = AscFOErrorAndWarningCodes.AscServiceInstanceOK,
                                HealthMessage = this.message.ToString(),
                                NodeName = NodeName,
                                Observer = ObserverName,
                                Property = $"{NodeName}_ServiceInstancesUpdated",
                                ReportType = HealthReportType.Node,
                                State = HealthState.Ok,
                                SourceId = $"{ObserverName}({AscFOErrorAndWarningCodes.AscServiceInstanceSendAlert})",
                            };

                            HasActiveFabricErrorOrWarning = false;
                        }
                    }
                }
                if (healthReport != null)
                {
                    healthReporter.ReportHealthToServiceFabric(healthReport); //Reports the health state on SFX
                    /*Report to logs */
                    string code_toBeAppended;
                    // ETW. EventSource tracing. This is very vital for triggering the FO's backend alert system
                    if (IsEtwEnabled)
                    {
                        if (healthReport.State == HealthState.Ok)
                        {
                            if (HasActiveFabricErrorOrWarning)
                            {
                                ObserverLogger.LogEtw(
                                    ObserverConstants.FabricObserverETWEventName,
                                    new
                                    {
                                        Code = AscFOErrorAndWarningCodes.AscServiceInstanceSendAlert,
                                        HealthEventDescription = this.message.ToString(),
                                        HealthState = "Warning",
                                        NodeName,
                                        ObserverName,
                                        Source = ObserverConstants.FabricObserverName,
                                    });
                            }
                            else
                            {
                                code_toBeAppended = null;
                                if (serviceInstancesUpdated)
                                {
                                    code_toBeAppended = AscFOErrorAndWarningCodes.AscServiceInstanceWarningInstanceIncreased;
                                }
                                else
                                {
                                    code_toBeAppended = AscFOErrorAndWarningCodes.AscServiceInstanceOK;
                                }
                                ObserverLogger.LogEtw(
                                    ObserverConstants.FabricObserverETWEventName,
                                    new
                                    {
                                        Code = code_toBeAppended,
                                        HealthEventDescription = this.message.ToString(),
                                        HealthState = "Ok",
                                        NodeName,
                                        ObserverName,
                                        Source = ObserverConstants.FabricObserverName,
                                    });
                            }
                        }
                        else
                        {
                            ObserverLogger.LogEtw(
                                ObserverConstants.FabricObserverETWEventName,
                                new
                                {
                                    Code = AscFOErrorAndWarningCodes.AscServiceInstanceSendAlert,
                                    HealthEventDescription = this.message.ToString(),
                                    HealthState = "Warning",
                                    NodeName,
                                    ObserverName,
                                    Source = ObserverConstants.FabricObserverName,
                                });
                        }
                    }
                }
                this.message.Clear();
                return Task.CompletedTask;
            }
            catch (Exception e) when
             (e is FabricException ||
              e is OperationCanceledException ||
              e is TaskCanceledException ||
              e is TimeoutException)
            {
                // These can happen, transiently. Ignore them.              
            }
            catch (Exception e)
            {
                // This will take down our observer and FO will not recreate it. We will have to redeploy FO. 
                //This will let us fix bugs that cause unhandled exceptions and then handle them if we can. 
                // Leave this in place when we deploy to test and staging, so that we could fix bugs that could take down ASCRPObserver.
                string msg = $"ASCRPObserver : Report Async : Exception occured :{Environment.NewLine}{e}";
                ObserverLogger.LogEtw(
                       ObserverConstants.FabricObserverETWEventName,
                       new
                       {
                           Code = AscFOErrorAndWarningCodes.AscrpObserverError, //This error code shows that ASCRPObserver just crashed due to some unexecpted error
                           Level = "Error",
                           Description = msg,
                           Source = ObserverName,
                           Node = NodeName,
                       });

                throw;
            }
            return Task.CompletedTask;
        }
    }

    public sealed class AscFOErrorAndWarningCodes
    {
        public const string AscServiceInstanceOK = "FOASC001";
        public const string AscServiceInstanceWarningInstanceIncreased = "FOASC002";
        public const string AscServiceInstanceSendAlert = "FOASC003";
        public const string AscServiceInstanceError = "FOASC005";
        public const string AscrpObserverError = "FOASC006";
    }
}