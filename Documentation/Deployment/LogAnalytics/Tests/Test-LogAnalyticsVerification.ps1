#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$kit = Split-Path $PSScriptRoot -Parent
$path = Join-Path $kit 'Test-ObserverLogAnalytics.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
$functions = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true))
. ([scriptblock]::Create(($functions.Extent.Text -join "`n")))
$realQuery = ${function:Invoke-Query}
$source = Get-Content -LiteralPath $path -Raw
$main = [scriptblock]::Create($source.Substring($source.IndexOf('# --- Main')))
$schema = Get-Content -LiteralPath (Join-Path $kit 'LogAnalyticsSchema.json') -Raw | ConvertFrom-Json
$selected = @('NodeName_s', 'ObserverName_s', 'Metric_s', 'Value_d', 'ReplicaId_d', 'Property_s',
    'property_s', 'healthState_s', 'id_s', 'datetime_t', 'Timestamp_t', 'UpgradeState_s', 'value_d')
$recordColumns = @($schema.applications.FabricObserver | Where-Object { $_.name -cin $selected })
if ($recordColumns.Count -ne $selected.Count) { throw 'Regression fixture does not match the declared schema.' }
$recordTypes = [hashtable]::new([StringComparer]::Ordinal)
foreach ($column in $recordColumns) { $recordTypes[$column.name] = $column.type }
$cases = 0

function Assert-Equal($Actual, $Expected, [string] $Message) {
    if ($Actual -cne $Expected) { throw "$Message`: expected '$Expected', got '$Actual'." }
}
function Assert-Throws([scriptblock] $Body, [string] $Pattern) {
    $failure = $null
    try { & $Body } catch { $failure = $_ }
    if (-not $failure -or "$failure" -notmatch $Pattern) { throw "Expected an explicit failure matching '$Pattern'; got '$failure'." }
}
function Reset-Scenario {
    $script:findings = [Collections.Generic.List[object]]::new()
    $script:queryRows = @()
    $script:queryCalls = [Collections.Generic.List[string]]::new()
    $script:workspaceCalls = [Collections.Generic.List[object]]::new()
    $script:StartTime = [datetimeoffset]::Parse('2026-01-02T00:00:00Z').UtcDateTime
    $script:EndTime = $StartTime.AddHours(4)
    $script:TimeTolerance = [timespan]::FromMinutes(5)
    $script:MaxDetailRows = 500
    $script:WorkspaceName = 'workspace'
    $script:ResourceGroupName = 'legacy-rg'
    $script:NewWorkspaceName = $null
    $script:NewResourceGroupName = $null
    $script:SubscriptionId = [guid]::Empty.ToString()
    $script:LegacyTableName = 'Legacy_CL'
    $script:NewTableName = 'New_CL'
    $script:TableName = 'Observer_CL'
    $script:Check = 'DualWrite'
    $script:Application = 'FabricObserver'
    $script:CutoverTime = [datetimeoffset]::Parse('2026-01-01T12:00:00Z').UtcDateTime
    $script:Window = [timespan]::FromHours(1)
    $script:Margin = [timespan]::FromMinutes(5)
    $script:MinRows = 10
    $script:PassThru = $true
    $script:OutputPath = $null
    $script:wrongTypeColumn = $null
    $script:bound = @{ StartTime = $StartTime; EndTime = $EndTime; LegacyTableName = $LegacyTableName
        NewTableName = $NewTableName; TableName = $TableName; CutoverTime = $CutoverTime }
    $script:cloud = [pscustomobject]@{
        AzureOperationalInsightsEndpoint = 'https://api.loganalytics.io/'
        AzureOperationalInsightsEndpointResourceId = 'https://api.loganalytics.io'
    }
    $script:queryResponse = '{"tables":[{"columns":[{"name":"Rows","type":"long"}],"rows":[[1]]}]}' | ConvertFrom-Json
}
function Test-Case([string] $Name, [scriptblock] $Body) {
    Reset-Scenario
    & $Body
    $script:cases++
    Write-Host "PASS: $Name"
}
function Get-AzContext { [pscustomobject]@{ Environment = $cloud; Subscription = [pscustomobject]@{ Id = $SubscriptionId } } }
function Get-Token { 'fake-token-no-network' }
function Invoke-RestMethod {
    param($Method, $Uri, $TimeoutSec, $Headers, $ContentType, $Body)
    $script:lastUri = $Uri
    return $queryResponse
}
function Get-Workspace($ResourceGroup, $Name) {
    $script:workspaceCalls.Add([pscustomobject]@{ Group = $ResourceGroup; Name = $Name })
    [pscustomobject]@{ Name = $Name; CustomerId = if ($ResourceGroup -eq 'legacy-rg' -and $Name -eq 'workspace') { 'old' } else { 'new' } }
}
function Get-Table {
    [pscustomobject]@{ SubType = 'Analytics'; Columns = @($recordColumns | ForEach-Object {
        [pscustomobject]@{ Name = $_.name; Type = if ($_.name -ceq $wrongTypeColumn) { 'string' } else { $_.type } }
    }) }
}
function Invoke-Query($WorkspaceCustomerId, $Kql) {
    $script:queryCalls.Add($Kql)
    return $queryRows
}
function New-Record([string] $Side, [datetime] $Time, [hashtable] $Values = @{}) {
    $row = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    $row['ComparisonSource'] = $Side; $row['TimeGenerated'] = $Time
    foreach ($column in $recordColumns) { $row[$column.name] = $null }
    $row['ObserverName_s'] = 'AppObserver'; $row['NodeName_s'] = 'Node0'; $row['Metric_s'] = 'Memory'
    $row['Value_d'] = 42
    foreach ($key in $Values.Keys) { $row[$key] = $Values[$key] }
    return ,$row
}
function Invoke-Check { & $main }
function Set-CutoverRows([int] $Before, [int] $After, [int] $FilledAfter) {
    $script:Check = 'Cutover'
    $script:queryRows = @(
        [pscustomobject]@{ Period = 'Before'; ObserverName_s = 'AppObserver'; Rows = $Before; n_Value_d = $Before; n_ObserverName_s = $Before },
        [pscustomobject]@{ Period = 'After'; ObserverName_s = 'AppObserver'; Rows = $After; n_Value_d = $FilledAfter; n_ObserverName_s = $After }
    )
}

foreach ($base in @('https://api.loganalytics.io', 'https://api.loganalytics.io/', 'https://api.loganalytics.io/v1', 'https://api.loganalytics.io/v1/')) {
    Test-Case "Query endpoint $base includes v1 exactly once" {
        $script:cloud.AzureOperationalInsightsEndpoint = $base
        $rows = @(& $realQuery 'fake-workspace-id' 'print Rows=1')
        Assert-Equal $lastUri 'https://api.loganalytics.io/v1/workspaces/fake-workspace-id/query' 'Query URL'
        Assert-Equal $rows[0]['Rows'] 1 'Returned row'
    }
}
Test-Case 'Configured cloud endpoint is preserved' {
    $script:cloud.AzureOperationalInsightsEndpoint = 'https://api.loganalytics.us/'
    $null = & $realQuery 'fake-workspace-id' 'print Rows=1'
    Assert-Equal $lastUri 'https://api.loganalytics.us/v1/workspaces/fake-workspace-id/query' 'Cloud query URL'
}
foreach ($code in @('PartialError', 'E_QUERY_RESULT_SET_TOO_LARGE')) {
    Test-Case "HTTP 200 $code rejects partial rows" {
        $script:queryResponse | Add-Member error ([pscustomobject]@{ code = $code; message = 'Incomplete results' })
        Assert-Throws { & $realQuery 'fake' 'print Rows=1' } 'no partial results'
    }
}
Test-Case 'Malformed query response fails explicitly' {
    $script:queryResponse = [pscustomobject]@{}
    Assert-Throws { & $realQuery 'fake' 'print Rows=1' } 'invalid result table'
}
Test-Case 'A valid empty query result is accepted' {
    $script:queryResponse.tables[0].rows = @()
    Assert-Equal @(& $realQuery 'fake' 'print Rows=1').Count 0 'Empty result count'
}
Test-Case 'Incomplete query row fails explicitly' {
    $script:queryResponse = '{"tables":[{"columns":[{"name":"Rows"}],"rows":[[1,2]]}]}' | ConvertFrom-Json
    Assert-Throws { & $realQuery 'fake' 'print Rows=1' } 'incomplete row'
}
Test-Case 'A column type mismatch cannot hide values through union type suffixes' {
    $script:wrongTypeColumn = 'Value_d'
    Assert-Equal (Invoke-Check).Status 'Fail' 'Mismatched column type'
    Assert-Equal $queryCalls.Count 0 'No ambiguous union query'
}
Test-Case 'Workspace-name-only override keeps the original resource group' {
    $script:NewWorkspaceName = 'destination'
    $null = Invoke-Check
    Assert-Equal $workspaceCalls[-1].Group 'legacy-rg' 'Original resource group'
    Assert-Equal $workspaceCalls[-1].Name 'destination' 'New workspace name'
}
foreach ($nameOverride in @($null, 'workspace', 'other-workspace')) {
    Test-Case "Destination overrides work with name '$nameOverride'" {
        $script:NewResourceGroupName = 'destination-rg'
        $script:NewWorkspaceName = $nameOverride
        $null = Invoke-Check
        Assert-Equal $workspaceCalls[-1].Group 'destination-rg' 'Destination resource group'
        Assert-Equal $workspaceCalls[-1].Name ($nameOverride ? $nameOverride : 'workspace') 'Destination workspace'
        if ($queryCalls[0] -notmatch "workspace\('new'\)\.New_CL") { throw 'Cross-workspace query was not selected.' }
    }
}
Test-Case 'Explicit start preserves an initial new-API outage' {
    $script:queryRows = @(
        (New-Record Legacy $StartTime.AddMinutes(10)),
        (New-Record Legacy $StartTime.AddHours(1)),
        (New-Record New $StartTime.AddHours(1))
    )
    $result = Invoke-Check
    Assert-Equal $result.Status 'Fail' 'Outage result'
    Assert-Equal $result.Details.Start $StartTime 'Explicit start'
    Assert-Equal $result.Details.OnlyInLegacy 1 'Missing initial record'
}
Test-Case 'Shortening a default window remains inconclusive' {
    $script:bound.Remove('StartTime')
    $script:queryRows = @((New-Record Legacy $EndTime.AddHours(-1)), (New-Record New $EndTime.AddHours(-1)))
    $result = Invoke-Check
    Assert-Equal $result.Status 'Warning' 'Shortened-window status'
    Assert-Equal @($result.Findings | Where-Object Item -eq 'Unverified interval').Count 1 'Omitted interval finding'
}
Test-Case 'Identical payloads hours apart cannot cancel out' {
    $script:queryRows = @((New-Record Legacy $StartTime.AddMinutes(10)), (New-Record New $StartTime.AddHours(2)))
    $result = Invoke-Check
    Assert-Equal $result.Status 'Fail' 'Out-of-tolerance status'
    Assert-Equal $result.Details.OnlyInLegacy 1 'Unmatched legacy occurrence'
}
Test-Case 'Duplicate occurrences are matched one-to-one' {
    $script:queryRows = @((New-Record Legacy $StartTime), (New-Record Legacy $StartTime.AddMinutes(1)), (New-Record New $StartTime))
    $result = Invoke-Check
    Assert-Equal $result.Status 'Fail' 'Duplicate deficit'
    Assert-Equal $result.Details.OnlyInLegacy 1 'Duplicate count'
}
Test-Case 'Equal duplicate multiplicities pass' {
    $script:queryRows = @((New-Record Legacy $StartTime), (New-Record Legacy $StartTime),
        (New-Record New $StartTime.AddMinutes(1)), (New-Record New $StartTime.AddMinutes(1)))
    $result = Invoke-Check
    Assert-Equal $result.Status 'Pass' 'Duplicate multiplicities'
    Assert-Equal $result.Details.LegacyRows 2 'Full duplicate count'
}
Test-Case 'One thousand repeated payload occurrences are counted independently' {
    $script:queryRows = @(for ($i = 0; $i -lt 1000; $i++) {
        New-Record Legacy $StartTime.AddSeconds($i)
        New-Record New $StartTime.AddSeconds($i).AddMilliseconds(1)
    })
    $result = Invoke-Check
    Assert-Equal $result.Status 'Pass' 'Repeated-payload comparison'
    Assert-Equal $result.Details.LegacyRows 1000 'Legacy occurrence count'
    Assert-Equal $result.Details.NewRows 1000 'New occurrence count'
}
Test-Case 'New-only records warn instead of claiming complete equivalence' {
    $script:queryRows = @((New-Record New $StartTime))
    Assert-Equal (Invoke-Check).Status 'Warning' 'New-only result'
}
Test-Case 'Earliest feasible matching handles overlapping tolerance windows' {
    $script:queryRows = @((New-Record Legacy $StartTime), (New-Record Legacy $StartTime.AddMinutes(5)),
        (New-Record New $StartTime.AddMinutes(4)), (New-Record New $StartTime.AddMinutes(10)))
    Assert-Equal (Invoke-Check).Status 'Pass' 'Overlapping windows'
}
Test-Case 'Outside-window counterpart is warning, not Pass' {
    $script:queryRows = @((New-Record Legacy $StartTime), (New-Record New $StartTime.AddMinutes(-1)))
    $result = Invoke-Check
    Assert-Equal $result.Status 'Warning' 'Boundary status'
    Assert-Equal $result.Details.BoundaryLegacyRows 1 'Boundary count'
}
Test-Case 'Inside-window matches take priority over outside records' {
    $script:queryRows = @((New-Record Legacy $StartTime.AddMinutes(-1)), (New-Record Legacy $StartTime),
        (New-Record New $StartTime))
    Assert-Equal (Invoke-Check).Status 'Pass' 'In-window priority'
}
Test-Case 'New occurrence matched to an outside legacy row warns' {
    $script:queryRows = @((New-Record New $EndTime), (New-Record Legacy $EndTime.AddSeconds(1)))
    $result = Invoke-Check
    Assert-Equal $result.Status 'Warning' 'New boundary status'
    Assert-Equal $result.Details.BoundaryNewRows 1 'New boundary count'
}
Test-Case 'Zero tolerance requires exact occurrence times' {
    $script:TimeTolerance = [timespan]::Zero
    $script:queryRows = @((New-Record Legacy $StartTime), (New-Record New $StartTime.AddTicks(1)))
    Assert-Equal (Invoke-Check).Status 'Fail' 'One-tick difference'
}
Test-Case 'Negative tolerance fails explicitly' {
    $script:TimeTolerance = [timespan]::FromSeconds(-1)
    Assert-Throws { Invoke-Check } 'must not be negative'
}
foreach ($milliseconds in @(1500, 1501)) {
    Test-Case "Fractional tolerance boundary at $milliseconds milliseconds" {
        $script:TimeTolerance = [timespan]::FromMilliseconds(1500)
        $script:queryRows = @((New-Record Legacy $StartTime), (New-Record New $StartTime.AddMilliseconds($milliseconds)))
        Assert-Equal (Invoke-Check).Status ($milliseconds -eq 1500 ? 'Pass' : 'Fail') 'Fractional threshold'
        if ($queryCalls[0] -notmatch '1\.5s') { throw 'Query buffer lost fractional precision.' }
    }
}
foreach ($pair in @(@('1', '1.000e0'), @('9007199254740992', '9.007199254740992e15'), @('-0', '0.0'))) {
    Test-Case "Equivalent numbers $($pair -join ' / ') match" {
        $script:queryRows = @((New-Record Legacy $StartTime @{ ReplicaId_d = $pair[0] }),
            (New-Record New $StartTime @{ ReplicaId_d = $pair[1] }))
        Assert-Equal (Invoke-Check).Status 'Pass' 'Equivalent numeric representation'
    }
}
foreach ($value in @('0', '9007199254740993')) {
    Test-Case "Real large-number change to $value fails" {
        $script:queryRows = @((New-Record Legacy $StartTime @{ ReplicaId_d = '9007199254740992' }),
            (New-Record New $StartTime @{ ReplicaId_d = $value }))
        $result = Invoke-Check
        Assert-Equal $result.Status 'Fail' 'Actual numeric difference'
        $difference = @($result.Details.ColumnDifferences | Where-Object Column -CEQ 'ReplicaId_d')
        Assert-Equal $difference.Count 1 'Large-value diagnostic column'
        Assert-Equal $difference[0].KnownLargeNumber $false 'No automatic precision waiver'
    }
}
foreach ($value in @('NaN', 'Infinity', '-Infinity')) {
    Test-Case "Non-finite numeric value $value is rejected" {
        $script:queryRows = @((New-Record Legacy $StartTime @{ Value_d = $value }))
        Assert-Throws { Invoke-Check } 'non-finite'
    }
}
Test-Case 'Named-metric generated IDs differ but payloads and event times match' {
    $script:queryRows = @(
        (New-Record Legacy $StartTime @{ property_s = 'metric'; id_s = "FO_$([guid]::NewGuid())"; datetime_t = $StartTime; value_d = 1 }),
        (New-Record New $StartTime.AddSeconds(1) @{ property_s = 'metric'; id_s = "FO_$([guid]::NewGuid())"; datetime_t = $StartTime.AddSeconds(1); value_d = '1.0' })
    )
    Assert-Equal (Invoke-Check).Status 'Pass' 'Generated metric metadata'
}
Test-Case 'Invalid named-metric ID fails explicitly' {
    $script:queryRows = @((New-Record Legacy $StartTime @{ property_s = 'metric'; id_s = 'invalid'; datetime_t = $StartTime }))
    Assert-Throws { Invoke-Check } 'invalid generated id'
}
Test-Case 'Differing named-metric values still fail with valid generated metadata' {
    $script:queryRows = @(
        (New-Record Legacy $StartTime @{ property_s = 'metric'; id_s = "FO_$([guid]::NewGuid())"; datetime_t = $StartTime; value_d = 1 }),
        (New-Record New $StartTime @{ property_s = 'metric'; id_s = "FO_$([guid]::NewGuid())"; datetime_t = $StartTime; value_d = 2 })
    )
    $result = Invoke-Check
    Assert-Equal $result.Status 'Fail' 'Substantive named-metric difference'
    Assert-Equal @($result.Details.ColumnDifferences | Where-Object Column -CEQ 'value_d').Count 1 'Named-metric value diagnostic'
    Assert-Equal @($result.Details.ColumnDifferences | Where-Object { $_.Column -in @('id_s', 'datetime_t') }).Count 0 'Generated fields excluded from exact-value diagnostics'
}
Test-Case 'Missing named-metric timestamp fails explicitly' {
    $script:queryRows = @((New-Record Legacy $StartTime @{ property_s = 'metric'; id_s = "FO_$([guid]::NewGuid())" }))
    Assert-Throws { Invoke-Check } 'invalid record timestamp'
}
foreach ($difference in @(1, 301)) {
    Test-Case "Upgrade timestamps differ by $difference seconds" {
        $script:queryRows = @(
            (New-Record Legacy $StartTime @{ UpgradeState_s = 'RollingForward'; Timestamp_t = $StartTime }),
            (New-Record New $StartTime @{ UpgradeState_s = 'RollingForward'; Timestamp_t = $StartTime.AddSeconds($difference) })
        )
        Assert-Equal (Invoke-Check).Status ($difference -le 300 ? 'Pass' : 'Fail') 'Upgrade timestamp tolerance'
    }
}
Test-Case 'Generated metadata cannot hide substantive payload changes' {
    $script:queryRows = @(
        (New-Record Legacy $StartTime @{ UpgradeState_s = 'RollingForward'; Timestamp_t = $StartTime; Value_d = 1 }),
        (New-Record New $StartTime @{ UpgradeState_s = 'RollingForward'; Timestamp_t = $StartTime.AddSeconds(1); Value_d = 2 })
    )
    Assert-Equal (Invoke-Check).Status 'Fail' 'Substantive upgrade difference'
}
Test-Case 'ClusterObserver upgrade records retain tolerant matching' {
    $script:Application = 'ClusterObserver'
    foreach ($column in $recordColumns) {
        $declared = @($schema.applications.ClusterObserver | Where-Object name -CEQ $column.name)
        if ($declared.Count -ne 1 -or $declared[0].type -ne $column.type) { throw 'ClusterObserver fixture schema differs.' }
    }
    $script:queryRows = @(
        (New-Record Legacy $StartTime @{ UpgradeState_s = 'RollingForward'; Timestamp_t = $StartTime }),
        (New-Record New $StartTime @{ UpgradeState_s = 'RollingForward'; Timestamp_t = $StartTime.AddSeconds(1) })
    )
    $result = Invoke-Check
    Assert-Equal $result.Status 'Pass' 'ClusterObserver upgrade comparison'
    Assert-Equal $result.Application 'ClusterObserver' 'Application output'
}
Test-Case 'Diagnostic sampling cannot turn missing records into Pass' {
    $script:MaxDetailRows = 1
    $script:queryRows = @((New-Record Legacy $StartTime), (New-Record Legacy $StartTime.AddMinutes(1)))
    $result = Invoke-Check
    Assert-Equal $result.Status 'Fail' 'Sampled deficit'
    Assert-Equal $result.Details.OnlyInLegacy 2 'Complete missing count'
    Assert-Equal $result.Details.DetailRowsSampled $true 'Diagnostic sampling flag'
}
foreach ($after in @(1, 9, 10)) {
    Test-Case "Missing populated column at $after post-cutover rows" {
        Set-CutoverRows 10 $after 0
        Assert-Equal (Invoke-Check).Status ($after -lt 10 ? 'Warning' : 'Fail') 'MinRows boundary'
    }
}
Test-Case 'Sufficient matching cutover data passes' {
    Set-CutoverRows 10 10 10
    Assert-Equal (Invoke-Check).Status 'Pass' 'Sufficient cutover sample'
}
Test-Case 'No pre-cutover baseline is inconclusive' {
    Set-CutoverRows 0 10 10
    Assert-Equal (Invoke-Check).Status 'Warning' 'Absent baseline'
}
Test-Case 'A sparsely populated baseline column remains inconclusive' {
    Set-CutoverRows 10 10 0
    $script:queryRows[0].n_Value_d = 1
    Assert-Equal (Invoke-Check).Status 'Warning' 'Sparse-column continuity'
}
Test-Case 'JSON output and PassThru retain explicit inconclusive status' {
    Set-CutoverRows 10 1 0
    $script:OutputPath = [IO.Path]::GetTempFileName()
    try {
        $result = Invoke-Check
        $saved = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
        Assert-Equal $result.Status 'Warning' 'PassThru status'
        Assert-Equal $saved.Status 'Warning' 'JSON status'
        foreach ($field in @('Check', 'Application', 'Workspace', 'Status', 'CheckedUtc', 'Details', 'Findings')) {
            if ($field -notin $saved.PSObject.Properties.Name) { throw "Missing output field $field." }
        }
    } finally { Remove-Item -LiteralPath $OutputPath -Force }
}
Test-Case 'Empty comparison is inconclusive' {
    Assert-Equal (Invoke-Check).Status 'Warning' 'Empty period'
}
"Migration checker PASS: $cases regression cases; all Azure calls intercepted."
