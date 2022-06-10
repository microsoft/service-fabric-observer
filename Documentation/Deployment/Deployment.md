# How to deploy FabricObserver?

Like all elementary services such as monitoring, Windows update, scripts, scaling, etc. those services should be installed with the initial cluster deployment. 

There are two options:
1. Add the resource provided in the ARM template service-fabric-observer.json in the template which also deploys the Service Fabric cluster. To guarantee the correct deployment order the first resource has to depend on the cluster resource. Using 'dependsOn' makes sure that the Service Fabric Resource Provider waits with the application deployment till the cluster deployment is ready.

```ARM
    {
	    "apiVersion": "[variables('sfApiVersion')]",
      "type": "Microsoft.ServiceFabric/clusters/applicationTypes",
      "name": "[concat(parameters('clusterName'), '/', variables('applicationTypeName'))]",
      "location": "[resourceGroup().location]",
      "dependsOn": [
        "[concat('Microsoft.ServiceFabric/clusters/', parameters('clusterName'))]"
      ],
      "properties": {
        "provisioningState": "Default"
      }
    },
``` 

2. The app can be deployed manually by using the provided PowerShell script file 'Deploy-FabricObserver.ps1'.


## Further reading
- [Upgrade the Service Fabric application by using Resource Manager](https://docs.microsoft.com/en-us/azure/service-fabric/service-fabric-concept-resource-model#upgrade-the-service-fabric-application-by-using-resource-manager)