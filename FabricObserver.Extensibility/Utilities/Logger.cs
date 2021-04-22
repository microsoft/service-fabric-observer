// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FabricObserver.Observers.Interfaces;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Time;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Local file logger.
    /// </summary>
    public sealed class Logger : IObserverLogger<ILogger>
    {
        private const int Retries = 5;

        // This needs to be static to prevent internal EventSource instantiation errors.
        private static EventSource etwLogger;

        // Text file logger for observers - info/warn/error.
        private ILogger OLogger
        {
            get; set;
        }

        private readonly string loggerName;

        private EventSource EtwLogger
        {
            get
            {
                if (EnableETWLogging && etwLogger == null)
                {
                    etwLogger = new EventSource(ObserverConstants.EventSourceProviderName);
                }

                return etwLogger;
            }
        }

        public bool EnableETWLogging
        {
            get; set;
        } = false;

        public bool EnableVerboseLogging
        {
            get; set;
        } = false;

        public string LogFolderBasePath
        {
            get; set;
        }

        public string FilePath
        {
            get; set;
        }

        public string FolderName
        {
            get;
        }

        public string Filename
        {
            get;
        }

        /// <summary>
        /// The maximum number of days that archive files will be stored.
        /// 0 means there is no limit set.
        /// </summary>
        public int MaxArchiveFileLifetimeDays
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Utilities.Logger"/> class.
        /// </summary>
        /// <param name="observerName">Name of observer.</param>
        /// <param name="logFolderBasePath">Base folder path.</param>
        /// <param name="maxArchiveFileLifetimeDays">Optional: Maximum number of days to keep archive files on disk.</param>
        public Logger(string observerName, string logFolderBasePath = null, int maxArchiveFileLifetimeDays = 7)
        {
            FolderName = observerName;
            Filename = observerName + ".log";
            loggerName = observerName;
            MaxArchiveFileLifetimeDays = maxArchiveFileLifetimeDays;

            if (!string.IsNullOrWhiteSpace(logFolderBasePath))
            {
                LogFolderBasePath = logFolderBasePath;
            }

            InitializeLoggers();
        }

        public static void ShutDown()
        {
            LogManager.Shutdown();
        }

        public static void Flush()
        {
            LogManager.Flush();
        }

        public void LogTrace(string format, params object[] parameters)
        {
            OLogger.Trace(format, parameters);
        }

        public void LogInfo(string format, params object[] parameters)
        {
            if (!EnableVerboseLogging)
            {
                return;
            }

            OLogger.Info(format, parameters);
        }

        public void LogError(string format, params object[] parameters)
        {
            OLogger.Error(format, parameters);
        }

        public void LogWarning(string format, params object[] parameters)
        {
            OLogger.Warn(format, parameters);
        }

        public void LogEtw<T>(string eventName, T data)
        {
            EtwLogger?.Write(eventName, data);
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
                        if (directory != null)
                        {
                            _ = Directory.CreateDirectory(directory);
                        }
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

        public bool TryDeleteInstanceLogFile()
        {
            if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
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
                catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
                {

                }

                Thread.Sleep(1000);
            }

            return false;
        }

        public void InitializeLoggers()
        {
            // default log directory.
            string logFolderBase;

            // Log directory supplied in Settings.xml.
            if (!string.IsNullOrEmpty(LogFolderBasePath))
            {
                logFolderBase = LogFolderBasePath;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Add current drive letter if not supplied for Windows path target.
                    if (!LogFolderBasePath.Substring(0, 3).Contains(":\\"))
                    {
                        string windrive = Environment.SystemDirectory.Substring(0, 3);
                        logFolderBase = windrive + LogFolderBasePath;
                    }
                }
                else
                {
                    // Remove supplied drive letter if Linux is the runtime target.
                    if (LogFolderBasePath.Substring(0, 3).Contains(":\\"))
                    {
                        logFolderBase = LogFolderBasePath.Remove(0, 3).Replace("\\", "/");
                    }
                }
            }
            else
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string windrive = Environment.SystemDirectory.Substring(0, 3);
                    logFolderBase = windrive + "observer_logs";
                }
                else
                {
                    logFolderBase = "/tmp/observer_logs";
                }
            }

            LogFolderBasePath = logFolderBase;
            string file = Path.Combine(logFolderBase, "fabric_observer.log");

            if (!string.IsNullOrEmpty(FolderName) && !string.IsNullOrEmpty(Filename))
            {
                string folderPath = Path.Combine(logFolderBase, FolderName);
                file = Path.Combine(folderPath, Filename);
            }

            FilePath = file;

            var targetName = loggerName + "LogFile";

            if (LogManager.Configuration == null)
            {
                LogManager.Configuration = new LoggingConfiguration();
            }

            if ((FileTarget)LogManager.Configuration?.FindTargetByName(targetName) == null)
            {
                var target = new FileTarget
                {
                    Name = targetName,
                    OptimizeBufferReuse = true,
                    ConcurrentWrites = false,
                    EnableFileDelete = true,
                    FileName = file,
                    Layout = "${longdate}--${uppercase:${level}}--${message}",
                    OpenFileCacheTimeout = 5,
                    ArchiveEvery = FileArchivePeriod.Day,
                    ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                    MaxArchiveDays = MaxArchiveFileLifetimeDays <= 0 ? 7 : MaxArchiveFileLifetimeDays,
                    AutoFlush = true,
                };

                LogManager.Configuration.AddTarget(loggerName + "LogFile", target);

                var ruleInfo = new LoggingRule(loggerName, NLog.LogLevel.Debug, target);

                LogManager.Configuration.LoggingRules.Add(ruleInfo);
                LogManager.ReconfigExistingLoggers();
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            OLogger = LogManager.GetLogger(loggerName);
        }
    }
}