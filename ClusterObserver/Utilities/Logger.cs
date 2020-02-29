// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Time;

namespace FabricClusterObserver.Utilities
{
    public sealed class Logger
    {
        // Text file logger for observers - info/warn/error.
        private ILogger OLogger { get; set; }

        private readonly string loggerName;

        internal string FolderName { get; }

        internal string Filename { get; }

        public bool EnableVerboseLogging { get; set; } = false;

        public string LogFolderBasePath { get; set; }

        public string FilePath { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Utilities.Logger"/> class.
        /// </summary>
        /// <param name="observerName">Name of observer.</param>
        /// <param name="logFolderBasePath">Base folder path.</param>
        public Logger(string observerName, string logFolderBasePath = null)
        {
            this.FolderName = observerName;
            this.Filename = observerName + ".log";
            this.loggerName = observerName;

            if (!string.IsNullOrEmpty(logFolderBasePath))
            {
                this.LogFolderBasePath = logFolderBasePath;
            }

            this.InitializeLoggers();
        }

        internal void InitializeLoggers()
        {
            // default log directory.
            string windrive = Environment.SystemDirectory.Substring(0, 2);
            string logFolderBase = windrive + "\\observer_logs";

            // log directory supplied in config. Set in ObserverManager.
            if (!string.IsNullOrEmpty(this.LogFolderBasePath))
            {
                logFolderBase = this.LogFolderBasePath;
            }

            string file = Path.Combine(logFolderBase, "fabric_observer.log");

            if (!string.IsNullOrEmpty(this.FolderName) && !string.IsNullOrEmpty(this.Filename))
            {
                string folderPath = Path.Combine(logFolderBase, this.FolderName);
                file = Path.Combine(folderPath, this.Filename);
            }

            this.FilePath = file;

            var targetName = this.loggerName + "LogFile";

            if (LogManager.Configuration == null)
            {
                LogManager.Configuration = new LoggingConfiguration();
            }

            if ((FileTarget)LogManager.Configuration?.FindTargetByName(targetName) == null)
            {
                var target = new FileTarget
                {
                    Name = targetName,
                    FileName = file,
                    Layout = "${longdate}--${uppercase:${level}}--${message}",
                    OpenFileCacheTimeout = 5,
                    ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                    ArchiveEvery = FileArchivePeriod.Day,
                    AutoFlush = true,
                };

                LogManager.Configuration.AddTarget(this.loggerName + "LogFile", target);

                var ruleInfo = new LoggingRule(this.loggerName, NLog.LogLevel.Debug, target);

                LogManager.Configuration.LoggingRules.Add(ruleInfo);
                LogManager.ReconfigExistingLoggers();
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            this.OLogger = LogManager.GetLogger(this.loggerName);
        }

        public void LogTrace(string observer, string format, params object[] parameters)
        {
            this.OLogger.Trace(observer + "|" + format, parameters);
        }

        public void LogInfo(string format, params object[] parameters)
        {
            if (!this.EnableVerboseLogging)
            {
                return;
            }

            this.OLogger.Info(format, parameters);
        }

        public void LogError(string format, params object[] parameters)
        {
            this.OLogger.Error(format, parameters);
        }

        public void LogWarning(string format, params object[] parameters)
        {
            this.OLogger.Warn(format, parameters);
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