// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Xml;

namespace FabricObserver.Utilities
{
    internal static class NetworkUsage
    {
        internal static int GetActivePortCount(
            int procId = -1,
            Protocol protocol = Protocol.TCP)
        {
            try
            {
                string findStrProc = string.Empty;
                string protoParam = string.Empty;

                if (protocol != Protocol.None)
                {
                    protoParam = "-p " + Enum.GetName(protocol.GetType(), protocol)?.ToLower();
                }

                findStrProc = $"| find /i \"{Enum.GetName(protocol.GetType(), protocol)?.ToLower()}\"";

                if (procId > 0)
                {
                    findStrProc = $"| find \"{procId}\"";
                }

                using (var p = new Process())
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netstat -ano {protoParam} {findStrProc} /c",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                    };

                    p.StartInfo = ps;
                    p.Start();

                    var stdOutput = p.StandardOutput;
                    string output = stdOutput.ReadToEnd().Trim('\n', '\r');
                    string exitStatus = p.ExitCode.ToString();
                    stdOutput.Close();

                    if (exitStatus != "0")
                    {
                        return -1;
                    }

                    return int.TryParse(output, out int ret) ? ret : 0;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            return -1;
        }

        /// <returns>List of string,int Tuples.</returns>
        /// <summary>
        ///  Returns number of ephemeral ports (ports within a dynamic numerical range) in use by a process
        ///  on node as a List.<Tuple<int, int>> (process id, port count in use by said process) ordered by port count, descending...
        ///  On failure, for handled exceptions..., this function returns a list of one Tuple<int, int> of value (-1, -1)...
        /// </summary>
        /// <param name="procId">Optional int process ID</param>
        /// <param name="protocol">Optional Protocol (defaults to TCP. Cannot be None...)</param>
        /// <returns></returns>
        internal static List<Tuple<int, int>> TupleGetEphemeralPortProcessCount(
                                                int procId = -1,
                                                Protocol protocol = Protocol.TCP)
        {
            try
            {
                // Unsupported by underlying API. Could throw an ArgumentException here if that makes you happy...
                if (protocol == Protocol.None)
                {
                    return new List<Tuple<int, int>> { Tuple.Create(-1, -1) };
                }

                string protoParam = Enum.GetName(protocol.GetType(), protocol)?.ToLower();

                using (var p = new Process())
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netstat -ano -p {protoParam}",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    p.StartInfo = ps;
                    p.Start();
                    var stdOutput = p.StandardOutput;

                    // (process, port count)
                    var ephemeralPortProcessTupleList = new List<Tuple<int, int>>();
                    var ephemeralPortList = new List<string>();

                    foreach (var portRow in stdOutput?.ReadToEnd().Split(
                        new string[] { "\r", "\n" },
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!portRow.ToLower().Contains(protoParam))
                        {
                            continue;
                        }

                        // caller supplied a proc id? It is always the last item in the netstat column output, thus
                        // LastIndex is used to protect against selecting a port number of the same value...
                        if (procId > -1 && portRow.LastIndexOf(procId.ToString()) < 0)
                        {
                            continue;
                        }

                        ephemeralPortList.Add(portRow);
                    }

                    var exitStatus = p.ExitCode.ToString();
                    stdOutput?.Close();

                    if (exitStatus != "0")
                    {
                        return new List<Tuple<int, int>> { Tuple.Create(-1, -1) };
                    }

                    Tuple<int, int> portRange = TupleGetDynamicPortRange(protocol);
                    int lowPortRange = portRange.Item1;
                    int highPortRange = portRange.Item2;

                    // Add tuple {process id, count} to list for active ports in dynamic range...
                    foreach (string line in ephemeralPortList)
                    {
                        int port = GetPortNumberFromConsoleOutputRow(line, protoParam);
                        string proc = line.ToLower().Replace(protoParam, string.Empty).Trim()
                                          .Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries)[3].Trim();

                        if (string.IsNullOrEmpty(proc))
                        {
                            continue;
                        }

                        if (port <= lowPortRange || port >= highPortRange
                            || ephemeralPortProcessTupleList.Any(x => x.Item1 == int.Parse(proc)))
                        {
                            continue;
                        }

                        ephemeralPortProcessTupleList.Add(Tuple.Create(
                            int.Parse(proc),
                            ephemeralPortList.Where(s => s.Split(
                                                            new string[] { " " },
                                                            StringSplitOptions.RemoveEmptyEntries)[4].Trim() == proc
                                                            && GetPortNumberFromConsoleOutputRow(s, protoParam) >= lowPortRange
                                                            && GetPortNumberFromConsoleOutputRow(s, protoParam) <= highPortRange).Count()));
                    }

                    var ret = ephemeralPortProcessTupleList.OrderByDescending(x => x.Item2).ToList();
                    return ret;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            return new List<Tuple<int, int>> { Tuple.Create(-1, -1) };
        }

        internal static Tuple<int, int> TupleGetDynamicPortRange(Protocol protocol = Protocol.TCP)
        {
            using (var p = new Process())
            {
                string protoParam = string.Empty;

                if (protocol != Protocol.None)
                {
                    protoParam = Enum.GetName(protocol.GetType(), protocol)?.ToLower();
                }

                try
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netsh int ipv4 show dynamicportrange {protoParam} | find /i \"port\"",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                    };

                    p.StartInfo = ps;
                    p.Start();

                    var stdOutput = p.StandardOutput;
                    string output = stdOutput?.ReadToEnd();
                    string startPort = output?.Substring(
                        output.Trim().IndexOf(":") + 2,
                        output.Substring(output.Trim().LastIndexOf(":") + 2).Length)
                                                         .Trim(' ', '\r', '\n');

                    string portCount = output?.Substring(output.Trim().LastIndexOf(":") + 2).Trim(' ', '\r', '\n');
                    string exitStatus = p.ExitCode.ToString();
                    stdOutput?.Close();

                    if (exitStatus != "0")
                    {
                        return Tuple.Create(-1, -1);
                    }

                    int lowPortRange = int.Parse(startPort);
                    int highPortRange = lowPortRange + int.Parse(portCount);

                    return Tuple.Create(lowPortRange, highPortRange);
                }
                catch (ArgumentException)
                {
                }
                catch (IOException)
                {
                }
                catch (Win32Exception)
                {
                }

                return Tuple.Create(-1, -1);
            }
        }

        internal static int GetActiveEphemeralPortCount(
                                int procId = -1,
                                Protocol protocol = Protocol.TCP)
        {
            try
            {
                // Unsupported by underlying API. Could throw an ArgumentException here if that makes you happy...
                if (protocol == Protocol.None)
                {
                    return -1;
                }

                string protoParam = Enum.GetName(protocol.GetType(), protocol)?.ToLower();

                using (var p = new Process())
                {
                    var ps = new ProcessStartInfo
                    {
                        Arguments = $"/c netstat -ano -p {protoParam}",
                        FileName = $"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\\cmd.exe",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    p.StartInfo = ps;
                    p.Start();
                    var stdOutput = p.StandardOutput;

                    var ephemeralPortList = new List<string>();

                    foreach (var portRow in stdOutput?.ReadToEnd().Split(
                        new string[] { "\r", "\n" },
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (portRow.ToLower().Contains(protoParam))
                        {
                            if (procId > -1 && portRow.LastIndexOf(procId.ToString()) < 0)
                            {
                                continue;
                            }

                            ephemeralPortList.Add(portRow);
                        }
                    }

                    var exitStatus = p.ExitCode.ToString();
                    stdOutput?.Close();

                    if (exitStatus != "0")
                    {
                        return -1;
                    }

                    int ephemeralPortsInUse = 0;
                    Tuple<int, int> portRange = TupleGetDynamicPortRange(protocol);
                    int lowPortRange = portRange.Item1;
                    int highPortRange = portRange.Item2;

                    // Compute count of active ports in dynamic range...
                    foreach (string line in ephemeralPortList)
                    {
                        int port = GetPortNumberFromConsoleOutputRow(line, protoParam);

                        if (port >= lowPortRange && port <= highPortRange)
                        {
                            ephemeralPortsInUse++;
                        }
                    }

                    return ephemeralPortsInUse;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            return -1;
        }

        internal static Tuple<int, int> TupleGetFabricApplicationPortRangeForNodeType(string nodeType, string clusterManifestXml)
        {
            if (string.IsNullOrEmpty(nodeType) || string.IsNullOrEmpty(clusterManifestXml))
            {
                return Tuple.Create(-1, -1);
            }

            try
            {
                // Safe XML pattern - *Do not use LoadXml*...
                var xdoc = new XmlDocument { XmlResolver = null };
                var sreader = new StringReader(clusterManifestXml);
                var reader = XmlReader.Create(sreader, new XmlReaderSettings() { XmlResolver = null });
                xdoc.Load(reader);

                // Cluster Information...
                var nsmgr = new XmlNamespaceManager(xdoc.NameTable);
                nsmgr.AddNamespace("sf", "http://schemas.microsoft.com/2011/01/fabric");

                // Application Port Range...
                var endpointsNodeList = xdoc.SelectNodes($"//sf:NodeTypes//sf:NodeType[@Name='{nodeType}']//sf:Endpoints", nsmgr);
                Tuple<int, int> ret = Tuple.Create(-1, -1);

                foreach (XmlNode node in endpointsNodeList)
                {
                    ret = Tuple.Create(int.Parse(node.ChildNodes[6].Attributes.Item(0).Value), int.Parse(node.ChildNodes[6].Attributes.Item(1).Value));
                }

                reader?.Dispose();

                return ret;
            }
            catch (XmlException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NullReferenceException)
            {
            }

            return Tuple.Create(-1, -1);
        }

        internal static int GetActiveFirewallRulesCount()
        {
            ManagementObjectCollection results = null;
            ManagementObjectSearcher searcher = null;
            int count = -1;

            try
            {
                var scope = new ManagementScope("\\\\.\\ROOT\\StandardCimv2");
                var q = new ObjectQuery("SELECT * FROM MSFT_NetFirewallRule WHERE Enabled=1");
                searcher = new ManagementObjectSearcher(scope, q);
                results = searcher.Get();
                count = results.Count;
            }
            catch (ManagementException)
            {
            }
            finally
            {
                results?.Dispose();
                searcher?.Dispose();
            }

            return count;
        }

        private static int GetPortNumberFromConsoleOutputRow(string row, string protoParam)
        {
            try
            {
                return int.Parse(row.ToLower().Replace(protoParam, string.Empty).Trim()
                          .Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries)[0]
                          .Split(':')[1]);
            }
            catch (ArgumentException)
            {
            }
            catch (FormatException)
            {
            }

            return -1;
        }
    }

    internal enum Protocol
    {
        None,
        TCP,
        UDP,
    }
}
