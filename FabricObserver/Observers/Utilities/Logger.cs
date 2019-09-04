// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;
using FabricObserver.Interfaces;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Time;

namespace FabricObserver.Utilities
{
    public sealed class Logger : IObserverLogger<ILogger>
    {
        private const int RetriesValue = 5;

        // Text file logger for observers - info/warn/error...
        private ILogger logger { get; set; }

        private string loggerName = null;

        private static int Retries => RetriesValue;

        internal string Foldername { get; }

        internal string Filename { get; }

        /// <inheritdoc/>
        public bool EnableVerboseLogging { get; set; } = false;

        /// <inheritdoc/>
        public string LogFolderBasePath { get; set; } = null;

        public string FilePath { get; set; } = null;

        public static EventSource EtwLogger { get; private set; }

        static Logger()
        {
            // The static type may have been disposed, so recreate it if that's the case...
            if (ObserverManager.EtwEnabled && !string.IsNullOrEmpty(ObserverManager.EtwProviderName) && EtwLogger == null)
            {
                EtwLogger = new EventSource(ObserverManager.EtwProviderName);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Utilities.Logger"/> class.
        /// </summary>
        /// <param name="observerName">Name of observer...</param>
        public Logger(string observerName)
        {
            this.Foldername = observerName;
            this.Filename = observerName + ".log";
            this.loggerName = observerName;

            this.InitializeLoggers();
        }

        internal void InitializeLoggers()
        {
            // default log directory...
            string windrive = Environment.SystemDirectory.Substring(0, 2);
            string logFolderBase = windrive + "\\observer_logs";

            // log directory supplied in config... Set in ObserverManager.
            if (!string.IsNullOrEmpty(this.LogFolderBasePath))
            {
                logFolderBase = this.LogFolderBasePath;
            }

            string file = Path.Combine(logFolderBase, "fabric_observer.log");

            if (!string.IsNullOrEmpty(this.Foldername) && !string.IsNullOrEmpty(this.Filename))
            {
                string folderPath = Path.Combine(logFolderBase, this.Foldername);
                file = Path.Combine(folderPath, this.Filename);
            }

            this.FilePath = file;

            var targetName = this.loggerName + "LogFile";

            if ((FileTarget)LogManager.Configuration.FindTargetByName(targetName) == null)
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
            this.logger = LogManager.GetLogger(this.loggerName);
        }

        /// <inheritdoc/>
        public void LogTrace(string observer, string format, params object[] parameters)
        {
            this.logger.Trace(observer + "|" + format, parameters);
        }

        /// <inheritdoc/>
        public void LogInfo(string format, params object[] parameters)
        {
            if (!this.EnableVerboseLogging)
            {
                return;
            }

            this.logger.Info(format, parameters);
        }

        /// <inheritdoc/>
        public void LogError(string format, params object[] parameters)
        {
            this.logger.Error(format, parameters);
        }

        /// <inheritdoc/>
        public void LogWarning(string format, params object[] parameters)
        {
            this.logger.Warn(format, parameters);
        }

        /// <inheritdoc/>
        public bool TryWriteLogFile(string path, string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            for (var i = 0; i < Retries; i++)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(path, content);
                    return true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                Thread.Sleep(1000);
            }

            return false;
        }

        public bool TryDeleteInstanceLog()
        {
            if (string.IsNullOrEmpty(this.FilePath) || !File.Exists(this.FilePath))
            {
                return false;
            }

            for (var i = 0; i < Retries; i++)
            {
                try
                {
                    File.Delete(this.FilePath);
                    return true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                Thread.Sleep(1000);
            }

            return false;
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