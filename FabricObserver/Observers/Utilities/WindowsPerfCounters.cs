// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;

namespace FabricObserver.Utilities
{
    public class WindowsPerfCounters : IDisposable
    {
        private PerformanceCounter diskReadsPerfCounter;
        private PerformanceCounter diskWritesPerfCounter;
        private PerformanceCounter diskAverageQueueLengthCounter;
        private PerformanceCounter cpuTimePerfCounter;
        private PerformanceCounter memCommittedBytesPerfCounter;
        private PerformanceCounter memProcessPrivateWorkingSetCounter;

        private bool disposedValue = false;

        private Logger Logger { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="WindowsPerfCounters"/> class.
        /// </summary>
        public WindowsPerfCounters()
        {
            this.InitializePerfCounters();
            this.Logger = new Logger("Utilities");
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
                this.Logger.LogWarning(e.ToString());

                throw;
            }

            return true;
        }

        internal float PerfCounterGetIOReadInfo(
            string instance,
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
                    this.Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());

                    // Don't throw...
                    return 0F;
                }
                else
                {
                    this.Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());
                    throw;
                }
            }
        }

        internal float PerfCounterGetIOWriteInfo(
            string instance,
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
                    this.Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());

                    // Don't throw...
                    return 0F;
                }
                else
                {
                    this.Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());

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
                    this.Logger.LogError($"{cat} {counter} PerfCounter handled exception: " + e.ToString());

                    // Don't throw...
                    return 0F;
                }
                else
                {
                    this.Logger.LogError($"{cat} {counter} PerfCounter unhandled exception: " + e.ToString());

                    throw;
                }
            }
        }

        internal float PerfCounterGetProcessorInfo(
            string countername = null,
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
                    this.Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());

                    // Don't throw...
                    return 0F;
                }
                else
                {
                    this.Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());

                    throw;
                }
            }
        }

        // Committed bytes...
        internal float PerfCounterGetMemoryInfoMB(
            string category = null,
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
                    this.Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());

                    // Don't throw...
                    return 0F;
                }
                else
                {
                    this.Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());
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
                    this.Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e.ToString());

                    // Don't throw...
                    return 0F;
                }
                else
                {
                    this.Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e.ToString());

                    throw;
                }
            }
        }

        protected virtual void Dispose(bool disposing)
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

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Dispose(true);
        }
    }
}
