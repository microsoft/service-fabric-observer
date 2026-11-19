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
    -Route DualWrite or Fresh omits the single-write rollback caveats, which do not apply to a separate new table.

-Check DualWrite
    Compares individual occurrences in the old and new tables. Standard payload values must match, with equivalent
    numeric representations normalized without rounding. Occurrences must be within TimeTolerance, using generated
    event timestamps for named metrics and upgrade events, or TimeGenerated otherwise. Generated IDs must be valid.
    Matches inside the requested period take precedence. A counterpart outside the period is a warning, not proof
    of a match within the period. Query errors and truncated results fail rather than certifying incomplete data.
    Rows only in the old table, or differing values, fail: the new API lost or changed data. Rows only in the new table
    are a warning: the old API can drop records without reporting an error.

-Check Cutover
    Single write: compares how often each column is filled, per observer, before and after the switch to
    LogsIngestionOnly in the same table. A column that was filled before and is never filled after indicates data loss.
    An observer with rows before and none after fails, unless it had fewer than MinRows before: rare, event-driven
    records are a warning to confirm manually.

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
    # Table: the chosen migration route. DualWrite and Fresh omit single-write rollback caveats; omit to show them.
    [ValidateSet('Fresh', 'SingleWrite', 'DualWrite')][string] $Route,
    # Table: how far back to count rows that use non-standard columns.
    [ValidateRange(1, 90)][int] $LookbackDays = 7,

    # DualWrite: the table written by the old API and the table written by the new API.
    [ValidatePattern('^[A-Za-z0-9_]+$')][string] $LegacyTableName,
    [ValidatePattern('^[A-Za-z0-9_]+$')][string] $NewTableName,
    # DualWrite: workspace of the new table when it differs from WorkspaceName.
    [string] $NewWorkspaceName,
    [string] $NewResourceGroupName,
    # DualWrite: period to compare. Default: the 24 hours ending 30 minutes ago. Only a default start can move
    # to the first new-table row; the omitted interval is then reported as unverified.
    [datetime] $StartTime,
    [datetime] $EndTime,
    # DualWrite: buffer around the comparison period and maximum difference between matched occurrence timestamps.
    [timespan] $TimeTolerance = [timespan]::FromMinutes(10),
    # DualWrite: maximum unmatched rows per table to fetch for the column-difference breakdown.
    [ValidateRange(1, 5000)][int] $MaxDetailRows = 500,

    # Cutover: when the application switched to LogsIngestionOnly. Without a 'Z' or offset, local time is assumed.
    [datetime] $CutoverTime,
    # Cutover: length of the period compared on each side of CutoverTime.
    [timespan] $Window = [timespan]::FromDays(1),
    # Cutover: period around CutoverTime that is ignored while the upgrade rolls through the cluster.
    [timespan] $Margin = [timespan]::FromMinutes(30),
    # Cutover: fewest rows an observer needs before the switch for its absence after to fail, and after the switch
    # before a missing column is reported.
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
    $endpoint = ($environment.AzureOperationalInsightsEndpoint ?? 'https://api.loganalytics.io').TrimEnd('/')
    if ($endpoint -notmatch '/v1$') { $endpoint += '/v1' }
    $token = Get-Token $resource
    try {
        $r = Invoke-RestMethod -Method Post -Uri "$endpoint/workspaces/$WorkspaceCustomerId/query" -TimeoutSec 660 `
            -Headers @{ Authorization = "Bearer $token"; Prefer = 'wait=600' } -ContentType 'application/json' `
            -Body (@{ query = $Kql } | ConvertTo-Json -Compress)
    } catch {
        throw "Log Analytics query failed: $($_.ErrorDetails.Message ?? $_.Exception.Message)"
    } finally { $token = $null }
    if ($null -ne $r.error) {
        throw "Log Analytics query returned an error; no partial results were accepted: $($r.error | ConvertTo-Json -Compress -Depth 10)"
    }
    if (-not $r.tables -or -not $r.tables[0].columns -or $null -eq $r.tables[0].rows) {
        throw 'Log Analytics query returned an invalid result table.'
    }
    $table = $r.tables[0]
    foreach ($row in $table.rows) {
        if ($row.Count -ne $table.columns.Count) { throw 'Log Analytics query returned an incomplete row.' }
        $o = [System.Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        for ($i = 0; $i -lt $table.columns.Count; $i++) { $o[$table.columns[$i].name] = $row[$i] }
        $o
    }
}

function Format-Kql([datetime] $Time) { "datetime($($Time.ToUniversalTime().ToString('o')))" }
function Format-Span([timespan] $Span) { "$($Span.TotalSeconds.ToString('0.#######', [Globalization.CultureInfo]::InvariantCulture))s" }
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
        if ($Route -notin 'DualWrite', 'Fresh') {
            Add-Finding Warning 'Single-write rollback' 'Conversion is irreversible. The template adds missing columns after conversion; Microsoft warns that adding columns stops legacy ingestion for the entire table. Do not rely on LegacyOnly or an old package to restore delivery.'
        }
    } else {
        if ($missing) {
            Add-Finding Warning 'Standard columns missing' "$($missing.Count) of $($recordColumns.Count) ($(Format-List $missing)). The new API drops these fields until they exist. The template with createTable=false adds them, but adding columns can stop legacy ingestion for the entire table. Treat this as a one-way change."
        }
        if ($nonStandard) {
            Add-Finding Info 'Non-standard columns' "$(Format-List $nonStandard.Name). Kept, but the new API does not write them."
        }
        if ($Route -notin 'DualWrite', 'Fresh') {
            Add-Finding Info 'Legacy rollback' 'Schema readiness does not prove legacy delivery. Adding columns after conversion can stop legacy ingestion; switching to LegacyOnly or an old package does not undo these changes.'
        }
        $recommendation = if ($missing -or $wrongType) { 'DeployTemplate' } else { 'Ready' }
    }
    return [ordered]@{
        Exists = $true; TableType = $table.SubType; RowsInLookback = $rows; Recommendation = $recommendation
        StandardColumnsPresent = $recordColumns.Count - $missing.Count; StandardColumnsExpected = $recordColumns.Count
        MissingStandardColumns = $missing; WrongTypeColumns = $wrongType; NonStandardColumns = $nonStandardReport
    }
}

# --- DualWrite ---------------------------------------------------------------------------------------------------------
function Get-NumericKey($Value) {
    # Normalize digits and exponent without rounding through floating point.
    $text = if ($Value -is [double] -or $Value -is [single]) {
        $Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    } else { [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture) }
    $m = [regex]::Match($text, '^(?<sign>[+-]?)(?<whole>\d*)(?:\.(?<fraction>\d*))?(?:[eE](?<exponent>[+-]?\d+))?$')
    if (-not $m.Success -or -not ($m.Groups['whole'].Value + $m.Groups['fraction'].Value)) {
        throw "Invalid or non-finite numeric query value '$text'."
    }
    $digits = ($m.Groups['whole'].Value + $m.Groups['fraction'].Value).TrimStart('0')
    if (-not $digits) { return '0' }
    $exponent = if ($m.Groups['exponent'].Success) {
        [int]::Parse($m.Groups['exponent'].Value, [Globalization.CultureInfo]::InvariantCulture)
    } else { 0 }
    $exponent -= $m.Groups['fraction'].Length
    $significant = $digits.TrimEnd('0')
    $exponent += $digits.Length - $significant.Length
    return "$(if ($m.Groups['sign'].Value -eq '-') { '-' })${significant}e$exponent"
}

function Get-RecordTime($Value) {
    if ($Value -is [datetimeoffset]) { return $Value.UtcDateTime }
    if ($Value -is [datetime]) {
        if ($Value.Kind -eq [DateTimeKind]::Local) { return $Value.ToUniversalTime() }
        return [datetime]::SpecifyKind($Value, [DateTimeKind]::Utc)
    }
    $time = [datetimeoffset]::MinValue
    if (-not [datetimeoffset]::TryParse(
        [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture),
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$time)) {
        throw "Missing or invalid record timestamp '$Value'."
    }
    return $time.UtcDateTime
}

function Get-ValueKey([string] $Column, $Value) {
    if ($null -eq $Value -or "$Value" -eq '') { return '' }
    switch ($recordTypes[$Column]) {
        'real' { return Get-NumericKey $Value }
        'datetime' { return (Get-RecordTime $Value).Ticks.ToString([Globalization.CultureInfo]::InvariantCulture) }
        'guid' { return ([guid]$Value).ToString('D') }
        default { return [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture) }
    }
}

function Get-RecordKey($Row, [string[]] $Columns) {
    $values = @($Columns | ForEach-Object { Get-ValueKey $_ $Row[$_] })
    return ConvertTo-Json -InputObject $values -Compress
}

function Get-Occurrence($Row, [string[]] $Columns, [datetime] $Start, [datetime] $End) {
    $time = Get-RecordTime $Row['TimeGenerated']
    $matchTime = $time
    $generated = @()
    $kind = 'Record'
    if ("$($Row['id_s'])" -or "$($Row['datetime_t'])" -or
        ("$($Row['property_s'])" -and -not "$($Row['healthState_s'])")) {
        $id = [guid]::Empty
        if ($Columns -cnotcontains 'id_s' -or $Columns -cnotcontains 'datetime_t' -or
            "$($Row['id_s'])" -notmatch '^FO_(.+)$' -or -not [guid]::TryParseExact($Matches[1], 'D', [ref]$id)) {
            throw 'Named metric metadata is missing or has an invalid generated id; its equivalence cannot be certified.'
        }
        $matchTime = Get-RecordTime $Row['datetime_t']
        $generated = @('id_s', 'datetime_t')
        $kind = 'NamedMetric'
    } elseif ("$($Row['UpgradeState_s'])" -or "$($Row['Timestamp_t'])") {
        if ($Columns -cnotcontains 'Timestamp_t') { throw 'Upgrade event timestamp is missing from the shared columns.' }
        $matchTime = Get-RecordTime $Row['Timestamp_t']
        $generated = @('Timestamp_t')
        $kind = 'Upgrade'
    }
    $payloadColumns = @($Columns | Where-Object { $_ -cnotin $generated })
    return [pscustomobject]@{
        Row = $Row; Time = $time; MatchTime = $matchTime; Generated = $generated
        Key = "$kind|$(Get-RecordKey $Row $payloadColumns)"
        InWindow = $time -ge $Start -and $time -le $End; Matched = $false
    }
}

function Match-Occurrences($Old, $New) {
    $oldGroups = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $newGroups = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($pair in @(@($Old, $oldGroups), @($New, $newGroups))) {
        foreach ($entry in $pair[0]) {
            if (-not $pair[1].ContainsKey($entry.Key)) { $pair[1][$entry.Key] = [Collections.Generic.List[object]]::new() }
            $pair[1][$entry.Key].Add($entry)
        }
    }
    foreach ($key in $oldGroups.Keys) {
        if (-not $newGroups.ContainsKey($key)) { continue }
        $oldRows = @($oldGroups[$key] | Sort-Object MatchTime)
        $newRows = @($newGroups[$key] | Sort-Object MatchTime)
        $i = 0; $j = 0
        while ($i -lt $oldRows.Count -and $j -lt $newRows.Count) {
            $delta = $oldRows[$i].MatchTime - $newRows[$j].MatchTime
            if ($delta -lt -$TimeTolerance) { $i++; continue }
            if ($delta -gt $TimeTolerance) { $j++; continue }
            $oldRows[$i].Matched = $true; $newRows[$j].Matched = $true
            [pscustomobject]@{ Legacy = $oldRows[$i]; New = $newRows[$j] }
            $i++; $j++
        }
    }
}

function Test-DualWrite($Workspace) {
    if ($TimeTolerance -lt [timespan]::Zero) { throw 'TimeTolerance must not be negative.' }
    $newWorkspace = if ($NewWorkspaceName -or $NewResourceGroupName) {
        Get-Workspace ($NewResourceGroupName ? $NewResourceGroupName : $ResourceGroupName) ($NewWorkspaceName ? $NewWorkspaceName : $WorkspaceName)
    } else { $Workspace }
    $legacy = Get-Table $Workspace $LegacyTableName
    $new = Get-Table $newWorkspace $NewTableName
    if (-not $legacy) { throw "Table '$LegacyTableName' not found in workspace '$($Workspace.Name)'." }
    if (-not $new) { throw "Table '$NewTableName' not found in workspace '$($newWorkspace.Name)'." }

    $newMissing = @($recordColumns.name | Where-Object { $_ -cnotin $new.Columns.Name })
    if ($newMissing) { Add-Finding Fail 'New table missing standard columns' "$(Format-List $newMissing). Redeploy the template." }
    $unverifiedLegacy = @($legacy.Columns.Name | Where-Object { $_ -cnotin $recordColumns.name })
    if ($unverifiedLegacy) {
        Add-Finding Fail 'Legacy columns outside the mapping contract' "$(Format-List $unverifiedLegacy). The default checker cannot certify these custom or value-dependent columns. Add their legacy names/types to the destination and DCR, and compare their stored values independently before cutover."
    }
    if ($newMissing -or $unverifiedLegacy) { return [ordered]@{ CommonColumns = 0 } }
    $wrongTypes = @(@($legacy.Columns) + @($new.Columns) | Where-Object {
        $recordTypes.ContainsKey($_.Name) -and $_.Type -ne $recordTypes[$_.Name]
    })
    if ($wrongTypes) {
        Add-Finding Fail 'Column type mismatch' "$(Format-List $wrongTypes.Name): source and destination columns must use their declared standard types."
        return [ordered]@{ CommonColumns = 0 }
    }
    $common = @($recordColumns.name | Where-Object { $_ -cin $legacy.Columns.Name -and $_ -cin $new.Columns.Name })
    $newRef = if ($newWorkspace.CustomerId -ne $Workspace.CustomerId) { "workspace('$($newWorkspace.CustomerId)').$NewTableName" } else { $NewTableName }

    $end = if ($bound.ContainsKey('EndTime')) { $EndTime.ToUniversalTime() } else { [datetime]::UtcNow.AddMinutes(-30) }
    $start = if ($bound.ContainsKey('StartTime')) { $StartTime.ToUniversalTime() } else { $end.AddDays(-1) }
    if ($start -ge $end) { throw 'StartTime must be before EndTime.' }
    $projection = (@('ComparisonSource', 'TimeGenerated') + @($common | ForEach-Object { Format-Column $_ })) -join ', '
    $rows = @(Invoke-Query $Workspace.CustomerId @"
let s = $(Format-Kql $start); let e = $(Format-Kql $end); let tol = $(Format-Span $TimeTolerance);
union ($LegacyTableName | where TimeGenerated between ((s - tol) .. (e + tol)) | extend ComparisonSource = 'Legacy'),
      ($newRef | where TimeGenerated between ((s - tol) .. (e + tol)) | extend ComparisonSource = 'New')
| project $projection
"@)
    $firstNew = @($rows | Where-Object { $_['ComparisonSource'] -ceq 'New' } |
        ForEach-Object { Get-RecordTime $_['TimeGenerated'] } | Where-Object { $_ -ge $start -and $_ -le $end } | Sort-Object | Select-Object -First 1)
    if (-not $bound.ContainsKey('StartTime') -and $firstNew.Count -and $firstNew[0] -gt $start) {
        Add-Finding Warning 'Unverified interval' "$($start.ToString('u')) to $($firstNew[0].ToString('u')) was omitted before the first new-table row. This does not certify when dual write began; use an explicit -StartTime to verify the whole period."
        $start = $firstNew[0]
    }
    $oldRows = [Collections.Generic.List[object]]::new()
    $newRows = [Collections.Generic.List[object]]::new()
    foreach ($row in $rows) {
        $entry = Get-Occurrence $row $common $start $end
        if ($entry.Time -lt $start.Subtract($TimeTolerance) -or $entry.Time -gt $end.Add($TimeTolerance)) { continue }
        switch -CaseSensitive ($row['ComparisonSource']) {
            'Legacy' { $oldRows.Add($entry) }
            'New' { $newRows.Add($entry) }
            default { throw 'Query returned an unknown comparison source.' }
        }
    }
    $oldIn = @($oldRows | Where-Object InWindow)
    $newIn = @($newRows | Where-Object InWindow)
    $null = @(Match-Occurrences $oldIn $newIn)
    $boundaryOld = @(Match-Occurrences @($oldIn | Where-Object { -not $_.Matched }) @($newRows | Where-Object { -not $_.InWindow }))
    $boundaryNew = @(Match-Occurrences @($oldRows | Where-Object { -not $_.InWindow }) @($newIn | Where-Object { -not $_.Matched }))
    $onlyOld = @($oldIn | Where-Object { -not $_.Matched })
    $onlyNew = @($newIn | Where-Object { -not $_.Matched })
    $result = [ordered]@{
        Start = $start; End = $end; CommonColumns = $common.Count
        LegacyRows = $oldIn.Count; NewRows = $newIn.Count
        OnlyInLegacy = $onlyOld.Count; OnlyInNew = $onlyNew.Count
        BoundaryLegacyRows = $boundaryOld.Count; BoundaryNewRows = $boundaryNew.Count
    }
    if ($result.LegacyRows -eq 0) { Add-Finding Warning $LegacyTableName 'No rows in the period. Is the old API still enabled (DualWrite)?' }
    if ($result.BoundaryLegacyRows -gt 0 -or $result.BoundaryNewRows -gt 0) {
        Add-Finding Warning 'Window boundary' "$($result.BoundaryLegacyRows) legacy and $($result.BoundaryNewRows) new row(s) have an excess count inside the period, but identical records outside it could cover the difference. Boundary matching is ambiguous; these are not confirmed missing rows. Inspect their timestamps or adjust the comparison period."
    }
    if ($onlyOld.Count) { Add-Finding Fail 'Missing in new table' "$($onlyOld.Count) legacy occurrence(s) have no equivalent counterpart within TimeTolerance." }
    if ($onlyNew.Count) { Add-Finding Warning 'Missing in legacy table' "$($onlyNew.Count) new occurrence(s) have no equivalent counterpart within TimeTolerance. The old API can drop records; inspect before cutover." }
    $oldDetail = @($onlyOld | Select-Object -First $MaxDetailRows)
    $newDetail = @($onlyNew | Select-Object -First $MaxDetailRows)
    # Pair unmatched rows describing the same event, then list the columns whose values differ.
    $keyColumns = @('NodeName_s', 'ObserverName_s', 'Property_s', 'property_s', 'Metric_s', 'ApplicationName_s', 'ServiceName_s', 'PartitionId_g') | Where-Object { $_ -cin $common }
    $newPool = [hashtable]::new([StringComparer]::Ordinal)
    foreach ($r in $newDetail) { $k = Get-RecordKey $r.Row $keyColumns; if (-not $newPool.ContainsKey($k)) { $newPool[$k] = [Collections.Generic.List[object]]::new() }; $newPool[$k].Add($r) }
    $diffs = [hashtable]::new([StringComparer]::Ordinal)
    $unpairedLegacy = 0
    foreach ($o in $oldDetail) {
        $candidates = $newPool[(Get-RecordKey $o.Row $keyColumns)]
        $match = $null
        if ($candidates) {
            $match = $candidates | Where-Object { [math]::Abs(($_.MatchTime - $o.MatchTime).Ticks) -le $TimeTolerance.Ticks } |
                Sort-Object MatchTime | Select-Object -First 1
        }
        if (-not $match) { $unpairedLegacy++; continue }
        [void]$candidates.Remove($match)
        foreach ($c in $common) {
            if ($c -cin $o.Generated -and $c -cin $match.Generated) { continue }
            $a = "$($o.Row[$c])"; $b = "$($match.Row[$c])"
            if ((Get-ValueKey $c $o.Row[$c]) -ceq (Get-ValueKey $c $match.Row[$c])) { continue }
            if (-not $diffs[$c]) { $diffs[$c] = [ordered]@{ Column = $c; Rows = 0; KnownLargeNumber = $false; ExampleLegacy = $a; ExampleNew = $b } }
            $diffs[$c].Rows++
        }
    }
    $unpairedNew = @($newPool.Values | ForEach-Object { $_ }).Count
    $partial = $onlyOld.Count -gt $MaxDetailRows -or $onlyNew.Count -gt $MaxDetailRows
    $result.ColumnDifferences = @($diffs.Values | Sort-Object { -$_.Rows } | ForEach-Object { [pscustomobject]$_ })
    $result.UnpairedLegacyRows = $unpairedLegacy
    $result.UnpairedNewRows = $unpairedNew
    $result.DetailRowsSampled = $partial

    foreach ($d in $result.ColumnDifferences) {
        Add-Finding Fail $d.Column "$($d.Rows) sampled row(s) have a different value. Example: legacy '$($d.ExampleLegacy)', new '$($d.ExampleNew)'."
    }
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
        if ($rb -gt 0 -and $ra -eq 0) {
            # Event-driven records (for example ClusterObserver's once-per-process upgrade status) can legitimately be absent after the switch.
            if ($rb -lt $MinRows) { Add-Finding Warning $label "Only $rb rows before the switch (fewer than -MinRows $MinRows), none after; too few to judge. These records may be event-driven, for example sent once per ClusterObserver process start. Confirm manually." }
            else { Add-Finding Fail $label "$rb rows before the switch, none after." }
            continue
        }
        if ($rb -eq 0) { Add-Finding Warning $label "No rows before the switch; $ra after. No baseline is available to validate column continuity."; continue }
        if ($ra -lt $MinRows) { Add-Finding Warning $label "Only $ra rows after the switch (fewer than -MinRows $MinRows); column fill rates not judged. Validation is inconclusive." }
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
        if ($rare) { Add-Finding Warning $label "Rarely filled columns not seen after the switch; too few rows to judge: $(Format-List $rare)." }
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
