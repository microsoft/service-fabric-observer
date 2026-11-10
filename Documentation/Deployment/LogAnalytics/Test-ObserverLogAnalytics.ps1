#Requires -Version 7.0
#Requires -Modules Az.Accounts
<#
.SYNOPSIS
Read-only checks for moving FabricObserver or ClusterObserver Log Analytics data to the Logs Ingestion API.

.DESCRIPTION
The script changes nothing. Sign in first with Connect-AzAccount; reading the workspace (for example the Log Analytics
Reader role) is enough.

-Check Table
    Inspects a table's definition and recent data, using the table resource (queries report GUID columns as strings).
    Before conversion: lists columns that are not standard FabricObserver/ClusterObserver columns (custom or extension
    fields) and recommends single write or dual write. Single write is one-way: adding columns after conversion
    can stop legacy ingestion for the entire table, including otherwise standard records.
    After deploying the template: confirms every standard column exists with the expected type, not legacy rollback.

-Check DualWrite
    Compares the old table with the new table over the same period. Rows are matched on the values of all standard
    columns both tables have; TimeGenerated differs between the two APIs and is not compared. Unmatched rows are paired
    by node, observer and property to show which columns differ. Column names are case-sensitive, as in KQL.
    Counts inside the requested period are compared first. A count difference that could be covered by an identical
    record outside the period, within TimeTolerance, is a warning rather than proof of a match or a missing record.

-Check Cutover
    Single write: compares how often each column is filled, per observer, before and after the switch to
    LogsIngestionOnly in the same table. A column that was filled before and is never filled after indicates data loss.

.EXAMPLE
.\Test-ObserverLogAnalytics.ps1 -Check Table -Application FabricObserver -ResourceGroupName my-rg -WorkspaceName my-workspace -TableName FabricObserver_CL

.EXAMPLE
.\Test-ObserverLogAnalytics.ps1 -Check DualWrite -Application FabricObserver -ResourceGroupName my-rg -WorkspaceName my-workspace -LegacyTableName FabricObserver_CL -NewTableName FabricObserverV2_CL

.EXAMPLE
.\Test-ObserverLogAnalytics.ps1 -Check Cutover -Application ClusterObserver -ResourceGroupName my-rg -WorkspaceName my-workspace -TableName ClusterObserver_CL -CutoverTime '2026-11-03T15:00:00Z'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Table', 'DualWrite', 'Cutover')][string] $Check,
    [Parameter(Mandatory)][ValidateSet('FabricObserver', 'ClusterObserver')][string] $Application,
    [Parameter(Mandatory)][string] $ResourceGroupName,
    [Parameter(Mandatory)][string] $WorkspaceName,
    # Defaults to the subscription of the current Az context.
    [string] $SubscriptionId,

    # Table and Cutover: the table to inspect.
    [ValidatePattern('^[A-Za-z0-9_]+$')][string] $TableName,
    # Table: how far back to count rows that use non-standard columns.
    [ValidateRange(1, 90)][int] $LookbackDays = 7,

    # DualWrite: the table written by the old API and the table written by the new API.
    [ValidatePattern('^[A-Za-z0-9_]+$')][string] $LegacyTableName,
    [ValidatePattern('^[A-Za-z0-9_]+$')][string] $NewTableName,
    # DualWrite: workspace of the new table when it differs from WorkspaceName.
    [string] $NewWorkspaceName,
    [string] $NewResourceGroupName,
    # DualWrite: period to compare. Default: the 24 hours ending 30 minutes ago. The start moves forward automatically
    # if the new table has no rows that early (before dual write began).
    [datetime] $StartTime,
    [datetime] $EndTime,
    # DualWrite: buffer around the comparison period and maximum time difference for diagnostic pairing.
    [timespan] $TimeTolerance = [timespan]::FromMinutes(10),
    # DualWrite: maximum unmatched rows per table to fetch for the column-difference breakdown.
    [ValidateRange(1, 5000)][int] $MaxDetailRows = 500,

    # Cutover: when the application switched to LogsIngestionOnly. Without a 'Z' or offset, local time is assumed.
    [datetime] $CutoverTime,
    # Cutover: length of the period compared on each side of CutoverTime.
    [timespan] $Window = [timespan]::FromDays(1),
    # Cutover: period around CutoverTime that is ignored while the upgrade rolls through the cluster.
    [timespan] $Margin = [timespan]::FromMinutes(30),
    # Cutover: fewest rows an observer needs after the switch before a missing column is reported.
    [ValidateRange(1, 100000)][int] $MinRows = 10,

    # Writes the full result as JSON.
    [string] $OutputPath,
    # Returns the result object.
    [switch] $PassThru
)

$ErrorActionPreference = 'Stop'
$bound = $PSBoundParameters
$schema = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LogAnalyticsSchema.json') -Raw | ConvertFrom-Json
$recordColumns = @($schema.applications.$Application)
$recordTypes = [hashtable]::new([StringComparer]::Ordinal); foreach ($c in $recordColumns) { $recordTypes[$c.name] = $c.type }
$findings = [System.Collections.Generic.List[object]]::new()

function Add-Finding([ValidateSet('Fail', 'Warning', 'Info')][string] $Severity, [string] $Item, [string] $Detail) {
    $findings.Add([pscustomobject]@{ Severity = $Severity; Item = $Item; Detail = $Detail })
}

function Assert-Parameters([string[]] $Names) {
    $missing = @($Names | Where-Object { -not $bound.ContainsKey($_) })
    if ($missing) { throw "-Check $Check requires: $(($missing | ForEach-Object { "-$_" }) -join ', ')" }
}

function Get-Token([string] $ResourceUrl) {
    $t = Get-AzAccessToken -ResourceUrl $ResourceUrl -WarningAction SilentlyContinue -ErrorAction Stop
    if ($t.Token -is [securestring]) { return [Net.NetworkCredential]::new('', $t.Token).Password }
    return $t.Token
}

function Invoke-Arm([string] $Path) {
    $r = Invoke-AzRestMethod -Method GET -Path $Path -ErrorAction Stop
    if ($r.StatusCode -eq 404) { return $null }
    if ($r.StatusCode -ge 300) { throw "Azure request failed (HTTP $($r.StatusCode)) for $Path`: $($r.Content)" }
    return $r.Content | ConvertFrom-Json -Depth 50
}

function Get-Workspace([string] $ResourceGroup, [string] $Name) {
    $path = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.OperationalInsights/workspaces/$Name"
    $ws = Invoke-Arm "$path`?api-version=2022-10-01"
    if (-not $ws) { throw "Workspace '$Name' not found in resource group '$ResourceGroup' (subscription $SubscriptionId)." }
    return [pscustomobject]@{ Path = $path; CustomerId = $ws.properties.customerId; Name = $Name }
}

function Get-Table($Workspace, [string] $Name) {
    $t = Invoke-Arm "$($Workspace.Path)/tables/$Name`?api-version=2022-10-01"
    if (-not $t) { return $null }
    $s = $t.properties.schema
    $builtIn = @(@($s.standardColumns.name) + @($schema.standardColumns.name) + 'TenantId' | Select-Object -Unique)
    return [pscustomobject]@{
        Name = $Name
        SubType = $s.tableSubType
        Columns = @($s.columns | Where-Object { $_.name -cnotin $builtIn } | ForEach-Object { [pscustomobject]@{ Name = $_.name; Type = $_.type } })
    }
}

function Invoke-Query([string] $WorkspaceCustomerId, [string] $Kql) {
    $environment = (Get-AzContext).Environment
    $resource = $environment.AzureOperationalInsightsEndpointResourceId ?? 'https://api.loganalytics.io'
    $endpoint = ($environment.AzureOperationalInsightsEndpoint ?? 'https://api.loganalytics.io/v1').TrimEnd('/')
    $token = Get-Token $resource
    try {
        $r = Invoke-RestMethod -Method Post -Uri "$endpoint/workspaces/$WorkspaceCustomerId/query" -TimeoutSec 660 `
            -Headers @{ Authorization = "Bearer $token"; Prefer = 'wait=600' } -ContentType 'application/json' `
            -Body (@{ query = $Kql } | ConvertTo-Json -Compress)
    } catch {
        throw "Log Analytics query failed: $($_.ErrorDetails.Message ?? $_.Exception.Message)"
    } finally { $token = $null }
    $table = $r.tables[0]
    foreach ($row in $table.rows) {
        $o = [System.Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        for ($i = 0; $i -lt $table.columns.Count; $i++) { $o[$table.columns[$i].name] = $row[$i] }
        $o
    }
}

function Format-Kql([datetime] $Time) { "datetime($($Time.ToUniversalTime().ToString('o')))" }
function Format-Span([timespan] $Span) { "$([long]$Span.TotalSeconds)s" }
function Format-Column([string] $Name) { "['$Name']" }
function Format-List([string[]] $Items, [int] $Max = 15) {
    if ($Items.Count -le $Max) { return $Items -join ', ' }
    return (($Items | Select-Object -First $Max) -join ', ') + ", ... ($($Items.Count) total)"
}

# --- Table -------------------------------------------------------------------------------------------------------------
function Test-Table($Workspace) {
    $table = Get-Table $Workspace $TableName
    if (-not $table) {
        Add-Finding Info $TableName 'Table does not exist. For fresh setup or dual write, deploy the template with createTable=true.'
        return [ordered]@{ Exists = $false; Recommendation = 'CreateTable' }
    }
    $byName = [hashtable]::new([StringComparer]::Ordinal); foreach ($c in $table.Columns) { $byName[$c.Name] = $c.Type }
    $missing = @($recordColumns | Where-Object { -not $byName.ContainsKey($_.name) } | ForEach-Object { $_.name })
    $wrongType = @($recordColumns | Where-Object { $byName.ContainsKey($_.name) -and $byName[$_.name] -ne $_.type } |
        ForEach-Object { [pscustomobject]@{ Column = $_.name; Type = $byName[$_.name]; Expected = $_.type } })
    $nonStandard = @($table.Columns | Where-Object { -not $recordTypes.ContainsKey($_.Name) })

    $rows = $null
    $usage = [hashtable]::new([StringComparer]::Ordinal)
    $counts = @(@('Rows = count()') + @($nonStandard | ForEach-Object { "$(Format-Column $_.Name) = countif(isnotempty(tostring($(Format-Column $_.Name))))" }))
    $result = @(Invoke-Query $Workspace.CustomerId "$TableName | where TimeGenerated > ago($($LookbackDays)d) | summarize $($counts -join ', ')")
    if ($result) {
        $rows = [long]$result[0].Rows
        foreach ($c in $nonStandard) { $usage[$c.Name] = [long]$result[0].($c.Name) }
    }
    $nonStandardReport = @($nonStandard | ForEach-Object {
        [pscustomobject]@{ Column = $_.Name; Type = $_.Type; RowsWithValue = $usage[$_.Name]; LegacyCustomField = $_.Name.EndsWith('_CF') }
    })
    $inUse = @($nonStandardReport | Where-Object { $_.RowsWithValue -gt 0 })
    $unused = @($nonStandardReport | Where-Object { -not ($_.RowsWithValue -gt 0) })

    foreach ($w in $wrongType) {
        Add-Finding Fail $w.Column "Type is $($w.Type); the standard schema expects $($w.Expected). The template cannot change a column's type. Resolve this before migrating."
    }
    $customFields = @($nonStandardReport | Where-Object LegacyCustomField)
    if ($customFields) {
        Add-Finding Warning 'Legacy custom fields' "$(Format-List $customFields.Column): extracted by the legacy custom-fields feature, which stops working after conversion."
    }

    if ($table.SubType -eq 'Classic') {
        if ($missing) {
            Add-Finding Info 'Standard columns not in table' "$($missing.Count) of $($recordColumns.Count) ($(Format-List $missing)). Normal for a table created by the old API; the template adds them after conversion."
        }
        if ($inUse) {
            Add-Finding Warning 'Non-standard columns in use' "$(Format-List ($inUse | ForEach-Object { "$($_.Column) ($($_.RowsWithValue) rows)" })) in the last $LookbackDays days. The new API does not write them, and after conversion the old API drops any row that has a field without a matching column, so rollback would lose those records. Use dual write (new table)."
            $recommendation = 'DualWrite'
        } elseif ($unused) {
            Add-Finding Warning 'Non-standard columns without recent data' "$(Format-List $unused.Column). They are kept after conversion, but the new API does not write them. Make sure nothing sends these fields anymore before choosing single write."
            $recommendation = 'SingleWrite'
        } else {
            Add-Finding Info 'Route' 'Only standard columns. Single write preserves the table name but is one-way. Use dual write (new table) if legacy rollback is required.'
            $recommendation = 'SingleWrite'
        }
        Add-Finding Warning 'Single-write rollback' 'Conversion is irreversible. The template adds missing columns after conversion; Microsoft warns that adding columns stops legacy ingestion for the entire table. Do not rely on LegacyOnly or an old package to restore delivery.'
    } else {
        if ($missing) {
            Add-Finding Warning 'Standard columns missing' "$($missing.Count) of $($recordColumns.Count) ($(Format-List $missing)). The new API drops these fields until they exist. The template with createTable=false adds them, but adding columns can stop legacy ingestion for the entire table. Treat this as a one-way change."
        }
        if ($nonStandard) {
            Add-Finding Info 'Non-standard columns' "$(Format-List $nonStandard.Name). Kept, but the new API does not write them."
        }
        Add-Finding Info 'Legacy rollback' 'Schema readiness does not prove legacy delivery. Adding columns after conversion can stop legacy ingestion; switching to LegacyOnly or an old package does not undo these changes.'
        $recommendation = if ($missing -or $wrongType) { 'DeployTemplate' } else { 'Ready' }
    }
    return [ordered]@{
        Exists = $true; TableType = $table.SubType; RowsInLookback = $rows; Recommendation = $recommendation
        StandardColumnsPresent = $recordColumns.Count - $missing.Count; StandardColumnsExpected = $recordColumns.Count
        MissingStandardColumns = $missing; WrongTypeColumns = $wrongType; NonStandardColumns = $nonStandardReport
    }
}

# --- DualWrite ---------------------------------------------------------------------------------------------------------
function Test-DualWrite($Workspace) {
    $newWorkspace = if ($NewWorkspaceName -and $NewWorkspaceName -ne $WorkspaceName) {
        Get-Workspace ($NewResourceGroupName ? $NewResourceGroupName : $ResourceGroupName) $NewWorkspaceName
    } else { $Workspace }
    $legacy = Get-Table $Workspace $LegacyTableName
    $new = Get-Table $newWorkspace $NewTableName
    if (-not $legacy) { throw "Table '$LegacyTableName' not found in workspace '$($Workspace.Name)'." }
    if (-not $new) { throw "Table '$NewTableName' not found in workspace '$($newWorkspace.Name)'." }

    $newMissing = @($recordColumns.name | Where-Object { $_ -cnotin $new.Columns.Name })
    if ($newMissing) { Add-Finding Warning 'New table missing standard columns' "$(Format-List $newMissing). Redeploy the template." }
    $common = @($recordColumns.name | Where-Object { $_ -cin $legacy.Columns.Name -and $_ -cin $new.Columns.Name })
    $newRef = if ($newWorkspace.CustomerId -ne $Workspace.CustomerId) { "workspace('$($newWorkspace.CustomerId)').$NewTableName" } else { $NewTableName }

    $end = if ($bound.ContainsKey('EndTime')) { $EndTime.ToUniversalTime() } else { [datetime]::UtcNow.AddMinutes(-30) }
    $start = if ($bound.ContainsKey('StartTime')) { $StartTime.ToUniversalTime() } else { $end.AddDays(-1) }
    if ($start -ge $end) { throw 'StartTime must be before EndTime.' }
    $first = @(Invoke-Query $Workspace.CustomerId "$newRef | where TimeGenerated between ($(Format-Kql $start.AddDays(-30)) .. $(Format-Kql $end)) | summarize First = min(TimeGenerated)")
    if (-not $first -or -not $first[0].First) {
        Add-Finding Fail $NewTableName 'No rows in the new table before EndTime. Check the application settings and its health warnings.'
        return [ordered]@{ Start = $start; End = $end; CommonColumns = $common.Count }
    }
    $firstNew = ([datetime]$first[0].First).ToUniversalTime()
    if ($firstNew -gt $start) {
        $start = $firstNew
        Add-Finding Info 'Start time' "Moved to $($start.ToString('u')), the first new-table row, to skip records sent before dual write began."
        if ($start -ge $end) {
            Add-Finding Warning 'Period' 'Dual write started too recently to compare. Wait, or set -EndTime later.'
            return [ordered]@{ Start = $start; End = $end; CommonColumns = $common.Count }
        }
    }

    # Functions take at most 64 arguments, so hash the values in chunks.
    $chunks = for ($i = 0; $i -lt $common.Count; $i += 40) {
        "tostring(pack_array($(($common[$i..([math]::Min($i + 39, $common.Count - 1))] | ForEach-Object { "tostring($(Format-Column $_))" }) -join ', ')))"
    }
    $fp = "hash_sha256(strcat($(@($chunks) -join ', ')))"
    $lets = @"
let s = $(Format-Kql $start); let e = $(Format-Kql $end); let tol = $(Format-Span $TimeTolerance);
let OldT = materialize($LegacyTableName | where TimeGenerated between ((s - tol) .. (e + tol)) | extend fp = $fp);
let NewT = materialize($newRef | where TimeGenerated between ((s - tol) .. (e + tol)) | extend fp = $fp);
let C = OldT | summarize OldRows = count(), OldIn = countif(TimeGenerated between (s .. e)) by fp
    | join kind=fullouter (NewT | summarize NewRows = count(), NewIn = countif(TimeGenerated between (s .. e)) by fp) on fp
    | project fp = coalesce(fp, fp1), OldRows = coalesce(OldRows, 0), NewRows = coalesce(NewRows, 0), OldIn = coalesce(OldIn, 0), NewIn = coalesce(NewIn, 0);
"@
    $summary = @(Invoke-Query $Workspace.CustomerId ($lets + @'
C | summarize Old = sum(OldIn), New = sum(NewIn),
    OnlyOld = sum(max_of(OldIn - NewRows, 0)),
    OnlyNew = sum(max_of(NewIn - OldRows, 0)),
    BoundaryOld = sum(min_of(max_of(OldIn - NewIn, 0), NewRows - NewIn)),
    BoundaryNew = sum(min_of(max_of(NewIn - OldIn, 0), OldRows - OldIn))
'@))[0]
    $result = [ordered]@{
        Start = $start; End = $end; CommonColumns = $common.Count
        LegacyRows = [long]$summary.Old; NewRows = [long]$summary.New
        OnlyInLegacy = [long]$summary.OnlyOld; OnlyInNew = [long]$summary.OnlyNew
        BoundaryLegacyRows = [long]$summary.BoundaryOld; BoundaryNewRows = [long]$summary.BoundaryNew
    }
    if ($result.LegacyRows -eq 0) { Add-Finding Warning $LegacyTableName 'No rows in the period. Is the old API still enabled (DualWrite)?' }
    if ($result.BoundaryLegacyRows -gt 0 -or $result.BoundaryNewRows -gt 0) {
        Add-Finding Warning 'Window boundary' "$($result.BoundaryLegacyRows) legacy and $($result.BoundaryNewRows) new row(s) have an excess count inside the period, but identical records outside it could cover the difference. Boundary matching is ambiguous; these are not confirmed missing rows. Inspect their timestamps or adjust the comparison period."
    }
    if ($result.OnlyInLegacy -eq 0 -and $result.OnlyInNew -eq 0) { return $result }

    $projection = (@('Source', 'TimeGenerated') + @($common | ForEach-Object { Format-Column $_ })) -join ', '
    $detail = @(Invoke-Query $Workspace.CustomerId ($lets + @"
let BadOld = C | where OldIn > NewRows | project fp, MissingRows = OldIn - NewRows;
let BadNew = C | where NewIn > OldRows | project fp, MissingRows = NewIn - OldRows;
union (OldT | where TimeGenerated between (s .. e) | join kind=inner BadOld on fp
           | sort by fp asc, TimeGenerated asc | serialize
           | extend MissingIndex = row_number(1, prev(fp) != fp) | where MissingIndex <= MissingRows
           | take $MaxDetailRows | extend Source = 'Legacy'),
      (NewT | where TimeGenerated between (s .. e) | join kind=inner BadNew on fp
           | sort by fp asc, TimeGenerated asc | serialize
           | extend MissingIndex = row_number(1, prev(fp) != fp) | where MissingIndex <= MissingRows
           | take $MaxDetailRows | extend Source = 'New')
| project $projection
"@))
    # Pair unmatched rows describing the same event, then list the columns whose values differ.
    $keyColumns = @('NodeName_s', 'ObserverName_s', 'Property_s', 'Metric_s', 'ApplicationName_s', 'ServiceName_s', 'PartitionId_g') | Where-Object { $_ -cin $common }
    $key = { param($r) ($keyColumns | ForEach-Object { "$($r.$_)" }) -join '|' }
    $newPool = [hashtable]::new([StringComparer]::Ordinal)
    foreach ($r in $detail | Where-Object Source -eq 'New') { $k = & $key $r; if (-not $newPool[$k]) { $newPool[$k] = [System.Collections.Generic.List[object]]::new() }; $newPool[$k].Add($r) }
    $diffs = [hashtable]::new([StringComparer]::Ordinal)
    $unpairedLegacy = 0
    foreach ($o in $detail | Where-Object Source -eq 'Legacy') {
        $candidates = $newPool[(& $key $o)]
        $match = $null
        if ($candidates) {
            $match = $candidates | Where-Object { [math]::Abs(([datetime]$_.TimeGenerated - [datetime]$o.TimeGenerated).TotalSeconds) -le $TimeTolerance.TotalSeconds } |
                Sort-Object { [math]::Abs(([datetime]$_.TimeGenerated - [datetime]$o.TimeGenerated).TotalSeconds) } | Select-Object -First 1
        }
        if (-not $match) { $unpairedLegacy++; continue }
        [void]$candidates.Remove($match)
        foreach ($c in $common) {
            $a = "$($o.$c)"; $b = "$($match.$c)"
            if ($a -ceq $b) { continue }
            if (-not $diffs[$c]) { $diffs[$c] = [ordered]@{ Column = $c; Rows = 0; KnownLargeNumber = $true; ExampleLegacy = $a; ExampleNew = $b } }
            $diffs[$c].Rows++
            $x = 0.0; $y = 0.0
            $large = $c.EndsWith('_d') -and [double]::TryParse($a, [ref]$x) -and [double]::TryParse($b, [ref]$y) -and
                [math]::Max([math]::Abs($x), [math]::Abs($y)) -ge 9007199254740992
            if (-not $large) { $diffs[$c].KnownLargeNumber = $false }
        }
    }
    $unpairedNew = @($newPool.Values | ForEach-Object { $_ }).Count
    $partial = $detail.Count -ge (2 * $MaxDetailRows) -or @($detail | Where-Object Source -eq 'Legacy').Count -ge $MaxDetailRows -or @($detail | Where-Object Source -eq 'New').Count -ge $MaxDetailRows
    $result.ColumnDifferences = @($diffs.Values | Sort-Object { -$_.Rows } | ForEach-Object { [pscustomobject]$_ })
    $result.UnpairedLegacyRows = $unpairedLegacy
    $result.UnpairedNewRows = $unpairedNew
    $result.DetailRowsSampled = $partial

    foreach ($d in $result.ColumnDifferences) {
        if ($d.KnownLargeNumber) {
            Add-Finding Info $d.Column "$($d.Rows) row(s) differ only in a number beyond double precision (known limitation). Example: legacy '$($d.ExampleLegacy)', new '$($d.ExampleNew)'."
        } else {
            Add-Finding Fail $d.Column "$($d.Rows) row(s) have a different value. Example: legacy '$($d.ExampleLegacy)', new '$($d.ExampleNew)'."
        }
    }
    if ($unpairedLegacy) { Add-Finding Fail 'Missing in new table' "$unpairedLegacy legacy row(s) have no counterpart in $NewTableName." }
    if ($unpairedNew) { Add-Finding Fail 'Missing in legacy table' "$unpairedNew new row(s) have no counterpart in $LegacyTableName." }
    if ($partial) { Add-Finding Warning 'Sample' "Column breakdown is based on the first $MaxDetailRows unmatched rows per table. Increase -MaxDetailRows for more." }
    return $result
}

# --- Cutover -----------------------------------------------------------------------------------------------------------
function Test-Cutover($Workspace) {
    $table = Get-Table $Workspace $TableName
    if (-not $table) { throw "Table '$TableName' not found in workspace '$($Workspace.Name)'." }
    $cut = $CutoverTime.ToUniversalTime()
    $afterEnd = [datetime]::UtcNow
    if ($cut.Add($Window) -lt $afterEnd) { $afterEnd = $cut.Add($Window) }
    if ($afterEnd -le $cut.Add($Margin)) { throw 'No data after CutoverTime + Margin yet. Run the check later or reduce -Margin.' }
    $columns = @($table.Columns.Name)
    $group = if ('ObserverName_s' -cin $columns) { 'ObserverName_s' } else { $null }
    $counts = @('Rows = count()') + @($columns | ForEach-Object { "$(Format-Column "n_$_") = countif(isnotempty(tostring($(Format-Column $_))))" })
    $kql = @"
let c = $(Format-Kql $cut); let w = $(Format-Span $Window); let m = $(Format-Span $Margin); let ae = $(Format-Kql $afterEnd);
$TableName
| where TimeGenerated between ((c - w) .. ae) and not(TimeGenerated between ((c - m) .. (c + m)))
| extend Period = iff(TimeGenerated < c, 'Before', 'After')
| summarize $($counts -join ', ') by Period$(if ($group) { ", $group" })
"@
    $rows = @(Invoke-Query $Workspace.CustomerId $kql)
    $observers = @($rows | ForEach-Object { $group ? "$($_.$group)" : '(all)' } | Select-Object -Unique | Sort-Object)
    $perObserver = foreach ($obs in $observers) {
        $before = $rows | Where-Object { $_.Period -eq 'Before' -and ($group ? "$($_.$group)" : '(all)') -eq $obs } | Select-Object -First 1
        $after = $rows | Where-Object { $_.Period -eq 'After' -and ($group ? "$($_.$group)" : '(all)') -eq $obs } | Select-Object -First 1
        $rb = [long]($before.Rows ?? 0); $ra = [long]($after.Rows ?? 0)
        $label = $obs ? $obs : '(no ObserverName)'
        if ($rb -gt 0 -and $ra -eq 0) { Add-Finding Fail $label "$rb rows before the switch, none after."; continue }
        if ($rb -eq 0) { Add-Finding Info $label "No rows before the switch; $ra after."; continue }
        if ($ra -lt $MinRows) { Add-Finding Info $label "Only $ra rows after the switch (fewer than -MinRows $MinRows); column fill rates not judged." }
        $lost = [System.Collections.Generic.List[string]]::new(); $dropped = [System.Collections.Generic.List[string]]::new(); $customLost = [System.Collections.Generic.List[string]]::new(); $rare = [System.Collections.Generic.List[string]]::new()
        foreach ($c in $columns) {
            $fb = [long]($before."n_$c" ?? 0); $fa = [long]($after."n_$c" ?? 0)
            if ($fb -eq 0 -or $ra -lt $MinRows) { continue }
            $pb = $fb / $rb; $pa = $fa / $ra
            # Rows expected to have this column after the switch if nothing changed; too few means absence proves nothing.
            $expected = $pb * $ra
            if ($fa -eq 0) {
                if (-not $recordTypes.ContainsKey($c)) { $customLost.Add($c) }
                elseif ($expected -lt 3) { $rare.Add($c) }
                else { $lost.Add("$c ($([math]::Round($pb * 100))% before)") }
            }
            elseif ($expected -ge 5 -and $pa -lt $pb / 2) { $dropped.Add("$c ($([math]::Round($pb * 100))% -> $([math]::Round($pa * 100))%)") }
        }
        if ($lost) { Add-Finding Fail $label "Standard columns filled before the switch but never after: $(Format-List $lost)." }
        if ($dropped) { Add-Finding Warning $label "Fill rate dropped by more than half: $(Format-List $dropped)." }
        if ($customLost) { Add-Finding Info $label "Non-standard columns no longer filled (the new API writes only standard columns): $(Format-List $customLost)." }
        if ($rare) { Add-Finding Info $label "Rarely filled columns not seen after the switch; too few rows to judge: $(Format-List $rare)." }
        [pscustomobject]@{ Observer = $label; RowsBefore = $rb; RowsAfter = $ra; StandardColumnsLost = $lost.Count; FillRateDrops = $dropped.Count }
    }
    if (-not $rows) { Add-Finding Fail $TableName 'No rows in the compared periods.' }
    return [ordered]@{
        Before = "$($cut.Subtract($Window).ToString('u')) - $($cut.Subtract($Margin).ToString('u'))"
        After = "$($cut.Add($Margin).ToString('u')) - $($afterEnd.ToString('u'))"
        Observers = @($perObserver)
    }
}

# --- Main --------------------------------------------------------------------------------------------------------------
switch ($Check) {
    'Table' { Assert-Parameters 'TableName' }
    'DualWrite' { Assert-Parameters 'LegacyTableName', 'NewTableName' }
    'Cutover' { Assert-Parameters 'TableName', 'CutoverTime' }
}
$context = Get-AzContext
if (-not $context) { throw 'Not signed in to Azure. Run Connect-AzAccount first.' }
if (-not $SubscriptionId) { $SubscriptionId = $context.Subscription.Id }
$workspace = Get-Workspace $ResourceGroupName $WorkspaceName

$details = switch ($Check) {
    'Table' { Test-Table $workspace }
    'DualWrite' { Test-DualWrite $workspace }
    'Cutover' { Test-Cutover $workspace }
}
$status = if ($findings.Severity -contains 'Fail') { 'Fail' } elseif ($findings.Severity -contains 'Warning') { 'Warning' } else { 'Pass' }
$result = [pscustomobject][ordered]@{
    Check = $Check; Application = $Application; Workspace = $WorkspaceName; Status = $status
    CheckedUtc = [datetime]::UtcNow.ToString('o'); Details = [pscustomobject]$details; Findings = @($findings)
}

$color = @{ Pass = 'Green'; Warning = 'Yellow'; Fail = 'Red'; Info = 'Gray' }
Write-Host "$Check check for $Application`: $status" -ForegroundColor $color[$status]
foreach ($k in $details.Keys) {
    $v = $details[$k]
    if ($v -is [System.Collections.IEnumerable] -and $v -isnot [string]) { continue }
    Write-Host "  $k`: $v"
}
foreach ($f in $findings) { Write-Host "  [$($f.Severity)] $($f.Item): $($f.Detail)" -ForegroundColor $color[$f.Severity] }
if ($OutputPath) { $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8; Write-Host "  Report: $OutputPath" }
if ($PassThru) { $result }
