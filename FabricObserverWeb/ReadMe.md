This is the SF Web API App (ASP.NET Core v2.2) used for "communicating" with Observers. Each observer writes out to their own log file (noisy when EnableVerboseLogging is set to true... Best to leave this as false and only log Warnings and Errors). The REST API reads from log files, which are kept up to date by ObserverManager (so, if AppObserver detects warning or error conditions, for example, this information will only live as long as it remains in this state. If the next iteration of the observer in warning/error reports Ok, then its log file will no longer contain prior health information and the API will no longer report error or warning (with details) when called. 

Observers are not accesible over the Internet. This is for security reasons. By design, you can't communicate with an observer from afar (over the Internet)... Use the web api to get information (real time) about service and node health states.

Example web API calls:

Check if an Observer has detected any Error or Warning conditions on specified node ([ ] = placeholder, add real values with no square braces...):

https://foo-bar-baz10.westus2.cloudapp.azure.com:443/api/ObserverLog/[Observer name]/[Node name] 

e.g., 

https://foo-bar-baz10.westus2.cloudapp.azure.com:443/api/ObserverLog/NodeObserver/_SFRole2_

will return JSON string:

{"date":"08-05-2019 21:07:11.6257","healthState":"Ok","message":""} 

when current state of node _SFRole_2 is healthy. In the case when it is not healthy, you will see healthState:Warning/Error with a message: that contains specific details of the Warning or Error state...

This API also supports html output for "pretty" printing on a web page. For example, 

https://foo-bar-baz10.westus2.cloudapp.azure.com:443/api/observermanager/_SFRole0

will display a bunch of very useful information about the specfied node (in this case _SFRole0). It also has a menu of nodes that will take you around the cluster to see what's going on on each node across OS and SF properties...


