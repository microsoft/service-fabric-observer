// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace FabricObserverWeb
{
    [Route("api/ObserverManager")]
    [Produces("text/html")]
    [ApiController]
    public class ObserverManagerController : ControllerBase
    {
        private const int MaxRetries = 3;
        private readonly StatelessServiceContext serviceContext;
        private readonly FabricClient fabricClient;
        private const string script = @"
                <script type='text/javascript'>
                function toggle(e) {
                    var container = document.getElementById(e);
                    var plusMarker = document.getElementById('plus');
                   
                    if (container === null)
                        return;
                    if (container.style.display === 'none') {
                        container.style.display = 'block';
                        plusMarker.innerText = '-';
                        window.scrollBy(0, 350);
                    } else {
                        container.style.display = 'none';
                        plusMarker.innerText = '+';
                    }
                }
                </script>";

        private StringBuilder sb;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManagerController"/> class.
        /// </summary>
        /// <param name="serviceContext">service context.</param>
        /// <param name="fabricClient">FabricClient instance.</param>
        public ObserverManagerController(StatelessServiceContext serviceContext, FabricClient fabricClient)
        {
            this.serviceContext = serviceContext;
            this.fabricClient = fabricClient;
        }

        // GET: api/ObserverManager
        [Produces("text/html")]
        [HttpGet]
        public string Get()
        {
            string html = string.Empty;
            string nodeName = serviceContext.NodeContext.NodeName;
            var configSettings = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings;
            string logFolder = null;
            string obsManagerLogFilePath = null;

            if (configSettings != null)
            {
                logFolder = Utilities.GetConfigurationSetting(configSettings, "FabricObserverLogs", "FabricObserverLogFolderName");
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Add current drive letter if not supplied for Windows path target.
                    if (!logFolder[..3].Contains(":\\"))
                    {
                        string windrive = Environment.SystemDirectory[..3];
                        logFolder = Path.Combine(windrive, logFolder);
                    }
                }
                else
                {
                    // Remove supplied drive letter if Linux is the runtime target.
                    if (logFolder[..3].Contains(":\\"))
                    {
                        logFolder = logFolder.Remove(0, 3);
                    }
                }

                var obsManagerLogFolderPath = Path.Combine(logFolder, "ObserverManager");
                obsManagerLogFilePath = Path.Combine(obsManagerLogFolderPath, "ObserverManager.log");
            }

            // Implicit retry loop. Will run only once if no exceptions arise.
            // Can only run at most MaxRetries times.
            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    string netFileInfoPath = Path.Combine(logFolder, "NetInfo.txt");
                    string sysFileInfoPath = Path.Combine(logFolder, "SysInfo.txt");
                    string evtVwrErrorLogPath = Path.Combine(logFolder, "EventVwrErrors.txt");
                    string diskFileInfoPath = Path.Combine(logFolder, "disks.txt");
                    string sfInfraInfoPath = Path.Combine(logFolder, "SFInfraInfo.txt");
                    string sysInfofileText = string.Empty, evtVwrErrorsText = string.Empty, log = string.Empty, diskInfoTxt = string.Empty, appHealthText = string.Empty, sfInfraText = string.Empty, netInfofileText = string.Empty;

                    // Only show info from current day, by default. This is just for web UI (html).
                    if (System.IO.File.Exists(obsManagerLogFilePath)
                        && System.IO.File.GetCreationTimeUtc(obsManagerLogFilePath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        log = System.IO.File.ReadAllText(obsManagerLogFilePath, Encoding.UTF8).Replace("\n", "<br/>");
                    }

                    if (System.IO.File.Exists(netFileInfoPath))
                    {
                        netInfofileText = "\n\n" + System.IO.File.ReadAllText(netFileInfoPath, Encoding.UTF8).Trim();
                    }

                    if (System.IO.File.Exists(sysFileInfoPath))
                    {
                        sysInfofileText = System.IO.File.ReadAllText(sysFileInfoPath, Encoding.UTF8).Trim();
                    }

                    if (System.IO.File.Exists(evtVwrErrorLogPath))
                    {
                        evtVwrErrorsText = System.IO.File.ReadAllText(evtVwrErrorLogPath).Replace("\n", "<br/>");
                    }

                    if (System.IO.File.Exists(diskFileInfoPath))
                    {
                        diskInfoTxt = System.IO.File.ReadAllText(diskFileInfoPath, Encoding.UTF8).Trim();
                    }

                    if (System.IO.File.Exists(sfInfraInfoPath))
                    {
                        sfInfraText = System.IO.File.ReadAllText(sfInfraInfoPath, Encoding.UTF8).Trim();
                    }

                    // Node links..
                    string nodeLinks = string.Empty;
                    var nodeList = fabricClient.QueryManager.GetNodeListAsync().Result;
                    var ordered = nodeList.OrderBy(node => node.NodeName);
                    string host = Request.Host.Value;

                    // Request originating from ObserverWeb node hyperlinks.
                    if (Request.QueryString.HasValue && Request.Query.ContainsKey("fqdn"))
                    {
                        host = Request.Query["fqdn"];

                        foreach (var node in ordered)
                        {
                            nodeLinks += "| <a href='" + Request.Scheme + "://" + host + "/api/ObserverManager/" + node.NodeName + "'>" + node.NodeName + "</a> | ";
                        }
                    }

                    sb = new StringBuilder();

                    _ = sb.AppendLine("<html>\n\t<head>");
                    _ = sb.AppendLine("\n\t\t<title>FabricObserver Node Health Information: Errors and Warnings</title>");
                    _ = sb.AppendLine("\n\t\t" + script);
                    _ = sb.AppendLine("\n\t\t<style type=\"text/css\">\n" +
                                   "\t\t\t.container {\n" +
                                   "\t\t\t\tfont-family: Consolas; font-size: 14px; background-color: lightblue; padding: 5px; border: 1px solid grey; " +
                                   "width: 98%;\n" +
                                   "\t\t\t}\n" +
                                   "\t\t\t.header {\n" +
                                   "\t\t\t\tfont-size: 25px; text-align: center; background-color: lightblue; " +
                                   "padding 5px; margin-bottom: 10px;\n" +
                                   "\t\t\t}\n" +
                                   "\t\t\tpre {\n" +
                                   "\t\t\t\toverflow-x: auto; white-space: pre-wrap; white-space: -moz-pre-wrap; white-space: -pre-wrap; white-space: -o-pre-wrap; word-wrap: break-word;" +
                                   "\t\t\t}\n" +
                                   "\t\t\t a:link { text-decoration: none; }" +
                                   "\n\t\t</style>");
                    _ = sb.AppendLine("\n\t</head>");
                    _ = sb.AppendLine("\n\t<body>");
                    _ = sb.AppendLine("\n\t\t\t <br/>");

                    if (!string.IsNullOrEmpty(sysInfofileText))
                    {
                        _ = sb.AppendLine("\n\t\t\t<div class=\"container\"><div style=\"position: relative; width: 80%; margin-left: auto; margin-right: auto; font-family: Consolas;\"><br/>" +
                                       "<h2>Host Machine and Service Fabric Information: Node " + serviceContext.NodeContext.NodeName + "</h2>" + nodeLinks + "<pre>" +
                                       sysInfofileText + "\n\nDisk Info: \n\n" + diskInfoTxt + netInfofileText + "\n\n" + sfInfraText + "</pre></div></div>");
                    }

                    _ = sb.AppendLine("\n\t\t\t\t<div class=\"container\"><div style=\"position: relative; width: 100%; margin-left: auto; margin-right: auto;\">" +
                                       "<br/><strong>Daily Errors and Warnings on " + nodeName + " - " + DateTime.UtcNow.ToString("MM/dd/yyyy") + " UTC</strong><br/><br/>" + log + appHealthText + "</div>");

                    if (!string.IsNullOrEmpty(evtVwrErrorsText))
                    {
                        _ = sb.AppendLine("\n\t\t\t" + evtVwrErrorsText);
                    }

                    _ = sb.AppendLine("\n\t\t\t</div>");
                    _ = sb.AppendLine("\n\t</body>");
                    _ = sb.AppendLine("</html>");
                    html = sb.ToString();
                    _ = sb.Clear();

                    break;
                }
                catch (IOException e)
                {
                    html = e.ToString();
                }

                // If we get here, let's wait a few seconds before the next iteration.
                Task.Delay(2000).Wait();
            }

            return html;
        }

        // GET: api/ObserverManager/_SFRole_0
        [HttpGet("{name}", Name = "GetNode")]
        public string Get(string name)
        {
            try
            {
                var node = fabricClient.QueryManager.GetNodeListAsync(name).Result;

                if (node.Count <= 0)
                {
                    return "no node found with that name.";
                }

                var addr = node[0].IpAddressOrFQDN;

                // Only use this API over SSL, but not in LRC.
                if (!addr.Contains("http://"))
                {
                    addr = "http://" + addr;
                }

                string fqdn = "?fqdn=" + Request.Host;
                var req = WebRequest.Create(addr + ":5000/api/ObserverManager" + fqdn);
                req.Credentials = CredentialCache.DefaultCredentials;
                var response = (HttpWebResponse)req.GetResponse();
                Stream dataStream = response.GetResponseStream();

                // Open the stream using a StreamReader for easy access.
                StreamReader reader = new StreamReader(dataStream ?? throw new InvalidOperationException("Get: dataStream is null."));
                string responseFromServer = reader.ReadToEnd();
                var ret = responseFromServer;

                // Cleanup the streams and the response.
                reader.Close();
                dataStream.Close();
                response.Close();

                return ret;
            }
            catch (Exception e)
            {
                return e.ToString();
            }
        }
    }
}