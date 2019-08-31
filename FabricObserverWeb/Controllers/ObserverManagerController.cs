// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.AspNetCore.Mvc;

namespace FabricObserver
{
    [Route("api/ObserverManager")]
    [Produces("text/html")]
    [ApiController]
    public class ObserverManagerController : ControllerBase
    {
        private const int MaxRetries = 3;
        StringBuilder sb = null;
        private readonly StatelessServiceContext ServiceContext = null;
        private FabricClient fabricClient;

        string script = @"
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

        public ObserverManagerController(StatelessServiceContext serviceContext, FabricClient fabricClient)
        {
            this.ServiceContext = serviceContext;
            this.fabricClient = fabricClient;
        }

        // GET: api/ObserverManager
        [Produces("text/html")]
        [HttpGet]
        public string Get()
        {
            string html = "";
            string observerLogFilePath = null;
            var nodeName = ServiceContext.NodeContext.NodeName;
            var configSettings = this.ServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings;
            string logFolder = null;
            string logFileName = null;

            // Windows only for now...
            if (configSettings != null) 
            {
                string windrive = Environment.SystemDirectory.Substring(0, 3);
                logFolder = Utilities.GetConfigurationSetting(configSettings, "FabricObserverLogs", "ObserverLogBaseFolderPath");
                logFileName = Utilities.GetConfigurationSetting(configSettings, "FabricObserverLogs", "ObserverManagerLogFileName");
                observerLogFilePath = Path.Combine(logFolder, logFileName);
            }

            // Implicit retry loop. Will run only once if no exceptions arise. 
            // Can only run at most MaxRetries times.
            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    var netFileInfoPath = observerLogFilePath.Replace(@"ObserverManager\ObserverManager.log", "NetInfo.txt");
                    var sysFileInfoPath = observerLogFilePath.Replace(@"ObserverManager\ObserverManager.log", "SysInfo.txt");
                    var evtVwrErrorLogPath = observerLogFilePath.Replace(@"ObserverManager\ObserverManager.log", "EventVwrErrors.txt");
                    var diskFileInfoPath = observerLogFilePath.Replace(@"ObserverManager\ObserverManager.log", "disks.txt");
                    var sfInfraInfoPath = observerLogFilePath.Replace(@"ObserverManager\ObserverManager.log", "SFInfraInfo.txt");

                    // These are the app-specific 
                    var currentDataHealthLogPathPart = observerLogFilePath.Replace(@"ObserverManager\ObserverManager.log", "apps");
                    string sysInfofileText = "", evtVwrErrorsText = "", log = "", diskInfoTxt = "", appHealthText = "", sfInfraText = "", netInfofileText = "";
                    bool hasSysInfo = false;

                    // Only show info from current day, by default. This is just for web UI (html).
                    if (System.IO.File.Exists(observerLogFilePath) 
                        && System.IO.File.GetCreationTimeUtc(observerLogFilePath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        log = System.IO.File.ReadAllText(observerLogFilePath, Encoding.UTF8).Replace("\n", "<br/>");
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

                    appHealthText = "";
                    if (Directory.Exists(currentDataHealthLogPathPart))
                    {
                        var currentAppDataLogFiles = Directory.GetFiles(currentDataHealthLogPathPart);
                        if (currentAppDataLogFiles.Length > 0)
                        {
                            foreach (var file in currentAppDataLogFiles)
                            {
                                appHealthText += System.IO.File.ReadAllText(file, Encoding.UTF8).Replace("\n", "<br/>");
                            }
                        }
                    }

                    // Node links..
                    string nodeLinks = "";

                    var nodeList = this.fabricClient.QueryManager.GetNodeListAsync().Result;
                    var ordered = nodeList.OrderBy(node => node.NodeName);
                    var host = Request.Host.Value;

                    // Request originating from ObserverWeb node hyperlinks...
                    if (Request.QueryString.HasValue && Request.Query.ContainsKey("fqdn"))
                    {
                        host = Request.Query["fqdn"];

                        foreach (var node in ordered)
                        {
                            nodeLinks += "| <a href='" + Request.Scheme + "://" + host + "/api/ObserverManager/" + node.NodeName + "'>" + node.NodeName + "</a> | ";
                        }
                    }

                    sb = new StringBuilder();

                    sb.AppendLine("<html>\n\t<head>");
                    sb.AppendLine("\n\t\t<title>FabricObserver Node Health Information: Errors and Warnings</title>");
                    sb.AppendLine("\n\t\t" + script);
                    sb.AppendLine("\n\t\t<style type=\"text/css\">\n" +
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
                    sb.AppendLine("\n\t</head>");
                    sb.AppendLine("\n\t<body>");
                    sb.AppendLine("\n\t\t\t <br/>");
                    if (!hasSysInfo && !string.IsNullOrEmpty(sysInfofileText))
                    {
                        sb.AppendLine("\n\t\t\t<div class=\"container\"><div style=\"position: relative; width: 80%; margin-left: auto; margin-right: auto; font-family: Consolas;\"><br/>" + 
                                       "<h2>Host Machine and Service Fabric Information: Node " + ServiceContext.NodeContext.NodeName + "</h2>" + nodeLinks + "<pre>" + 
                                       sysInfofileText + "\n\nDisk Info: \n\n" + diskInfoTxt + netInfofileText + "\n\n" + sfInfraText + "</pre></div></div>");

                        hasSysInfo = true;
                    }
                    sb.AppendLine("\n\t\t\t\t<div class=\"container\"><div style=\"position: relative; width: 100%; margin-left: auto; margin-right: auto;\">" +
                                  "<br/><strong>Daily Errors and Warnings on " + nodeName + " - " + DateTime.UtcNow.ToString("MM/dd/yyyy") + " UTC</strong><br/><br/>" + log + appHealthText + "</div>");

                    if (!string.IsNullOrEmpty(evtVwrErrorsText))
                    {
                        sb.AppendLine("\n\t\t\t" + evtVwrErrorsText);
                    }

                    sb.AppendLine("\n\t\t\t</div>");
                    sb.AppendLine("\n\t</body>");
                    sb.AppendLine("</html>");
                    html = sb.ToString();
                    sb.Clear();

                    break;
                }
                catch (IOException e)
                {
                    html = e.ToString();
                }

                // If we get here, let's wait a few seconds before the next iteration...
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
                var node = this.fabricClient.QueryManager.GetNodeListAsync(name).Result;

                if (node.Count > 0)
                {
                    var addr = node[0].IpAddressOrFQDN;

                    // Only use this API over SSL, but not in LRC...
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
                    StreamReader reader = new StreamReader(dataStream);
                    string responseFromServer = reader.ReadToEnd();
                    var ret = responseFromServer;

                    // Cleanup the streams and the response...
                    reader.Close();
                    dataStream.Close();
                    response.Close();

                    return ret;
                }
                else
                {
                    return "no node found with that name...";
                }
            }
            catch (Exception e)
            {
                return e.ToString();
            }
        }
    }
}