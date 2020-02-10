**FabricObserver Error and Warning Codes as defined in class file FOErrorWarningCodes.cs** 

|Member Name|Code|description|  
| :--- | :--- | :--- | 
| AppErrorCpuTime | FO001 | Percentage of total CPU usage has exceeded configured Error threshold for an app service process. | 
| AppWarningCpuTime | FO002 | Percentage of total CPU usage has exceeded configured Warning threshold for an app service process. | 
| NodeErrorCpuTime | FO003 | Percentage of total CPU usage has exceeded configured Error threshold on a VM instance. | 
| NodeWarningCpuTime | FO004 | Percentage of total CPU usage has exceeded configured Warning threshold on a VM instance. | 
| ErrorCertificateExpiration | FO005 | Certificate expiration has occured. | 
| WarningCertificateExpiration | FO006 | Certificate expiration is immenent. |  
| NodeErrorDiskSpacePercentUsed | FO007 | Disk usage percentatge has exceeded configured Error threshold on a VM instance. | 
| NodeErrorDiskSpaceMB | FO008 | Disk usage space (MB) has exceeded configured Error threshold on a VM instance. | 
| NodeWarningDiskSpacePercentUsed | FO009 | Disk usage percentatge has exceeded configured Warning threshold on a VM instance. |  
| NodeWarningDiskSpaceMB | FO010 | Disk usage space (MB) has exceeded configured Warning threshold on a VM instance. |  
| NodeErrorDiskAverageQueueLength | FO011 | Avergage disk queue length has exceeded configured Erorr threshold. |  
| NodeWarningDiskAverageQueueLength | FO012 | Average disk queue length has exceeded configured Warning threshold. |  
| AppErrorMemoryPercentUsed | FO013 | Percentage of total physical memory usage has exceeded configured Error threshold for an app service process. |  
| AppWarningMemoryPercentUsed | FO014 | Percentage of total physical memory usage has exceeded configured Warning threshold for an app service process. |  
| AppErrorMemoryCommittedMB | FO015 | Committed memory (MB) has exceeded configured Error threshold for an app service process. |  
| AppWarningMemoryCommittedMB | FO016 | Committed memory (MB) has exceeded configured Warning threshold for an app service process. |  
| NodeErrorMemoryPercentUsed | FO017 | Percentage of total physical memory usage has exceeded configured Warning threshold on VM instance. |  
| NodeWarningMemoryPercentUsed | FO018 | Percentage of total physical memory usage has exceeded configured Warning threshold on VM instance. | 
| NodeErrorMemoryCommittedMB | FO019 | Total Committed memory (MB) has exceeded configured Error threshold on a VM instance. | 
| NodeWarningMemoryCommittedMB | FO020 | Total Committed memory (MB) has exceeded configured Warning threshold on a VM instance. | 
| AppErrorNetworkEndpointUnreachable | FO021 | Error: Configured endpoint detected as unreachable. | 
| AppWarningNetworkEndpointUnreachable | FO022 | Warning: Configured endpoint detected as unreachable. | 
| AppErrorTooManyActiveTcpPorts | FO023 | Number of active TCP ports at or exceeding configured Error threshold for an App service process.  | 
| AppWarningTooManyActiveTcpPorts | FO024 | Number of active TCP ports at or exceeding configured Warning threshold for an App service process. | 
| NodeErrorTooManyActiveTcpPorts | FO025 | Number of active TCP ports at or exceeding configured Error threshold on a VM instance. | 
| NodeWarningTooManyActiveTcpPorts | FO026 | Number of active TCP ports at or exceeding configured Warning threshold on a VM instance.  | 
| ErrorTooManyFirewallRules | FO027 | Number of enabled Firewall Rules at or exceeding configured Error threshold on a VM instance.  | 
| WarningTooManyFirewallRules | FO028 | Number of enabled Firewall Rules at or exceeding configured Warning threshold on a VM instance. | 
| AppErrorTooManyActiveEphemeralPorts | FO029 | Number of active Ephemeral TCP ports (ports in the Windows dynamic port range) at or exceeding configured Error threshold for an App service process. | 
| AppWarningTooManyActiveEphemeralPorts | FO030 | Number of active Ephemeral TCP ports (ports in the Windows dynamic port range) at or exceeding configured Warning threshold for an App service process. | 
| NodeErrorTooManyActiveEphemeralPorts | FO031 | Number of active Ephemeral TCP ports (ports in the Windows dynamic port range) at or exceeding configured Error threshold on a VM instance.  | 
| NodeWarningTooManyActiveEphemeralPorts | FO032 | Number of active Ephemeral TCP ports (ports in the Windows dynamic port range) at or exceeding configured Warning threshold on a VM instance.  |  
| Ok | FO000 | Ok HealthState | 
