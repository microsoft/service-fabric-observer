namespace FabricObserverWeb.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;

    [Route("api/ObserverLog")]
    [Produces("application/json", "application/html")]
    [ApiController]
    public class ObserverLogController : Controller
    {
        private const int MaxRetries = 3;
        private readonly FabricClient fabricClient;
        private readonly StatelessServiceContext serviceContext = null;
        private StringBuilder sb = null;

        private string script = @"
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

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverLogController"/> class.
        /// </summary>
        /// <param name="serviceContext">service context...</param>
        /// <param name="fabricClient">FabricClient instance...</param>
        public ObserverLogController(StatelessServiceContext serviceContext, FabricClient fabricClient)
        {
            this.serviceContext = serviceContext;
            this.fabricClient = fabricClient;
        }

        // GET: api/ObserverLog/DiskObserver/html returns html output...
        // GET: api/ObserverLog/DiskObserver returns json output...
        [HttpGet("{name}/{format?}", Name = "GetObserverLogFormatted")]
        public ActionResult Get(string name, string format = "json")
        {
            if (format.ToLower() == "html")
            {
                return this.Content(this.GetHtml(name), "text/html");
            }

            JsonResult ret = this.Json(new ObserverLogEntry
            {
                Date = DateTime.UtcNow.ToString(
                "MM-dd-yyyy HH:mm:ss.ffff",
                CultureInfo.InvariantCulture),
                HealthState = "Ok",
                Message = string.Empty,
            });

            string networkObserverLogText = null, osObserverLogText = null, nodeObserverLogText = null, appObserverLogText = null, fabricSystemObserverLogText = null, diskObserverLogText = null;
            string observerLogFilePath = null;

            try
            {
                var configSettings = this.serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings;
                string logFolder, logFileName;

                if (configSettings != null)
                {
                    logFolder = Utilities.GetConfigurationSetting(configSettings, "FabricObserverLogs", "ObserverLogBaseFolderPath");

                    if (!Directory.Exists(logFolder))
                    {
                        throw new ArgumentException($"Specified log folder, {logFolder}, does not exist");
                    }

                    logFileName = Utilities.GetConfigurationSetting(configSettings, "FabricObserverLogs", "ObserverManagerLogFileName");
                    observerLogFilePath = Path.Combine(logFolder, logFileName);
                }
            }
            catch (Exception e)
            {
                ret = this.Json(e.ToString());
                return ret;
            }

            // Implicit retry loop. Will run only once if no exceptions arise.
            // Can only run at most MaxRetries times.
            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    // Observer log paths...
                    var appObserverLogPath = observerLogFilePath.Replace("ObserverManager", "AppObserver");
                    var osObserverLogPath = observerLogFilePath.Replace("ObserverManager", "OSObserver");
                    var diskObserverLogPath = observerLogFilePath.Replace("ObserverManager", "DiskObserver");
                    var networkObserverLogPath = observerLogFilePath.Replace("ObserverManager", "NetworkObserver");
                    var fabricSystemObserverLogPath = observerLogFilePath.Replace("ObserverManager", "FabricSystemObserver");
                    var nodeObserverLogPath = observerLogFilePath.Replace("ObserverManager", "NodeObserver");

                    // Observer logs...
                    if (System.IO.File.Exists(appObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(appObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        appObserverLogText = System.IO.File.ReadAllText(appObserverLogPath, Encoding.UTF8);
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

                    switch (name.ToLower())
                    {
                        case "appobserver":
                            if (!string.IsNullOrEmpty(appObserverLogText))
                            {
                                var reportItems = this.GetObserverErrWarnLogEntryListFromLogText(appObserverLogText);

                                ret = this.Json(reportItems);
                            }

                            break;
                        case "diskobserver":
                            if (!string.IsNullOrEmpty(diskObserverLogText))
                            {
                                var reportItems = this.GetObserverErrWarnLogEntryListFromLogText(diskObserverLogText);

                                ret = this.Json(reportItems);
                            }

                            break;
                        case "fabricsystemobserver":
                            if (!string.IsNullOrEmpty(fabricSystemObserverLogText))
                            {
                                var reportItems = this.GetObserverErrWarnLogEntryListFromLogText(fabricSystemObserverLogText);

                                ret = this.Json(reportItems);
                            }

                            break;
                        case "networkobserver":
                            if (!string.IsNullOrEmpty(networkObserverLogText))
                            {
                                var reportItems = this.GetObserverErrWarnLogEntryListFromLogText(networkObserverLogText);

                                ret = this.Json(reportItems);
                            }

                            break;
                        case "nodeobserver":
                            if (!string.IsNullOrEmpty(nodeObserverLogText))
                            {
                                var reportItems = this.GetObserverErrWarnLogEntryListFromLogText(nodeObserverLogText);

                                ret = this.Json(reportItems);
                            }

                            break;
                        case "osobserver":
                            if (!string.IsNullOrEmpty(osObserverLogText))
                            {
                                var reportItems = this.GetObserverErrWarnLogEntryListFromLogText(osObserverLogText);

                                ret = this.Json(reportItems);
                            }

                            break;
                        default:
                            ret = this.Json("Specified Observer, " + name + ", does not exist...");
                            break;
                    }

                    break;
                }
                catch (IOException)
                {
                }

                // If we get here, let's wait a second before the next iteration...
                Task.Delay(1000).Wait();
            }

            return ret;
        }

        // This only makes sense if you enable communication between nodes and/or over the Internet
        // over a secure channel... By default, this API service is node-local (node-only) with no comms outside of VM...
        // GET: api/ObserverLog/DiskObserver/_SFRole_0
        // GET: api/ObserverLog/DiskObserver/_SFRole_0/html
        [HttpGet("{observername}/{nodename}/{format?}", Name = "GetObserverLogNode")]
        public ContentResult Get(string observername, string nodename, string format = "json")
        {
            try
            {
                var node = this.fabricClient.QueryManager.GetNodeListAsync(nodename).Result;

                if (node.Count > 0)
                {
                    var addr = node[0].IpAddressOrFQDN;

                    // By default this service is node-local, http, port 5000...
                    // If you modify the service to support Internet communication over a
                    // secure channel, then change this code to force https.
                    if (!addr.Contains("http://"))
                    {
                        addr = "http://" + addr;
                    }

                    string fqdn = "?fqdn=" + this.Request.Host;

                    // If you modify the service to support Internet communication over a
                    // secure channel, then change this code to reflect the correct port...
                    var req = WebRequest.Create(addr + $":5000/api/ObserverLog/{observername}/{format}{fqdn}");
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

                    return this.Content(ret, format.ToLower() == "html" ? "text/html" : "text/json");
                }
                else
                {
                    return this.Content("no node found with that name...");
                }
            }
            catch (ArgumentException ae)
            {
                return this.Content($"Error processing request: {ae.Message}");
            }
            catch (IOException ioe)
            {
                return this.Content($"Error processing request: {ioe.Message}");
            }
        }

        private string GetHtml(string name)
        {
            string html = string.Empty;
            string observerLogFilePath = null;
            var nodeName = this.serviceContext.NodeContext.NodeName;
            var configSettings = this.serviceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings;

            string logFolder, logFileName;
            string networkObserverLogText = null, osObserverLogText = null, nodeObserverLogText = null, appObserverLogText = null, fabricSystemObserverLogText = null, diskObserverLogText = null;

            if (configSettings != null)
            {
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
                    // Observer log paths...
                    var appObserverLogPath = observerLogFilePath.Replace("ObserverManager", "AppObserver");
                    var osObserverLogPath = observerLogFilePath.Replace("ObserverManager", "OSObserver");
                    var diskObserverLogPath = observerLogFilePath.Replace("ObserverManager", "DiskObserver");
                    var networkObserverLogPath = observerLogFilePath.Replace("ObserverManager", "NetworkObserver");
                    var fabricSystemObserverLogPath = observerLogFilePath.Replace("ObserverManager", "FabricSystemObserver");
                    var nodeObserverLogPath = observerLogFilePath.Replace("ObserverManager", "NodeObserver");

                    // Observer logs...
                    if (System.IO.File.Exists(appObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(appObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        appObserverLogText = System.IO.File.ReadAllText(appObserverLogPath, Encoding.UTF8).Replace("\n", "<br/>");
                    }

                    if (System.IO.File.Exists(diskObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(diskObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        diskObserverLogText = System.IO.File.ReadAllText(diskObserverLogPath, Encoding.UTF8).Replace("\n", "<br/>");
                    }

                    if (System.IO.File.Exists(fabricSystemObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(fabricSystemObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        fabricSystemObserverLogText = System.IO.File.ReadAllText(fabricSystemObserverLogPath, Encoding.UTF8).Replace("\n", "<br/>");
                    }

                    if (System.IO.File.Exists(networkObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(networkObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        networkObserverLogText = System.IO.File.ReadAllText(networkObserverLogPath, Encoding.UTF8).Replace("\n", "<br/>");
                    }

                    if (System.IO.File.Exists(nodeObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(nodeObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        nodeObserverLogText = System.IO.File.ReadAllText(nodeObserverLogPath, Encoding.UTF8).Replace("\n", "<br/>");
                    }

                    if (System.IO.File.Exists(osObserverLogPath)
                        && System.IO.File.GetCreationTimeUtc(osObserverLogPath).ToShortDateString() == DateTime.UtcNow.ToShortDateString())
                    {
                        osObserverLogText = System.IO.File.ReadAllText(osObserverLogPath, Encoding.UTF8).Trim();
                    }

                    var host = this.Request.Host.Value;

                    string nodeLinks = string.Empty;

                    // Request originating from ObserverWeb node hyperlinks...
                    if (this.Request.QueryString.HasValue && this.Request.Query.ContainsKey("fqdn"))
                    {
                        host = this.Request.Query["fqdn"];

                        // Node links...
                        var nodeList = this.fabricClient.QueryManager.GetNodeListAsync().Result;
                        var ordered = nodeList.OrderBy(node => node.NodeName);

                        foreach (var node in ordered)
                        {
                            nodeLinks += "| <a href='" + this.Request.Scheme + "://" + host + "/api/ObserverLog/" + name + "/" + node.NodeName + "/html'>" + node.NodeName + "</a> | ";
                        }
                    }

                    this.sb = new StringBuilder();

                    this.sb.AppendLine("<html>\n\t<head>");
                    this.sb.AppendLine("\n\t\t<title>FabricObserver Observer Report: Errors and Warnings</title>");
                    this.sb.AppendLine("\n\t\t" + this.script);
                    this.sb.AppendLine("\n\t\t<style type=\"text/css\">\n" +
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
                    this.sb.AppendLine("\n\t</head>");
                    this.sb.AppendLine("\n\t<body>");
                    this.sb.AppendLine("\n\t\t\t <br/>");
                    this.sb.AppendLine("\n\t\t\t\t<div class=\"container\"><div style=\"position: relative; width: 100%; margin-left: auto; margin-right: auto;\"><br/><strong>Errors and Warnings for " + name + " on " + nodeName + "</strong><br/><br/>" + nodeLinks);

                    switch (name.ToLower())
                    {
                        case "appobserver":
                            if (!string.IsNullOrEmpty(appObserverLogText))
                            {
                                this.sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + appObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "diskobserver":
                            if (!string.IsNullOrEmpty(diskObserverLogText))
                            {
                                this.sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + diskObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "fabricsystemobserver":
                            if (!string.IsNullOrEmpty(fabricSystemObserverLogText))
                            {
                                this.sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + fabricSystemObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "networkobserver":
                            if (!string.IsNullOrEmpty(networkObserverLogText))
                            {
                                this.sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + networkObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "nodeobserver":
                            if (!string.IsNullOrEmpty(nodeObserverLogText))
                            {
                                this.sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + nodeObserverLogText + "<br/><br/>");
                            }

                            break;
                        case "osobserver":
                            if (!string.IsNullOrEmpty(osObserverLogText))
                            {
                                this.sb.AppendLine("\n\t\t\t<br/><br/>" + "\n\t\t\t" + osObserverLogText + "<br/><br/>");
                            }

                            break;
                        default:
                            this.sb.AppendLine("\n\t\t\t<br/>Specified Observer, " + name + ", does not exist...");
                            break;
                    }

                    this.sb.AppendLine("\n\t\t\t</div>");
                    this.sb.AppendLine("\n\t</body>");
                    this.sb.AppendLine("</html>");
                    html = this.sb.ToString();
                    this.sb.Clear();
                    break;
                }
                catch (IOException ie)
                {
                    html = ie.ToString();
                }

                // If we get here, let's wait a few seconds before the next iteration...
                Task.Delay(2000).Wait();
            }

            return html;
        }

        private List<ObserverLogEntry> GetObserverErrWarnLogEntryListFromLogText(string observerLogText)
        {
            var reportItems = new List<ObserverLogEntry>();
            var logArray = observerLogText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            foreach (var item in logArray)
            {
                if (!item.Contains("--") &&
                    (!item.Contains("WARN") || !item.Contains("ERROR")))
                {
                    continue;
                }

                string[] arr = item.Split("--", StringSplitOptions.RemoveEmptyEntries);

                var logReport = new ObserverLogEntry
                {
                    Date = arr[0],
                    HealthState = arr[1],
                    Message = arr[2].Trim('\n'),
                };

                reportItems.Add(logReport);
            }

            return reportItems;
        }
    }
}