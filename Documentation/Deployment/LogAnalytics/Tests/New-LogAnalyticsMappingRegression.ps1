#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputPath)
$ErrorActionPreference = 'Stop'
$kit = Split-Path $PSScriptRoot -Parent
$names = @(
    'value_d', 'Value_d', 'value_s', 'value_b', 'value_t', 'value_g', 'id_s', 'datetime_t',
    'SnapshotId_s', 'SnapshotId_g', 'SnapshotTimestamp_s', 'SnapshotTimestamp_t',
    'ChildProcessInfo_s', 'NodeDeactivationInfo_EffectiveIntent_d'
)
$functions = @(
    foreach ($application in 'FabricObserver', 'ClusterObserver') {
        $template = Get-Content -LiteralPath (Join-Path $kit "$application.LogAnalytics.json") -Raw | ConvertFrom-Json
        $lines = @($template.variables.transformKql -split '\r?\n' | ForEach-Object { $_.Trim().TrimEnd(',') })
        $projections = @(
            foreach ($name in $names) {
                $matching = @($lines | Where-Object { $_.StartsWith("$name = ", [StringComparison]::Ordinal) })
                if ($matching.Count -ne 1) { throw "Missing/duplicate generated projection: $application/$name." }
                $matching[0]
            }
        ) -join ",`n    "
        $bag = (@($names | ForEach-Object { "'$_', $_" })) -join ', '
        @"
let ApplyMapping = (source:(Case:string, Payload:dynamic, Expected:dynamic))
{
    source
    | project Case,
    $projections
    | project Case, Actual=bag_pack($bag)
};
"@
    }
)
if ($functions.Count -ne 2 -or $functions[0] -cne $functions[1]) {
    throw 'The selected FO/CO projections differ; validate them with separate queries.'
}
$query = @'
let Inputs = datatable(Case:string, Payload:dynamic, Expected:dynamic)
[
    'number', dynamic({"value":12.5}), dynamic({"value_d":12.5,"value_s":"","value_b":null,"value_t":null,"value_g":null,"Value_d":null}),
    'zero', dynamic({"value":0}), dynamic({"value_d":0,"value_s":"","value_t":null,"value_g":null}),
    'text', dynamic({"value":"plain text"}), dynamic({"value_s":"plain text","value_d":null,"value_t":null,"value_g":null}),
    'numeric-string', dynamic({"value":"123.456"}), dynamic({"value_s":"123.456","value_d":null,"value_t":null,"value_g":null}),
    'boolean', dynamic({"value":true}), dynamic({"value_b":true,"value_s":"","value_d":null,"value_t":null,"value_g":null}),
    'null', dynamic({"value":null}), dynamic({"value_s":"","value_d":null,"value_b":null,"value_t":null,"value_g":null}),
    'missing', dynamic({}), dynamic({"value_s":"","value_d":null,"value_b":null,"value_t":null,"value_g":null,"id_s":"","datetime_t":null}),
    'typed', dynamic({"Value":42.5}), dynamic({"Value_d":42.5,"value_d":null,"value_s":""}),
    'both-cases', dynamic({"Value":99.5,"value":12.5}), dynamic({"Value_d":99.5,"value_d":12.5,"value_s":""}),
    'date', dynamic({"value":"2026-01-02T03:04:05.1234567Z"}), dynamic({"value_t":"2026-01-02T03:04:05.1234567Z","value_s":"","value_d":null,"value_g":null}),
    'zero-date', dynamic({"value":"0001-01-01T00:00:00Z"}), dynamic({"value_t":null,"value_s":"","value_d":null,"value_g":null}),
    'guid-D', dynamic({"value":"8145d822-13a7-44ad-859c-36f31a84f6dd"}), dynamic({"value_g":"8145d822-13a7-44ad-859c-36f31a84f6dd","value_s":"","value_t":null}),
    'guid-N', dynamic({"value":"8145d82213a744ad859c36f31a84f6dd"}), dynamic({"value_g":"8145d822-13a7-44ad-859c-36f31a84f6dd","value_s":"","value_t":null}),
    'guid-braces', dynamic({"value":"{8145d822-13a7-44ad-859c-36f31a84f6dd}"}), dynamic({"value_g":"8145d822-13a7-44ad-859c-36f31a84f6dd","value_s":""}),
    'guid-parentheses', dynamic({"value":"(8145d822-13a7-44ad-859c-36f31a84f6dd)"}), dynamic({"value_g":"8145d822-13a7-44ad-859c-36f31a84f6dd","value_s":""}),
    'guid-prefix-text', dynamic({"value":"8145d822-13a7-44ad-859c-36f31a84f6dd-not-a-guid"}), dynamic({"value_g":null,"value_s":"8145d822-13a7-44ad-859c-36f31a84f6dd-not-a-guid","value_t":null}),
    'metadata', dynamic({"id":"FO_probe","datetime":"2026-01-02T03:04:05Z"}), dynamic({"id_s":"FO_probe","datetime_t":"2026-01-02T03:04:05Z"}),
    'snapshot-fallback', dynamic({"SnapshotId":"not-a-guid","SnapshotTimestamp":"not-a-date"}), dynamic({"SnapshotId_s":"not-a-guid","SnapshotId_g":null,"SnapshotTimestamp_s":"not-a-date","SnapshotTimestamp_t":null}),
    'snapshot-typed', dynamic({"SnapshotId":"8145d822-13a7-44ad-859c-36f31a84f6dd","SnapshotTimestamp":"2026-01-02T03:04:05Z"}), dynamic({"SnapshotId_s":"","SnapshotId_g":"8145d822-13a7-44ad-859c-36f31a84f6dd","SnapshotTimestamp_s":"","SnapshotTimestamp_t":"2026-01-02T03:04:05Z"}),
    'arrays-and-nested', dynamic({"value":[1,2],"ChildProcessInfo":[{"Value":12.5}],"NodeDeactivationInfo":{"EffectiveIntent":3}}), dynamic({"value_s":"[1,2]","ChildProcessInfo_s":"[{\"Value\":12.5}]","NodeDeactivationInfo_EffectiveIntent_d":3})
];
let Goals = Inputs | project Case, Expected;
'@ + "`n" + $functions[0] + "`n" + @'
let Score = (rows:(Case:string, Actual:dynamic))
{
    rows
    | join kind=inner Goals on Case
    | mv-expand Key=bag_keys(Expected) to typeof(string)
    | extend Got=Actual[Key], Want=Expected[Key]
    | extend Matches=case(
        isnull(Want), isnull(Got),
        Key endswith '_t', coalesce(todatetime(Got)==todatetime(Want), false),
        Key endswith '_g', coalesce(toguid(Got)==toguid(Want), false),
        Key endswith '_d', coalesce(todouble(Got)==todouble(Want), false),
        Key endswith '_b', coalesce(tobool(Got)==tobool(Want), false),
        tostring(Want) startswith '[', tostring(parse_json(tostring(Got)))==tostring(parse_json(tostring(Want))),
        tostring(Got)==tostring(Want))
    | summarize Mismatches=countif(Matches==false) by Case
    | summarize Cases=count(), Passed=countif(Mismatches==0), Failed=countif(Mismatches>0), FailedCases=make_set_if(Case,Mismatches>0)
};
union
    (Inputs | invoke ApplyMapping() | invoke Score() | extend Application='FabricObserver'),
    (Inputs | invoke ApplyMapping() | invoke Score() | extend Application='ClusterObserver')
| project Application, Cases, Passed, Failed, FailedCases
'@
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), ([regex]::Replace($query, '\r?\n', "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))
"Read-only synthetic KQL written to $OutputPath. No Azure calls or resource changes were made."
