using System;
using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    internal class WindowsProcessInfoProvider : ProcessInfoProvider
    {
        private readonly PerformanceCounter memProcessPrivateWorkingSetCounter = new PerformanceCounter();

        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            const string cat = "Process";
            const string counter = "Working Set - Private";

            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException ex)
            {
                // "Process with an Id of 12314 is not running."
                this.Logger.LogError(ex.Message);
                return 0;
            }

            try
            {
                this.memProcessPrivateWorkingSetCounter.CategoryName = cat;
                this.memProcessPrivateWorkingSetCounter.CounterName = counter;
                this.memProcessPrivateWorkingSetCounter.InstanceName = process.ProcessName;

                return this.memProcessPrivateWorkingSetCounter.NextValue() / (1024 * 1024);
            }
            catch (Exception e)
            {
                if (e is ArgumentNullException || e is PlatformNotSupportedException
                    || e is System.ComponentModel.Win32Exception || e is UnauthorizedAccessException)
                {
                    this.Logger.LogError($"{cat} {counter} PerfCounter handled error: " + e);

                    // Don't throw.
                    return 0F;
                }

                this.Logger.LogError($"{cat} {counter} PerfCounter unhandled error: " + e);

                throw;
            }
        }
    }
}
