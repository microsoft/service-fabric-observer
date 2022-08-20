// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;
using System.Fabric;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    public sealed class ServiceEventSource : EventSource
    {
        private static ServiceEventSource _current = null;
        private static readonly object _lock = new object();

        public static ServiceEventSource Current
        {
            get
            {
                if (_current == null)
                {
                    lock (_lock)
                    {
                        if (_current == null)
                        {
                            _current = new ServiceEventSource();
                        }
                    }
                }
                return _current;
            }
            set // This is used by unit tests.
            {
                lock (_lock)
                {
                    _current = value;
                }
            }
        }

        private static string GetProviderName()
        {
            try
            {
                var config = FabricRuntime.GetActivationContext().GetConfigurationPackageObject("Config");
                string providerName =
                    config?.Settings?.Sections[ObserverConstants.ObserverManagerConfigurationSectionName]?.Parameters[ObserverConstants.ETWProviderName]?.Value;

                return !string.IsNullOrWhiteSpace(providerName) ? providerName : ObserverConstants.DefaultEventSourceProviderName;
            }
            catch (Exception e) when (e is FabricException || e is InvalidOperationException || e is TimeoutException)
            {
                // The handled exception types are expected and benign.
                // InvalidOperationException will always happen when this code is run from a unit test, for example,
                // as there is no FabricRuntime static available.
            }

            return ObserverConstants.DefaultEventSourceProviderName;
        }

        public ServiceEventSource() : base(GetProviderName())
        {
            
        }

        // Define an instance method for each event you want to record and apply an [Event] attribute to it.
        // The method name is the name of the event.
        // Pass any parameters you want to record with the event (only primitive integer types, DateTime, Guid & string are allowed).
        // Each event method implementation should check whether the event source is enabled, and if it is, call WriteEvent() method to raise the event.
        // The number and types of arguments passed to every event method must exactly match what is passed to WriteEvent().
        // Put [NonEvent] attribute on all methods that do not define an event.
        // For more information see https://msdn.microsoft.com/en-us/library/system.diagnostics.tracing.eventsource.aspx
        [NonEvent]
        public void Message(string message, params object[] args)
        {
            if (!IsEnabled())
            {
                return;
            }

            string finalMessage = string.Format(message, args);
            Message(finalMessage);
        }

        [NonEvent]
        internal void Write<T>(T data, string eventName, EventKeywords keywords)
        {
            if (!IsEnabled())
            {
                return;
            }

            var options = new EventSourceOptions
            {
                ActivityOptions = EventActivityOptions.None,
                Keywords = keywords,
                Opcode = EventOpcode.Info,
                Level = EventLevel.Verbose
            };

            Write(eventName, options, data);
        }

        private const int MessageEventId = 1;

        [Event(MessageEventId, Level = EventLevel.Informational, Message = "{0}")]
        public void Message(string message)
        {
            if (IsEnabled())
            {
                WriteEvent(MessageEventId, message);
            }
        }

        [NonEvent]
        public void ServiceMessage(StatelessServiceContext serviceContext, string message, params object[] args)
        {
            if (!IsEnabled())
            {
                return;
            }

            string finalMessage = string.Format(message, args);
            ServiceMessage(
                serviceContext.ServiceName.ToString(),
                serviceContext.ServiceTypeName,
                serviceContext.InstanceId,
                serviceContext.PartitionId,
                serviceContext.CodePackageActivationContext.ApplicationName,
                serviceContext.CodePackageActivationContext.ApplicationTypeName,
                serviceContext.NodeContext.NodeName,
                finalMessage);
        }

        [NonEvent]
        public void VerboseMessage(string message, params object[] args)
        {
            string finalMessage = string.Format(message, args);
            VerboseMessage(finalMessage);
        }

        // For very high-frequency events it might be advantageous to raise events using WriteEventCore API.
        // This results in more efficient parameter handling, but requires explicit allocation of EventData structure and unsafe code.
        // To enable this code path, define UNSAFE conditional compilation symbol and turn on unsafe code support in project properties.
        private const int ServiceMessageEventId = 2;

        [Event(ServiceMessageEventId, Level = EventLevel.Informational, Message = "{7}")]
        private
#if UNSAFE
        unsafe
#endif
        void ServiceMessage(
            string serviceName,
            string serviceTypeName,
            long replicaOrInstanceId,
            Guid partitionId,
            string applicationName,
            string applicationTypeName,
            string nodeName,
            string message)
        {
#if !UNSAFE
            WriteEvent(ServiceMessageEventId, serviceName, serviceTypeName, replicaOrInstanceId, partitionId, applicationName, applicationTypeName, nodeName, message);
#else
            const int numArgs = 8;
            fixed (char* pServiceName = serviceName, pServiceTypeName = serviceTypeName, pApplicationName = applicationName, pApplicationTypeName = applicationTypeName, pNodeName = nodeName, pMessage = message)
            {
                EventData* eventData = stackalloc EventData[numArgs];
                eventData[0] = new EventData { DataPointer = (IntPtr) pServiceName, Size = SizeInBytes(serviceName) };
                eventData[1] = new EventData { DataPointer = (IntPtr) pServiceTypeName, Size = SizeInBytes(serviceTypeName) };
                eventData[2] = new EventData { DataPointer = (IntPtr) (&replicaOrInstanceId), Size = sizeof(long) };
                eventData[3] = new EventData { DataPointer = (IntPtr) (&partitionId), Size = sizeof(Guid) };
                eventData[4] = new EventData { DataPointer = (IntPtr) pApplicationName, Size = SizeInBytes(applicationName) };
                eventData[5] = new EventData { DataPointer = (IntPtr) pApplicationTypeName, Size = SizeInBytes(applicationTypeName) };
                eventData[6] = new EventData { DataPointer = (IntPtr) pNodeName, Size = SizeInBytes(nodeName) };
                eventData[7] = new EventData { DataPointer = (IntPtr) pMessage, Size = SizeInBytes(message) };

                WriteEventCore(ServiceMessageEventId, numArgs, eventData);
            }
#endif
        }

        private const int ServiceTypeRegisteredEventId = 3;

        [Event(ServiceTypeRegisteredEventId, Level = EventLevel.Informational, Message = "Service host process {0} registered service type {1}", Keywords = Keywords.ServiceInitialization)]
        public void ServiceTypeRegistered(int hostProcessId, string serviceType)
        {
            WriteEvent(ServiceTypeRegisteredEventId, hostProcessId, serviceType);
        }

        private const int ServiceHostInitializationFailedEventId = 4;

        [Event(ServiceHostInitializationFailedEventId, Level = EventLevel.Error, Message = "Service host initialization failed", Keywords = Keywords.ServiceInitialization)]
        public void ServiceHostInitializationFailed(string exception)
        {
            WriteEvent(ServiceHostInitializationFailedEventId, exception);
        }

        // A pair of events sharing the same name prefix with a "Start"/"Stop" suffix implicitly marks boundaries of an event tracing activity.
        // These activities can be automatically picked up by debugging and profiling tools, which can compute their execution time, child activities,
        // and other statistics.
        private const int ServiceRequestStartEventId = 5;

        [Event(ServiceRequestStartEventId, Level = EventLevel.Informational, Message = "Service request '{0}' started", Keywords = Keywords.Requests)]
        public void ServiceRequestStart(string requestTypeName)
        {
            WriteEvent(ServiceRequestStartEventId, requestTypeName);
        }

        private const int ServiceRequestStopEventId = 6;

        [Event(ServiceRequestStopEventId, Level = EventLevel.Informational, Message = "Service request '{0}' finished", Keywords = Keywords.Requests)]
        public void ServiceRequestStop(string requestTypeName, string exception = "")
        {
            WriteEvent(ServiceRequestStopEventId, requestTypeName, exception);
        }

        private const int VerboseMessageEventId = 7;

        [Event(VerboseMessageEventId, Level = EventLevel.Verbose, Message = "{0}")]
        public void VerboseMessage<T>(T message)
        {
            if (IsEnabled())
            {
                WriteEvent(VerboseMessageEventId, message);
            }
        }

#if UNSAFE
        private int SizeInBytes(string s)
        {
            if (s == null)
            {
                return 0;
            }
            else
            {
                return (s.Length + 1) * sizeof(char);
            }
        }
#endif

        // Event keywords can be used to categorize events.
        // Each keyword is a bit flag. A single event can be associated with multiple keywords (via EventAttribute.Keywords property).
        // Keywords must be defined as a public class named 'Keywords' inside EventSource that uses them.
        public static class Keywords
        {
            public const EventKeywords Requests = (EventKeywords)0x1L;
            public const EventKeywords ServiceInitialization = (EventKeywords)0x2L;
            public const EventKeywords ResourceUsage = (EventKeywords)0x4L;
            public const EventKeywords ErrorOrWarning = (EventKeywords)0x8L;
            public const EventKeywords InternalData = (EventKeywords)0x10L;
        }
    }
}