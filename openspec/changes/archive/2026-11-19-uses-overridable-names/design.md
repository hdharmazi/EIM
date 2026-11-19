## Context

`per-environment-deployment` (implemented, reviewed, not yet archived) introduced owner configurations and `uses`. In `script/deployment/Deploy-Wilson.ps1`:

- `$InheritedNameKeys` lists twelve name keys. `Get-ResourceNames $Config $Owner` copies all twelve from the owner's resolved names.
- `Get-UsesProblems` refuses any of the twelve in a using configuration's `names`.
- `Get-InfraPlan`, `Get-ExistingResources` and `Get-FoundationProblems` treat the vault, plan and workspace as owner-only:
  - an owner creates them when missing;
  - a using configuration never probes or creates them, and preflight fails when they are missing.
- The Service Bus secret `eim-servicebus` is written by the owner's `settings` (`Sync-ServiceBusSecret`). A using configuration's `settings` and preflight fail while the owner's secret is missing.
- `sql` (owners only) keeps `app-outbound-*` firewall rules for `Get-SqlUserApps $Names $true`, which is the owner's own three apps. This covers a using configuration's Automation app too, because that app runs on the owner's plan and so has the same outbound addresses.

Which configuration uses which resource today:

| Resource | Used by the using configuration's apps | Used by the owner's shared apps |
|---|---|---|
| resource group, namespace | yes | yes |
| SQL server and database | yes | yes |
| gateway, dashboard API, static web app | called / shown | they are the shared apps |
| Key Vault | `ServiceBus` reference on its Automation app | `ServiceBus` on the owner's Automation app, `Simulation__ServiceBusConnectionString` on the dashboard API |
| App Service plan | hosts its Automation app; determines that app's outbound addresses on the owner's SQL firewall | hosts the gateway, the dashboard API and the owner's Automation app |
| Log Analytics workspace | backs its Automation Insights | backs the gateway and owner Automation Insights |

A using configuration deploys only its queues, its Automation storage, Insights and app, that app's settings, identity, vault policy and database user. The vault and the workspace are the only inherited resources that the using configuration's app can point at independently, without touching anything the owner runs or deploys. The plan looks similar, but it decides the app's outbound addresses, which only the owner's `sql` can allow (D9).

## Goals / Non-Goals

**Goals:**
- A using configuration can set `names.keyVault` or `names.logAnalytics`, and gets that resource as its own: created when missing, used as found otherwise, and referenced by its own Automation app.
- The other ten inherited keys, including `appServicePlan`, stay locked, with the same preflight message.
- Behaviour for the shipped configurations is unchanged: the same names and the same infra plan.
- The SQL firewall coverage is unchanged.
- Production isolation keeps holding, including for override names.
- No secrets in configurations or tests, and no Azure resource is deleted.

**Non-Goals:** see proposal.md. Also: no change to `deploy.test.json`, the package or the pipelines, and nothing about the Service Bus queue configuration (a separate change).

## Decisions

### D1. Split the inherited keys into locked and overridable

```powershell
$LockedNameKeys      = @('resourceGroup', 'serviceBusNamespace', 'sqlServer', 'sqlDatabase', 'appServicePlan',
                         'gatewayApp', 'gatewayStorage', 'gatewayInsights', 'dashboardApi', 'staticWebApp')
$OverridableNameKeys = @('keyVault', 'logAnalytics')
```

`$InheritedNameKeys` is removed. Nothing outside the script and its tests reads it.

- `Get-UsesProblems` loops `$LockedNameKeys` only. The message is unchanged: "'<name>' sets names.<key>, which it inherits from '<target>'; remove it (or set it in deploy.<target>.json)".

**Why these two.** Two checks were applied:

1. Each one is referenced only by an app setting or resource property of the using configuration's own Automation app:
   - the `ServiceBus` reference (`keyVault`)
   - the Insights `WorkspaceResourceId` (`logAnalytics`)
2. The owner's apps keep their own references. The owner's dashboard API and gateway resolve names with `Get-ResourceNames $owner`, never with the using configuration's names.

`appServicePlan` passes both checks but fails a third: nothing outside the configuration may depend on the choice. An Automation app on another plan has other outbound addresses, and the owner's `sql` firewall would then have to cover an app that the owner's configuration does not name (D9).

**Alternatives considered:**
- Make every key overridable. Rejected: a using configuration cannot run `sql`, `schema`, `gateway`, `backend` or `frontend`. An overridden SQL server, gateway or dashboard would point the Automation app at something nothing deploys, migrates or grants. An overridden resource group or namespace would also split the owner's queues and the dashboard simulation.
- Also make `appServicePlan` overridable. Rejected by the user; see D9.
- Drop `uses` for `test` and make it an owner. Rejected: `test` would then need its own namespace, SQL server and database, gateway and dashboard. That is the opposite of the request.
- A separate `own: ["keyVault"]` list instead of `names` overrides. Rejected: it adds a second mechanism. `names` already exists, and a blank value already means "use the default".

### D2. Owned keys: `Get-OwnedNameKeys`

A new pure function, `Get-OwnedNameKeys($Config, $Owner)`. It returns the subset of `$OverridableNameKeys` that the configuration owns:

- `$Owner` is set, and
- `names.<key>` is not blank, and
- its value differs from the owner's resolved name, ignoring case.

It returns `@()` when `$Owner` is `$null`. An owner configuration owns everything already, through `$IsOwner`.

The main block computes `$ownedKeys = @(Get-OwnedNameKeys $config $owner)` once and passes it on. The functions that take it gain a trailing parameter `[string[]]$OwnedKeys = @()`, so existing call sites and self-tests keep working:

- `Get-RequiredConfigPaths`
- `Get-PlaceholderProblems`
- `Get-OfflinePreflightProblems`
- `Get-ExistingResources`
- `Get-InfraPlan`
- `Get-FoundationProblems`
- `Invoke-PreflightComponent`
- `Invoke-InfraComponent`
- `Invoke-SettingsComponent`

The main block's name listing prints an "(own)" marker after an owned name. For example, `KeyVault  kv-eim-wtc-test (own)`.

**Alternative:** add an `Own` property to the `$Names` object. Rejected for three reasons:
- the self-test asserts the exact property list;
- `Get-IsolationProblems` iterates over the properties;
- the main block prints them.

A separate list keeps `$Names` a plain name map.

Map between config keys and the `$Existing` / `$Foundation` keys: `keyVault` maps to `KeyVault`, and `logAnalytics` to `LogAnalytics`. A small script-level map, `$OwnedResourceKeys`, holds it.

### D3. `Get-ResourceNames`

When `$Owner` is set:
- every key in `$LockedNameKeys` is copied from the owner, whatever the configuration sets (preflight has already refused a set one);
- every key in `$OverridableNameKeys` is copied from the owner only when the configuration's `names.<key>` is blank. Otherwise the configuration's value stays.

It does not fall back to the derived pattern. A using configuration that wants `kv-eim-wtc-test` writes that name. A blank value means "inherit", never "derive my own". The reason is that the owner's name is the safe default, while a silently derived `kv-eim-wtc-test` would create a resource nobody asked for.

An override equal to the owner's name resolves to the owner's spelling. That makes it a no-op both in the names and in `Get-OwnedNameKeys`.

`Get-NameProblems` validates the result as before. An override vault name that Azure rejects fails with the existing message.

### D4. `Get-ExistingResources` and the infra plan

`Get-ExistingResources($Names, $IsOwner, $OwnedKeys)` also probes `KeyVault` and `LogAnalytics` for a using configuration, for each one that is owned. It uses the same `show` calls and the `Managed` tag flag. It never probes `Plan` for a using configuration.

In `Get-InfraPlan($Config, $Names, $Existing, $IsOwner, $OwnedKeys)`, the vault, plan and workspace steps of the owner-only block move into a helper, `Get-FoundationResourceSteps($Config, $Names, $Existing, [string[]]$Keys, $TagArgs, $Tags)`. Both callers use it:
- owners: as today, with `keyVault`, `appServicePlan` and `logAnalytics`;
- using configurations: only the keys in `$OwnedKeys` (a subset of `keyVault` and `logAnalytics`), at the same position. The order stays vault, then workspace, all before the queues, so the plan order matches the owner's.

The steps are the same ones owners get:
- `create Key Vault <name> (access policies)`: kind `keyvault`, `--enable-rbac-authorization false`, soft-delete recovery.
- `create Log Analytics workspace <name> (<logAnalytics.retentionDays> days)`.

Each one:
- goes into `$Names.ResourceGroup` (the owner's group);
- is tagged with the using configuration's tags (`eim-environment=test`);
- uses the using configuration's own merged `logAnalytics.retentionDays` (from `deploy.base.json` unless overridden).

No other step changes, because the rest of the plan already reads `$Names`:
- `Get-FunctionAppSteps` creates the Automation app `--plan $Names.AppServicePlan`, which is always the owner's plan.
- `Get-InsightsSteps` and the `insights` step kind link to `$Names.LogAnalytics`.
- `Get-IdentitySteps` grants secret `get` on `$Names.KeyVault`.

The policy step's union logic (`Get-SecretPermissionUnion`) is unchanged and additive.

`Get-RequiredConfigPaths` adds `logAnalytics` for `infra` when `logAnalytics` is owned. That way a placeholder retention value is caught offline. A vault has no sizing key, so nothing is added for `keyVault`.

**Placement warning.** An Automation Insights resource that already exists stays linked to its workspace: the script does not relink existing resources. A new pure function, `Get-PlacementWarnings($Names, [string]$InsightsWorkspaceId)`, returns one warning when the id is not blank and its last segment is not, ignoring case, `$Names.LogAnalytics`. For example: "appins-eim-automation-wtc-test is linked to workspace 'log-eim-wtc-bat', not 'log-eim-wtc-test'; relink it by hand or recreate it".

`Invoke-InfraComponent` calls it for a using configuration that owns `logAnalytics` and whose Automation Insights exists. It reads `properties.WorkspaceResourceId` with the existing `az resource show` pattern. The warning never fails the run.

There is no plan placement warning: the Automation app's plan is always the owner's, as today.

### D5. Preflight and the foundation check

`Get-FoundationProblems($Names, $Foundation, $IsOwner, $Owner, $OwnedKeys)`:
- For an owner, nothing changes.
- For a using configuration, a missing resource whose key is owned becomes a note: "Key Vault 'kv-eim-wtc-test' does not exist; 'infra' will create it". Every other missing resource, including the plan, stays a problem: "'infra' and 'sql' must run for '<owner>' first".
- The RBAC-mode check is unchanged. It already uses `$Names.KeyVault`, so it covers an owned vault.

In `Invoke-PreflightComponent`, the owner-secret check ("run 'settings' for '<owner>' first") runs only when `keyVault` is not owned. With an owned vault, the configuration writes its own secret, as an owner does.

`Get-FoundationState` does not change. It already probes the resolved names.

### D6. The Service Bus secret for an owned vault (approved)

The secret is written in `settings`, not `infra`, for consistency with owners (per-environment-deployment D8). The user approved this.

- `infra` creates resources and grants access and never handles secret values.
- `settings` writes the secret immediately before the app setting that references it.

In `Invoke-SettingsComponent`, a using configuration that owns `keyVault` calls `Sync-ServiceBusSecret $Names` before writing the Automation settings. `$Names.ServiceBusNamespace` and `$Names.ResourceGroup` are the owner's, so the value is the owner's namespace connection string, copied into the owned vault, only when it is missing or differs. The function, its temp-file handling, its preview text and its error message do not change.

A using configuration that inherits the vault keeps the current check, unchanged: it fails when the owner's secret is missing.

`Get-AutomationSettings` needs no change: `ServiceBus = Get-KeyVaultSecretReference $Names` already follows `$Names.KeyVault`.

The pipeline does not change. `Deploy_test` with `deployInfra` and `applySettings` runs `preflight`, `infra`, `settings` and so on, so on the first run the vault is created before the secret is written.

**Alternative:** write the secret in `infra`. Rejected for two reasons:
- `infra` would then handle a secret value, which no component other than `settings` does today;
- it would differ from the owner flow, where the pipeline's `applySettings` controls when secrets and settings change.

### D7. Verify and health (approved)

They need no new check: both follow the reference automatically.

- `settings` writes `ServiceBus` from `$Names.KeyVault`.
- `health` reads the App Service Key Vault reference list (`Get-KeyVaultReferences`) and fails on any status other than `Resolved` (`Get-ReferenceProblems`). Both are vault-agnostic. An owned vault without the secret or without the app's policy fails health, naming the app and `ServiceBus`.
- The code-only drift check in preflight (`Get-SettingsDrift`) warns when `ServiceBus` differs, for example after the override was added and before `settings` ran. Values are references, not secrets.

`verify` prints the vault each reference resolves against, so an operator can see which vault is in use. The user approved this. A new pure helper, `Format-ReferenceStatus($Reference)`, returns `"<status> (<vaultName>)"`, or `<status>` when `vaultName` is blank. `Write-ReferenceReport` uses it. `health` does not change.

### D8. Deployer rights on an owned vault

| Component (using configuration) | Rights needed on the owned vault | Notes |
|---|---|---|
| `preflight` | control-plane read (`keyvault show`) | RBAC-mode check; Contributor on the RG covers it |
| `infra` | control-plane write (create, `set-policy`) | Contributor on the RG, the same as owners need on the owner vault. `az keyvault create` adds an access policy for the creating principal, so a vault the pipeline creates is writable by it. |
| `settings` | data-plane secret `get` and `set` | Automatic when the pipeline principal created the vault. A vault that existed before must get an access policy for the deployer by hand. The existing error message already says so. Also needs `listKeys` on the owner's namespace, which Contributor on the RG covers. |
| `myip`, `sqlusers`, `myipremove`, `automation`, `verify`, `health` | none | `verify` and `health` read app settings and reference status through the App Service API, not the vault |

With an owned vault, the using configuration needs nothing on the owner's vault any more. Its old access policy there stays in place, because the script removes nothing.

An owned workspace needs no extra rights: Contributor on the RG covers creating it and linking Insights to it.

### D9. `appServicePlan` stays locked; the SQL firewall is unchanged (approved)

The user decided to keep the plan locked. Reason: an Automation app on its own plan gets its own outbound addresses. A using configuration cannot run `sql`. So either the owner's `sql` would have to read and cover the apps of other configurations, or the addresses would never be allowed on a managed server, and any hand-added `app-outbound-*` rule would be deleted as stale by the owner's next `sql`.

With the plan locked:
- every Automation app runs on its owner's plan and shares its outbound addresses;
- `Get-AppOutboundIps`, `Invoke-SqlComponent`, `Get-SqlPlan` and `Get-SqlUserApps` do not change;
- `sql-identity-access` does not change;
- the sentence in "The deployment script deletes no Azure resource" that says the script "SHALL NOT read, change or report any app that its configuration does not name" stays as it is, with no exception.

An owned vault or workspace has no effect on outbound addresses.

**Alternatives considered:**
- Make `appServicePlan` overridable and have the owner's `sql` also cover the Automation apps of configurations that use it with their own plan. Rejected by the user: it widens the owner's `sql` to read other configurations' apps, and on the unmanaged `azql-wtc-sandbox` it still needs hand-added rules.
- Make it overridable and only warn. Rejected: on a managed server the owner's next `sql` would delete the hand-added rules as stale.

### D10. Isolation

`Get-IsolationProblems` resolves every configuration with `Get-ResourceNames $config $owner -SkipValidation`. Override names are therefore compared automatically:

- a non-production using configuration whose `names.keyVault` or `names.logAnalytics` equals, ignoring case, any name resolved for a production configuration fails, naming both configurations and the name;
- production stays unable to use a non-production configuration.

The C# mirror in `DeploymentConfigTests` gets the same resolver change, so `Production_and_nonproduction_names_are_disjoint` covers override names too.

### D11. Package and pipeline

No change:
- `New-DeploymentPackage.ps1` already packages each using configuration's owner file. Its README line "its own queues and Automation app, on the resources of <owner>" stays accurate for the shipped configurations.
- `pipelines/templates/deploy-environment.yml` already runs `infra` and `settings` for `test` when `deployInfra` and `applySettings` are ticked, with the same service connection, which is Contributor on the shared resource group.
- No new gated function, no new configuration key and no new secret variable.

### D12. `deploy.test.json` stays inheriting

`test` keeps `bat`'s vault and workspace in this change. `docs/deployment/deployment.md` documents the option with the example in proposal.md and the rights in D8. Adding it later is a one-line configuration change followed by a `Deploy_test` run with `deployInfra` and `applySettings`.

## Failure behaviour

This is deployment tooling, not message processing, so there is no terminal or transient message classification. The behaviour is as follows.

- **Offline (before any Azure call):**
  - A locked key (including `appServicePlan`) overridden in a using configuration fails preflight, unchanged.
  - An invalid override name fails name validation.
  - An override equal to a production name fails isolation.
  - A placeholder `logAnalytics.retentionDays` fails when `logAnalytics` is owned and `infra` runs.
- **Preflight (read-only):**
  - A missing owned resource is a note.
  - A missing inherited resource is a problem.
  - An owned vault in RBAC mode is a problem.
- **`infra`:**
  - Create steps are idempotent: a second run creates nothing.
  - A soft-deleted owned vault is recovered, as for owners.
  - A failed `set-policy` throws the existing message, naming the app and the vault.
  - A workspace placement mismatch is a warning only.
- **`settings`:**
  - `Sync-ServiceBusSecret` writes the secret only when it is missing or differs, and never prints the value.
  - If the deployer lacks data-plane rights, it fails with the existing "the deployer needs secret get and set on the vault (an access policy)" message before any app setting is written.
  - The app settings write is idempotent.
- **`health`:** fails, naming the app and `ServiceBus`, when the reference does not resolve. The Azure CLI throttling and transient retry in `Invoke-Az` (`$AzAttempts`) is unchanged.
- **Ordering:** `infra` (vault and policy) runs before `settings` (secret, then reference), as `Resolve-Components` and the pipeline already order them.
- **Deletion:** none. Removing an override later makes `settings` point back at the owner's vault and `infra` grant the owner-vault policy again. The owned vault or workspace stays and is no longer referenced.

## Risks / Trade-offs

- [Two copies of the same Service Bus connection string, in the owner's vault and in the owned vault] → Both are written from the same namespace key by their configuration's `settings`. After a key rotation, both `bat` and `test` must run `settings`. This is documented in deployment.md. `health` catches a stale reference only if the reference fails to resolve, not if the value is stale.
- [Key Vault names are global and soft-deleted names are held] → `Get-NameProblems` validates the format. Soft-delete recovery is reused. A name taken in another tenant fails `infra` with the Azure error, before any setting changes.
- [Key Vault reference resolution can lag after a new policy] → This is the same as for owners today. `health` waits for the host, not for references. Re-running `health` is the documented remedy.
- [A pre-existing vault named by an override, not created by the pipeline principal] → `settings` fails with the access-policy message. A manual policy is needed (D8).
- [An existing Automation Insights stays on the owner's workspace after `logAnalytics` is owned] → `infra` warns, naming both workspaces. Telemetry keeps flowing to the old workspace until the relink is done by hand.
- [Signature changes ripple through many functions] → Every new parameter is trailing, with a default of `@()`, so existing callers and self-tests are unaffected.

## Migration Plan

1. Implement and test (tasks.md, sections 1 and 2). Behaviour for the shipped configurations is identical: the self-test asserts the unchanged `test` plan and names.
2. No deployment is required. The next `eim` run behaves as before.
3. To give `test` its own vault later (not part of this change):
   - add `"names": { "keyVault": "kv-eim-wtc-test" }` to `deploy.test.json`;
   - run `eim` with `deployInfra` and `applySettings`;
   - check that `verify` shows `ServiceBus  Resolved (kv-eim-wtc-test)` for `azf-eim-automation-wtc-test`.
4. Rollback: revert the commit. If a configuration already owns a vault, removing the override first makes it reference the owner's vault again on its next `settings`.

## Archive order

The deltas modify requirements that `per-environment-deployment` adds or rewrites:
- "A configuration can use another environment's resources"
- "The deployment script deletes no Azure resource"
- "The Service Bus connection is held in the Key Vault"
- "Function App hosting baseline", "Missing resources are created and existing resources are used as found", "Preflight checks the foundation resources"

`openspec validate` checks delta structure only, so this change validates now. It must be archived after `align-structure-and-deployment`, `add-production-environment` and `per-environment-deployment`.

## Open Questions

None. The three questions of the previous version are decided:
1. The secret is written in `settings` (D6).
2. `appServicePlan` stays locked; the SQL firewall is unchanged (D9).
3. `verify` shows the vault next to each reference status (D7).
