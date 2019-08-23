This is the SF Web API App (ASP.NET Core v2.2) used for "communicating" with Observers from within a node (you can choose to expose this service to the Internet. By default, if you deploy FOWebApi as is, then only a service running on the same node can call into its REST API...). 

Each observer writes out to their own log file (noisy when EnableVerboseLogging is set to true... **Best to leave this as false and only log Warnings and Errors**). The REST API reads from log files, which are kept up to date by ObserverManager (so, if AppObserver detects warning or error conditions, for example, this information will only live as long as it remains in this state. If the next iteration of the observer in warning/error reports Ok, then its log file will no longer contain prior health information and the API will no longer report error or warning (with details) when called. 

Why?  

By design, you can't communicate with an observer from afar (over the Internet)... Use the web api to get information (real time) about service and node health states that an observe monitors and reports on.


Example web API calls:

Check if an Observer has detected any Error or Warning conditions on local node:  

http://localhost:5000/api/ObserverLog/[Observer name]

e.g., 

http://localhost:5000/api/ObserverLog/NodeObserver

will return JSON string:

{"date":"08-16-2019 21:07:11.6257","healthState":"Ok","message":""} 

when current state of the local node is healthy. In the case when it is not healthy, you will see healthState:Warning/Error with the message field containing the details, including an FOxxx error code.

This API also supports html output for "pretty" printing on a web page. For example, 

http://localhost:5000/api/observermanager

will display a bunch of very useful information about the node. 

If you decide to expose this api over the Internet on a secure channel (SSL), and provide an FQDN setting in Settings.xml, then new features will become available like a node menu where you can navigate around the cluster to view node states. Also, you will be able to query for observer data on any node by supplying a node name on the API URI.


