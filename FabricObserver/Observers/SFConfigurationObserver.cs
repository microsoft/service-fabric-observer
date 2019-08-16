// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Utilities;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace FabricObserver
{
    // This observer doesn't monitor or report health status. 
    // It provides information about the currently installed Service Fabric runtime environment.
    // The output (a local file) is used by the API service and the HTML frontend (https://[domain:[port]]/api/ObserverManager).
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
        //private const string SFInfrastructureFabricDnsIpAddressRegistryName = "FabricDnsServerIPAddress";
        private const string SFInfrastructureLogRootRegistryName = "FabricLogRoot";
        private const string SFInfrastructureRootDirectoryRegistryName = "FabricRoot";
        private const string SFInfrastructureVersionRegistryName = "FabricVersion";
        //private const string SFInfrastructureUseFabricInstallerSvcRegistryName = "UseFabricInstallerSvc";
        private const string SFInfrastructureIsSFVolumeDiskServiceEnabledName = "IsSFVolumeDiskServiceEnabled";
        private const string SFInfrastructureEnableUnsupportedPreviewFeaturesName = "EnableUnsupportedPreviewFeatures";
        private const string SFInfrastructureNodeLastBootUpTime = "NodeLastBootUpTime";

        // Values...
        private string SFVersion, SFBinRoot, SFCodePath, SFDataRoot, SFLogRoot, SFRootDir, SFNodeLastBootTime, SFCompatibilityJsonPath; //_SFFabricDnsServerIPAddress;
        private bool? SFVolumeDiskServiceEnabled = null, UnsupportedPreviewFeaturesEnabled = null, SFEnableCircularTraceSession = null;

        public SFConfigurationObserver() : base(ObserverConstants.SFConfigurationObserverName) { }

        public override async Task ObserveAsync(CancellationToken token)
        {
            try
            {
                SFVersion = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureVersionRegistryName, null);
                SFBinRoot = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureBinRootRegistryName, null);
                SFCompatibilityJsonPath = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureCompatibilityJsonPathRegistryName, null);
                SFCodePath = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureCodePathRegistryName, null);
                SFDataRoot = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureDataRootRegistryName, null);
                SFLogRoot = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureLogRootRegistryName, null);
                SFRootDir = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureRootDirectoryRegistryName, null);
                SFEnableCircularTraceSession = Convert.ToBoolean(Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureEnableCircularTraceSessionRegistryName, null));
                SFVolumeDiskServiceEnabled = Convert.ToBoolean(Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureIsSFVolumeDiskServiceEnabledName, null));
                UnsupportedPreviewFeaturesEnabled = Convert.ToBoolean(Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureEnableUnsupportedPreviewFeaturesName, null));
                SFNodeLastBootTime = (string)Registry.GetValue(SFWindowsRegistryPath, SFInfrastructureNodeLastBootUpTime, null);
            }
            catch (ArgumentException ae)
            {
                HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                   ObserverName,
                                                   HealthState.Warning, NodeName +
                                                   " | Handled Exception, but failed to read registry value:\n" +
                                                   ae.ToString());
            }
            catch (IOException ie)
            {
                HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                   ObserverName,
                                                   HealthState.Warning, NodeName +
                                                   " | Handled Exception, but failed to read registry value:\n" +
                                                   ie.ToString());
            }
            catch (Exception e)
            {
                HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                   ObserverName,
                                                   HealthState.Warning, NodeName +
                                                   " | Unhandled Exception trying to read registry value:\n" +
                                                   e.ToString());
                throw;
            }

            token.ThrowIfCancellationRequested();

            await ReportAsync(token).ConfigureAwait(true);

            LastRunDateTime = DateTime.Now;
        }

        private async Task<string> GetDeployedAppsInfoAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            ApplicationList appList = null;
            var sb = new StringBuilder();
            string clusterManifestXml = null, appManifestXml = null;

            if (IsTestRun)
            {
                clusterManifestXml = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "clusterManifest.xml"));  
            }
            else
            {
                appList = await FabricClientInstance.QueryManager.GetApplicationListAsync().ConfigureAwait(true);
                clusterManifestXml = await FabricClientInstance.ClusterManager.GetClusterManifestAsync().ConfigureAwait(true);
            }

            token.ThrowIfCancellationRequested();

            // Safe XML pattern - *Do not use LoadXml*...
            var xdoc = new XmlDocument { XmlResolver = null };
            var sreader = new StringReader(clusterManifestXml);
            var reader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null });
            xdoc.Load(reader);

            // Cluster Information...
            var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
            nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");

            // Failover Manager...
            var FMparameterNodes = xdoc.SelectNodes("//sf:Section[@Name='FailoverManager']//sf:Parameter", nsmgr);
            sb.AppendLine("\nCluster Information:\n");

            foreach (XmlNode node in FMparameterNodes)
            {
                token.ThrowIfCancellationRequested();

                sb.AppendLine(node.Attributes.Item(0).Value + ": " + node.Attributes.Item(1).Value);
            }

            token.ThrowIfCancellationRequested();

            // Node Information...
            sb.AppendLine($"\nNode Info:\n");
            sb.AppendLine($"Node Name: {NodeName}");
            sb.AppendLine($"Node Id: {FabricServiceContext.NodeContext.NodeId}");
            sb.AppendLine($"Node Instance Id: {FabricServiceContext.NodeContext.NodeInstanceId}");
            sb.AppendLine($"Node Type: {FabricServiceContext.NodeContext.NodeType}");
            Tuple<int, int> portRange = NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(FabricServiceContext.NodeContext.NodeType, clusterManifestXml);
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

            if (!string.IsNullOrEmpty(this.SFNodeLastBootTime))
            {
                sb.AppendLine("Last Rebooted: " + this.SFNodeLastBootTime);
            }

            // Stop here for unit testing...
            if (IsTestRun)
            {
                string ret = sb.ToString();
                sb.Clear();
                return ret;
            }

            // Application Info...
            sb.AppendLine("\nDeployed Apps:\n");
            foreach (var app in appList)
            {
                token.ThrowIfCancellationRequested();

                var appName = app.ApplicationName;
                var appType = app.ApplicationTypeName;
                var appVersion = app.ApplicationTypeVersion;
                var healthState = app.HealthState.ToString();
                var status = app.ApplicationStatus.ToString();
                appManifestXml = await FabricClientInstance.ApplicationManager.GetApplicationManifestAsync(appType, appVersion).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                ServicePackageActivationMode? processModel = null;
                string serviceName = "";
                sreader = new StringReader(appManifestXml);
                reader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null });
                xdoc.Load(reader);

                var node = xdoc.SelectSingleNode("//sf:Service", nsmgr);
                if (node != null)
                {
                    processModel = (ServicePackageActivationMode)Enum.Parse(typeof(ServicePackageActivationMode),
                                                                            node.Attributes["ServicePackageActivationMode"]?.Value);
                    serviceName = node.Attributes["Name"]?.Value;
                }

                sb.AppendLine("Application Name: " + appName.OriginalString);
                sb.AppendLine("Type: " + appType);
                sb.AppendLine("Version: " + appVersion);
                sb.AppendLine("Health state: " + healthState);
                sb.AppendLine("Status: " + status);
                sb.AppendLine("ProcessModel: " + processModel.ToString());

                // App's Service(s)...
                sb.AppendLine("\n\tServices:");

                var serviceDescription = await FabricClientInstance.ServiceManager.GetServiceDescriptionAsync(new Uri(appName.OriginalString + "/" + serviceName)).ConfigureAwait(true);
                var kind = serviceDescription.Kind.ToString();
                var type = serviceDescription.ServiceTypeName;
                var metrics = serviceDescription.Metrics;

                // Get established port count per service...
                int procId = -1;
                int ports = -1, ephemeralPorts = -1;

                try
                {
                    if (serviceName != "")
                    {
                        var processes = Process.GetProcessesByName(serviceName);

                        if (processes?.Length > 0)
                        {
                            foreach (var process in processes)
                            {
                                token.ThrowIfCancellationRequested();

                                procId = process.Id;
                                
                                // Stop after one process (TODO: handle multiple instances of the same service running on the same node... Is this common?)
                                break;
                            }
                        }
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }

                if (procId > -1)
                {
                    ports = NetworkUsage.GetActivePortCount(procId);
                    ephemeralPorts = NetworkUsage.GetActiveEphemeralPortCount(procId);
                }

                sb.AppendLine("\tProcess Name: " + serviceName);
                sb.AppendLine("\tTypeName: " + type);
                sb.AppendLine("\tKind: " + kind);

                if (ports > -1)
                {
                    sb.AppendLine("\tActive Ports: " + ports);
                }

                if (ephemeralPorts > -1)
                {
                    sb.AppendLine("\tActive Ephemeral Ports: " + ephemeralPorts);
                }

                sb.AppendLine();
            }

            string s = sb.ToString();
            sb.Clear();
            reader?.Dispose();

            return s;
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var sb = new StringBuilder();

            sb.AppendLine("\nService Fabric information:\n");

            if (!string.IsNullOrEmpty(this.SFVersion))
            {
                sb.AppendLine("Runtime Version: " + this.SFVersion);
            }

            if (this.SFBinRoot != null)
            {
                sb.AppendLine("Fabric Bin root directory: " + this.SFBinRoot);
            }

            if (this.SFCodePath != null)
            {
                sb.AppendLine("Fabric Code Path: " + this.SFCodePath);
            }

            if (!string.IsNullOrEmpty(this.SFDataRoot))
            {
                sb.AppendLine("Data root directory: " + this.SFDataRoot);
            }

            if (!string.IsNullOrEmpty(this.SFLogRoot))
            {
                sb.AppendLine("Log root directory: " + this.SFLogRoot);
            }

            if (this.SFVolumeDiskServiceEnabled != null)
            {
                sb.AppendLine("Volume Disk Service Enabled: " + this.SFVolumeDiskServiceEnabled);
            }

            if (this.UnsupportedPreviewFeaturesEnabled != null)
            {
                sb.AppendLine("Unsupported Preview Features Enabled: " + this.UnsupportedPreviewFeaturesEnabled);
            }

            if (this.SFCompatibilityJsonPath != null)
            {
                sb.AppendLine("Compatibility Json path: " + this.SFCompatibilityJsonPath);
            }

            if (this.SFEnableCircularTraceSession != null)
            {
                sb.AppendLine("Enable Circular trace session: " + this.SFEnableCircularTraceSession);
            }

            sb.Append(await GetDeployedAppsInfoAsync(token).ConfigureAwait(true));
            sb.AppendLine();

            token.ThrowIfCancellationRequested();

            var logPath = Path.Combine(ObserverLogger.LogFolderBasePath, "SFInfraInfo.txt");

            // This file is used by the web application (ObserverWebApi)...
            if (!ObserverLogger.TryWriteLogFile(logPath, sb.ToString()))
            {
                HealthReporter.ReportFabricObserverServiceHealth(FabricServiceContext.ServiceName.OriginalString,
                                                                 ObserverName,
                                                                 HealthState.Warning,
                                                                 "Unable to create SFInfraInfo.txt file...");
            }

            sb.Clear();
        }
    }
}