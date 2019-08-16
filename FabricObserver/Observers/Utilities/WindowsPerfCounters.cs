// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;

/*  Disk Counters - Information...
            Performance Counter         Information Provided	                                            Expected Value
        
            % Disk Read Time            Amount of time your disks are being read	                        Less than 15-20%
            % Disk Time                 Amount of time your disks are in use	                            Less than 15-20%
            % Disk Write Time	        Amount of time your disks are being written to	                    Less than 15-20%
            % Idle Time                 Amount of time your disks are idle or not performing any action	    Over 85%
            Current Disk Queue Length	Number of requests outstanding on the disk	                        Less than 1**
            Disk Reads/sec	            Overall rate of read operations on the disk 
                                        (Can be used to determine IOP’s to evaluate hardware 
                                        needs and as a benchmark for hardware upgrades.)	                Less than 70-80%*
            Disk Writes/sec	            Overall rate of write operations on the disk 
                                        (Can be used to determine IOP’s to evaluate hardware 
                                        needs and as a benchmark for hardware upgrades.)	                Less than 70-80%*
            Split IO/sec	            Overall rate at which the operating system divides I/O requests 
                                        to the disk into multiple requests.	
                                                                                                            On single disk volumes, a high Split IO/sec 
                                                                                                            value may indicate a badly fragmented drive. 
                                                                                                        
            \LogicalDisk(*)\% Free Space
            \LogicalDisk(*)\Free Megabytes
            \LogicalDisk(*)\Current Disk Queue Length
            \LogicalDisk(*)\% Disk Time
            \LogicalDisk(*)\Avg. Disk Queue Length
            \LogicalDisk(*)\% Disk Read Time
            \LogicalDisk(*)\Avg. Disk Read Queue Length
            \LogicalDisk(*)\% Disk Write Time
            \LogicalDisk(*)\Avg. Disk Write Queue Length
            \LogicalDisk(*)\Avg. Disk sec/Transfer
            \LogicalDisk(*)\Avg. Disk sec/Read
            \LogicalDisk(*)\Avg. Disk sec/Write
            \LogicalDisk(*)\Disk Transfers/sec
            \LogicalDisk(*)\Disk Reads/sec
            \LogicalDisk(*)\Disk Writes/sec
            \LogicalDisk(*)\Disk Bytes/sec
            \LogicalDisk(*)\Disk Read Bytes/sec
            \LogicalDisk(*)\Disk Write Bytes/sec
            \LogicalDisk(*)\Avg. Disk Bytes/Transfer
            \LogicalDisk(*)\Avg. Disk Bytes/Read
            \LogicalDisk(*)\Avg. Disk Bytes/Write
            \LogicalDisk(*)\% Idle Time
            \LogicalDisk(*)\Split IO/Sec

   
    Memory

            \Memory\Page Faults/sec
            \Memory\Available Bytes
            \Memory\Committed Bytes
            \Memory\Commit Limit
            \Memory\Write Copies/sec
            \Memory\Transition Faults/sec
            \Memory\Cache Faults/sec
            \Memory\Demand Zero Faults/sec
            \Memory\Pages/sec
            \Memory\Pages Input/sec
            \Memory\Page Reads/sec
            \Memory\Pages Output/sec
            \Memory\Pool Paged Bytes
            \Memory\Pool Nonpaged Bytes
            \Memory\Page Writes/sec
            \Memory\Pool Paged Allocs
            \Memory\Pool Nonpaged Allocs
            \Memory\Free System Page Table Entries
            \Memory\Cache Bytes
            \Memory\Cache Bytes Peak
            \Memory\Pool Paged Resident Bytes
            \Memory\System Code Total Bytes
            \Memory\System Code Resident Bytes
            \Memory\System Driver Total Bytes
            \Memory\System Driver Resident Bytes
            \Memory\System Cache Resident Bytes
            \Memory\% Committed Bytes In Use
            \Memory\Available KBytes
            \Memory\Available MBytes
            \Memory\Transition Pages RePurposed/sec
            \Memory\Free & Zero Page List Bytes
            \Memory\Modified Page List Bytes
            \Memory\Standby Cache Reserve Bytes
            \Memory\Standby Cache Normal Priority Bytes
            \Memory\Standby Cache Core Bytes
            \Memory\Long-Term Average Standby Cache Lifetime (s)

    
    Networking

            Network Adapter, Network Interface 
                \Network Adapter(*)\Bytes Total/sec
                \Network Adapter(*)\Packets/sec
                \Network Adapter(*)\Packets Received/sec
                \Network Adapter(*)\Packets Sent/sec
                \Network Adapter(*)\Current Bandwidth
                \Network Adapter(*)\Bytes Received/sec
                \Network Adapter(*)\Packets Received Unicast/sec
                \Network Adapter(*)\Packets Received Non-Unicast/sec
                \Network Adapter(*)\Packets Received Discarded
                \Network Adapter(*)\Packets Received Errors
                \Network Adapter(*)\Packets Received Unknown
                \Network Adapter(*)\Bytes Sent/sec
                \Network Adapter(*)\Packets Sent Unicast/sec
                \Network Adapter(*)\Packets Sent Non-Unicast/sec
                \Network Adapter(*)\Packets Outbound Discarded
                \Network Adapter(*)\Packets Outbound Errors
                \Network Adapter(*)\Output Queue Length
                \Network Adapter(*)\Offloaded Connections
                \Network Adapter(*)\TCP Active RSC Connections
                \Network Adapter(*)\TCP RSC Coalesced Packets/sec
                \Network Adapter(*)\TCP RSC Exceptions/sec
                \Network Adapter(*)\TCP RSC Average Packet Size
            
            Network QoS Policy 
                \Network QoS Policy(*)\Packets dropped/sec
                \Network QoS Policy(*)\Packets dropped
                \Network QoS Policy(*)\Bytes transmitted/sec
                \Network QoS Policy(*)\Bytes transmitted
                \Network QoS Policy(*)\Packets transmitted/sec
                \Network QoS Policy(*)\Packets transmitted
    
           Network Virtualization     
               \Network Virtualization(*)\Unicast Replicated Packets out
                \Network Virtualization(*)\Inbound Packets dropped
                \Network Virtualization(*)\Outbound Packets dropped
                \Network Virtualization(*)\Provider address duplicate detection failures
                \Network Virtualization(*)\Missing policy icmp errors received
                \Network Virtualization(*)\Missing policy icmp errors sent
                \Network Virtualization(*)\Missing policy notifications indicated
                \Network Virtualization(*)\Missing policy notifications dropped
                \Network Virtualization(*)\Packets looped back
                \Network Virtualization(*)\Packets forwarded
                \Network Virtualization(*)\Packets buffered
                \Network Virtualization(*)\Policy lookup failures
                \Network Virtualization(*)\Policy cache hits
                \Network Virtualization(*)\Policy cache misses
                \Network Virtualization(*)\Broadcast packets received
                \Network Virtualization(*)\Broadcast packets sent
                \Network Virtualization(*)\Multicast packets received
                \Network Virtualization(*)\Multicast packets sent
                \Network Virtualization(*)\Unicast packets received (GRE)
                \Network Virtualization(*)\Unicast packets sent (GRE)  
             

    Processor
    
            \Processor(*)\% Processor Time
            \Processor(*)\% User Time
            \Processor(*)\% Privileged Time
            \Processor(*)\Interrupts/sec
            \Processor(*)\% DPC Time
            \Processor(*)\% Interrupt Time
            \Processor(*)\DPCs Queued/sec
            \Processor(*)\DPC Rate
            \Processor(*)\% Idle Time
            \Processor(*)\% C1 Time
            \Processor(*)\% C2 Time
            \Processor(*)\% C3 Time
            \Processor(*)\C1 Transitions/sec
            \Processor(*)\C2 Transitions/sec
     
*/

namespace FabricObserver.Utilities
{
    class WindowsPerfCounters : IDisposable
    {
        private PerformanceCounter diskReadsPerfCounter;
        private PerformanceCounter diskWritesPerfCounter;
        private PerformanceCounter diskAverageQueueLengthCounter;
        private PerformanceCounter cpuTimePerfCounter;
        private PerformanceCounter memCommittedBytesPerfCounter;
        private PerformanceCounter memProcessPrivateWorkingSetCounter;

        private bool disposedValue = false;
        private Logger Logger { get; }

        public WindowsPerfCounters()
        {
            InitializePerfCounters();
            Logger = new Logger("ObserverManager");
        }

        public bool InitializePerfCounters()
        {
            try
            {
                this.diskReadsPerfCounter = new PerformanceCounter();
                this.diskWritesPerfCounter = new PerformanceCounter();
                this.diskAverageQueueLengthCounter = new PerformanceCounter();
                this.cpuTimePerfCounter = new PerformanceCounter();
                this.memCommittedBytesPerfCounter = new PerformanceCounter();
                this.memProcessPrivateWorkingSetCounter = new PerformanceCounter();
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
            catch (Exception e)
            {
                Logger.LogWarning(e.ToString());

                throw;
            }

            return true;
        }

        internal float PerfCounterGetIOReadInfo(string instance,
                                                string category = null,
                                                string countername = null)
        {
            string cat = "LogicalDisk";
            string counter = "% Disk Read Time";

            try
            {
                if (!string.IsNullOrEmpty(category))
                {
                    cat = category;
                }

                if (!string.IsNullOrEmpty(countername))
                {
                    counter = countername;
                }

                this.diskReadsPerfCounter.CategoryName = cat;
                this.diskReadsPerfCounter.CounterName = counter;
                this.diskReadsPerfCounter.InstanceName = instance;

                return this.diskReadsPerfCounter.NextValue();
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is PlatformNotSupportedException
                    || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());
                    return 0F;
                    // Don't throw...
                }
                else
                {
                    Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());
                    throw;
                }
            }
        }

        internal float PerfCounterGetIOWriteInfo(string instance,
                                                 string category = null,
                                                 string countername = null)
        {
            string cat = "LogicalDisk";
            string counter = "% Disk Write Time";

            try
            {
                if (!string.IsNullOrEmpty(category))
                {
                    cat = category;
                }

                if (!string.IsNullOrEmpty(countername))
                {
                    counter = countername;
                }

                this.diskWritesPerfCounter.CategoryName = cat;
                this.diskWritesPerfCounter.CounterName = counter;
                this.diskWritesPerfCounter.InstanceName = instance;

                return this.diskWritesPerfCounter.NextValue();
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is PlatformNotSupportedException
                    || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());
                    return 0F;
                    // Don't throw...
                }
                else
                {
                    Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());

                    throw;
                }
            }
        }

        internal float PerfCounterGetAverageDiskQueueLength(string instance)
        {
            string cat = "LogicalDisk";
            string counter = "Avg. Disk Queue Length";

            try
            {
                this.diskAverageQueueLengthCounter.CategoryName = cat;
                this.diskAverageQueueLengthCounter.CounterName = counter;
                this.diskAverageQueueLengthCounter.InstanceName = instance;

                return this.diskAverageQueueLengthCounter.NextValue();
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is PlatformNotSupportedException
                   || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogError($"{cat} {counter} PerfCounter handled exception: " + e.ToString());

                    return 0F;
                    // Don't throw...
                }
                else
                {
                    Logger.LogError($"{cat} {counter} PerfCounter unhandled exception: " + e.ToString());

                    throw;
                }
            }
        }

        internal float PerfCounterGetProcessorInfo(string countername = null,
                                                   string category = null,
                                                   string instance = null)
        {
            string cat = "Processor";
            string counter = "% Processor Time";
            string inst = "_Total";

            try
            {
                if (!string.IsNullOrEmpty(category))
                {
                    cat = category;
                }

                if (!string.IsNullOrEmpty(countername))
                {
                    counter = countername;
                }

                if (!string.IsNullOrEmpty(instance))
                {
                    inst = instance;
                }

                this.cpuTimePerfCounter.CategoryName = cat;
                this.cpuTimePerfCounter.CounterName = counter;
                this.cpuTimePerfCounter.InstanceName = inst;

                return this.cpuTimePerfCounter.NextValue();
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is PlatformNotSupportedException
                    || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());

                    return 0F;
                    // Don't throw...
                }
                else
                {
                    Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());

                    throw;
                }
            }
        }

        // Committed bytes...
        internal float PerfCounterGetMemoryInfoMB(string category = null,
                                                  string countername = null)
        {
            string cat = "Memory";
            string counter = "Committed Bytes";

            try
            {
                if (!string.IsNullOrEmpty(category))
                {
                    cat = category;
                }

                if (!string.IsNullOrEmpty(countername))
                {
                    counter = countername;
                }

                this.memCommittedBytesPerfCounter.CategoryName = cat;
                this.memCommittedBytesPerfCounter.CounterName = counter;

                return this.memCommittedBytesPerfCounter.NextValue() / 1024 / 1024;
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is PlatformNotSupportedException
                    || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());
                    return 0F;
                    // Don't throw...
                }
                else
                {
                    Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());
                    throw;
                }
            }
        }

        internal float PerfCounterGetProcessPrivateWorkingSetMB(string procName)
        {
            string cat = "Process";
            string counter = "Working Set - Private";

            try
            {
                this.memProcessPrivateWorkingSetCounter.CategoryName = cat;
                this.memProcessPrivateWorkingSetCounter.CounterName = counter;
                this.memProcessPrivateWorkingSetCounter.InstanceName = procName;

                return this.memProcessPrivateWorkingSetCounter.NextValue() / 1024 / 1024;
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is PlatformNotSupportedException
                    || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());

                    return 0F;
                    // Don't throw...
                }
                else
                {
                    Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());

                    throw;
                }
            }
        }

        #region IDisposable Support

        protected void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    if (this.diskReadsPerfCounter != null)
                    {
                        this.diskReadsPerfCounter.Dispose();
                        this.diskReadsPerfCounter = null;
                    }

                    if (this.diskWritesPerfCounter != null)
                    {
                        this.diskWritesPerfCounter.Dispose();
                        this.diskWritesPerfCounter = null;
                    }

                    if (this.diskAverageQueueLengthCounter != null)
                    {
                        this.diskAverageQueueLengthCounter.Dispose();
                        this.diskAverageQueueLengthCounter = null;
                    }

                    if (this.memCommittedBytesPerfCounter != null)
                    {
                        this.memCommittedBytesPerfCounter.Dispose();
                        this.memCommittedBytesPerfCounter = null;
                    }

                    if (this.cpuTimePerfCounter != null)
                    {
                        this.cpuTimePerfCounter.Dispose();
                        this.cpuTimePerfCounter = null;
                    }

                    if (this.memProcessPrivateWorkingSetCounter != null)
                    {
                        this.memProcessPrivateWorkingSetCounter.Dispose();
                        this.memProcessPrivateWorkingSetCounter = null;
                    }
                }

                this.disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}
