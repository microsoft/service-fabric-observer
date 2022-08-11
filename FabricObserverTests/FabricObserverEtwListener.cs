// -----------------------------------------------------------------------
// <copyright file="FabricObserverEtwListener.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace FabricObserver.Observers
{
    using FabricObserver.Observers.Utilities;
    using FabricObserver.Observers.Utilities.Telemetry;
    using System.Diagnostics.Tracing;

    /// <summary>
    /// Class to read the ETW events for fabric observer.
    /// The setting 'EnableETWProvider' must be enabled in the settings.xml.
    /// </summary>
    public class FabricObserverEtwListener : EventListener
    {
        private readonly object lockObj = new object();
        private readonly FabricObserverMetrics fabricObserverMetrics;

        /// <summary>
        /// Gets or sets set up a local logger.
        /// </summary>
        internal Logger Logger
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricObserverEtwListener"/> class.
        /// </summary>
        /// <param name="observerLogger"> Logger for the observer. </param>
        public FabricObserverEtwListener(Logger observerLogger)
        {
            Logger = observerLogger;
            fabricObserverMetrics = new FabricObserverMetrics(Logger);
            StartFoEventSourceListener();
            Logger.LogInfo($"FabricObserverEtwListenerInfo: Started FabricObserverEtwListener.");
        }

        /// <summary>
        /// Starts event listening on FO's ServiceEventSource object. 
        /// </summary>
        /// <param name="eventSource">The event source</param>
        protected void StartFoEventSourceListener()
        {
            ServiceEventSource.Current ??= new ServiceEventSource();
            EnableEvents(ServiceEventSource.Current, EventLevel.Informational | EventLevel.Warning | EventLevel.Error);
            Logger.LogInfo($"FabricObserverEtwListenerInfo: Enabled Events.");
        }

        /// <summary>
        /// Called whenever an event has been written by an event source for which the event listener has enabled events.
        /// </summary>
        /// <param name="eventData">Instance of information associated with the event dispatched for fabric observer event listener.</param>
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            lock (lockObj)
            {
                Logger.LogInfo($"FabricObserverEtwListenerInfo: Using event data to parse to telemetry.");
                
                // Parse the event data as TelemetryData and publish as Azure metrics.
                fabricObserverMetrics.EventDataToTelemetryData(eventData);
            }
        }

        /// <summary>
        /// Dispose event source object.
        /// </summary>
        public override void Dispose()
        {
            DisableEvents(ServiceEventSource.Current);
            base.Dispose();
        }
    }
}