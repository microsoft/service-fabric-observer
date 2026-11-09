# Set up or migrate Log Analytics telemetry

Set up FabricObserver or ClusterObserver telemetry with the **Logs Ingestion API (new method)**, or migrate from the **HTTP Data Collector API (previous method)**.

**Choose your path:**

| Path | Use when | Destination |
|---|---|---|
| [**First-time setup** (`Fresh`)](#path-1-first-time-setup-fresh) | You have no existing telemetry table. | New table. |
| [**Single-write migration** (`SingleWrite`)](#path-2-single-write-migration-singlewrite) | The table check finds standard columns only, and you accept non-reversible table conversion. | Existing table; existing table references stay the same. |
| [**Dual-write migration** (`DualWrite`)](#path-3-dual-write-migration-dualwrite) | You have custom fields (add them to the DCR and new table first), or want to validate before switching. | Separate new permanent table; update queries, alerts and workbooks. |

**Using both applications?** Repeat the procedure independently for each. They may share a workspace and managed identity, but use separate DCRs and tables by default.

## Before you begin

You need FabricObserver 3.3.4 or later (or ClusterObserver 2.3.3 or later), an existing Log Analytics workspace, and access to:
- Read the workspace and its data, create resources in its resource group, and grant roles on the DCR.
- Attach a user-assigned managed identity (UAMI) to every hosting VM scale set or VM. The person attaching it needs **Managed Identity Operator** on that identity, plus permission to update the hosts.

**Package version:** FabricObserver 3.3.4 and ClusterObserver 2.3.3 are the first versions with the parameters in this guide; earlier versions do not have them. Use one of these versions or later before deploying resources or converting a table. The repository's [build and deployment guidance](../../../README.md#build-and-run) describes how to build and deploy the application.

The default templates create or reuse `id-observer-loganalytics` UAMI in the **Log Analytics resource group**, deploy the DCR in the workspace region, and grant **Monitoring Metrics Publisher** on the DCR. Sharing this identity across clusters avoids tying fleet telemetry to one cluster's lifetime. Neither application provisions these resources.

Use the DCR's HTTPS ingestion endpoint by default. Private Link requires a **Data Collection Endpoint (DCE)** and the corresponding network setup; see [endpoint requirements](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/logs-ingestion-api-overview#endpoint).

**Tools:** PowerShell 7 and `Az.Accounts` for the migration kit; a separate, working Service Fabric PowerShell console for application upgrades. Sign in explicitly. Keep workspace keys and cluster credentials out of scripts, saved parameter fragments and logs.

## Set your inputs once

Open PowerShell 7 at the observer repository root. Replace the placeholders, choose your path, and list **every hosting node type's scale set**:

```powershell
$kit = Join-Path (Get-Location) 'Documentation\Deployment\LogAnalytics'
$helper = Join-Path $kit 'Setup-ObserverLogAnalytics.ps1'
$setup = @{
    SubscriptionId = 'YOUR_SUBSCRIPTION_ID'
    ResourceGroupName = 'YOUR_LOG_ANALYTICS_RESOURCE_GROUP'
    WorkspaceName = 'YOUR_WORKSPACE'
    Application = 'FabricObserver' # Or ClusterObserver.
    Route = 'DualWrite' # Or Fresh / SingleWrite.
    ClusterResourceGroupName = 'YOUR_CLUSTER_RESOURCE_GROUP'
    ScaleSetNames = @('YOUR_SCALE_SET')
}
Connect-AzAccount -Subscription $setup.SubscriptionId
$check = @{
    SubscriptionId = $setup.SubscriptionId; ResourceGroupName = $setup.ResourceGroupName
    WorkspaceName = $setup.WorkspaceName; Application = $setup.Application
}
```

Defaults: `FabricObserver_CL` / `ClusterObserver_CL` for fresh setup and single write; `FabricObserverV2_CL` / `ClusterObserverV2_CL` for dual-write destinations. If your old `LogAnalyticsLogType` is customized, set `$setup.LegacyTableName` to its actual table name. Override `$setup.TableName` for a different destination.

The [helper](Setup-ObserverLogAnalytics.ps1) runs **one named step per call**. It never converts a table, changes a host, upgrades an application, fetches keys or cuts over. It uses the existing templates and [read-only checker](Test-ObserverLogAnalytics.ps1); [manual resource setup](#manual-and-advanced-setup) is also available.

## Follow your path

Complete [Before you begin](#before-you-begin) and [Set your inputs once](#set-your-inputs-once), then follow **one** checklist below in order. Each link opens the instructions for that step; return to your checklist afterward. The shared sections are references, not an additional sequence to run. Resolve failures before continuing.

### Path 1: First-time setup (`Fresh`)

**Start:** No existing telemetry table, whether the observer application is new or already deployed. If it already writes to a legacy table, choose a migration route instead. **Finish:** Expected records arrive in the new table through the new API.

1. **Select the route.** Set `$setup.Route = 'Fresh'`. Use an unused destination table name; override `$setup.TableName` if the default name already exists.
2. **Preview and deploy resources.** [Deploy the application's template](#deploy-the-applications-template): run `Prepare -WhatIf`, review it, then run `Prepare`. Retain `$resources`.
3. **Prepare the hosts.** [Attach the identity](#attach-the-identity-to-hosts), roll it onto existing nodes, and check every hosting node type.
4. **Check readiness.** [Check the table and network](#check-the-table-and-network); resolve schema, connectivity and permission issues.
5. **Generate settings.** [Generate the parameter fragment](#generate-the-parameter-fragment). Confirm the mode is `LogsIngestionOnly`.
6. **Configure the new or existing application.** Run the common fragment-loading block in [Apply the parameters](#apply-the-parameters), then choose exactly one branch:
   - **New application:** Follow the [new-application branch](#new-application-fresh) to merge the fragment with your reviewed deployment parameters and pass the result to application creation. Create the service instance as required by the package.
   - **Already deployed application:** [Upgrade to a supported version](#upgrade-to-a-supported-version) if needed, preserving current settings without requiring legacy Log Analytics credentials. Follow the [existing-application branch](#existing-application-any-route) to merge the fragment with current parameters and perform a parameter-only upgrade.
7. **Verify and finish.** [Verify fresh setup](#verify-fresh-setup). Confirm stored records, expected fields and timestamps, and application health. Missing representative records are not a successful validation.

**Skip:** Existing-table checks, conversion, dual-write comparison and the separate cutover block. Only a new application skips the package-upgrade step; an existing application must already run, or first upgrade to, a supported version. Fresh setup selects `LogsIngestionOnly` in either case.

**If you must stop:** Follow [Stop, roll back or clean up](#stop-roll-back-or-clean-up). There is no previous legacy delivery path to restore.

### Path 2: Single-write migration (`SingleWrite`)

**Start:** Working legacy telemetry, standard columns only, and acceptance of irreversible conversion. **Finish:** New-API delivery works in the existing table and dependent consumers still work.

> **Pending validation:** Legacy ingestion after the template adds columns is unresolved. Do not proceed to conversion until this is resolved for your schema and rollback plan. This checklist does not establish that `LegacyOnly` is a safe rollback. Use dual write if you require an unchanged legacy table.

1. **Select the route.** Set `$setup.Route = 'SingleWrite'`. Set `$setup.LegacyTableName` if customized; `$setup.TableName`, if supplied, must name that existing table. Retain complete legacy settings securely.
2. **Check and record the baseline.** [Check the existing table](#check-the-existing-table). Resolve failures; use dual write for non-standard fields. Record representative legacy records and the queries, alerts and workbooks to validate afterward.
3. **Prepare the application package.** [Upgrade to a supported version](#upgrade-to-a-supported-version), preserving legacy settings. Verify the completed upgrade and continued legacy delivery before making irreversible changes.
4. **Pass the validation gate, then convert.** Resolve the pending-validation warning above, read [Convert the existing table](#convert-the-existing-table), explicitly accept the one-way operation, and verify conversion completed.
5. **Preview and deploy resources.** [Deploy the application's template](#deploy-the-applications-template): run `Prepare -WhatIf`, review it, then run `Prepare` against the converted table.
6. **Prepare and check hosts.** [Attach the identity](#attach-the-identity-to-hosts), complete the host rollout, then [check the table and network](#check-the-table-and-network).
7. **Switch delivery.** [Generate the parameter fragment](#generate-the-parameter-fragment), confirm `LogsIngestionOnly`, then [apply the parameters](#apply-the-parameters) to the existing application. Retain the UTC switch time and wait for upgrade completion.
8. **Verify the migration.** [Verify single write](#verify-single-write) using the recorded switch time. Inspect stored values, timestamps, health and dependent queries, alerts and workbooks; a retained table name alone does not validate compatibility.
9. **Finish or roll back.** Follow [Stop, roll back or clean up](#stop-roll-back-or-clean-up), subject to the validation gate. Retain legacy credentials and the previous package until rollback testing is complete; clear credentials only when rollback is no longer needed.

**Skip:** Creating a separate destination, dual-write comparison and the separate cutover block. Step 7 already switches to `LogsIngestionOnly`. Application rollback never undoes table conversion.

### Path 3: Dual-write migration (`DualWrite`)

**Start:** Working legacy telemetry and an unused, separate destination table. **Finish:** Consumers use the new table, new delivery continues, and legacy delivery stops.

1. **Select the route.** Set `$setup.Route = 'DualWrite'`. Identify the actual legacy table and choose a different, unused permanent destination name. Retain complete legacy settings securely.
2. **Check the baseline and mappings.** [Check the existing table](#check-the-existing-table). Record representative legacy telemetry. If you must keep custom fields, list each one with its legacy column name and type (for example `MyField_s`); the default templates map standard fields only, so you add them in step 4.
3. **Prepare the application package.** [Upgrade to a supported version](#upgrade-to-a-supported-version), preserving legacy settings, and verify continued legacy delivery.
4. **Preview and deploy resources.** For default mappings, [deploy the application's template](#deploy-the-applications-template): run `Prepare -WhatIf`, review it, then run `Prepare`. For custom fields, copy the application's template and, for each field, add a column with the legacy name and type to `recordColumns` and a matching projection from `Payload` to `transformKql`, following the existing entries. Deploy the copy using [manual and advanced setup](#manual-and-advanced-setup).
5. **Prepare and check hosts.** [Attach the identity](#attach-the-identity-to-hosts), complete the host rollout, then [check the table and network](#check-the-table-and-network).
6. **Enable both writers.** For default mappings, [generate the parameter fragment](#generate-the-parameter-fragment) and confirm `DualWrite`; for custom mappings, use the outputs from the manual deployment. [Apply the parameters](#apply-the-parameters), preserving complete legacy settings, and wait for upgrade completion.
7. **Compare stored records.** [Verify dual write](#verify-dual-write) over settled, representative traffic. Resolve failures and warnings. For custom fields, confirm yourself that the new table has each custom column with the same name and type as the old table, and that its stored values match; the checker compares standard fields only.
8. **Test rollback before cutover.** Follow [Stop, roll back or clean up](#stop-roll-back-or-clean-up) to select `LegacyOnly` and verify legacy records. Restore `DualWrite` with the same mode-change block, wait for completion, and verify both destinations again.
9. **Move dependent consumers.** Repoint queries, alerts and workbooks to the new permanent table and test them before retiring legacy delivery.
10. **Cut over and verify.** Only after step 7 passes, including any custom-field check, [cut over](#cut-over-dualwrite-only) to `LogsIngestionOnly`. Wait for upgrade completion; confirm new-table delivery continues and old-table delivery stops after in-flight data has settled.
11. **Finish.** Retain legacy settings until rollback is no longer required, then explicitly clear legacy credentials using [Stop, roll back or clean up](#stop-roll-back-or-clean-up). Stopping legacy delivery does not require deleting the old table or its retained data.

**Skip:** Existing-table conversion. The old table remains unchanged throughout this path.

## Shared Azure resource steps

### Check the existing table

Migration routes only:
```powershell
$precheck = & $helper @setup -Step CheckTable
```
Review non-standard columns and type differences. Resolve failures before proceeding. Custom/extension fields require dual write; the default templates do not map them, so add them as described in [Path 3](#path-3-dual-write-migration-dualwrite) step 4.

### Convert the existing table

Single write only. Read the warning below, then use the [documented table conversion operation](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/custom-logs-migrate#convert-a-table-from-v1-to-v2). Skip this step for fresh setup and dual write.

> **One-way step:** Conversion cannot be undone without deleting the table and losing its data. Automatic column creation and legacy custom-field extraction stop. Legacy ingestion after the template adds columns is pending validation; do not assume application rollback restores it. Records with unmapped fields or incompatible types can be dropped in full even when the old API returns HTTP 200. Conversion rollback is not application rollback.

### Deploy the application's template

```powershell
& $helper @setup -Step Prepare -WhatIf
$resources = & $helper @setup -Step Prepare
```
`-WhatIf` reads prerequisites but deploys nothing. Fresh/dual write must use a new destination name. Single write requires a converted table; the template adds missing standard columns and keeps existing column names/types, but does not preserve custom column descriptions. The helper stops if it finds non-standard columns on the single-write path. If deployment finishes after a timeout, inspect it and resume with `-Step Parameters`, not another `Prepare`.

### Attach the identity to hosts

In each scale set, open **Identity -> User assigned -> Add**, select `$resources.ManagedIdentityResourceId`, and preserve all existing identities. Apply the updated model to existing nodes using your cluster's supported rollout procedure; ensure future nodes receive it too. Then check:
```powershell
& $helper @setup -Step CheckHosts
```
This checks only the listed Uniform scale sets and existing VMs, not token acquisition or uploads. Standalone VMs, Flexible scale sets and managed Service Fabric clusters need their platform-specific identity procedure; do not directly modify a managed cluster's underlying scale sets.

### Check the table and network

```powershell
& (Join-Path $kit 'Test-ObserverLogAnalytics.ps1') @check -Check Table -TableName $resources.TableName
```
Resolve missing/wrong-type columns. Verify HTTPS access from hosting nodes to the ingestion endpoint. Allow time for role propagation; new grants can take approximately 30 minutes, with upload-failure warnings until effective.

## Shared application steps

Use **Service Fabric application parameters**, not .NET `app.config`. Retain desired per-observer telemetry switches.

### Upgrade to a supported version

Existing applications only; skip the package upgrade if the application already runs FabricObserver 3.3.4+ or ClusterObserver 2.3.3+. Otherwise, before conversion or enabling the new writer, upgrade to one of those versions while preserving current effective application parameters. Use the package's deployment instructions and the repository's [application deployment guidance](../../../README.md#deploy-fabricobserver-using-client-powershell).

- **SingleWrite or DualWrite:** Preserve complete legacy settings and keep `LegacyOnly` selected during the package upgrade. Afterward, verify application health and continued stored legacy telemetry before proceeding.
- **Fresh, already deployed application:** Preserve the current telemetry provider and enablement settings during the package upgrade; enable the new Log Analytics path only when its parameters are ready. No legacy Log Analytics credentials or baseline are required.

Wait for **RollingForwardCompleted** and verify application health before changing parameters. Do not proceed if the package upgrade or required existing telemetry fails. Fresh setup of a new application skips this step.

### Generate the parameter fragment

This reads the completed deployment; it does not change the application.
```powershell
$settings = & $helper @setup -Step Parameters
$parameterFile = Join-Path ([IO.Path]::GetTempPath()) "$($setup.Application)-loganalytics.parameters.xml"
$settings.ParameterXml | Set-Content -LiteralPath $parameterFile -Encoding utf8
$parameterFile
```
The fragment contains `ObserverManagerEnableTelemetryProvider=true`, `TelemetryProvider=AzureLogAnalytics`, the path's write mode, and these outputs:

| Parameter | Required value |
|---|---|
| `LogAnalyticsIngestionEndpoint` | HTTPS base DCR/DCE ingestion endpoint, not an ARM URL or full request URL. |
| `LogAnalyticsDcrImmutableId` | Immutable `dcr-...` ID, not the resource name or ARM ID. |
| `LogAnalyticsStreamName` | Exact input stream: `Custom-FabricObserver` or `Custom-ClusterObserver` for the default mappings. |
| `LogAnalyticsManagedIdentityClientId` | User-assigned identity's **client ID**, not principal/object ID or ARM ID. |

### Apply the parameters

In your **SF-connected console**, load the generated fragment and supply the application name. Variables do not transfer between consoles. Run this common block first, then **exactly one** of the new/existing application branches below. Do not print parameter maps: they may contain credentials.
```powershell
$parameterFile = Read-Host 'Generated parameter file path'
[xml]$fragment = Get-Content -LiteralPath $parameterFile -Raw -ErrorAction Stop
if (-not $fragment.Parameters.Parameter) {
    throw 'Expected a generated fragment containing Parameters/Parameter entries.'
}
$applicationName = Read-Host 'Application name, e.g. fabric:/FabricObserver'
```

#### New application (Fresh)

First copy and register the FabricObserver 3.3.4+ or ClusterObserver 2.3.3+ application package using its deployment instructions and the [application deployment guidance](../../../README.md#deploy-fabricobserver-using-client-powershell). For ClusterObserver, follow the same steps with `Build-ClusterObserver.ps1` and the `ClusterObserverType` package. Use the block below **instead of** the guide's `New-ServiceFabricApplication` command so the generated settings are passed at creation.

Choose and review a base Service Fabric application parameter file, such as [FabricObserver Cloud.xml](../../../FabricObserverApp/ApplicationParameters/Cloud.xml) or [ClusterObserver Cloud.xml](../../../ClusterObserverApp/ApplicationParameters/Cloud.xml). These files have an `Application` root; an empty `Parameters` element uses the package's defaults. Add any desired per-observer overrides before proceeding. The generated fragment is separate and has a `Parameters` root.

This block copies base parameters first, then overlays the fragment, so the generated Log Analytics settings take precedence:
```powershell
$baseParameterFile = Read-Host 'Reviewed base application parameter XML file path'
[xml]$baseParameters = Get-Content -LiteralPath $baseParameterFile -Raw -ErrorAction Stop
if (-not $baseParameters.Application) {
    throw 'Expected a Service Fabric application parameter file with an Application root.'
}
$parameters = @{}
foreach ($p in $baseParameters.Application.Parameters.Parameter) { $parameters[$p.Name] = $p.Value }
foreach ($p in $fragment.Parameters.Parameter) { $parameters[$p.Name] = $p.Value }
$applicationTypeName = Read-Host 'Registered application type name, e.g. FabricObserverType'
$applicationTypeVersion = Read-Host 'Registered application type version, e.g. 3.3.4'
New-ServiceFabricApplication -ApplicationName $applicationName `
    -ApplicationTypeName $applicationTypeName -ApplicationTypeVersion $applicationTypeVersion `
    -ApplicationParameter $parameters -ErrorAction Stop
```
Use the type name and version you registered (FabricObserver 3.3.4+ or ClusterObserver 2.3.3+). Then create the service instance, which application creation does not do:
```powershell
# FabricObserver: one instance per node.
New-ServiceFabricService -Stateless -PartitionSchemeSingleton -ApplicationName $applicationName `
    -ServiceName "$applicationName/FabricObserverService" -ServiceTypeName FabricObserverType -InstanceCount -1
# ClusterObserver: a single instance.
# New-ServiceFabricService -Stateless -PartitionSchemeSingleton -ApplicationName $applicationName `
#     -ServiceName "$applicationName/ClusterObserverService" -ServiceTypeName ClusterObserverType -InstanceCount 1
```
Return to your Fresh checklist.

#### Existing application (any route)

This branch applies to Fresh, SingleWrite and DualWrite. Confirm that the application runs FabricObserver 3.3.4+ or ClusterObserver 2.3.3+; otherwise first complete [Upgrade to a supported version](#upgrade-to-a-supported-version). Do not use this block to create a new application.

Retrieve current parameters, overlay only the generated fragment, and perform a parameter-only upgrade. Existing per-observer settings remain in the map unless the fragment explicitly overrides them. Fresh does not require legacy Log Analytics credentials.
```powershell
$app = Get-ServiceFabricApplication -ApplicationName $applicationName
$parameters = @{}
foreach ($p in $app.ApplicationParameters) { $parameters[$p.Name] = $p.Value }
foreach ($p in $fragment.Parameters.Parameter) { $parameters[$p.Name] = $p.Value }
$switchStartedUtc = [datetime]::UtcNow
Start-ServiceFabricApplicationUpgrade -ApplicationName $applicationName `
    -ApplicationTypeVersion $app.ApplicationTypeVersion -ApplicationParameter $parameters `
    -Monitored -FailureAction Rollback
Get-ServiceFabricApplicationUpgrade -ApplicationName $applicationName
$switchStartedUtc.ToString('o') # Record this for Verify single write.
```
Wait for **RollingForwardCompleted**. Return to your route's verification step.

| `LogAnalyticsWriteMode` | What sends | Migration use |
|---|---|---|
| `LegacyOnly` (default) | Old API only; new settings ignored. | Upgrade without migration, or roll back. |
| `DualWrite` | Both APIs independently, with complete settings for each. | Validate separate old/new tables. |
| `LogsIngestionOnly` | New API only; old settings ignored. | Fresh setup or cutover. |

For migration routes, keep legacy credentials until rollback testing is complete. Fresh setup does not require them. `LogAnalyticsWorkspaceId`, `LogAnalyticsSharedKey` and `LogAnalyticsLogType` configure only the old API; they do not select the new table. There is **no automatic fallback**.

## Shared verification and cutover steps

Confirm recent stored records, expected fields and timestamps, and Service Fabric health after the upgrade. An accepted request or matching counts alone does not prove data compatibility. Allow for ingestion delays; ClusterObserver sends health telemetry when entities are unhealthy, not a continuous healthy-cluster heartbeat.

### Verify fresh setup

Query the destination table for the enabled observers' expected records. In the workspace's Logs query editor, replace `YOUR_DESTINATION_TABLE` with the actual destination name:
```kusto
YOUR_DESTINATION_TABLE
| where TimeGenerated > ago(1h)
| order by TimeGenerated desc
| take 20
```
Inspect fields and timestamps for representative records produced after deployment. An empty result is not proof of success: allow for ingestion delay and confirm the enabled observers actually produced telemetry in that window. For ClusterObserver, arrange representative unhealthy-entity telemetry in your test environment rather than expecting a healthy-cluster heartbeat.

Fresh setup is complete when expected records and application health are verified. No comparison to an old table or additional cutover is required.

### Verify single write

In PowerShell 7, compare before/after column fill. Replace `YOUR_SWITCH_START_UTC` with the time printed by the upgrade block:
```powershell
& (Join-Path $kit 'Test-ObserverLogAnalytics.ps1') @check -Check Cutover `
    -TableName $settings.TableName -CutoverTime 'YOUR_SWITCH_START_UTC'
```
Inspect stored values as well as column fill. Resolve failures and warnings, and validate dependent queries, alerts and workbooks before considering the migration complete. The application already uses `LogsIngestionOnly`; do not run the dual-write cutover block.

### Verify dual write

Compare stored counts and field values:
```powershell
$oldTable = if ($setup.LegacyTableName) { $setup.LegacyTableName } else { "$($setup.Application)_CL" }
& (Join-Path $kit 'Test-ObserverLogAnalytics.ps1') @check -Check DualWrite `
    -LegacyTableName $oldTable -NewTableName $settings.TableName
```
For a manual deployment, use its actual destination name in place of `$settings.TableName`. The default comparison ends 30 minutes ago; wait for a settled window with representative traffic. Review **Warning** results, including boundary ambiguity and sparse columns; do not treat them as Pass. Repoint queries, alerts and workbooks to the new permanent table before retiring the old path.

The checker compares standard fields only. For custom fields, confirm yourself before cutover that the new table's custom columns match the old table's names and types, and that their stored values match.

### Cut over (DualWrite only)

Fresh and single write already select `LogsIngestionOnly`; skip this section for those routes. For dual write, first complete comparison, rollback testing and consumer updates in its checklist.

In the SF console, retrieve current parameters again and change only the mode:
```powershell
$applicationName = Read-Host 'Application name, e.g. fabric:/FabricObserver'
$app = Get-ServiceFabricApplication -ApplicationName $applicationName
$parameters = @{}
foreach ($p in $app.ApplicationParameters) { $parameters[$p.Name] = $p.Value }
$parameters['LogAnalyticsWriteMode'] = 'LogsIngestionOnly'
Start-ServiceFabricApplicationUpgrade -ApplicationName $applicationName `
    -ApplicationTypeVersion $app.ApplicationTypeVersion -ApplicationParameter $parameters `
    -Monitored -FailureAction Rollback
Get-ServiceFabricApplicationUpgrade -ApplicationName $applicationName
```
Wait for **RollingForwardCompleted**, allow in-flight data to settle, and verify that new-table delivery continues and old-table delivery stops. Dual write increases ingestion volume and potentially cost while active.

### Stop, roll back or clean up

**Dual-write rollback:** Run the mode-change block in [Cut over](#cut-over-dualwrite-only) with `'LegacyOnly'` instead of `'LogsIngestionOnly'`. Restore complete legacy settings first if they were removed. Wait for upgrade completion and verify stored legacy records. To resume validation, use the same block with `'DualWrite'`, wait for completion, and verify both destinations again.

**Single-write rollback:** Subject to the [pending-validation gate](#path-2-single-write-migration-singlewrite), the same mode-change block can select `LegacyOnly`, but it targets the **converted table**, not a classic table. Do not rely on this restoring legacy delivery after schema changes until validated. Verify stored legacy records rather than HTTP acceptance alone. Keep a known-working previous package if a full application rollback is needed; that also does not undo conversion.

**Stop delivery (any route, including Fresh):** Set `ObserverManagerEnableTelemetryProvider=false` in the effective application parameters and deploy the change. Clearing the four new settings while leaving the new API selected is an invalid configuration, not a clean disable. Fresh setup has no legacy path to roll back to.

**Credential cleanup (migration routes):** Once rollback is no longer needed, explicitly clear legacy credentials in the effective application parameters and deploy; merely omitting them can retain old values. Verify new-API delivery afterward. Retiring legacy delivery does not require deleting the old table.

## Troubleshooting

| Health property | Check |
|---|---|
| `LogAnalyticsConfiguration` | Write mode: unknown values stop all Log Analytics sending. |
| `LogAnalyticsLegacyConfiguration` | Selected old API's workspace ID and shared key. |
| `LogAnalyticsIngestionConfiguration` | Selected new API's settings, host identity, permission, token and upload failures. |

Check local Warning logs as well as Service Fabric health. A selected API with invalid settings does not start; in dual write, the valid API can continue. Upload failures are not replayed. FabricObserver re-sends active warnings every 5 minutes; a ClusterObserver warning from a previous hosting node may require manual clearing. Mixed upload successes/failures can alternate Warning and Ok, so inspect stored data too.

## Manual and advanced setup

The helper is optional. Deploy [FabricObserver.LogAnalytics.json](FabricObserver.LogAnalytics.json) or [ClusterObserver.LogAnalytics.json](ClusterObserver.LogAnalytics.json) in the workspace's resource group: set `workspaceName`, `tableName`, `createTable=true` for fresh/dual write or `false` **after conversion** for single write, and `location` to the workspace region. Read the four `LogAnalytics*` outputs and `ManagedIdentityResourceId`; follow the same host, configuration, verification and rollback checkpoints above.

Optional helper/template overrides: `DcrName`, `DeploymentName` (helper only), `ManagedIdentityName`, `ExistingManagedIdentityResourceId`, `DataCollectionEndpointResourceId`, and `GrantPublisherRole=$false` when permission is granted separately. Choose distinct DCR/deployment names for multiple deployments of the same application in one resource group. Redeploying the default identity resource can reset manually added identity tags.

For custom fields (dual write only), add each one to the table schema (`recordColumns`) and DCR transformation (`transformKql`) with its legacy column name and type, then confirm the stored values before cutover; the default mappings are not a catch-all. Shared streams, cross-subscription hosts and private-link networks require manual setup. Keep the one-way conversion and rollback limits above even when not using the helper.
