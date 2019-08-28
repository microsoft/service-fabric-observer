// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Interfaces;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Time;
using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;

namespace FabricObserver.Utilities
{
    public sealed class Logger : IObserverLogger<ILogger>, IDisposable
    {
        // Text file logger for observers - info/warn/error...
        private ILogger logger { get; set; }
        private string loggerName = null;
        private const int retries = 5;
        private static int Retries => retries;

        internal string Foldername { get; }
        internal string Filename { get; }
        
        public bool EnableVerboseLogging { get; set; } = false;
        public string LogFolderBasePath { get; set; } = null;
        public string FilePath { get; set; } = null;
        public static EventSource EtwLogger { get; private set; }
     
        static Logger()
        {
            if (!ObserverManager.EtwEnabled || string.IsNullOrEmpty(ObserverManager.EtwProviderName))
            {
                return;
            }

            if (EtwLogger == null)
            {
                EtwLogger = new EventSource(ObserverManager.EtwProviderName);
            }
        }

        public Logger(string observerName)
        {
            Foldername = observerName;
            Filename = observerName + ".log";
            this.loggerName = observerName;

            // The static type may have been disposed, so recreate it if that's the case...
            if (ObserverManager.EtwEnabled && !string.IsNullOrEmpty(ObserverManager.EtwProviderName) && EtwLogger == null)
            {
                EtwLogger = new EventSource(ObserverManager.EtwProviderName);
            }

            InitializeLoggers();
        }

        internal void InitializeLoggers()
        {
            // default log directory...
            string windrive = Environment.SystemDirectory.Substring(0, 2);
            string logFolderBase = windrive + "\\observer_logs";

            // log directory supplied in config... Set in ObserverManager.
            if (!string.IsNullOrEmpty(LogFolderBasePath))
            {
                logFolderBase = LogFolderBasePath;
            }

            string file = Path.Combine(logFolderBase, "fabric_observer.log");

            if (!string.IsNullOrEmpty(Foldername) && !string.IsNullOrEmpty(Filename))
            {
                string folderPath = Path.Combine(logFolderBase, Foldername);
                file = Path.Combine(folderPath, Filename);
            }

            FilePath = file;

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
                    AutoFlush = true
                };

                LogManager.Configuration.AddTarget(this.loggerName + "LogFile", target);

                var ruleInfo = new LoggingRule(this.loggerName, NLog.LogLevel.Debug, target);

                LogManager.Configuration.LoggingRules.Add(ruleInfo);
                LogManager.ReconfigExistingLoggers();
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            logger = LogManager.GetLogger(this.loggerName);
        }

        public void LogTrace(string Observer, string format, params object[] parameters)
        {
            logger.Trace(Observer + "|" + format, parameters);
        }

        public void LogInfo(string format, params object[] parameters)
        {
            if (!EnableVerboseLogging)
            {
                return;
            }

            logger.Info(format, parameters);
        }

        public void LogError(string format, params object[] parameters)
        {
            logger.Error(format, parameters);
        }

        public void LogWarning(string format, params object[] parameters)
        {
            logger.Warn(format, parameters);
        }

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
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                Thread.Sleep(1000);
            }

            return false;
        }

        public bool TryDeleteInstanceLog()
        {
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                return false;
            }

            for (var i = 0; i < Retries; i++)
            {
                try
                {
                    File.Delete(FilePath);
                    return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

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

        public void Dispose()
        {
            ((IDisposable)EtwLogger)?.Dispose();
            EtwLogger = null;
        }
    }
}