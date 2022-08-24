// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Query;
using System.IO;
using System.Runtime.InteropServices;
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
    public sealed class SFConfigurationObserver : ObserverBase
    {
        private string SFVersion;
        private string SFBinRoot;
        private string SFCodePath;
        private string SFDataRoot;
        private string SFLogRoot;
        private string SFNodeLastBootTime;
        private string SFCompatibilityJsonPath;
        private bool? SFVolumeDiskServiceEnabled;
        private bool? unsupportedPreviewFeaturesEnabled;
        private bool? SFEnableCircularTraceSession;

        public string SFRootDir
        {
            get; private set;
        }

        public string ClusterManifestPath
        {
            get; set;
        }

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public SFConfigurationObserver(StatelessServiceContext context) : base(null, context)
        {

        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            if (!IsObserverWebApiAppDeployed || (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval))
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            Token = token;

            try
            {
                var config = ServiceFabricConfiguration.Instance;
                SFVersion = config.FabricVersion;
                SFBinRoot = config.FabricBinRoot;
                SFCompatibilityJsonPath = config.CompatibilityJsonPath;
                SFCodePath = config.FabricCodePath;
                SFDataRoot = config.FabricDataRoot;
                SFLogRoot = config.FabricLogRoot;
                SFRootDir = config.FabricRoot;
                SFEnableCircularTraceSession = config.EnableCircularTraceSession;
                SFVolumeDiskServiceEnabled = config.IsSFVolumeDiskServiceEnabled;
                unsupportedPreviewFeaturesEnabled = config.EnableUnsupportedPreviewFeatures;
                SFNodeLastBootTime = config.NodeLastBootUpTime;

                await ReportAsync(token);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException)
            {
                
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogWarning($"Unhandled Exception in ObserveAsync:{Environment.NewLine}{e}");

                // Fix the bug..
                throw;
            }

            LastRunDateTime = DateTime.Now;
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var sb = new StringBuilder();
            _ = sb.AppendLine($"{Environment.NewLine}Service Fabric information:{Environment.NewLine}");

            if (!string.IsNullOrEmpty(SFVersion))
            {
                _ = sb.AppendLine("Runtime Version: " + SFVersion);
            }

            if (SFBinRoot != null)
            {
                _ = sb.AppendLine("Fabric Bin root directory: " + SFBinRoot);
            }

            if (SFCodePath != null)
            {
                _ = sb.AppendLine("Fabric Code Path: " + SFCodePath);
            }

            if (!string.IsNullOrEmpty(SFDataRoot))
            {
                _ = sb.AppendLine("Data root directory: " + SFDataRoot);
            }

            if (!string.IsNullOrEmpty(SFLogRoot))
            {
                _ = sb.AppendLine("Log root directory: " + SFLogRoot);
            }

            if (SFVolumeDiskServiceEnabled != null)
            {
                _ = sb.AppendLine("Volume Disk Service Enabled: " + SFVolumeDiskServiceEnabled);
            }

            if (unsupportedPreviewFeaturesEnabled != null)
            {
                _ = sb.AppendLine("Unsupported Preview Features Enabled: " + unsupportedPreviewFeaturesEnabled);
            }

            if (SFCompatibilityJsonPath != null)
            {
                _ = sb.AppendLine("Compatibility Json path: " + SFCompatibilityJsonPath);
            }

            if (SFEnableCircularTraceSession != null)
            {
                _ = sb.AppendLine("Enable Circular trace session: " + SFEnableCircularTraceSession);
            }

            _ = sb.Append(await GetDeployedAppsInfoAsync(token));
            _ = sb.AppendLine();

            token.ThrowIfCancellationRequested();

            var logPath = Path.Combine(ObserverLogger.LogFolderBasePath, "SFInfraInfo.txt");

            // This file is used by the web application (ObserverWebApi).
            if (!ObserverLogger.TryWriteLogFile(logPath, sb.ToString()))
            {
                ObserverLogger.LogWarning("Unable to create SFInfraInfo.txt file.");
            }

            _ = sb.Clear();
        }

        private async Task<string> GetDeployedAppsInfoAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            ApplicationList appList = null;
            var sb = new StringBuilder();
            string clusterManifestXml = null;

            try
            {
                appList = await FabricClientInstance.QueryManager.GetApplicationListAsync();
                    
                if (!string.IsNullOrWhiteSpace(ClusterManifestPath))
                {
                    clusterManifestXml = await File.ReadAllTextAsync(ClusterManifestPath, token);
                }
                else
                {
                    clusterManifestXml = await FabricClientInstance.ClusterManager.GetClusterManifestAsync(AsyncClusterOperationTimeoutSeconds, Token);
                }
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
            
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
                    xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null });
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
                _ = sb.AppendLine($"{Environment.NewLine}Node Info:{Environment.NewLine}");
                _ = sb.AppendLine($"Node Name: {NodeName}");
                _ = sb.AppendLine($"Node Type: {NodeType}");
                var (lowPort, highPort) = NetworkUsage.TupleGetFabricApplicationPortRangeForNodeType(NodeType, clusterManifestXml);

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

                if (!string.IsNullOrEmpty(SFNodeLastBootTime))
                {
                    _ = sb.AppendLine("Last Rebooted: " + SFNodeLastBootTime);
                }

                // Application Info.
                if (appList != null)
                {
                    _ = sb.AppendLine($"{Environment.NewLine}Deployed Apps:{Environment.NewLine}");

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
                        _ = sb.AppendLine($"{Environment.NewLine}\tServices:");
                        var serviceList = await FabricClientInstance.QueryManager.GetServiceListAsync(app.ApplicationName);
                        var replicaList = await FabricClientInstance.QueryManager.GetDeployedReplicaListAsync(NodeName, app.ApplicationName);

                        foreach (var service in serviceList)
                        {
                            var kind = service.ServiceKind.ToString();
                            var type = service.ServiceTypeName;
                            var serviceManifestVersion = service.ServiceManifestVersion;
                            var serviceName = service.ServiceName;
                            var serviceDescription = await FabricClientInstance.ServiceManager.GetServiceDescriptionAsync(serviceName);
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
                                    ports = OSInfoProvider.Instance.GetActiveTcpPortCount(procId, RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ConfigPackage.Path : null);
                                    ephemeralPorts = OSInfoProvider.Instance.GetActiveEphemeralPortCount(procId, RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ConfigPackage.Path : null);
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
                                if (IsEtwEnabled)
                                {
                                    ObserverLogger.LogEtw(
                                        ObserverConstants.FabricObserverETWEventName,
                                        new
                                        {
                                            Level = 0, // Info
                                            Node = NodeName,
                                            Observer = ObserverName,
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
                                            EphemeralPorts = ephemeralPorts
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