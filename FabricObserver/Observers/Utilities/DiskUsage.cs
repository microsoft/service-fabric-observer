// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FabricObserver.Observers.Utilities
{
    internal static class DiskUsage
    {
        private static WindowsPerfCounters winPerfCounters;
        private static object winPerfCountersLock = new object();

        internal static bool ShouldCheckDrive(DriveInfo driveInfo)
        {
            if (!driveInfo.IsReady)
            {
                return false;
            }

            // Skip not interesting Linux mount points.
            if (driveInfo.TotalSize == 0 ||
                string.Equals(driveInfo.DriveFormat, "squashfs", StringComparison.Ordinal) ||
                string.Equals(driveInfo.DriveFormat, "tmpfs", StringComparison.Ordinal) ||
                string.Equals(driveInfo.RootDirectory.FullName, "/boot/efi", StringComparison.Ordinal))
            {
                return false;
            }

            // CDRom and Network drives do not have Avg queue length perf counter
            if (driveInfo.DriveType == DriveType.CDRom || driveInfo.DriveType == DriveType.Network)
            {
                return false;
            }

            return true;
        }

        internal static double GetTotalDiskSpace(string driveName, SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            return GetTotalDiskSpace(new DriveInfo(driveName), sizeUnit);
        }

        internal static double GetTotalDiskSpace(DriveInfo driveInfo, SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            long total = driveInfo.TotalSize;
            return Math.Round(ConvertToSizeUnits(total, sizeUnit), 2);
        }

        internal static int GetCurrentDiskSpaceUsedPercent(string driveName)
        {
            return GetCurrentDiskSpaceUsedPercent(new DriveInfo(driveName));
        }

        internal static int GetCurrentDiskSpaceUsedPercent(DriveInfo driveInfo)
        {
            long availableMB = driveInfo.AvailableFreeSpace / 1024 / 1024;
            long totalMB = driveInfo.TotalSize / 1024 / 1024;
            double usedPct = ((double)(totalMB - availableMB)) / totalMB;

            return (int)(usedPct * 100);
        }

        internal static List<(string DriveName, double DiskSize, int PercentConsumed)>
            GetCurrentDiskSpaceTotalAndUsedPercentAllDrives(SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            DriveInfo[] allDrives = DriveInfo.GetDrives();

            return (
                from drive in allDrives
                where ShouldCheckDrive(drive)
                let totalSize = GetTotalDiskSpace(drive, sizeUnit)
                let pctUsed = GetCurrentDiskSpaceUsedPercent(drive)
                select (drive.Name, totalSize, pctUsed)).ToList();
        }

        internal static double GetAvailableDiskSpace(string driveName, SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            var driveInfo = new DriveInfo(driveName);
            long available = driveInfo.AvailableFreeSpace;

            return Math.Round(ConvertToSizeUnits(available, sizeUnit), 2);
        }

        internal static double GetUsedDiskSpace(string driveName, SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            var driveInfo = new DriveInfo(driveName);
            long used = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;

            return Math.Round(ConvertToSizeUnits(used, sizeUnit), 2);
        }

        internal static float GetAverageDiskQueueLength(string instance)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (winPerfCounters == null)
                {
                    lock (winPerfCountersLock)
                    {
                        if (winPerfCounters == null)
                        {
                            winPerfCounters = new WindowsPerfCounters();
                        }
                    }
                }

                return winPerfCounters.PerfCounterGetAverageDiskQueueLength(instance);
            }

            // We do not support this on Linux for now
            return 0f;
        }

        private static double ConvertToSizeUnits(double amount, SizeUnit sizeUnit)
        {
            switch (sizeUnit)
            {
                case SizeUnit.Bytes:
                    return amount;

                case SizeUnit.Kilobytes:
                    return amount / 1024;

                case SizeUnit.Megabytes:
                    return amount / 1024 / 1024;

                case SizeUnit.Gigabytes:
                    return amount / 1024 / 1024 / 1024;

                case SizeUnit.Terabytes:
                    return amount / 1024 / 1024 / 1024 / 1024;

                default:
                    return amount;
            }
        }
    }

    public enum SizeUnit
    {
        Bytes,
        Kilobytes,
        Megabytes,
        Gigabytes,
        Terabytes,
    }
}
