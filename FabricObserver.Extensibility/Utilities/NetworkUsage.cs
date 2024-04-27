// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.IO;
using System.Xml;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Static class that houses networking utilities.
    /// </summary> 
    public static class NetworkUsage
    {
        public static (int LowPort, int HighPort) TupleGetFabricApplicationPortRangeForNodeType(string nodeType, string clusterManifestXml)
        {
            if (string.IsNullOrEmpty(nodeType) || string.IsNullOrEmpty(clusterManifestXml))
            {
                return (-1, -1);
            }

            try
            {
                using (var sreader = new StringReader(clusterManifestXml))
                {
                    using (var xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null }))
                    {
                        // Safe XML pattern - *Do not use LoadXml*.
                        var xdoc = new XmlDocument { XmlResolver = null };
                        xdoc.Load(xreader);

                        // Cluster Information.
                        var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                        nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");

                        // SF Application Port Range.
                        var applicationEndpointsNode = xdoc.SelectSingleNode($"//sf:NodeTypes//sf:NodeType[@Name='{nodeType}']//sf:ApplicationEndpoints", nsmgr);

                        if (applicationEndpointsNode == null)
                        {
                            return (-1, -1);
                        }

                        var ret = (int.Parse(applicationEndpointsNode.Attributes?.Item(0)?.Value ?? "-1"),
                                   int.Parse(applicationEndpointsNode.Attributes?.Item(1)?.Value ?? "-1"));

                        return ret;
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException or NullReferenceException or XmlException)
            {
                // continue
            }

            return (-1, -1);
        }

        // Leave this function here to protect against breaking current consumers. This was changed to prevent cross platform visibility warnings at compile time.
        // See OperatingSystemProvider.cs.
        public static int GetActiveFirewallRulesCount()
        {
            return OSInfoProvider.Instance.GetActiveFirewallRulesCount();
        }
    }
}
