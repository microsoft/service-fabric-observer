// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FabricObserver.Interfaces;
using NLog;
using NLog.Targets;
using NLog.Time;

namespace FabricObserver.Observers.Utilities
{
    // CSV file logger for long-running monitoring data (memory/cpu/disk/network usage data).
    public class DataTableFileLogger : IDataTableFileLogger<ILogger>
    {
        private static ILogger dataLogger
        {
            get; set;
        }

        private readonly Dictionary<string, DateTime> FolderCleanedState;

        public string DataLogFolder 
        { 
            get; set; 
        }

        public string BaseDataLogFolderPath 
        { 
            get; set; 
        }

        public bool EnableCsvLogging
        {
            get; set;
        }

        public CsvFileWriteFormat FileWriteFormat
        {
            get; set;
        }

        /// <summary>
        /// The maximum number of archive files that will be stored.
        /// 0 means there is no limit set. 
        /// </summary>
        public int MaxArchiveCsvFileLifetimeDays
        {
            get; set;
        } = 0;

        public DataTableFileLogger()
        {
            FolderCleanedState = new Dictionary<string, DateTime>();
        }

        public void ConfigureLogger(string filename)
        {
            // default log directory.
            string logBasePath;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string windrive = Environment.SystemDirectory.Substring(0, 3);
                logBasePath = windrive + "\\fabric_observer_csvdata";
            }
            else
            {
                logBasePath = "/tmp/fabric_observer_csvdata";
            }

            // log directory supplied in config. Set in ObserverManager.
            if (!string.IsNullOrWhiteSpace(BaseDataLogFolderPath))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Add current drive letter if not supplied for Windows path target.
                    if (!BaseDataLogFolderPath.Substring(0, 3).Contains(":\\"))
                    {
                        string windrive = Environment.SystemDirectory.Substring(0, 3);
                        logBasePath = windrive + BaseDataLogFolderPath;
                    }
                }
                else
                {
                    // Remove supplied drive letter if Linux is the runtime target.
                    if (BaseDataLogFolderPath.Substring(0, 3).Contains(":\\"))
                    {
                        BaseDataLogFolderPath = BaseDataLogFolderPath.Remove(0, 3);
                    }

                    logBasePath = BaseDataLogFolderPath;
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
            if (MaxArchiveCsvFileLifetimeDays > 0 && FileWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives)
            {
                // Add folder path to state dictionary.
                if (!FolderCleanedState.ContainsKey(logFullPath))
                {
                    FolderCleanedState.Add(logFullPath, DateTime.UtcNow);
                }
                else
                {
                    // Only clean a folder that hasn't been cleaned for MaxArchiveCsvFileLifetimeDays days.
                    if (DateTime.UtcNow.Subtract(FolderCleanedState[logFullPath]) >= TimeSpan.FromDays(MaxArchiveCsvFileLifetimeDays))
                    {
                        CleanLogFolder(logFullPath, TimeSpan.FromDays(MaxArchiveCsvFileLifetimeDays));
                    }
                }
            }

            if (dataLogger == null)
            {
                dataLogger = LogManager.GetLogger("FabricObserver.Utilities.DataTableFileLogger");
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            FileTarget dataLog = (FileTarget)LogManager.Configuration.FindTargetByName("AvgTargetDataStore");
                       dataLog.FileName = csvPath;
                       dataLog.AutoFlush = true;
                       dataLog.ConcurrentWrites = false;
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
            // If you use the AppInsights IObserverTelemetry impl, then this will, for example,
            // send traces up to AppInsights instead of writing locally. See NLog.config for settings.
            // **NOTE**: It is preferred that if you want data for every reading that an observer conducts then you 
            // just enable Telemetry on the observers you want the data from. This will be high volume telemetry... 
            // You don't need to use this logger for that. This really should only be used to create csv files on disk,
            // which will take place as one file (datestamped) per configured metric reading per supported observer. Not all 
            // observers currently support csv logging: Only AppObserver, FabricSystemObserver and NodeObserver support csv file logging.
            if (!EnableCsvLogging)
            {
                if (dataLogger == null)
                {
                    dataLogger = LogManager.GetCurrentClassLogger();
                }

                dataLogger.Info($"{target}/{metric}/{stat}: {value}");

                return;
            }

            // Else, reconfigure logger to write to file on disk.
            ConfigureLogger(fileName);

            dataLogger.Info(
                "{target}{metric}{stat}{value}",
                target,
                metric,
                stat,
                value);
        }

        public static void ShutDown()
        {
            LogManager.Shutdown();
        }

        public static void Flush()
        {
            LogManager.Flush();
        }

        private void CleanLogFolder(string folderPath, TimeSpan maxAge)
        {
            int count = 0;

            if (Directory.Exists(folderPath))
            {
                string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                
                foreach (string file in files)
                {
                    try
                    {
                        if (DateTime.UtcNow.Subtract(File.GetCreationTime(file)) >= maxAge)
                        {
                            File.Delete(file);
                            count++;
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException || e is PathTooLongException)
                    {

                    }
                }

                if (count > 0)
                {
                    // The dictionary will always contain the folderPath key. See calling code.
                    FolderCleanedState[folderPath] = DateTime.UtcNow; 
                }
            }
        }
    }
}
