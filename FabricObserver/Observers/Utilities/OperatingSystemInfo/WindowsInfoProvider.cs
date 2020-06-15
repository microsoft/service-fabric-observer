using System;
using System.Management;

namespace FabricObserver.Observers.Utilities
{
    internal class WindowsInfoProvider : OperatingSystemInfoProvider
    {
        internal override (long TotalMemory, int PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse()
        {
            ManagementObjectSearcher win32OsInfo = null;
            ManagementObjectCollection results = null;

            try
            {
                win32OsInfo = new ManagementObjectSearcher("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
                results = win32OsInfo.Get();

                foreach (var prop in results)
                {
                    long visibleTotal = -1;
                    long freePhysical = -1;

                    foreach (var p in prop.Properties)
                    {
                        string n = p.Name;
                        object v = p.Value;

                        if (n.Contains("TotalVisible", StringComparison.OrdinalIgnoreCase))
                        {
                            visibleTotal = Convert.ToInt64(v);
                        }

                        if (n.Contains("FreePhysical", StringComparison.OrdinalIgnoreCase))
                        {
                            freePhysical = Convert.ToInt64(v);
                        }
                    }

                    if (visibleTotal <= -1 || freePhysical <= -1)
                    {
                        continue;
                    }

                    double used = ((double)(visibleTotal - freePhysical)) / visibleTotal;
                    int usedPct = (int)(used * 100);

                    return (visibleTotal / 1024 / 1024, usedPct);
                }
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (ManagementException)
            {
            }
            finally
            {
                win32OsInfo?.Dispose();
                results?.Dispose();
            }

            return (-1L, -1);
        }
    }
}
