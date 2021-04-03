// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.IO;
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
        public string DataLogFolder 
        { 
            get; set; 
        }

        private static ILogger dataLogger
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

            if (dataLogger == null)
            {
                dataLogger = LogManager.GetLogger("FabricObserver.Utilities.DataTableFileLogger");
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            var dataLog = (FileTarget)LogManager.Configuration.FindTargetByName("AvgTargetDataStore");
            dataLog.FileName = csvPath;
            dataLog.AutoFlush = true;
            dataLog.ConcurrentWrites = true;
            dataLog.ArchiveEvery = FileArchivePeriod.Day;
            dataLog.ArchiveNumbering = ArchiveNumberingMode.DateAndSequence;
            dataLog.AutoFlush = true;
            dataLog.CreateDirs = true;

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
    }
}
