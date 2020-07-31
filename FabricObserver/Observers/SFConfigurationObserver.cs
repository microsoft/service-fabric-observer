// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FabricObserver.Observers.Utilities;

namespace FabricObserver.Observers
{
    // This observer doesn't monitor or report health status. It is only useful if you employ the FabricObserverWebApi App.
    // It provides information about the currently installed Service Fabric runtime environment, apps, and services.
    // The output (a local file) is used by the FO API service to render an HTML page (http://localhost:5000/api/ObserverManager).
    public class SFConfigurationObserver : ObserverBase
    {
        // Values.
        private string sfVersion;

        // Values.
        private string sfBinRoot;

        // Values.
        private string sfCodePath;

        // Values.
        private string sfDataRoot;

        // Values.
        private string sfLogRoot;

        // Values.
        private string sfNodeLastBootTime;

        // Values.
        private string sfCompatibilityJsonPath;
        private bool? sfVolumeDiskServiceEnabled;
        private bool? unsupportedPreviewFeaturesEnabled;
        private bool? sfEnableCircularTraceSession;

        /// <summary>
        /// Initializes a new instance of the <see cref="SFConfigurationObserver"/> class.
        /// </summary>
        public SFConfigurationObserver()
        {
        }

        public string SFRootDir { get; private set; }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            // This observer is only useful if you enable the web api for producing
            // an html page with a bunch of information that's easy to read in one go.
            if (!ObserverManager.ObserverWebAppDeployed
                || (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval))
            {
                return;
            }

            token.ThrowIfCancellationRequested();

            try
            {
                ServiceFabricConfiguration config = ServiceFabricConfiguration.Instance;
                this.sfVersion = config.FabricVersion;
                this.sfBinRoot = config.FabricBinRoot;
                this.sfCompatibilityJsonPath = config.CompatibilityJsonPath;
                this.sfCodePath = config.FabricCodePath;
                this.sfDataRoot = config.FabricDataRoot;
                this.sfLogRoot = config.FabricLogRoot;
                this.SFRootDir = config.FabricRoot;
                this.sfEnableCircularTraceSession = config.EnableCircularTraceSession;
                this.sfVolumeDiskServiceEnabled = config.IsSFVolumeDiskServiceEnabled;
                this.unsupportedPreviewFeaturesEnabled = config.EnableUnsupportedPreviewFeatures;
                this.sfNodeLastBootTime = config.NodeLastBootUpTime;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    $"{this.NodeName} | Handled Exception, but failed to read registry value:\n{e}");
            }
            catch (Exception e)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    $"this.NodeName | Unhandled Exception trying to read registry value:\n{e}");

                throw;
            }

            token.ThrowIfCancellationRequested();

            await this.ReportAsync(token).ConfigureAwait(true);

            this.LastRunDateTime = DateTime.Now;
        }

        /// <inheritdoc/>
        public override async Task ReportAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var sb = new StringBuilder();

            _ = sb.AppendLine("\nService Fabric information:\n");

            if (!string.IsNullOrEmpty(this.sfVersion))
            {
                _ = sb.AppendLine("Runtime Version: " + this.sfVersion);
            }

            if (this.sfBinRoot != null)
            {
                _ = sb.AppendLine("Fabric Bin root directory: " + this.sfBinRoot);
            }

            if (this.sfCodePath != null)
            {
                _ = sb.AppendLine("Fabric Code Path: " + this.sfCodePath);
            }

            if (!string.IsNullOrEmpty(this.sfDataRoot))
            {
                _ = sb.AppendLine("Data root directory: " + this.sfDataRoot);
            }

            if (!string.IsNullOrEmpty(this.sfLogRoot))
            {
                _ = sb.AppendLine("Log root directory: " + this.sfLogRoot);
            }

            if (this.sfVolumeDiskServiceEnabled != null)
            {
                _ = sb.AppendLine("Volume Disk Service Enabled: " + this.sfVolumeDiskServiceEnabled);
            }

            if (this.unsupportedPreviewFeaturesEnabled != null)
            {
                _ = sb.AppendLine("Unsupported Preview Features Enabled: " + this.unsupportedPreviewFeaturesEnabled);
            }

            if (this.sfCompatibilityJsonPath != null)
            {
                _ = sb.AppendLine("Compatibility Json path: " + this.sfCompatibilityJsonPath);
            }

            if (this.sfEnableCircularTraceSession != null)
            {
                _ = sb.AppendLine("Enable Circular trace session: " + this.sfEnableCircularTraceSession);
            }

            _ = sb.Append(await this.GetDeployedAppsInfoAsync(token).ConfigureAwait(true));
            _ = sb.AppendLine();

            token.ThrowIfCancellationRequested();

            var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "SFInfraInfo.txt");

            // This file is used by the web application (ObserverWebApi).
            if (!this.ObserverLogger.TryWriteLogFile(logPath, sb.ToString()))
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    "Unable to create SFInfraInfo.txt file.");
            }

            _ = sb.Clear();
        }

        private async Task<string> GetDeployedAppsInfoAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            ApplicationList appList = null;
            var sb = new StringBuilder();
            string clusterManifestXml = null;

            if (this.IsTestRun)
            {
                clusterManifestXml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"));
            }
            else
            {
                try
                {
                    appList = await this.FabricClientInstance.QueryManager.GetApplicationListAsync().ConfigureAwait(true);
                    clusterManifestXml = await this.FabricClientInstance.ClusterManager.GetClusterManifestAsync(this.AsyncClusterOperationTimeoutSeconds, this.Token).ConfigureAwait(true);
                }
                catch (Exception e) when (e is FabricException || e is TimeoutException)
                {
                }
            }

            token.ThrowIfCancellationRequested();

            XmlReader xreader = null;
            XmlDocument xdoc = null;
            XmlNamespaceManager nsmgr = null;
            StringReader sreader = null;
            string ret;

            try
            {
                if (clusterManifestXml != null)
                {
                    // Safe XML pattern - *Do not use LoadXml*.
                    xdoc = new XmlDocument { XmlResolver = null };
                    sreader = new StringReader(clusterManifestXml);
                    xreader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null });
                    xdoc.Load(xreader);

                    // Cluster Information.
                    nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                    nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");

                    // Failover Manager.
                    var fMparameterNodes = xdoc.SelectNodes("//sf:Section[@Name='FailoverManager']//sf:Parameter", nsmgr);
                    _ = sb.AppendLine("\nCluster Information:\n");

                    if (fMparameterNodes != null)
                    {
                        foreach (XmlNode node in fMparameterNodes)
                        {
                            token.ThrowIfCancellationRequested();

                            _ = sb.AppendLine(node?.Attributes?.Item(0).Value + ": " + node?.Attributes?.Item(1).Value);
                        }
                    }
                }

                token.ThrowIfCancellationRequested();

                // Node Information.
                _ = sb.AppendLine("\nNode Info:\n");
                _ = sb.AppendLine($"Node Name: {this.NodeName}");
                _ = sb.AppendLine($"Node Id: {this.FabricServiceContext.NodeContext.NodeId}");
                _ = sb.AppendLine($"Node Instance Id: {this.FabricServiceContext.NodeContext.NodeInstanceId}");
                _ = sb.AppendLine($"Node Type: {this.FabricServiceContext.NodeContext.NodeType}");
                var (lowPort, highPort) = NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(this.FabricServiceContext.NodeContext.NodeType, clusterManifestXml);

                if (lowPort > -1)
                {
                    _ = sb.AppendLine($"Application Port Range: {lowPort} - {highPort}");
                }

                var infraNode = xdoc?.SelectSingleNode("//sf:Node", nsmgr);

                if (infraNode != null)
                {
                    _ = sb.AppendLine("Is Seed Node: " + infraNode.Attributes?["IsSeedNode"]?.Value);
                    _ = sb.AppendLine("Fault Domain: " + infraNode.Attributes?["FaultDomain"]?.Value);
                    _ = sb.AppendLine("Upgrade Domain: " + infraNode.Attributes?["UpgradeDomain"]?.Value);
                }

                token.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(this.sfNodeLastBootTime))
                {
                    _ = sb.AppendLine("Last Rebooted: " + this.sfNodeLastBootTime);
                }

                // Stop here for unit testing.
                if (this.IsTestRun)
                {
                    ret = sb.ToString();
                    _ = sb.Clear();

                    return ret;
                }

                // Application Info.
                if (appList != null)
                {
                    _ = sb.AppendLine("\nDeployed Apps:\n");

                    foreach (var app in appList)
                    {
                        token.ThrowIfCancellationRequested();

                        var appName = app.ApplicationName.OriginalString;
                        var appType = app.ApplicationTypeName;
                        var appVersion = app.ApplicationTypeVersion;
                        var healthState = app.HealthState.ToString();
                        var status = app.ApplicationStatus.ToString();

                        _ = sb.AppendLine("Application Name: " + appName);
                        _ = sb.AppendLine("Type: " + appType);
                        _ = sb.AppendLine("Version: " + appVersion);
                        _ = sb.AppendLine("Health state: " + healthState);
                        _ = sb.AppendLine("Status: " + status);

                        // Service(s).
                        _ = sb.AppendLine("\n\tServices:");
                        var serviceList = await this.FabricClientInstance.QueryManager.GetServiceListAsync(app.ApplicationName).ConfigureAwait(true);
                        var replicaList = await this.FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(this.NodeName, app.ApplicationName).ConfigureAwait(true);

                        foreach (var service in serviceList)
                        {
                            var kind = service.ServiceKind.ToString();
                            var type = service.ServiceTypeName;
                            var serviceManifestVersion = service.ServiceManifestVersion;
                            var serviceName = service.ServiceName;
                            var serviceDescription = await this.FabricClientInstance.ServiceManager.GetServiceDescriptionAsync(serviceName).ConfigureAwait(true);
                            var processModel = serviceDescription.ServicePackageActivationMode.ToString();

                            foreach (var rep in replicaList)
                            {
                                if (service.ServiceName != rep.ServiceName)
                                {
                                    continue;
                                }

                                // Get established port count per service.
                                int procId = (int)rep.HostProcessId;
                                int ports = -1, ephemeralPorts = -1;

                                if (procId > -1)
                                {
                                    ports = OperatingSystemInfoProvider.Instance.GetActivePortCount(procId);
                                    ephemeralPorts = OperatingSystemInfoProvider.Instance.GetActiveEphemeralPortCount(procId);
                                }

                                _ = sb.AppendLine("\tService Name: " + serviceName.OriginalString);
                                _ = sb.AppendLine("\tTypeName: " + type);
                                _ = sb.AppendLine("\tKind: " + kind);
                                _ = sb.AppendLine("\tProcessModel: " + processModel);
                                _ = sb.AppendLine("\tServiceManifest Version: " + serviceManifestVersion);

                                if (ports > -1)
                                {
                                    _ = sb.AppendLine("\tActive Ports: " + ports);
                                }

                                if (ephemeralPorts > -1)
                                {
                                    _ = sb.AppendLine("\tActive Ephemeral Ports: " + ephemeralPorts);
                                }

                                _ = sb.AppendLine();

                                // ETW.
                                if (this.IsEtwEnabled)
                                {
                                    Logger.EtwLogger?.Write(
                                        ObserverConstants.FabricObserverETWEventName,
                                        new
                                        {
                                            Level = 0, // Info
                                            Node = this.NodeName,
                                            Observer = this.ObserverName,
                                            AppName = appName,
                                            AppType = appType,
                                            AppVersion = appVersion,
                                            AppHealthState = healthState,
                                            AppStatus = status,
                                            ServiceName = serviceName.OriginalString,
                                            ServiceTypeName = type,
                                            Kind = kind,
                                            ProcessModel = processModel,
                                            ServiceManifestVersion = serviceManifestVersion,
                                            ActivePorts = ports,
                                            EphemeralPorts = ephemeralPorts,
                                        });
                                }

                                break;
                            }
                        }
                    }
                }

                ret = sb.ToString();
                _ = sb.Clear();
            }
            finally
            {
                sreader?.Dispose();
                xreader?.Dispose();
            }

            return ret;
        }
    }
}