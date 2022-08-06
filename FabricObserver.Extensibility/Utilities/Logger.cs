// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities.Telemetry;
using Newtonsoft.Json;
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
        private readonly string loggerName;

        // Text file logger for observers - info/warn/error.
        private ILogger OLogger
        {
            get; set;
        }

        private string FolderName
        {
            get;
        }

        private string Filename
        {
            get;
        }

        public bool EnableETWLogging
        {
            get; set;
        }

        public bool EnableVerboseLogging
        {
            get; set;
        }

        public string LogFolderBasePath
        {
            get; set;
        }

        public string FilePath
        {
            get;
            private set;
        }

        /// <summary>
        /// The maximum number of days that archive files will be stored.
        /// 0 means there is no limit set.
        /// </summary>
        private int MaxArchiveFileLifetimeDays
        {
            get;
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

        /// <summary>
        /// Logs EventSource events and automatically determines Level based on object (T data) content inspection.
        /// </summary>
        /// <typeparam name="T">Anonymous/generic type.</typeparam>
        /// <param name="eventName">Name of the event.</param>
        /// <param name="data">Anonymous object instance.</param>
        public void LogEtw<T>(string eventName, T data)
        {
            if (!EnableETWLogging || data == null)
            {
                return;
            }

            if (!JsonHelper.TrySerializeObject(data, out string telemetryData))
            {
                return;
            }

            var anonType = new
            {
                telemetryData
            };

            // TelemetryData?
            if (data is TelemetryData telemData)
            {
                if (telemData.HealthState == System.Fabric.Health.HealthState.Warning)
                {
                    ServiceEventSource.Current.WriteWarning(eventName, anonType);
                    return;
                }

                if (telemData.HealthState == System.Fabric.Health.HealthState.Error)
                {
                    ServiceEventSource.Current.WriteError(eventName, anonType);
                    return;
                }

                // Info event.
                ServiceEventSource.Current.WriteInfo(eventName, anonType);
                return;
            }

            if (data is ServiceFabricUpgradeEventData upgradeEventData)
            {
                if (upgradeEventData.FabricUpgradeProgress?.UpgradeState == System.Fabric.FabricUpgradeState.Failed 
                    || upgradeEventData.FabricUpgradeProgress?.UpgradeState == System.Fabric.FabricUpgradeState.RollingBackInProgress)
                {
                    ServiceEventSource.Current.WriteWarning(eventName, anonType);
                    return;
                }

                if (upgradeEventData.ApplicationUpgradeProgress?.UpgradeState == System.Fabric.ApplicationUpgradeState.Failed
                    || upgradeEventData.ApplicationUpgradeProgress?.UpgradeState == System.Fabric.ApplicationUpgradeState.RollingBackInProgress)
                {
                    ServiceEventSource.Current.WriteWarning(eventName, anonType);
                    return;
                }

                // Info event.
                ServiceEventSource.Current.WriteInfo(eventName, anonType);
                return;
            }

            if (data is MachineTelemetryData)
            {
                // Info event.
                ServiceEventSource.Current.WriteInfo(eventName, anonType);
                return;
            }

            // Some FO ETW events are written as anonymous .NET types (anonymous object intances with fields/properties).
            // This means they are JSON-serializable for use in content inspection.

            if (telemetryData.Contains("Warning"))
            {
                ServiceEventSource.Current.WriteWarning(eventName, anonType);
                return;
            }

            if (telemetryData.Contains("Error"))
            {
                ServiceEventSource.Current.WriteError(eventName, anonType);
                return;
            }

            ServiceEventSource.Current.WriteInfo(eventName, anonType);
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
                catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
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

            try
            {
                Retry.Do(() => File.Delete(FilePath), TimeSpan.FromSeconds(1), CancellationToken.None);
                return true;
            }
            catch (AggregateException)
            {

            }
 
            return false;
        }

        private void InitializeLoggers()
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
                    logFolderBase = windrive + "fabric_observer_logs";
                }
                else
                {
                    logFolderBase = "/tmp/fabric_observer_logs";
                }
            }

            LogFolderBasePath = logFolderBase;
            string file = Path.Combine(logFolderBase, "fabric_observer.log");

            if (!string.IsNullOrWhiteSpace(FolderName) && !string.IsNullOrWhiteSpace(Filename))
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
                    ConcurrentWrites = true,
                    EnableFileDelete = true,
                    FileName = file,
                    Layout = "${longdate}--${uppercase:${level}}--${message}",
                    OpenFileCacheTimeout = 5,
                    ArchiveEvery = FileArchivePeriod.Day,
                    ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                    MaxArchiveDays = MaxArchiveFileLifetimeDays <= 0 ? 7 : MaxArchiveFileLifetimeDays,
                    AutoFlush = true
                };

                LogManager.Configuration.AddTarget(loggerName + "LogFile", target);
                var ruleInfo = new LoggingRule(loggerName, NLog.LogLevel.Debug, target);
                LogManager.Configuration.LoggingRules.Add(ruleInfo);
                LogManager.ReconfigExistingLoggers();
            }

            TimeSource.Current = new AccurateUtcTimeSource();
            OLogger = LogManager.GetLogger(loggerName);

            // Clean out old log files. This is to ensure the supplied policy is enforced if FO is restarted before the MaxArchiveFileLifetimeDays has been reached.
            // This is because Logger FileTarget settings are not preserved across FO deployments.
            if (MaxArchiveFileLifetimeDays > 0)
            {
                TryCleanFolder(Path.Combine(logFolderBase, FolderName), "*.log", TimeSpan.FromDays(MaxArchiveFileLifetimeDays));
            }
        }

        public void TryCleanFolder(string folderPath, string searchPattern, TimeSpan maxAge)
        {
            if (!Directory.Exists(folderPath))
            {
                return;
            }

            string[] files = new string[] { };

            try
            {
                files = Directory.GetFiles(folderPath, searchPattern, SearchOption.AllDirectories);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }

            foreach (string file in files)
            {
                try
                {
                    if (DateTime.UtcNow.Subtract(File.GetCreationTime(file)) >= maxAge)
                    {
                        Retry.Do(() => File.Delete(file), TimeSpan.FromSeconds(1), CancellationToken.None);
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is AggregateException)
                {
                    LogWarning($"Unable to delete file {file}:{Environment.NewLine}{e}");
                }
            }
        }
    }
}