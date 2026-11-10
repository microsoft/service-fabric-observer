#Requires -Version 7.0
#Requires -Modules Az.Accounts
<#
.SYNOPSIS
Checkpointed setup for the default FabricObserver and ClusterObserver Log Analytics mappings.

.DESCRIPTION
Sign in and select SubscriptionId first. Run one step at a time; see README.md for the migration checkpoints.
CheckTable delegates to Test-ObserverLogAnalytics.ps1. Prepare deploys the existing ARM template using Az.Accounts
only, in the workspace region. CheckHosts reads the identities on Uniform scale sets and their existing VMs.
Parameters reads a completed deployment and returns an application parameter map and XML fragment.

The helper never converts a table, changes hosts, upgrades an application, fetches legacy keys, or cuts over.
SingleWrite requires an already converted table with standard columns only. It adds missing columns, which can stop
legacy ingestion; switching to LegacyOnly or an old package does not undo this. Fresh and DualWrite require a new
destination table. To resume after deployment, use Parameters rather than repeating Prepare.

.EXAMPLE
.\Setup-ObserverLogAnalytics.ps1 -Step Prepare -Application FabricObserver -Route DualWrite -SubscriptionId <ID> -ResourceGroupName my-rg -WorkspaceName my-workspace -WhatIf

.EXAMPLE
$settings = .\Setup-ObserverLogAnalytics.ps1 -Step Parameters -Application ClusterObserver -Route SingleWrite -SubscriptionId <ID> -ResourceGroupName my-rg -WorkspaceName my-workspace
$settings.ParameterXml
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('CheckTable', 'Prepare', 'CheckHosts', 'Parameters')][string] $Step,
    [Parameter(Mandatory)][ValidateSet('FabricObserver', 'ClusterObserver')][string] $Application,
    [Parameter(Mandatory)][ValidateSet('Fresh', 'SingleWrite', 'DualWrite')][string] $Route,
    [Parameter(Mandatory)][guid] $SubscriptionId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $ResourceGroupName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $WorkspaceName,
    [ValidatePattern('^[A-Za-z0-9_]+_CL$')][string] $TableName,
    [ValidatePattern('^[A-Za-z0-9_]+_CL$')][string] $LegacyTableName,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string] $DcrName,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string] $DeploymentName,
    [ValidateNotNullOrEmpty()][string] $ManagedIdentityName = 'id-observer-loganalytics',
    [ValidatePattern('(?i)^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft.ManagedIdentity/userAssignedIdentities/[^/]+$')]
    [string] $ExistingManagedIdentityResourceId,
    [ValidatePattern('(?i)^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft.Insights/dataCollectionEndpoints/[^/]+$')]
    [string] $DataCollectionEndpointResourceId,
    [bool] $GrantPublisherRole = $true,
    # CheckHosts only; hosts must be in SubscriptionId.
    [string] $ClusterResourceGroupName,
    [string[]] $ScaleSetNames
)

$ErrorActionPreference = 'Stop'
if (-not $LegacyTableName) { $LegacyTableName = "${Application}_CL" }
if (-not $TableName) { $TableName = if ($Route -eq 'DualWrite') { "${Application}V2_CL" } else { $LegacyTableName } }
if (-not $DcrName) { $DcrName = "dcr-$($Application.ToLowerInvariant())" }
if (-not $DeploymentName) { $DeploymentName = "observer-loganalytics-$($Application.ToLowerInvariant())" }
if ($Route -eq 'DualWrite' -and $TableName -eq $LegacyTableName) {
    throw 'DualWrite requires separate legacy and new tables.'
}
if ($Step -eq 'CheckHosts' -and ([string]::IsNullOrWhiteSpace($ClusterResourceGroupName) -or
        -not $ScaleSetNames -or @($ScaleSetNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count)) {
    throw 'CheckHosts requires ClusterResourceGroupName and every hosting ScaleSetNames entry.'
}
$context = Get-AzContext
if (-not $context -or $context.Subscription.Id -ne $SubscriptionId.ToString()) {
    throw "Select subscription $SubscriptionId with Connect-AzAccount or Set-AzContext before running this helper."
}
$groupPath = "/subscriptions/$SubscriptionId/resourceGroups/$([uri]::EscapeDataString($ResourceGroupName))"
$workspacePath = "$groupPath/providers/Microsoft.OperationalInsights/workspaces/$([uri]::EscapeDataString($WorkspaceName))"
$deploymentPath = "$groupPath/providers/Microsoft.Resources/deployments/$DeploymentName`?api-version=2022-09-01"

function Invoke-Arm([string] $Path, [string] $Method = 'GET', [string] $Payload, [switch] $AllowNotFound) {
    # The outer Prepare step owns confirmation; reads must still run during its WhatIf preview.
    $request = @{ Path = $Path; Method = $Method; DefaultProfile = $context; ErrorAction = 'Stop'; WhatIf = $false; Confirm = $false }
    if ($Payload) { $request.Payload = $Payload }
    $response = Invoke-AzRestMethod @request
    if ($AllowNotFound -and $response.StatusCode -eq 404) { return $null }
    if ($response.StatusCode -ge 300) { throw "Azure request failed (HTTP $($response.StatusCode)) for $Path`: $($response.Content)" }
    return $response.Content | ConvertFrom-Json -Depth 100
}

function Get-TableCheck {
    $WhatIfPreference = $false
    $ConfirmPreference = 'None'
    $name = if ($Route -eq 'DualWrite') { $LegacyTableName } else { $TableName }
    & (Join-Path $PSScriptRoot 'Test-ObserverLogAnalytics.ps1') -Check Table -Application $Application `
        -SubscriptionId $SubscriptionId.ToString() -ResourceGroupName $ResourceGroupName `
        -WorkspaceName $WorkspaceName -TableName $name -PassThru
}

function Get-SetupResult($Deployment) {
    if ($Deployment.properties.provisioningState -ne 'Succeeded') {
        throw "Deployment '$DeploymentName' is $($Deployment.properties.provisioningState), not Succeeded. Inspect it in Azure before continuing."
    }
    $expected = @{
        workspaceName = $WorkspaceName; tableName = $TableName; createTable = ($Route -ne 'SingleWrite'); dcrName = $DcrName
        managedIdentityName = $ManagedIdentityName; existingManagedIdentityResourceId = [string]$ExistingManagedIdentityResourceId
        dataCollectionEndpointResourceId = [string]$DataCollectionEndpointResourceId; grantPublisherRole = $GrantPublisherRole
    }
    foreach ($name in $expected.Keys) {
        $parameter = $Deployment.properties.parameters.$name
        if ($null -eq $parameter -or $parameter.value -ne $expected[$name]) {
            throw "Deployment '$DeploymentName' does not match $name for this setup. Check the inputs and deployment name."
        }
    }
    $parameters = [ordered]@{
        ObserverManagerEnableTelemetryProvider = 'true'
        TelemetryProvider = 'AzureLogAnalytics'
        LogAnalyticsWriteMode = if ($Route -eq 'DualWrite') { 'DualWrite' } else { 'LogsIngestionOnly' }
    }
    foreach ($name in @('LogAnalyticsIngestionEndpoint', 'LogAnalyticsDcrImmutableId',
            'LogAnalyticsStreamName', 'LogAnalyticsManagedIdentityClientId')) {
        $value = $Deployment.properties.outputs.$name.value
        if ([string]::IsNullOrWhiteSpace($value)) { throw "Missing deployment output: $name" }
        $parameters[$name] = [string]$value
    }
    if ($parameters.LogAnalyticsStreamName -cne "Custom-$Application") {
        throw "Deployment '$DeploymentName' does not use the default $Application stream."
    }
    $identityId = $Deployment.properties.outputs.ManagedIdentityResourceId.value
    if ([string]::IsNullOrWhiteSpace($identityId)) { throw 'Missing deployment output: ManagedIdentityResourceId' }

    $text = [System.Text.StringBuilder]::new()
    $format = [System.Xml.XmlWriterSettings]::new()
    $format.Indent = $true
    $format.NewLineChars = "`r`n"
    $format.OmitXmlDeclaration = $true
    $writer = [System.Xml.XmlWriter]::Create($text, $format)
    try {
        $writer.WriteStartElement('Parameters')
        foreach ($name in $parameters.Keys) {
            $writer.WriteStartElement('Parameter')
            $writer.WriteAttributeString('Name', $name)
            $writer.WriteAttributeString('Value', $parameters[$name])
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
    } finally { $writer.Dispose() }
    return [pscustomobject]@{
        ApplicationName = "fabric:/$Application"; TableName = $TableName
        ManagedIdentityResourceId = $identityId; ApplicationParameters = $parameters; ParameterXml = $text.ToString()
    }
}

switch ($Step) {
    'CheckTable' { Get-TableCheck }
    'Prepare' {
        $workspace = Invoke-Arm "$workspacePath`?api-version=2022-10-01"
        if ([string]::IsNullOrWhiteSpace($workspace.location)) { throw 'The workspace region could not be read.' }
        if ($Route -eq 'SingleWrite') {
            $check = Get-TableCheck
            if (-not $check.Details.Exists -or $check.Details.TableType -ne 'DataCollectionRuleBased') {
                throw 'SingleWrite requires an existing, already converted table. Review the one-way conversion warning in README.md; convert manually before Prepare.'
            }
            if ($check.Status -eq 'Fail' -or @($check.Details.NonStandardColumns).Count) {
                throw 'The default SingleWrite helper requires standard columns and types only. Resolve the table check or use DualWrite; custom mappings require manual setup.'
            }
            Write-Warning 'SingleWrite is one-way: this template adds missing columns to the converted table, which can stop legacy ingestion. LegacyOnly or an old package does not restore the original table behavior. Choose DualWrite if legacy rollback is required.'
        } else {
            $table = Invoke-Arm "$workspacePath/tables/$TableName`?api-version=2022-10-01" -AllowNotFound
            if ($null -ne $table) {
                throw "Table '$TableName' already exists. Choose a new name, or use Parameters to resume a completed deployment."
            }
        }
        $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot "$Application.LogAnalytics.json") -Raw | ConvertFrom-Json -AsHashtable -Depth 100
        $values = @{
            workspaceName = $WorkspaceName; tableName = $TableName; createTable = ($Route -ne 'SingleWrite')
            location = $workspace.location; dcrName = $DcrName; managedIdentityName = $ManagedIdentityName
            existingManagedIdentityResourceId = [string]$ExistingManagedIdentityResourceId
            dataCollectionEndpointResourceId = [string]$DataCollectionEndpointResourceId; grantPublisherRole = $GrantPublisherRole
        }
        $parameters = @{}
        foreach ($name in $values.Keys) { $parameters[$name] = @{ value = $values[$name] } }
        $payload = @{ properties = @{ mode = 'Incremental'; template = $template; parameters = $parameters } } | ConvertTo-Json -Depth 100
        if (-not $PSCmdlet.ShouldProcess("$ResourceGroupName / $WorkspaceName / $TableName",
                "Deploy $Application table, DCR, managed identity and selected role assignment")) { return }
        $deployment = Invoke-Arm $deploymentPath -Method PUT -Payload $payload
        $deadline = [datetime]::UtcNow.AddMinutes(30)
        while ($deployment.properties.provisioningState -notin @('Succeeded', 'Failed', 'Canceled')) {
            if ([datetime]::UtcNow -ge $deadline) {
                throw "Timed out waiting for deployment '$DeploymentName'. It may still be running; inspect it in Azure and resume with Parameters after success."
            }
            Start-Sleep -Seconds 5
            $deployment = Invoke-Arm $deploymentPath
        }
        if ($deployment.properties.provisioningState -ne 'Succeeded') {
            throw "Deployment '$DeploymentName' failed: $($deployment.properties.error | ConvertTo-Json -Depth 20 -Compress)"
        }
        Write-Host 'Azure deployment succeeded. Host identity attachment, application configuration and stored-record verification are still required.'
        Get-SetupResult $deployment
    }
    'Parameters' { Get-SetupResult (Invoke-Arm $deploymentPath) }
    'CheckHosts' {
        $settings = Get-SetupResult (Invoke-Arm $deploymentPath)
        $identityId = $settings.ManagedIdentityResourceId
        $hosts = [System.Collections.Generic.List[object]]::new()
        foreach ($scaleSet in $ScaleSetNames) {
            $path = "/subscriptions/$SubscriptionId/resourceGroups/$([uri]::EscapeDataString($ClusterResourceGroupName))/providers/Microsoft.Compute/virtualMachineScaleSets/$([uri]::EscapeDataString($scaleSet))"
            $model = Invoke-Arm "$path`?api-version=2024-11-01"
            if ($model.properties.orchestrationMode -eq 'Flexible') {
                throw "Scale set '$scaleSet' is Flexible. Verify its VM identities manually; CheckHosts covers Uniform scale sets only."
            }
            if ($identityId -notin @($model.identity.userAssignedIdentities.PSObject.Properties.Name)) {
                throw "Identity is not attached to scale set '$scaleSet': $identityId"
            }
            $nextPath = "$path/virtualMachines?api-version=2024-11-01"
            $count = 0
            do {
                $page = Invoke-Arm $nextPath
                foreach ($vm in $page.value) {
                    $count++
                    if ($vm.properties.latestModelApplied -ne $true -or
                        $identityId -notin @($vm.identity.userAssignedIdentities.PSObject.Properties.Name)) {
                        throw "Scale set '$scaleSet', instance '$($vm.instanceId)': identity or latest model not confirmed. Apply the host update using your cluster's supported rollout procedure."
                    }
                    $hosts.Add([pscustomobject]@{ ScaleSet = $scaleSet; Instance = $vm.instanceId; IdentityAttached = $true })
                }
                $nextPath = $null
                if ($page.nextLink) {
                    $next = [uri]$page.nextLink
                    $arm = [uri]$context.Environment.ResourceManagerUrl
                    if (-not $next.IsAbsoluteUri -or $next.Scheme -ne 'https' -or $next.Authority -ne $arm.Authority -or
                        -not $next.AbsolutePath.StartsWith("$path/virtualMachines", [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'Unexpected VM pagination URL; host verification stopped.'
                    }
                    $nextPath = $next.PathAndQuery
                }
            } while ($nextPath)
            if ($count -eq 0) { throw "Scale set '$scaleSet' has no VMs to verify. CheckHosts does not prove future-node identity inheritance." }
        }
        Write-Host 'Identity and latest model confirmed on the supplied scale sets and existing VMs. This does not prove token acquisition or ingestion.'
        $hosts.ToArray()
    }
}
