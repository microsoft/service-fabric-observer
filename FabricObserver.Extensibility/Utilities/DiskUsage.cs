﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    public static class DiskUsage
    {
        public static bool ShouldCheckDrive(DriveInfo driveInfo)
        {
            if (!driveInfo.IsReady)
            {
                return false;
            }

            // Skip not interesting Linux mount points.
            if (driveInfo.TotalSize == 0 ||
                string.Equals(driveInfo.DriveFormat, "squashfs", StringComparison.Ordinal) ||
                string.Equals(driveInfo.DriveFormat, "tmpfs", StringComparison.Ordinal) ||
                string.Equals(driveInfo.DriveFormat, "overlay", StringComparison.Ordinal) ||
                string.Equals(driveInfo.RootDirectory.FullName, "/boot/efi", StringComparison.Ordinal))
            {
                return false;
            }

            // CDRom and Network drives do not have Avg queue length perf counter
            if (driveInfo.DriveType is DriveType.CDRom or DriveType.Network)
            {
                return false;
            }

            return true;
        }

        public static double GetTotalDiskSpace(string driveName, SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            return GetTotalDiskSpace(new DriveInfo(driveName), sizeUnit);
        }

        public static double GetTotalDiskSpace(DriveInfo driveInfo, SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            long total = driveInfo.TotalSize;
            return Math.Round(ConvertToSizeUnits(total, sizeUnit), 2);
        }

        public static double GetCurrentDiskSpaceUsedPercent(string driveName)
        {
            return GetCurrentDiskSpaceUsedPercent(new DriveInfo(driveName));
        }

        public static double GetCurrentDiskSpaceUsedPercent(DriveInfo driveInfo)
        {
            long availableMB = driveInfo.AvailableFreeSpace / 1024 / 1024;
            long totalMB = driveInfo.TotalSize / 1024 / 1024;
            double usedPct = ((double)(totalMB - availableMB)) / totalMB;

            return (double)(usedPct * 100);
        }

        public static List<(string DriveName, double DiskSize, double PercentConsumed)>
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

        public static double GetAvailableDiskSpace(string driveName, SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            var driveInfo = new DriveInfo(driveName);
            long available = driveInfo.AvailableFreeSpace;

            return Math.Round(ConvertToSizeUnits(available, sizeUnit), 2);
        }

        public static double GetUsedDiskSpace(string driveName, SizeUnit sizeUnit = SizeUnit.Bytes)
        {
            var driveInfo = new DriveInfo(driveName);
            long used = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;

            return Math.Round(ConvertToSizeUnits(used, sizeUnit), 2);
        }

        public static float GetAverageDiskQueueLength(string instance)
        {
            return OSInfoProvider.Instance.GetAverageDiskQueueLength(instance);
        }

        private static double ConvertToSizeUnits(double amount, SizeUnit sizeUnit)
        {
            return sizeUnit switch
            {
                SizeUnit.Bytes => amount,
                SizeUnit.Kilobytes => amount / 1024,
                SizeUnit.Megabytes => amount / 1024 / 1024,
                SizeUnit.Gigabytes => amount / 1024 / 1024 / 1024,
                SizeUnit.Terabytes => amount / 1024 / 1024 / 1024 / 1024,
                _ => amount,
            };
        }
    }

    public enum SizeUnit
    {
        Bytes,
        Kilobytes,
        Megabytes,
        Gigabytes,
        Terabytes
    }
}
