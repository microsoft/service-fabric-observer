// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Query;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace FabricObserverWeb.Controllers
{
    [Route("api/ObserverLog")]
    [Produces("application/json", "application/html")]
    [ApiController]
    public class ObserverLogController : Controller
    {
        private const int MaxRetries = 3;
        private readonly FabricClient fabricClient;
        private readonly StatelessServiceContext serviceContext;
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
        /// Initializes a new instance of the <see cref="ObserverLogController"/> class.
        /// </summary>
        /// <param name="serviceContext">service context.</param>
        /// <param name="fabricClient">FabricClient instance.</param>
        public ObserverLogController(StatelessServiceContext serviceContext, FabricClient fabricClient)
        {
            this.serviceContext = serviceContext;
            this.fabricClient = fabricClient;
        }

        // GET: api/ObserverLog/DiskObserver/html returns html output.
        // GET: api/ObserverLog/DiskObserver returns json output.
        [HttpGet("{name}/{format?}", Name = "GetObserverLogFormatted")]
        public ActionResult Get(string name, string format = "json")
        {
            if (format.ToLower() == "html")
            {
                return Content(GetHtml(name), "text/html");
            }

            // Note: FO produces Json output for health report messages/logs..
            ContentResult ret = Content(string.Empty);

            string networkObserverLogText = null, osObserverLogText = null, nodeObserverLogText = null, appObserverLogText = null, certObserverLogText = null, fabricSystemObserverLogText = null, diskObserverLogText = null;
            string logFolder = string.Empty;
            var configSettings = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings;

            try
            {
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
                }

                if (!Directory.Exists(logFolder))
                {
                    throw new ArgumentException($"Specified log folder, {logFolder}, does not exist.");
                }
            }
            catch (Exception e)
            {
                ret = Content(e.ToString());
                return ret;
            }

            // Observer log paths.
            string appObserverLogPath = Path.Combine(logFolder, "AppObserver", "AppObserver.log");
            string certObserverLogPath = Path.Combine(logFolder, "CertificateObserver", "CertificateObserver.log");
            string osObserverLogPath = Path.Combine(logFolder, "OSObserver", "OSObserver.log");
            string diskObserverLogPath = Path.Combine(logFolder, "DiskObserver", "DiskObserver.log");
            string networkObserverLogPath = Path.Combine(logFolder, "NetworkObserver", "NetworkObserver.log");
            string fabricSystemObserverLogPath = Path.Combine(logFolder, "FabricSystemObserver", "FabricSystemObserver.log");
            string nodeObserverLogPath = Path.Combine(logFolder, "NodeObserver", "NodeObserver.log");

            // Implicit retry loop. Will run only once if no exceptions arise.
            // Can only run at most MaxRetries times.
            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    if (System.IO.File.Exists(appObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(appObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        appObserverLogText = System.IO.File.ReadAllText(appObserverLogPath, Encoding.UTF8);
                    }

                    if (System.IO.File.Exists(certObserverLogPath)
                       && System.IO.File.GetCreationTimeUtc(certObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        certObserverLogText = System.IO.File.ReadAllText(certObserverLogPath, Encoding.UTF8);
                    }

                    if (System.IO.File.Exists(diskObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(diskObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        diskObserverLogText = System.IO.File.ReadAllText(diskObserverLogPath, Encoding.UTF8);
                    }

                    if (System.IO.File.Exists(fabricSystemObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(fabricSystemObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        fabricSystemObserverLogText = System.IO.File.ReadAllText(fabricSystemObserverLogPath, Encoding.UTF8);
                    }

                    if (System.IO.File.Exists(networkObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(networkObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        networkObserverLogText = System.IO.File.ReadAllText(networkObserverLogPath, Encoding.UTF8);
                    }

                    if (System.IO.File.Exists(nodeObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(nodeObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        nodeObserverLogText = System.IO.File.ReadAllText(nodeObserverLogPath, Encoding.UTF8);
                    }

                    if (System.IO.File.Exists(osObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(osObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        osObserverLogText = System.IO.File.ReadAllText(osObserverLogPath, Encoding.UTF8);
                    }

                    string reportItems = string.Empty;

                    switch (name.ToLower())
                    {
                        case "appobserver":
                            reportItems = GetObserverErrWarnLogEntryListFromLogText(appObserverLogText);
                            break;

                        case "certificateobserver":
                            reportItems = GetObserverErrWarnLogEntryListFromLogText(certObserverLogText);
                            break;

                        case "diskobserver":
                            reportItems = GetObserverErrWarnLogEntryListFromLogText(diskObserverLogText);
                            break;

                        case "fabricsystemobserver":
                            reportItems = GetObserverErrWarnLogEntryListFromLogText(fabricSystemObserverLogText);
                            break;

                        case "networkobserver":
                            reportItems = GetObserverErrWarnLogEntryListFromLogText(networkObserverLogText);
                            break;

                        case "nodeobserver":
                             reportItems = GetObserverErrWarnLogEntryListFromLogText(nodeObserverLogText);
                            break;

                        case "osobserver":
                             reportItems = GetObserverErrWarnLogEntryListFromLogText(osObserverLogText); 
                            break;

                        default:
                            return Json("Data for specified Observer, " + name + ", does not exist.");
                    }

                    ret = Content(reportItems);
                    break;
                }
                catch (IOException)
                {
                }

                // If we get here, let's wait a second before the next iteration.
                Task.Delay(1000).Wait();
            }

            return ret;
        }

        // This only makes sense if you enable communication between nodes and/or over the Internet
        // over a secure channel. By default, this API service is node-local (node-only) with no comms outside of VM.
        // GET: api/ObserverLog/DiskObserver/_SFRole_0
        // GET: api/ObserverLog/DiskObserver/_SFRole_0/html
        [HttpGet("{observername}/{nodename}/{format}", Name = "GetObserverLogNode")]
        public ContentResult Get(string observername, string nodename, string format)
        {
            return string.IsNullOrWhiteSpace(format) ? Content("Error processing request: format is not specified. Supported values are \"html\" or \"json\" e.g., api/ObserverLog/DiskObserver/_SFRole_0/html") : GetContentResult(observername, nodename, format);
        }

        [HttpGet("{observername}/{nodename}/{format?}", Name = "GetObserverLogNode")]
        private ContentResult GetContentResult(string observername, string nodename, string format = "json")
        {
            try
            {
                var nodeList = fabricClient.QueryManager.GetNodeListAsync(nodename).GetAwaiter().GetResult();

                if (nodeList.Count <= 0)
                {
                    return Content("no node found with that name.");
                }

                string addr = nodeList[0].IpAddressOrFQDN;

                if (!addr.Contains(".") || nodeList.Count == 1)
                {
                    addr = "localhost";
                }

                // By default this service is node-local, http, port 5000.
                // If you modify the service to support Internet communication over a
                // secure channel, then change this code to force https.
                if (!addr.Contains("http://"))
                {
                    addr = "http://" + addr;
                }

                string fqdn = "?fqdn=" + Request.Host;

                // If you modify the service to support Internet communication over a
                // secure channel, then change this code to reflect the correct port.
                WebRequest req = WebRequest.Create(addr + $":5000/api/ObserverLog/{observername}/{format}{fqdn}");
                req.Credentials = CredentialCache.DefaultCredentials;
                HttpWebResponse response = (HttpWebResponse) req.GetResponse();
                Stream dataStream = response.GetResponseStream();

                // Open the stream using a StreamReader for easy access.
                StreamReader reader = new StreamReader(dataStream ?? throw new InvalidOperationException("Get: dataStream is null"));
                string responseFromServer = reader.ReadToEnd();
                string ret = responseFromServer;

                // Cleanup the streams and the response.
                reader.Close();
                dataStream.Close();
                response.Close();

                return Content(ret, format.ToLower() == "html" ? "text/html" : "text/json");

            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is IOException)
            {
                return Content($"Error processing request: {e}");
            }
        }

        private string GetHtml(string name)
        {
            string html = string.Empty;
            string nodeName = serviceContext.NodeContext.NodeName;
            ConfigurationSettings configSettings = serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings;
            string networkObserverLogText = null, osObserverLogText = null, nodeObserverLogText = null, appObserverLogText = null, fabricSystemObserverLogText = null, diskObserverLogText = null;
            string logFolder = string.Empty;

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
            }

            if (!Directory.Exists(logFolder))
            {
                throw new ArgumentException($"Specified log folder, {logFolder}, does not exist.");
            }

            // Observer log paths.
            string appObserverLogPath = Path.Combine(logFolder, "AppObserver", "AppObserver.log");
            string osObserverLogPath = Path.Combine(logFolder, "OSObserver", "OSObserver.log");
            string diskObserverLogPath = Path.Combine(logFolder, "DiskObserver", "DiskObserver.log");
            string networkObserverLogPath = Path.Combine(logFolder, "NetworkObserver", "NetworkObserver.log");
            string fabricSystemObserverLogPath = Path.Combine(logFolder, "FabricSystemObserver", "FabricSystemObserver.log");
            string nodeObserverLogPath = Path.Combine(logFolder, "NodeObserver", "NodeObserver.log");

            // Implicit retry loop. Will run only once if no exceptions arise.
            // Can only run at most MaxRetries times.
            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    if (System.IO.File.Exists(appObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(appObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        appObserverLogText = System.IO.File.ReadAllText(appObserverLogPath, Encoding.UTF8).Replace(Environment.NewLine, "<br/>");
                    }

                    if (System.IO.File.Exists(diskObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(diskObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        diskObserverLogText = System.IO.File.ReadAllText(diskObserverLogPath, Encoding.UTF8).Replace(Environment.NewLine, "<br/>");
                    }

                    if (System.IO.File.Exists(fabricSystemObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(fabricSystemObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        fabricSystemObserverLogText = System.IO.File.ReadAllText(fabricSystemObserverLogPath, Encoding.UTF8).Replace(Environment.NewLine, "<br/>");
                    }

                    if (System.IO.File.Exists(networkObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(networkObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        networkObserverLogText = System.IO.File.ReadAllText(networkObserverLogPath, Encoding.UTF8).Replace(Environment.NewLine, "<br/>");
                    }

                    if (System.IO.File.Exists(nodeObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(nodeObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        nodeObserverLogText = System.IO.File.ReadAllText(nodeObserverLogPath, Encoding.UTF8).Replace(Environment.NewLine, "<br/>");
                    }

                    if (System.IO.File.Exists(osObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(osObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        osObserverLogText = System.IO.File.ReadAllText(osObserverLogPath, Encoding.UTF8).Trim();
                    }

                    string host = Request.Host.Value;

                    string nodeLinks = string.Empty;

                    // Request originating from ObserverWeb node hyperlinks.
                    if (Request.QueryString.HasValue && Request.Query.ContainsKey("fqdn"))
                    {
                        host = Request.Query["fqdn"];

                        // Node links.
                        NodeList nodeList = fabricClient.QueryManager.GetNodeListAsync().Result;
                        IOrderedEnumerable<Node> ordered = nodeList.OrderBy(node => node.NodeName);

                        foreach (Node node in ordered)
                        {
                            nodeLinks += "| <a href='" + Request.Scheme + "://" + host + "/api/ObserverLog/" + name + "/" + node.NodeName + "/html'>" + node.NodeName + "</a> | ";
                        }
                    }

                    sb = new StringBuilder();

                    _ = sb.AppendLine("<html>\n\t<head>");
                    _ = sb.AppendLine("\n\t\t<title>FabricObserver Observer Report: Errors and Warnings</title>");
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
                    _ = sb.AppendLine("\n\t\t\t\t<div class=\"container\"><div style=\"position: relative; width: 100%; margin-left: auto; margin-right: auto;\"><br/><strong>Errors and Warnings for " + name + " on " + nodeName + "</strong><br/><br/>" + nodeLinks);

                    switch (name.ToLower())
                    {
                        case "appobserver":
                            if (!string.IsNullOrEmpty(appObserverLogText))
                            {
                                _ = sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + appObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "diskobserver":
                            if (!string.IsNullOrEmpty(diskObserverLogText))
                            {
                                _ = sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + diskObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "fabricsystemobserver":
                            if (!string.IsNullOrEmpty(fabricSystemObserverLogText))
                            {
                                _ = sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + fabricSystemObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "networkobserver":
                            if (!string.IsNullOrEmpty(networkObserverLogText))
                            {
                                _ = sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + networkObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "nodeobserver":
                            if (!string.IsNullOrEmpty(nodeObserverLogText))
                            {
                                _ = sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + nodeObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "osobserver":
                            if (!string.IsNullOrEmpty(osObserverLogText))
                            {
                                _ = sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + osObserverLogText + "<br/><br/>");
                            }

                            break;
                        default:
                            _ = sb.AppendLine("\n\t\t\t<br/>Specified Observer, " + name + ", does not exist.");
                            break;
                    }

                    _ = sb.AppendLine("\n\t\t\t</div>");
                    _ = sb.AppendLine("\n\t</body>");
                    _ = sb.AppendLine("</html>");
                    html = sb.ToString();
                    _ = sb.Clear();
                    break;
                }
                catch (IOException ie)
                {
                    html = ie.ToString();
                }

                // If we get here, let's wait a few seconds before the next iteration.
                Task.Delay(2000).Wait();
            }

            return html;
        }

        private static string GetObserverErrWarnLogEntryListFromLogText(string observerLogText)
        {
            if (string.IsNullOrEmpty(observerLogText))
            {
                var ret = new ObserverLogEntry
                {
                    Date = DateTime.UtcNow.ToString("MM-dd-yyyy HH:mm:ss.ffff", CultureInfo.InvariantCulture),
                    HealthState = "Ok",
                    Message = string.Empty
                };

                return JsonSerializer.Serialize(ret);
            }

            string[] logArray = observerLogText.Split($"{Environment.NewLine}", StringSplitOptions.RemoveEmptyEntries);
            string entry = "[";

            foreach (string item in logArray)
            {
                
                if (item.Contains("--INFO") || !(item.Contains("--WARN") || item.Contains("--ERROR")))
                {
                    continue;
                }

                string[] arr = item.Split("--", StringSplitOptions.RemoveEmptyEntries);

                // Note: This is already Json (it's a serialized instance of FO's TelemetryData type)..
                // It could be the case that an WARN or ERROR entry does not contain JSON (like an observer unhandled exception), which would crash the below code, thus try-catch.
                try
                {
                    entry += arr[2][arr[2].IndexOf("{", StringComparison.Ordinal)..] + ",";
                }
                catch (ArgumentException)
                {
                }
            }

            entry = entry.TrimEnd(',');
            entry += "]";

            return entry;
        }
    }
}