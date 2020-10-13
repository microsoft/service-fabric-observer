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
        public bool EnableCsvLogging 
        { 
            get; set; 
        } = false;

        public string DataLogFolderPath 
        { 
            get; set; 
        } = null;

        private static ILogger Logger
        {
            get; set;
        }

        public void ConfigureLogger(string filename)
        {
            // default log directory.
            string logPath;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string windrive = Environment.SystemDirectory.Substring(0, 3);
                logPath = windrive + "\\fabric_observer_csvdata";
            }
            else
            {
                logPath = "/tmp/fabric_observer_csvdata";
            }

            // log directory supplied in config. Set in ObserverManager.
            if (!string.IsNullOrEmpty(DataLogFolderPath))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Add current drive letter if not supplied for Windows path target.
                    if (!DataLogFolderPath.Substring(0, 3).Contains(":\\"))
                    {
                        string windrive = Environment.SystemDirectory.Substring(0, 3);
                        logPath = windrive + DataLogFolderPath;
                    }
                }
                else
                {
                    // Remove supplied drive letter if Linux is the runtime target.
                    if (DataLogFolderPath.Substring(0, 3).Contains(":\\"))
                    {
                        DataLogFolderPath = DataLogFolderPath.Remove(0, 3);
                    }

                    logPath = DataLogFolderPath;
                }
            }

            var csvPath = Path.Combine(logPath, filename + ".csv");

            if (Logger == null)
            {
                Logger = LogManager.GetLogger("FabricObserver.Utilities.DataTableFileLogger");
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
            // If you provided an IObserverTelemetry impl, then this will, for example,
            // send traces up to App Insights (Azure). See the App.config for settings example.
            if (!EnableCsvLogging)
            {
                if (!ObserverManager.TelemetryEnabled)
                {
                    return;
                }

                if (Logger == null)
                {
                    Logger = LogManager.GetCurrentClassLogger();
                }

                Logger.Info($"{target}/{metric}/{stat}: {value}");

                return;
            }

            // Else, reconfigure logger to write to file on disk.
            ConfigureLogger(fileName);

            Logger.Info(
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
