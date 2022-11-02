
$subscriptionId = "<YOUR-AZURE-SUBSCRIPTION-ID>" 
Try {
  Select-AzSubscription -SubscriptionId $subscriptionId -ErrorAction Stop
} Catch {
    Login-AzAccount
    Set-AzContext -SubscriptionId $subscriptionId
}

$resourceGroup = "<YOUR-CLUSTER-RESOURCE-NAME>"
$armTemplate = "service-fabric-observer.json"
$armTemplateParameters = "service-fabric-observer.v3.2.4.831.parameters.json"

cd "<LOCAL-FO-REPO-PATH>\Documentation\Deployment"

New-AzResourceGroupDeployment -Name "deploy-service-fabric-observer" -ResourceGroupName $resourceGroup -TemplateFile $armTemplate -TemplateParameterFile $armTemplateParameters -Verbose -Mode Incremental

