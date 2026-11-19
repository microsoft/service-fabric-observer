#Requires -Version 7.6
[CmdletBinding()]
param(
    [string] $AssemblyPath = (Join-Path $PSScriptRoot '..\..\..\..\FabricObserver\bin\Release\net10.0\FabricObserver.Extensibility.dll')
)
$ErrorActionPreference = 'Stop'
$kit = Split-Path $PSScriptRoot -Parent
$root = [IO.Path]::GetFullPath((Join-Path $kit '..\..\..'))
$dataPath = Join-Path $root 'FabricObserver.Extensibility\Utilities\Telemetry'
if (-not (Test-Path -LiteralPath $AssemblyPath)) {
    throw 'Build FabricObserver in Release first, or pass -AssemblyPath for the built FabricObserver.Extensibility.dll and its dependencies.'
}
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$resolver = [Newtonsoft.Json.Serialization.DefaultContractResolver]::new()
$null = [Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll'))
$namespace = 'FabricObserver.Observers.Utilities.Telemetry'
$classes = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($file in Get-ChildItem -LiteralPath $dataPath -Filter '*.cs') {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName))
    foreach ($node in ($tree.GetRoot().DescendantNodes() | Where-Object { $_.GetType().Name -eq 'ClassDeclarationSyntax' })) {
        $classes.Add($node.Identifier.ValueText, $node)
    }
}

function Get-TypeName([Type] $Type) {
    if ($Type.IsArray) { return "$(Get-TypeName $Type.GetElementType())[]" }
    $nullable = [Nullable]::GetUnderlyingType($Type)
    if ($nullable) { return "$(Get-TypeName $nullable)?" }
    if ($Type.IsGenericType) {
        return ($Type.Name -split '`')[0] + '<' + ((@($Type.GetGenericArguments() | ForEach-Object { Get-TypeName $_ })) -join ',') + '>'
    }
    return $Type.Name
}

function Get-SourceTypeName([string] $Name) {
    $aliases = @{ string = 'String'; bool = 'Boolean'; byte = 'Byte'; short = 'Int16'; int = 'Int32'; long = 'Int64'; ushort = 'UInt16'; uint = 'UInt32'; ulong = 'UInt64'; float = 'Single'; double = 'Double'; decimal = 'Decimal'; object = 'Object' }
    $name = [regex]::Replace($Name.Replace(' ', ''), '(?:[A-Za-z_]\w*\.)+', '')
    return [regex]::Replace($name, '\b(?:string|bool|byte|short|int|long|ushort|uint|ulong|float|double|decimal|object)\b', { param($match) $aliases[$match.Value] })
}

function Assert-CurrentModel([Type] $Type) {
    if (-not $classes.ContainsKey($Type.Name)) { throw "No current source declaration for $Type." }
    $declaration = $classes[$Type.Name]
    if ($declaration.BaseList) {
        $parent = (Get-SourceTypeName $declaration.BaseList.Types[0].Type.ToString())
        if ($classes.ContainsKey($parent) -and $Type.BaseType.Name -cne $parent) {
            throw "Stale base type for $Type. Rebuild before checking mappings."
        }
    } elseif ($Type.BaseType -ne [object]) {
        throw "Stale base type for $Type. Rebuild before checking mappings."
    }
    $members = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($member in $classes[$Type.Name].Members) {
        $modifiers = @($member.Modifiers | ForEach-Object ValueText)
        if ('public' -cnotin $modifiers -or 'static' -cin $modifiers) { continue }
        if ($member.GetType().Name -eq 'PropertyDeclarationSyntax') {
            $members.Add($member.Identifier.ValueText, (Get-SourceTypeName $member.Type.ToString()))
        } elseif ($member.GetType().Name -eq 'FieldDeclarationSyntax') {
            foreach ($variable in $member.Declaration.Variables) {
                $members.Add($variable.Identifier.ValueText, (Get-SourceTypeName $member.Declaration.Type.ToString()))
            }
        }
    }
    $actual = @($Type.GetMembers([Reflection.BindingFlags]'Public,Instance,DeclaredOnly') | Where-Object {
        $_ -is [Reflection.PropertyInfo] -or $_ -is [Reflection.FieldInfo]
    })
    if ($actual.Count -ne $members.Count) { throw "Stale model assembly for $Type. Rebuild before checking mappings." }
    foreach ($member in $actual) {
        $typeName = if ($member -is [Reflection.PropertyInfo]) { Get-TypeName $member.PropertyType } else { Get-TypeName $member.FieldType }
        if (-not $members.ContainsKey($member.Name) -or $members[$member.Name] -cne $typeName) {
            throw "Stale model member: $Type/$($member.Name). Rebuild before checking mappings."
        }
    }
}

$required = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$typeBySuffix = @{ s = 'string'; d = 'real'; b = 'boolean'; t = 'datetime'; g = 'guid' }
function Add-Required([string] $Field, [string] $Suffix) {
    $required["$($Field.Replace('.', '_'))_$Suffix"] = $typeBySuffix[$Suffix]
}
function Add-ModelFields([Type] $Type, [string] $Prefix = '') {
    $contract = $resolver.ResolveContract($Type)
    foreach ($property in $contract.Properties) {
        if ($property.Ignored) { continue }
        $field = if ($Prefix) { "$Prefix.$($property.PropertyName)" } else { $property.PropertyName }
        $child = $resolver.ResolveContract($property.PropertyType)
        if ($child -is [Newtonsoft.Json.Serialization.JsonObjectContract]) { Add-ModelFields $property.PropertyType $field; continue }
        if ($child -is [Newtonsoft.Json.Serialization.JsonArrayContract] -or $property.PropertyType -eq [string]) { Add-Required $field 's' }
        elseif ($property.PropertyType -eq [bool]) { Add-Required $field 'b' }
        elseif ($property.PropertyType -in @([DateTime], [DateTimeOffset])) { Add-Required $field 't' }
        elseif ($property.PropertyType -eq [Guid]) { Add-Required $field 'g' }
        elseif ($property.PropertyType.IsEnum -or $property.PropertyType.IsPrimitive -or $property.PropertyType -eq [decimal]) { Add-Required $field 'd' }
        else { throw "Unclassified emitted type: $field / $($property.PropertyType)." }
    }
}

$base = $assembly.GetType("$namespace.TelemetryDataBase", $true)
$models = [Collections.Generic.HashSet[Type]]::new()
foreach ($type in $assembly.GetTypes()) {
    if ($type.IsPublic -and -not $type.IsAbstract -and $base.IsAssignableFrom($type)) { $null = $models.Add($type) }
}
function Test-ModelSource([string] $Name) {
    if ($Name -ceq 'TelemetryDataBase') { return $true }
    if (-not $classes.ContainsKey($Name) -or -not $classes[$Name].BaseList) { return $false }
    foreach ($base in $classes[$Name].BaseList.Types) {
        if (Test-ModelSource (($base.Type.ToString() -split '\.')[-1])) { return $true }
    }
    return $false
}
foreach ($class in $classes.Values) {
    if (Test-ModelSource $class.Identifier.ValueText) {
        $null = $models.Add($assembly.GetType("$namespace.$($class.Identifier.ValueText)", $true))
    }
}
$sender = $assembly.GetType("$namespace.LogsIngestionTelemetry", $true)
foreach ($method in $sender.GetMethods([Reflection.BindingFlags]'Public,Instance,DeclaredOnly')) {
    foreach ($parameter in $method.GetParameters()) {
        $candidates = @($parameter.ParameterType)
        if ($parameter.ParameterType.IsGenericType) { $candidates += $parameter.ParameterType.GetGenericArguments() }
        foreach ($type in $candidates) {
            if (-not $type.IsGenericParameter -and $type.IsClass -and $type.Namespace -ceq $namespace -and $type.Name -cne 'ServiceFabricUpgradeEventData') {
                $null = $models.Add($type)
            }
        }
    }
}
foreach ($type in $models) { Assert-CurrentModel $type; Add-ModelFields $type }

$payload = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $dataPath 'LogAnalyticsPayload.cs')))
$references = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in ([string][AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator)) {
    $references[[IO.Path]::GetFileName($path)] = $path
}
foreach ($name in 'FabricObserver.Extensibility.dll', 'System.Fabric.dll', 'TelemetryLib.dll', 'Newtonsoft.Json.dll') {
    if (-not $references.ContainsKey($name)) {
        $references.Add($name, (Join-Path (Split-Path $AssemblyPath -Parent) $name))
    }
}
$metadata = @($references.Values | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    'LogAnalyticsMappingContract',
    [Microsoft.CodeAnalysis.SyntaxTree[]]@($payload),
    [Microsoft.CodeAnalysis.MetadataReference[]]$metadata,
    $options)
$diagnostics = @($compilation.GetDiagnostics() | Where-Object { $_.Severity.ToString() -ceq 'Error' })
if ($diagnostics.Count) { throw ($diagnostics -join [Environment]::NewLine) }
$semantic = $compilation.GetSemanticModel($payload)
$builders = 0
foreach ($method in ($payload.GetRoot().DescendantNodes() | Where-Object { $_.GetType().Name -eq 'MethodDeclarationSyntax' })) {
    $creations = @($method.DescendantNodes() | Where-Object { $_.GetType().Name -eq 'AnonymousObjectCreationExpressionSyntax' })
    if ($creations.Count -ne 1) { throw "Unclassified payload builder: $($method.Identifier.ValueText). Update its independent mapping contract." }
    foreach ($creation in $creations) {
        $builders++
        foreach ($member in $creation.Initializers) {
            $field = if ($member.NameEquals) { $member.NameEquals.Name.Identifier.ValueText } else { ($member.Expression.ToString() -split '\.')[-1] }
            $expression = $member.Expression.ToString()
            if ($field -ceq 'value' -and $method.Identifier.ValueText -ceq 'Metric') {
                foreach ($suffix in 's', 'd', 'b', 't', 'g') { Add-Required $field $suffix }
            } else {
                $type = [Microsoft.CodeAnalysis.CSharp.CSharpExtensions]::GetTypeInfo($semantic, $member.Expression).Type
                $suffix = switch ($type.ToString()) {
                    'string' { 's' }; 'System.TimeSpan' { 's' }; 'System.DateTime' { 't' }; 'System.DateTimeOffset' { 't' }
                    'System.Guid' { 'g' }; 'bool' { 'b' }
                    { $_ -in @('byte', 'short', 'int', 'long', 'ushort', 'uint', 'ulong', 'float', 'double', 'decimal') } { 'd' }
                    default { throw "Unclassified anonymous field: $field / $type." }
                }
                Add-Required $field $suffix
            }
        }
    }
}
# Legacy value-dependent columns are independent of the schema under test.
foreach ($field in 'ClusterId', 'PartitionId', 'SnapshotId', 'NodeId') { Add-Required $field 'g' }
foreach ($field in 'ProcessStartTime', 'LastBootUpTime', 'OSInstallDate', 'SnapshotTimestamp', 'NodeUpAt', 'NodeDownAt') { Add-Required $field 't' }

function Get-CoverageErrors($Columns, [string] $Transform) {
    $errors = [Collections.Generic.List[string]]::new()
    $byName = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($column in $Columns) {
        if (-not $byName.TryAdd($column.name, $column.type)) { $errors.Add("Duplicate column: $($column.name)") }
    }
    $lines = @($Transform -split '\r?\n' | ForEach-Object { $_.Trim().TrimEnd(',') })
    foreach ($entry in $required.GetEnumerator()) {
        if (-not $byName.ContainsKey($entry.Key) -or $byName[$entry.Key] -cne $entry.Value) {
            $errors.Add("Missing/wrong column: $($entry.Key) ($($entry.Value))")
            continue
        }
        $matching = @($lines | Where-Object { $_.StartsWith("$($entry.Key) = ", [StringComparison]::Ordinal) })
        $field = $entry.Key -creplace '_[sdbtg]$', ''
        $expected = 'Payload' + (($field -split '_' | ForEach-Object { "['$_']" }) -join '')
        $paths = @($matching | ForEach-Object { [regex]::Matches($_, "Payload(?:\['[^']+'\])+") | ForEach-Object Value } | Sort-Object -Unique -CaseSensitive)
        if ($matching.Count -ne 1 -or $paths.Count -ne 1 -or $paths[0] -cne $expected) {
            $errors.Add("Missing/wrong case-sensitive projection: $($entry.Key) -> $expected")
        }
        if ($entry.Value -ceq 'real' -and $matching.Count -eq 1) {
            $guard = [regex]::Match($matching[0], 'gettype\(.+?\) in \((?<types>[^)]+)\)')
            if (-not $guard.Success -or $guard.Groups['types'].Value -cnotmatch "'double'") {
                $errors.Add("JSON floating-point values excluded: $($entry.Key)")
            }
        }
        if ($entry.Value -ceq 'datetime' -and $matching.Count -eq 1) {
            if ($matching[0] -cnotmatch "gettype\(.+?\) in \('string','datetime'\)") {
                $errors.Add("Non-date JSON values can enter date column: $($entry.Key)")
            }
        }
    }
    return $errors.ToArray()
}

$schema = Get-Content -LiteralPath (Join-Path $kit 'LogAnalyticsSchema.json') -Raw | ConvertFrom-Json
foreach ($application in 'FabricObserver', 'ClusterObserver') {
    $template = Get-Content -LiteralPath (Join-Path $kit "$application.LogAnalytics.json") -Raw | ConvertFrom-Json
    foreach ($columns in @(@($schema.applications.$application), @($template.variables.recordColumns))) {
        $errors = @(Get-CoverageErrors $columns $template.variables.transformKql)
        if ($errors.Count) { throw ("{0} mapping contract failed:{1}{2}" -f $application, [Environment]::NewLine, ($errors -join [Environment]::NewLine)) }
    }
    $withoutNamed = @($template.variables.recordColumns | Where-Object { $_.name -cnotmatch '^(value_[sdbtg]|id_s|datetime_t)$' })
    if (-not @(Get-CoverageErrors $withoutNamed $template.variables.transformKql).Count) { throw 'Negative control failed: omitted named-metric fields were not detected.' }
    $withoutShared = @($template.variables.recordColumns | Where-Object { $_.name -cnotin @('OSName_s', 'UpgradeState_s') })
    if (@(Get-CoverageErrors $withoutShared $template.variables.transformKql).Count -lt 2) { throw 'Negative control failed: omitted shared-provider fields were not detected.' }
    $wrongCase = $template.variables.transformKql.Replace("Payload['value']", "Payload['Value']")
    if (-not @(Get-CoverageErrors $template.variables.recordColumns $wrongCase).Count) { throw 'Negative control failed: wrong-case source access was not detected.' }
    $wrongType = @($template.variables.recordColumns | ForEach-Object {
        [pscustomobject]@{ name = $_.name; type = if ($_.name -ceq 'value_d') { 'string' } else { $_.type } }
    })
    if (-not @(Get-CoverageErrors $wrongType $template.variables.transformKql).Count) { throw 'Negative control failed: a changed legacy column type was not detected.' }
    $withoutDouble = $template.variables.transformKql.Replace(",'double'", '')
    if (-not @(Get-CoverageErrors $template.variables.recordColumns $withoutDouble).Count) { throw 'Negative control failed: decimal-value exclusion was not detected.' }
    $withoutDateGuard = $template.variables.transformKql.Replace("gettype(Payload['value']) in ('string','datetime')", 'true')
    if (-not @(Get-CoverageErrors $template.variables.recordColumns $withoutDateGuard).Count) { throw 'Negative control failed: numeric/boolean date coercion was not detected.' }
    "$application PASS: $($required.Count) independently required columns; omitted fields, wrong case/type, decimal exclusion and date-coercion defects detected."
}
"Source contract PASS: $($models.Count) concrete models and $builders anonymous builders, including inherited fields and nested SDK contracts."

$tokens = $null; $parseErrors = $null
$checker = [Management.Automation.Language.Parser]::ParseFile((Join-Path $kit 'Test-ObserverLogAnalytics.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors -join [Environment]::NewLine) }
$dualWrite = @($checker.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-DualWrite'
}, $true))
if ($dualWrite.Count -ne 1) { throw 'Cannot locate the customer dual-write checker.' }
foreach ($scenario in 'missing-column', 'unmapped-legacy-column') {
    $observed = & {
        param($Scenario, $Columns, $Body)
        $recordColumns = $Columns
        $LegacyTableName = 'Legacy_CL'; $NewTableName = 'New_CL'
        $WorkspaceName = 'fake'; $NewWorkspaceName = $null
        $StartTime = [datetime]'2026-01-01T00:00:00Z'; $EndTime = $StartTime.AddHours(1)
        $TimeTolerance = [timespan]::FromMinutes(10)
        $bound = @{ StartTime = $StartTime; EndTime = $EndTime }
        $workspace = [pscustomobject]@{ Name = 'fake'; CustomerId = [Guid]::Empty.ToString() }
        $legacyColumns = @($Columns | ForEach-Object { [pscustomobject]@{ Name = $_.name; Type = $_.type } })
        if ($Scenario -ceq 'unmapped-legacy-column') {
            $legacyColumns += [pscustomobject]@{ Name = 'CustomerField_s'; Type = 'string' }
        }
        $newColumns = @($Columns | Where-Object { $Scenario -cne 'missing-column' -or $_.name -cne 'value_d' } |
            ForEach-Object { [pscustomobject]@{ Name = $_.name; Type = $_.type } })
        $findings = [Collections.Generic.List[object]]::new()
        function Get-Table($Workspace, $Name) {
            [pscustomobject]@{ Columns = if ($Name -ceq $LegacyTableName) { $legacyColumns } else { $newColumns } }
        }
        function Add-Finding($Severity, $Item, $Detail) {
            $findings.Add([pscustomobject]@{ Severity = $Severity; Item = $Item; Detail = $Detail })
        }
        function Format-List($Names) { $Names -join ', ' }
        function Format-Kql($Value) { "datetime($($Value.ToString('o')))" }
        function Invoke-Query { return @() }
        $null = & $Body $workspace
        $findings.ToArray()
    } $scenario @($schema.applications.FabricObserver) $dualWrite[0].Body.GetScriptBlock()
    $item = if ($scenario -ceq 'missing-column') { 'New table missing standard columns' } else { 'Legacy columns outside the mapping contract' }
    if (-not @($observed | Where-Object { $_.Severity -ceq 'Fail' -and $_.Item -ceq $item }).Count) {
        throw "Customer checker did not fail $scenario."
    }
}
'Customer checker PASS: missing expected columns and unverified legacy columns fail; Azure calls were intercepted.'
