// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.IO;
using NLog;
using NLog.Targets;
using NLog.Time;

namespace FabricObserver.Observers.Utilities
{
    // CSV file logger for long-running monitoring data (memory/cpu/disk/network usage data).
    public class DataTableFileLogger
    {
        private static NLog.Logger DataLogger
        {
            get; set;
        }

        public string DataLogFolder 
        { 
            get; set; 
        }

        public string BaseDataLogFolderPath 
        { 
            get; set; 
        }

        public CsvFileWriteFormat FileWriteFormat
        {
            get; set;
        }

        /// <summary>
        /// The maximum number of days that archive files will be stored.
        /// 0 means there is no limit set. 
        /// </summary>
        public int MaxArchiveCsvFileLifetimeDays
        {
            get; set;
        } = 0;

        public void ConfigureLogger(string filename)
        {
            // default log directory.
            string logBasePath;

            // log directory supplied in config. Set in ObserverManager.
            if (!string.IsNullOrWhiteSpace(BaseDataLogFolderPath))
            {
                logBasePath = BaseDataLogFolderPath;

                if (OperatingSystem.IsWindows())
                {
                    // Add current drive letter if not supplied for Windows path target.
                    if (!BaseDataLogFolderPath[..3].Contains(":\\"))
                    {
                        string windrive = Environment.SystemDirectory[..3];
                        logBasePath = windrive + BaseDataLogFolderPath;
                    }
                    else
                    {
                        logBasePath = BaseDataLogFolderPath;
                    }
                }
                else
                {
                    // Remove supplied drive letter if Linux is the runtime target.
                    if (BaseDataLogFolderPath[..3].Contains(":\\"))
                    {
                        logBasePath = BaseDataLogFolderPath.Remove(0, 3).Replace("\\", "/");
                    }
                }
            }
            else 
            {
                if (OperatingSystem.IsWindows())
                {
                    string windrive = Environment.SystemDirectory[..3];
                    logBasePath = windrive + "\\fabric_observer_csvdata";
                }
                else
                {
                    logBasePath = "/tmp/fabric_observer_csvdata";
                }
            }
            
            string logFullPath = logBasePath;

            // This means a log path was supplied by an observer. See AppObserver for an example.
            if (!string.IsNullOrWhiteSpace(DataLogFolder))
            {
                logFullPath = Path.Combine(logBasePath, DataLogFolder);
            }

            var csvPath = Path.Combine(logFullPath, filename + ".csv");

            // Clean out old files.
            if (MaxArchiveCsvFileLifetimeDays > 0)
            {
                TryCleanLogFolder(logFullPath, TimeSpan.FromDays(MaxArchiveCsvFileLifetimeDays));
            }

            DataLogger ??= LogManager.GetLogger("FabricObserver.Utilities.DataTableFileLogger");

            TimeSource.Current = new AccurateUtcTimeSource();
            FileTarget dataLog = (FileTarget)LogManager.Configuration.FindTargetByName("AvgTargetDataStore");
                       dataLog.FileName = csvPath;
                       dataLog.AutoFlush = true;
                       dataLog.ConcurrentWrites = true;
                       dataLog.EnableFileDelete = true;
                       dataLog.AutoFlush = true;
                       dataLog.CreateDirs = true;

            if (FileWriteFormat == CsvFileWriteFormat.SingleFileWithArchives)
            {
                dataLog.MaxArchiveDays = MaxArchiveCsvFileLifetimeDays;
                dataLog.ArchiveEvery = FileArchivePeriod.Day;
                dataLog.ArchiveNumbering = ArchiveNumberingMode.DateAndSequence;
            }

            LogManager.ReconfigExistingLoggers();
        }

        /* NLog: For writing out to local CSV file.
           Format:
            <column name="date" layout="${longdate}" />
            <column name="target" layout="${event-properties:target}" />
            <column name="metric" layout="${event-properties:metric}" />
            <column name="stat" layout="${event-properties:stat}" />
            <column name="value" layout="${event-properties:value}" />
        */

        public void LogData(
                    string fileName,
                    string target,
                    string metric,
                    string stat,
                    double value)
        {
            ConfigureLogger(fileName);

            try
            {
                DataLogger.Info(
                    "{target}{metric}{stat}{value}",
                    target,
                    metric,
                    stat,
                    Math.Round(value, 2));
            }
            catch (ArgumentException)
            {

            }
        }

        public static void ShutDown()
        {
            LogManager.Shutdown();
        }

        public static void Flush()
        {
            LogManager.Flush();
        }

        private static void TryCleanLogFolder(string folderPath, TimeSpan maxAge)
        {
            if (!Directory.Exists(folderPath))
            {
                return;
            }

            string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                
            foreach (string file in files)
            {
                try
                {
                    if (DateTime.UtcNow.Subtract(File.GetCreationTime(file)) >= maxAge)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
                {

                }
            }
        }
    }
}
