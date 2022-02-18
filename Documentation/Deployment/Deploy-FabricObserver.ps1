
$subscriptionId = "" 
Try {
  Select-AzSubscription -SubscriptionId $subscriptionId -ErrorAction Stop
} Catch {
    Login-AzAccount
    Set-AzContext -SubscriptionId $subscriptionId
}

$resourceGroup = "chrpap171850-group"
$armTemplate = "service-fabric-observer.json"
$armTemplateParameters = "service-fabric-observer.v3.1.20.parameters.json"

cd "D:\Code\inputoutputcode\service-fabric-observer\Documentation\Deployment"

New-AzResourceGroupDeployment -Name "deploy-service-fabric-observer" -ResourceGroupName $resourceGroup -TemplateFile $armTemplate -TemplateParameterFile $armTemplateParameters -Verbose -Mode Incremental

