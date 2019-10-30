This is the FabricObserver API App (ASP.NET Core v2.1 for VS 2017 or v2.2 for VS 2019), used for "communicating" with Observers from within a node (but you can choose to expose this service to the Internet if you want to. By default, if you deploy FOWebApi as is, then only a service running on the same node can call into its REST API...). 

Each observer writes out to their own log file (**noisy** when EnableVerboseLogging is set to true in Settings.xml for whatever observer you choose to enable)... If you deploy FabricObserverWebApp, you **really should only log Warnings and Errors**). The REST API reads from log files, which are kept up to date by ObserverManager (so, if AppObserver detects warning or error conditions, for example, this information will only live as long as it remains in this state. If the next iteration of the observer in warning/error reports Ok, then its log file will no longer contain prior health information and the API will no longer report error or warning (with details) when called. 

Why?  

By design, you can't communicate with an observer. Use the web api to get current information about app service and node health states that an observer monitors and reports on.


Example web API calls:

Check if an Observer has detected any Error or Warning conditions on local node:  

http://localhost:5000/api/ObserverLog/[Observer name]

e.g., 

http://localhost:5000/api/ObserverLog/NodeObserver

will return JSON string:

{"date":"08-29-2019 21:07:11.6257","healthState":"Ok","message":""} 

when current state of the local node is healthy. In the case when it is not healthy, you will see healthState:Warning/Error with the message field containing the details, including an [FOxxx error code](/Documentation/ErrorCodes.md).

This API also supports html output for "pretty" printing on a web page. For example, 

http://localhost:5000/api/observermanager

will display a bunch of very useful information about the node. 

If you decide to expose this api over the Internet on a secure channel (SSL), and provide an FQDN (including port) setting in Settings.xml, then new features will become available like a node menu where you can navigate around the cluster to view node states. Also, you will be able to query for observer data on any node by supplying a node name on the API URI: e.g., https://[FQDN:Port]/api/ObserverLog/NodeObserver/[NodeName]
