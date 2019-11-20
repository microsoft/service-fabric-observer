// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FabricObserver.Utilities;
using Microsoft.Win32;

namespace FabricObserver
{
    // This observer doesn't monitor or report health status.
    // It provides information about the currently installed Service Fabric runtime environment, apps, and services.
    // The output (a local file) is used by the FO API service to render an HTML page (http://localhost:5000/api/ObserverManager).
    public class SFConfigurationObserver : ObserverBase
    {
        // SF Reg Key Path...
        private const string SFWindowsRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Service Fabric";

        // Keys...
        private const string SFInfrastructureCompatibilityJsonPathRegistryName = "CompatibilityJsonPath";
        private const string SFInfrastructureEnableCircularTraceSessionRegistryName = "EnableCircularTraceSession";
        private const string SFInfrastructureBinRootRegistryName = "FabricBinRoot";
        private const string SFInfrastructureCodePathRegistryName = "FabricCodePath";
        private const string SFInfrastructureDataRootRegistryName = "FabricDataRoot";
        private const string SFInfrastructureLogRootRegistryName = "FabricLogRoot";
        private const string SFInfrastructureRootDirectoryRegistryName = "FabricRoot";
        private const string SFInfrastructureVersionRegistryName = "FabricVersion";
        private const string SFInfrastructureIsSFVolumeDiskServiceEnabledName = "IsSFVolumeDiskServiceEnabled";
        private const string SFInfrastructureEnableUnsupportedPreviewFeaturesName = "EnableUnsupportedPreviewFeatures";
        private const string SFInfrastructureNodeLastBootUpTime = "NodeLastBootUpTime";

        // Values...
        private string sFVersion;

        // Values...
        private string sFBinRoot;

        // Values...
        private string sFCodePath;

        // Values...
        private string sFDataRoot;

        // Values...
        private string sFLogRoot;

        // Values...
        private string sFRootDir;

        // Values...
        private string sFNodeLastBootTime;

        // Values...
        private string sFCompatibilityJsonPath;
        private bool? sFVolumeDiskServiceEnabled = null;
        private bool? unsupportedPreviewFeaturesEnabled = null;
        private bool? sFEnableCircularTraceSession = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="SFConfigurationObserver"/> class.
        /// </summary>
        public SFConfigurationObserver()
            : base(ObserverConstants.SFConfigurationObserverName)
        {
        }

        /// <inheritdoc/>
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example...
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
                this.sFVersion = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureVersionRegistryName, null);
                this.sFBinRoot = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureBinRootRegistryName, null);
                this.sFCompatibilityJsonPath = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureCompatibilityJsonPathRegistryName, null);
                this.sFCodePath = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureCodePathRegistryName, null);
                this.sFDataRoot = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureDataRootRegistryName, null);
                this.sFLogRoot = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureLogRootRegistryName, null);
                this.sFRootDir = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureRootDirectoryRegistryName, null);
                this.sFEnableCircularTraceSession = Convert.ToBoolean(Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureEnableCircularTraceSessionRegistryName, null));
                this.sFVolumeDiskServiceEnabled = Convert.ToBoolean(Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureIsSFVolumeDiskServiceEnabledName, null));
                this.unsupportedPreviewFeaturesEnabled = Convert.ToBoolean(Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureEnableUnsupportedPreviewFeaturesName, null));
                this.sFNodeLastBootTime = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureNodeLastBootUpTime, null);
            }
            catch (ArgumentException ae)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    $"{this.NodeName} | Handled Exception, but failed to read registry value:\n{ae.ToString()}");
            }
            catch (IOException ie)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    $"{this.NodeName} | Handled Exception, but failed to read registry value:\n {ie.ToString()}");
            }
            catch (Exception e)
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    $"this.NodeName | Unhandled Exception trying to read registry value:\n{e.ToString()}");
                throw;
            }

            token.ThrowIfCancellationRequested();

            await this.ReportAsync(token).ConfigureAwait(true);

            this.LastRunDateTime = DateTime.Now;
        }

        private async Task<string> GetDeployedAppsInfoAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            ApplicationList appList = null;
            var sb = new StringBuilder();
            string clusterManifestXml;

            if (this.IsTestRun)
            {
                clusterManifestXml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"));
            }
            else
            {
                appList = await this.FabricClientInstance.QueryManager.GetApplicationListAsync().ConfigureAwait(true);
                clusterManifestXml = await this.FabricClientInstance.ClusterManager.GetClusterManifestAsync().ConfigureAwait(true);
            }

            token.ThrowIfCancellationRequested();

            XmlReader xreader = null;
            StringReader sreader = null;
            string ret = null;

            try
            {
                // Safe XML pattern - *Do not use LoadXml*...
                var xdoc = new XmlDocument { XmlResolver = null };
                sreader = new StringReader(clusterManifestXml);
                xreader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null });
                xdoc.Load(xreader);

                // Cluster Information...
                var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");

                // Failover Manager...
                var fMparameterNodes = xdoc.SelectNodes("//sf:Section[@Name='FailoverManager']//sf:Parameter", nsmgr);
                sb.AppendLine("\nCluster Information:\n");

                foreach (XmlNode node in fMparameterNodes)
                {
                    token.ThrowIfCancellationRequested();

                    sb.AppendLine(node.Attributes.Item(0).Value + ": " + node.Attributes.Item(1).Value);
                }

                token.ThrowIfCancellationRequested();

                // Node Information...
                sb.AppendLine($"\nNode Info:\n");
                sb.AppendLine($"Node Name: {this.NodeName}");
                sb.AppendLine($"Node Id: {this.FabricServiceContext.NodeContext.NodeId}");
                sb.AppendLine($"Node Instance Id: {this.FabricServiceContext.NodeContext.NodeInstanceId}");
                sb.AppendLine($"Node Type: {this.FabricServiceContext.NodeContext.NodeType}");
                Tuple<int, int> portRange = NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(this.FabricServiceContext.NodeContext.NodeType, clusterManifestXml);

                if (portRange.Item1 > -1)
                {
                    sb.AppendLine($"Application Port Range: {portRange.Item1} - {portRange.Item2}");
                }

                var infraNode = xdoc.SelectSingleNode("//sf:Node", nsmgr);

                if (infraNode != null)
                {
                    sb.AppendLine("Is Seed Node: " + infraNode.Attributes["IsSeedNode"]?.Value);
                    sb.AppendLine("Fault Domain: " + infraNode.Attributes["FaultDomain"]?.Value);
                    sb.AppendLine("Upgrade Domain: " + infraNode.Attributes["UpgradeDomain"]?.Value);
                }

                token.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(this.sFNodeLastBootTime))
                {
                    sb.AppendLine("Last Rebooted: " + this.sFNodeLastBootTime);
                }

                // Stop here for unit testing...
                if (this.IsTestRun)
                {
                    ret = sb.ToString();
                    sb.Clear();

                    return ret;
                }

                // Application Info...
                if (appList != null)
                {
                    sb.AppendLine("\nDeployed Apps:\n");

                    foreach (var app in appList)
                    {
                        token.ThrowIfCancellationRequested();

                        var appName = app.ApplicationName.OriginalString;
                        var appType = app.ApplicationTypeName;
                        var appVersion = app.ApplicationTypeVersion;
                        var healthState = app.HealthState.ToString();
                        var status = app.ApplicationStatus.ToString();

                        sb.AppendLine("Application Name: " + appName);
                        sb.AppendLine("Type: " + appType);
                        sb.AppendLine("Version: " + appVersion);
                        sb.AppendLine("Health state: " + healthState);
                        sb.AppendLine("Status: " + status);

                        // App's Service(s)...
                        sb.AppendLine("\n\tServices:");
                        var serviceList = await this.FabricClientInstance.QueryManager.GetServiceListAsync(app.ApplicationName).ConfigureAwait(true);
                        var replicaList = await this.FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(this.NodeName, app.ApplicationName).ConfigureAwait(true);

                        foreach (var service in serviceList)
                        {
                            var kind = service.ServiceKind.ToString();
                            var type = service.ServiceTypeName;
                            var serviceManifestversion = service.ServiceManifestVersion;
                            var serviceName = service.ServiceName;
                            var serviceDescription = await this.FabricClientInstance.ServiceManager.GetServiceDescriptionAsync(serviceName).ConfigureAwait(true);
                            var processModel = serviceDescription.ServicePackageActivationMode.ToString();

                            foreach (var rep in replicaList)
                            {
                                if (service.ServiceName != rep.ServiceName)
                                {
                                    continue;
                                }

                                // Get established port count per service...
                                int procId = (int)rep.HostProcessId;
                                int ports = -1, ephemeralPorts = -1;

                                if (procId > -1)
                                {
                                    ports = NetworkUsage.GetActivePortCount(procId);
                                    ephemeralPorts = NetworkUsage.GetActiveEphemeralPortCount(procId);
                                }

                                sb.AppendLine("\tService Name: " + serviceName.OriginalString);
                                sb.AppendLine("\tTypeName: " + type);
                                sb.AppendLine("\tKind: " + kind);
                                sb.AppendLine("\tProcessModel: " + processModel);
                                sb.AppendLine("\tServiceManifest Version: " + serviceManifestversion);

                                if (ports > -1)
                                {
                                    sb.AppendLine("\tActive Ports: " + ports);
                                }

                                if (ephemeralPorts > -1)
                                {
                                    sb.AppendLine("\tActive Ephemeral Ports: " + ephemeralPorts);
                                }

                                sb.AppendLine();

                                // ETW...
                                if (this.IsEtwEnabled)
                                {
                                    Logger.EtwLogger?.Write(
                                        $"FabricObserverDataEvent",
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
                                            ServiceManifestVersion = serviceManifestversion,
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
                sb.Clear();
            }
            finally
            {
                sreader.Dispose();
                xreader.Dispose();
            }

            return ret;
        }

        /// <inheritdoc/>
        public override async Task ReportAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var sb = new StringBuilder();

            sb.AppendLine("\nService Fabric information:\n");

            if (!string.IsNullOrEmpty(this.sFVersion))
            {
                sb.AppendLine("Runtime Version: " + this.sFVersion);
            }

            if (this.sFBinRoot != null)
            {
                sb.AppendLine("Fabric Bin root directory: " + this.sFBinRoot);
            }

            if (this.sFCodePath != null)
            {
                sb.AppendLine("Fabric Code Path: " + this.sFCodePath);
            }

            if (!string.IsNullOrEmpty(this.sFDataRoot))
            {
                sb.AppendLine("Data root directory: " + this.sFDataRoot);
            }

            if (!string.IsNullOrEmpty(this.sFLogRoot))
            {
                sb.AppendLine("Log root directory: " + this.sFLogRoot);
            }

            if (this.sFVolumeDiskServiceEnabled != null)
            {
                sb.AppendLine("Volume Disk Service Enabled: " + this.sFVolumeDiskServiceEnabled);
            }

            if (this.unsupportedPreviewFeaturesEnabled != null)
            {
                sb.AppendLine("Unsupported Preview Features Enabled: " + this.unsupportedPreviewFeaturesEnabled);
            }

            if (this.sFCompatibilityJsonPath != null)
            {
                sb.AppendLine("Compatibility Json path: " + this.sFCompatibilityJsonPath);
            }

            if (this.sFEnableCircularTraceSession != null)
            {
                sb.AppendLine("Enable Circular trace session: " + this.sFEnableCircularTraceSession);
            }

            sb.Append(await this.GetDeployedAppsInfoAsync(token).ConfigureAwait(true));
            sb.AppendLine();

            token.ThrowIfCancellationRequested();

            var logPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "SFInfraInfo.txt");

            // This file is used by the web application (ObserverWebApi)...
            if (!this.ObserverLogger.TryWriteLogFile(logPath, sb.ToString()))
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    this.FabricServiceContext.ServiceName.OriginalString,
                    this.ObserverName,
                    HealthState.Warning,
                    "Unable to create SFInfraInfo.txt file...");
            }

            sb.Clear();
        }
    }
}