## Context

Ticket **#5246**. This change follows two unarchived changes on the same branch: `align-structure-and-deployment` (committed) and `add-production-environment` (approved; committed before this change is built). It supersedes the uncommitted `remove-legacy-cutover` change, whose folder this change deletes. All of its scope is folded in here (decision D11).

### Current state

`script/deployment/Deploy-Wilson.ps1` merges `deploy.base.json`, `deploy.<name>.json`, an optional overrides file and `EIM_DEPLOY_CONFIG_OVERRIDES`.

**Configurations and scopes.**
- `nonprod` and `prdshared` have `scope: shared`. They own the plan, Log Analytics, gateway, dashboard and migrations.
- `bat`, `test` and `prd` have `scope: environment`. They own the queues and the Automation app.
- `sharedSuffix` groups configurations into stamps. `Get-SharedConfigurationName` and `Get-StampIsolationProblems` enforce one shared configuration per stamp.
- `Resolve-Components` rejects components outside a configuration's scope.

**Names and resources.**
- `Get-ResourceNames` derives shared names with `sharedSuffix` and Automation names with the environment.
- Preflight requires the resource group, Service Bus namespace, SQL server and database to exist (`Get-FoundationProblems`). The script never creates them.

**Secrets.**
- `EIM_DEPLOY_EIMDB` (SQL authentication) and `EIM_DEPLOY_SERVICEBUS` come from secret variables.
- `Get-SecretTargetProblems` checks that they point at the configured server, database and namespace.
- The app settings `EIMDB`, `ConnectionStrings__EIMDB`, `ServiceBus` and `Simulation__ServiceBusConnectionString` carry the secret values directly.

**Legacy and cutover machinery.**
- The `cutover`, `rollback` and `remove` components and `-IncludeDefaultKeys`.
- `legacy` sections and `gateway.legacyVerificationTargets`.
- `Get-GatedSettingsToWrite` writes `AzureWebJobs.<fn>.Disabled=true` only when a setting is absent.

**Pipelines.**
- `eim.yml`: Build, Deploy_shared, Deploy_bat, Deploy_test.
- `eim-prd.yml`: Deploy_prdshared, Deploy_prd.

### Constraints

- Nothing new exists in Azure yet. The only existing resources are:
  - `d365fsc-integration-nonprod`, `asb-eim-wtc-nonprod`, `kv-d365fsc-wtc-nonprod`, `azql-wtc-sandbox`/`eimdb`
  - `wtc-dashboard-api-dev`, `wtc-dashboard-frontend-dev`
  - the old apps
  - the empty production resource group `d365fsc-integration-prod`
- `azql-wtc-sandbox` has no Entra admin today.
- The service connections are workload-identity ARM connections with Contributor on their resource group. Contributor cannot assign RBAC roles.
- `Microsoft.Data.SqlClient` 7.0.0 has no Azure.Identity dependency. Entra connection strings need `Microsoft.Data.SqlClient.Extensions.Azure` 1.0.0, as in Wastequip's `ARM.Integration.Core.csproj`.
- The DbMigrator uses `dbup-sqlserver` 7.2.0, which brings `Microsoft.Data.SqlClient` 6.1.4 and Azure.Identity 1.17.1. It already supports `Authentication=Active Directory Default`.
- The apps need only DML and EXECUTE. A search of the C# code found no runtime DDL; only the DbMigrator calls `sp_getapplock`.
- Reference implementation (read-only): Wastequip `EIM/script/deployment/Deploy-Wastequip.ps1`. It has the `sql` and `sqlusers` components, `Get-SqlConnectionString`, `Invoke-DeployerSql`, `Confirm-KeyVault` and `Grant-VaultAccess`.

## Goals / Non-Goals

**Goals:**
- One configuration file per environment, with no stamps, scopes or shared configurations. `uses` lets an environment borrow another environment's resources.
- Environment-suffixed derived names, overridable per resource.
- Missing resources are created, and existing resources are used as found.
- SQL access through Entra ID: managed identities for the apps, the deployer's identity for migrations and users, and no SQL password anywhere.
- No secret pipeline variables.
- Fold in the whole `remove-legacy-cutover` scope.
- Production stays isolated from non-production.

**Non-Goals:** see the proposal's Non-goals. In short:
- no renames of existing resources
- no changes to existing resources beyond identities and access policies
- no RBAC data roles
- no deletion of resources, users, queues or rows
- no engine, migration or local-development changes

## Decisions

### D1. Archive order and spec targets

The deltas target the capabilities as they stand after archiving `align-structure-and-deployment`, then `add-production-environment`, then this change. MODIFIED, REMOVED and RENAMED headers quote the requirement names those two changes produce.

`resource-naming-cutover` keeps its capability name in the deltas. After the archive, a manual task renames the folder to `openspec/specs/resource-naming/`, because OpenSpec deltas cannot rename a capability. The new capability `sql-identity-access` holds the SQL authentication requirements.

### D2. Configuration model: one file per environment, optional `uses`

The configuration files are:
- `deploy.base.json`, holding values common to all environments
- `deploy.bat.json`
- `deploy.test.json`
- `deploy.prd.json`

`deploy.nonprod.json` and `deploy.prdshared.json` are deleted. The keys `scope` and `sharedSuffix` are removed. `Get-DeployConfig` fails naming the file when either key is present, so a stale override cannot bring them back silently.

Shapes (no secrets; `<...>` marks a placeholder):

```jsonc
// deploy.base.json (excerpt)
{
  "client": "wtc", "workload": "eim", "production": false,
  "location": "centralus", "subscriptionId": "<subscription id or name>",
  "appServicePlan": { "sku": "P0v3" },
  "serviceBus": { "sku": "Standard" },
  "logAnalytics": { "retentionDays": 30 },
  "sql": {
    "sku": "S0", "backupRedundancy": "Local", "firewall": "appOutboundIps",
    "entraAdmin": "", "entraAdminObjectId": "", "entraAdminType": "Group"
  },
  "entra": { ... unchanged ... },
  "gateway": { "baseUrl": null, "maxRequestBytes": 4194304, ... },        // no legacyVerificationTargets
  "automation": { ..., "triggersEnabled": false, "keepDisabled": [], ... },
  "dashboard": { "simulationEnabled": true, "simulationQueuePrefix": "bat" }
  // no "names", no "sharedSuffix"
}

// deploy.bat.json
{
  "environment": "bat",
  "names": {
    "resourceGroup": "d365fsc-integration-nonprod",
    "serviceBusNamespace": "asb-eim-wtc-nonprod",
    "keyVault": "kv-d365fsc-wtc-nonprod",
    "sqlServer": "azql-wtc-sandbox",
    "sqlDatabase": "eimdb",
    "dashboardApi": "wtc-dashboard-api-dev",
    "staticWebApp": "wtc-dashboard-frontend-dev"
  },
  "automation": { "triggersEnabled": true }
}

// deploy.test.json
{ "environment": "test", "uses": "bat", "automation": { "triggersEnabled": true } }

// deploy.prd.json
{
  "environment": "prd", "production": true,
  "names": { "resourceGroup": "d365fsc-integration-prod" },
  "logAnalytics": { "retentionDays": 90 },
  "sql": { "entraAdmin": "<production sql entra admin group>",
           "entraAdminObjectId": "<production sql entra admin group object id>" },
  "entra": { "<production ...>" },
  "automation": { "excludedUsers": "<production semicolon separated user guids>", "triggersEnabled": false },
  "dashboard": { "simulationEnabled": false, "simulationQueuePrefix": "" }
}
```

**Owner configuration.** A configuration without `uses` is an owner. It may request every component:

```
preflight, infra, sql, settings, myip, schema, sqlusers, myipremove,
gateway, backend, frontend, automation, verify, health
```

`all` expands to all of them except `myip` and `myipremove`, as today.

**Using configuration.** A configuration with `uses: "<env>"` may request only:

```
preflight, infra, settings, myip, sqlusers, myipremove, automation, verify, health
```

- Its `infra` creates only its queues and its Automation storage, Insights and app. The app goes on the owner's plan and the Insights link to the owner's workspace.
- Its `settings` writes only its Automation app's settings.
- Its `sqlusers` grants only its Automation app's identity, on the owner's database.
- Requesting `sql`, `schema`, `gateway`, `backend` or `frontend` fails, naming the owner configuration, before any Azure call.

This replaces `$ScopeComponents`, `Get-SharedConfigurationName`, `Get-StampConfigurations` and the scope branch of `Resolve-Components`.

`Resolve-Components($Requested, [bool]$IsOwner, [string]$Owner)` keeps the fixed order. `$ComponentOrder` becomes:

```
preflight, infra, sql, settings, myip, schema, sqlusers, myipremove,
gateway, backend, frontend, automation, verify, health
```

The new function `Get-UsesProblems($Config, $AllConfigs)` returns a message, and preflight fails before any Azure call, when any of these holds:
- `uses` names a configuration that does not exist ("unknown target").
- `uses` names the configuration itself, or a configuration that itself has `uses` ("chains are not allowed", which also covers cycles).
- The configuration is `production: true` and its target is not.
- The configuration sets a `names` key that `uses` inherits. The message names the key and the target.

This function is pure, so it is tested by dot-sourcing.

The inherited name keys are:

```
resourceGroup, serviceBusNamespace, keyVault, sqlServer, sqlDatabase,
appServicePlan, logAnalytics, gatewayApp, gatewayStorage, gatewayInsights,
dashboardApi, staticWebApp
```

*Alternatives considered:*
- **Keep stamps.** Rejected by the user.
- **Merge the owner's whole configuration into the using configuration.** Rejected. It would make the `test` settings depend on `bat` values, such as triggers, simulation and Entra. Only resource identity is shared.
- **Allow chains** (for example `a` uses `b` uses `c`). Rejected as unneeded, and it would need cycle handling.

### D3. Naming

`Get-ResourceNames($Config, $Owner)` derives every name from the configuration's own `environment` (`$e`), with `$c` the client and `$w` the workload. When `$Owner` is given, it copies the inherited keys from the owner's resolved names.

| Key | Pattern |
|---|---|
| resourceGroup | `rg-{w}-{c}-{e}` |
| serviceBusNamespace | `sbns-{w}-{c}-{e}` |
| keyVault | `kv-{w}-{c}-{e}` |
| sqlServer | `sql-{w}-{c}-{e}` |
| sqlDatabase | `sqldb-{w}-{c}-{e}` |
| appServicePlan | `asp-{w}-{c}-{e}` |
| logAnalytics | `log-{w}-{c}-{e}` |
| gatewayApp | `azf-{w}-gateway-{c}-{e}` |
| gatewayStorage | `st{c}{w}gw{e}` |
| gatewayInsights | `appins-{w}-gateway-{c}-{e}` |
| dashboardApi | `app-{w}-dashboard-api-{c}-{e}` |
| staticWebApp | `swa-{w}-dashboard-{c}-{e}` |
| automationApp | `azf-{w}-automation-{c}-{e}` |
| automationStorage | `st{c}{w}auto{e}` |
| automationInsights | `appins-{w}-automation-{c}-{e}` |
| queuePrefix | `{e}` |

A non-blank `names.<key>` wins over the pattern. A blank override falls back to the pattern.

Validation in preflight, extending the existing checks:
- storage: `^[a-z0-9]{3,24}$`
- Function App: at most 32 characters
- Key Vault: `^[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$`
- SQL server: `^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$`

Resolved names:

| Resource | bat | test (uses bat) | prd |
|---|---|---|---|
| Resource group | `d365fsc-integration-nonprod` (override) | bat's | `d365fsc-integration-prod` (override) |
| Service Bus namespace | `asb-eim-wtc-nonprod` (override) | bat's | `sbns-eim-wtc-prd` |
| Key Vault | `kv-d365fsc-wtc-nonprod` (override) | bat's | `kv-eim-wtc-prd` |
| SQL server / database | `azql-wtc-sandbox` / `eimdb` (overrides) | bat's | `sql-eim-wtc-prd` / `sqldb-eim-wtc-prd` |
| Plan / workspace | `asp-eim-wtc-bat` / `log-eim-wtc-bat` | bat's | `asp-eim-wtc-prd` / `log-eim-wtc-prd` |
| Gateway | `azf-eim-gateway-wtc-bat`, `stwtceimgwbat`, `appins-eim-gateway-wtc-bat` | bat's | `azf-eim-gateway-wtc-prd`, `stwtceimgwprd`, `appins-eim-gateway-wtc-prd` |
| Dashboard | `wtc-dashboard-api-dev`, `wtc-dashboard-frontend-dev` (overrides) | bat's | `app-eim-dashboard-api-wtc-prd`, `swa-eim-dashboard-wtc-prd` |
| Automation | `azf-eim-automation-wtc-bat`, `stwtceimautobat`, `appins-eim-automation-wtc-bat` | `azf-eim-automation-wtc-test`, `stwtceimautotest`, `appins-eim-automation-wtc-test` | `azf-eim-automation-wtc-prd`, `stwtceimautoprd`, `appins-eim-automation-wtc-prd` |
| Queues | `bat-*` | `test-*` | `prd-*` |

A fully isolated environment needs only `{ "environment": "prdmck" }`. It then derives `rg-eim-wtc-prdmck`, `sbns-eim-wtc-prdmck`, `kv-eim-wtc-prdmck`, `sql-eim-wtc-prdmck`, `azf-eim-automation-wtc-prdmck` (29 characters), and so on. This works as long as its environment name keeps every derived name valid.

The `prd` names are unchanged from `add-production-environment`, except the Key Vault, which is now created as `kv-eim-wtc-prd`.

*Alternatives considered:* keeping the `nonprod` suffix for bat's plan, workspace and gateway. Rejected: the user chose environment suffixes. They can still be overridden.

### D4. Create if missing, use as found

**Managed resources.** A resource the script creates is tagged:
- `eim-workload`
- `eim-client`
- `eim-environment`, set to the configuration that created it

`eim-scope` is dropped. A resource with an `eim-environment` tag is *managed*. `infra` converges configuration only on managed resources. That covers the Function App baseline (`Get-FunctionAppSteps`), the dashboard API site settings, and the SQL firewall.

**Existing resources the script did not create** are used as found: no SKU, tier, plan, tag, firewall, authentication or site-configuration change. The existing rule "kept dashboard resources are deployed in place" becomes this general rule.

**Additive exceptions,** needed for Entra and Key Vault and recorded here:
1. `infra` enables the system-assigned identity (`az functionapp|webapp identity assign`) on every app the configuration deploys:
   - owners: the gateway, the dashboard API and their own Automation app
   - using configurations: their own Automation app

   This includes the kept app `wtc-dashboard-api-dev`.
2. `infra` adds a Key Vault access policy with secret `get` for each of those identities (`az keyvault set-policy --object-id ... --secret-permissions get`). This also applies to the kept vault `kv-d365fsc-wtc-nonprod`.

The `infra` plan per resource, for owners:

| Resource | Missing | Exists |
|---|---|---|
| Resource group | `az group create` with tags. Needs subscription-scope rights; `bat` and `prd` already exist. | used |
| Service Bus namespace | create, `serviceBus.sku` | used |
| Queues (own prefix) | create with today's properties | used |
| Key Vault | create with `--enable-rbac-authorization false`; recover if soft-deleted (Wastequip `Confirm-KeyVault`) | used; preflight fails if it uses RBAC authorization, naming the vault |
| Plan | create Linux, `appServicePlan.sku` | used |
| Log Analytics | create PerGB2018, `logAnalytics.retentionDays` | used |
| Gateway storage, Insights, app | create on the plan; baseline | baseline only if managed |
| Dashboard API | create (DOTNETCORE:10.0, Always On, FTPS off, TLS 1.2, HTTPS only) | used |
| Static web app | create Standard | used |
| Automation storage, Insights, app | create; baseline | baseline only if managed |
| Identities, vault access | assign / set-policy | assign / set-policy (idempotent) |

For a using configuration, `infra` creates only its queues, its Automation resources and its identity and access policy. Preflight fails when any owner resource it needs is missing: the resource group, namespace, vault, plan, workspace, SQL server or database. The message states that `infra` and `sql` must run for the owner first. This replaces `Get-FoundationProblems`.

*Alternatives considered:* converging configuration on every named resource. Rejected: the user ruled that existing resources keep their SKU and configuration.

### D5. `sql` component (owners only)

`Invoke-SqlComponent` runs after `infra`, because firewall rules need the apps' outbound IPs.

**Server missing:**
- Run `az sql server create` with:
  - `--enable-ad-only-auth`
  - `--external-admin-principal-type <sql.entraAdminType>`
  - `--external-admin-name <sql.entraAdmin>`
  - `--external-admin-sid <sql.entraAdminObjectId>`
  - `--minimal-tls-version 1.2`
  - tags
- `sql.entraAdmin` and `sql.entraAdminObjectId` must be filled in. Otherwise the `sql` plan fails before any change, naming both keys.
- There is no SQL admin login, so there is no password. Wilson does not take Wastequip's optional `sqlAdminLogin`.

**Database missing:** run `az sql db create --service-objective <sql.sku> --backup-storage-redundancy <sql.backupRedundancy>`, with tags.

**Firewall,** only on a managed server:
- `sql.firewall = appOutboundIps` keeps one rule `app-outbound-<a-b-c-d>` for each `possibleOutboundIpAddresses` entry of the configuration's apps, and deletes stale `app-outbound-*` rules.
- `azureServices` ensures `AllowAllWindowsAzureIps`.

This mirrors Wastequip. Using configurations share the owner's plan, so they share its outbound IPs.

**Existing, unmanaged server** (`azql-wtc-sandbox`) is read only:
- `sql` reports the Entra admin, whether Entra-only authentication is on, and whether every app outbound IP is covered by a rule or by `AllowAllWindowsAzureIps`.
- A missing Entra admin or an uncovered IP is a warning naming the server and IPs. It is never a change.
- When `sql.entraAdminObjectId` is filled in and differs from the server's admin SID, `sql` warns.
- Preflight fails when `schema` or `sqlusers` is requested and the server has no Entra admin. The message names the server and the manual step.

`Get-SqlPlan($Config, $Names, $Existing)` is pure, so it is tested by dot-sourcing.

### D6. `sqlusers` component and contained users

`Invoke-SqlUsersComponent` connects as the deployer:
- It gets a token with `az account get-access-token --resource https://database.windows.net/` and sets `SqlConnection.AccessToken`, as Wastequip's `Invoke-DeployerSql` does.
- It needs `myip` on the agent first.

For each app identity the configuration deploys:
1. Read the principal id with `az webapp identity show`. A missing identity fails with "run `infra` first".
2. Read the identity's client id from ARM: `GET {siteId}/providers/Microsoft.ManagedIdentity/identities/default?api-version=2023-01-31`, then `properties.clientId`. Fall back to `az ad sp show --id <principalId> --query appId`.
3. If `sys.database_principals` has no user with that name, run the following, built with `QUOTENAME` from parameters, never by string-concatenating config values:

   ```sql
   CREATE USER [<app name>] WITH SID = <client id as binary(16)>, TYPE = E
   ```

   If a user with that name exists with a different SID (the app was recreated), fail naming the user. The script never drops a user; the fix is manual.
4. Run `ALTER ROLE db_datareader ADD MEMBER`, `ALTER ROLE db_datawriter ADD MEMBER` and `GRANT EXECUTE TO` the user. All three are idempotent.
5. Read back and print each user with its roles. This is the component's proof.

**Why `WITH SID ... TYPE = E` rather than Wastequip's `FROM EXTERNAL PROVIDER`:** `FROM EXTERNAL PROVIDER` makes the SQL server resolve the name through Microsoft Graph. When a service principal runs it, the server identity needs Directory Readers. Nobody can grant that with Contributor. The SID form needs no Graph lookup and works the same on `azql-wtc-sandbox` and on a new server.

**Gating.** `sqlusers` writes to the database, including the shared `eimdb`.
- `all` includes it.
- The pipeline runs it only when the new manual parameter `grantDatabaseAccess` is selected (default off), behind the stage approval.
- The first run against `eimdb` is a manual user task with `-WhatIf` first (tasks 4.x).
- Luigi never runs it.
- Preview (`-WhatIf`) lists the users and roles it would create and opens no SQL connection.

The deployer must be the server's Entra admin or a member of the admin group. The recommended admin is a group that holds the operators and the service connection's principal. A login failure is caught and rethrown as "`<identity>` cannot sign in to `<server>`: add it to the server's Entra admin group", with no token in the message.

### D7. Connection strings, code and the migrator

`Get-SqlConnectionString($Names, $Authentication)` builds:

```
Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<db>;Authentication=<auth>;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;
```

- The apps use `Active Directory Managed Identity`. That covers `EIMDB` on the Automation app and the gateway, and `ConnectionStrings__EIMDB` on the dashboard API. These values are not secret.
- The migrator uses `Active Directory Default`. `Invoke-SchemaComponent` passes `EIMDB_MIGRATOR_CONNECTION` plus `AZURE_TOKEN_CREDENTIALS=AzureCliCredential` in the child environment. Under `AzureCLI@2` that is the service connection's principal. Locally, it is the operator's `az login`.

**Code:**
- `ARM.Integration.Core/ARM.Integration.Core.csproj` adds `Microsoft.Data.SqlClient.Extensions.Azure` 1.0.0. It flows to EIM, the gateway, Orchestrations, Automation and the dashboard API through their project references. Without it, SqlClient 7 throws on `Authentication=Active Directory Managed Identity` at the first open.
- No C# logic changes. `SQLServerHelper` and the repositories already use `SqlConnectionStringBuilder` or plain strings, which accept `Authentication`.
- The DbMigrator needs no package change, because SqlClient 6.1.4 brings Azure.Identity. Its usage text and `docs/deployment/database-migrations.md` gain the Entra example.
- A new test, `Wilson.Integration.Tests/SqlEntraAuthenticationTests.cs`, asserts that SqlClient resolves a provider for `ActiveDirectoryManagedIdentity` and `ActiveDirectoryDefault`.
- Local development keeps SQL or Windows authentication against a local SQL Server.

**Token lifetime under workload identity.** `AzureCLI@2` logs in with a short-lived federated assertion. Later `az account get-access-token` calls for a new resource can fail once that assertion expires. Mitigation:
- The database step is its own `AzureCLI@2` step.
- Its first action (`myip`) is followed immediately by one `az account get-access-token --resource https://database.windows.net/`, which puts the SQL token in the CLI cache. The token is valid for at least an hour.
- The migrator's `AzureCliCredential` and `sqlusers` then use the cached token.

### D8. Service Bus connection through Key Vault

**Owner `settings`:**
- Reads the namespace's `RootManageSharedAccessKey` primary connection string (`az servicebus namespace authorization-rule keys list`).
- Compares it with the Key Vault secret `eim-servicebus`, and writes the secret only when it is missing or different.
- Passes the value through a temp file that is deleted in `finally`.
- Never prints it. The preview lists only the secret name.

**App settings** use a versionless Key Vault reference:
- `ServiceBus` on the Automation app
- `Simulation__ServiceBusConnectionString` on the dashboard API, when simulation is on

The reference has the form:

```
@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/eim-servicebus/)
```

**Using configurations** reference the owner's secret. Their preflight fails if the secret does not exist yet: "run `settings` for `<owner>` first".

**`verify`** reports the Key Vault reference status of `ServiceBus` and `Simulation__ServiceBusConnectionString` from `GET {siteId}/config/configreferences/appsettings`. `health` fails the stage when a reference is not `Resolved`, naming the app and setting.

The Automation host's existing `RequiredSettings` check also refuses to start with an unresolved reference (`SettingState.UnresolvedKeyVaultReference`).

*Alternatives considered:*
- **Keep `EIM_DEPLOY_SERVICEBUS` as a secret variable.** Rejected: every isolated environment would need manual secret plumbing, and it keeps a secret in the variable groups.
- **Managed identity with Service Bus data roles.** Rejected: Contributor cannot assign roles (non-goal).

### D9. No deployment secrets

The following are deleted:
- `Get-RequiredSecrets`, `Resolve-DeploySecrets`, `Get-SecretTargetProblems`
- `Get-SqlServerFromConnection`, `Get-SqlCatalogFromConnection`, `Get-ServiceBusNamespaceFromConnection`
- the `$Secrets` parameters

The pipelines map only `EIM_DEPLOY_CONFIG_OVERRIDES`, which is not secret. `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` disappear from the repository, except in the openspec change records and `script/Superseded/README.md`, which describes retired scripts. The variable groups `eim-wtc-nonprod` and `eim-wtc-prd` keep their names. Removing the two secret variables from them is a manual task.

The requirement that "secrets point at the configured resources" is removed: there is nothing to point.

### D10. Triggers from configuration (from `remove-legacy-cutover` D2)

- `automation.triggersEnabled` is a JSON boolean: `false` in the base, `true` in `bat` and `test`, and explicitly `false` in `prd`.
- `Get-GatedSettings([string[]]$GatedFunctions, [bool]$TriggersEnabled, [string[]]$KeepDisabled)` returns every `AzureWebJobs.<fn>.Disabled`, `'false'` or `'true'`. `settings` writes the whole map on every run, overwriting portal changes.
- `Get-TriggerConfigProblems($Config)` fails preflight when `settings` or `automation` is requested and the key is missing or not a boolean, or when `keepDisabled` names a function outside `gatedFunctions`.
- `'AzureWebJobs.*.Disabled'` leaves `$DriftNameOnlySettings`. The code-only drift check fails on a missing gated setting and warns, naming it, on a differing value.
- `automation` still refuses to deploy while a gated setting is missing ("run `settings` first").

### D11. Removing the legacy and cutover machinery (from `remove-legacy-cutover` D3)

**Deleted from `Deploy-Wilson.ps1`:**
- the `cutover`, `rollback` and `remove` components, `$OnDemandComponents` and `-IncludeDefaultKeys`
- `Get-CutoverRefusal` through `ConvertTo-LockSeconds`, and `Get-CutoverStatePath` through `Invoke-RemoveComponent`
- `Get-EffectiveFunctionStates`, `Set-GatedTriggers` and `Move-GatedTriggers`
- `Get-LegacyNames`, `Get-TriggerGuardApps` and `Get-LegacyNameConflicts`
- the `legacy` config path
- the `legacyVerificationTargets` loop in `Get-DashboardApiSettings`
- the legacy entries in `Get-VerifyTargets`
- `$QueueSettingNames`, `$ParityCriticalSettings`, `$ParityIgnoredSettings`, `$RemovableTypes` and `$GatewayKeyFunctions`

**Kept:**
- `Get-StaleVerificationTargetSettings`, which deletes leftover `Gateway__Applications__*` settings
- `Get-QueueNames`, `Get-GatedTriggerState` and `Write-GatedReport`

**Script-wide rule.** The script deletes no Azure resource. Its only deletions are:
- the app settings it owns: `retiredSettings`, stale `Simulation__*` and `Gateway__Applications__*`
- the `myip` firewall rule
- stale `app-outbound-*` rules on managed SQL servers

**Docs.** `docs/deployment/resource-rename-cutover.md` and all links to it are deleted.

**Tests.** `RepositoryLayoutTests` gains a test, `No_deployment_file_describes_a_cutover`. It bans these terms in the deployment files:
- `legacy`, `cutover`, `cut over`
- `rollback`, `roll back`
- `retiredAutomationApps`, `IncludeDefaultKeys`
- `sharedSuffix`, `prdshared`, `Deploy_shared`
- `EIM_DEPLOY_EIMDB`, `EIM_DEPLOY_SERVICEBUS`

`docs/deployment/database-migrations.md` is exempt for the rollback terms only.

### D12. Pipelines

**`pipelines/eim.yml`:**
- Stages: Build, then Deploy_bat, then Deploy_test. The Build stage packages `bat,test,prd`.
- **Deploy_bat:**
  - `environment: bat`, `devopsEnvironment: eim-wtc-bat`, `ownsResources: true`
  - `components: gateway, backend, frontend, automation`
  - migrations and database access available
- **Deploy_test:**
  - `environment: test`, `devopsEnvironment: eim-wtc-test`, `ownsResources: false`
  - `components: automation`
  - database access available; migrations never run
- Both stages use `sc-eim-wtc-nonprod` and `eim-wtc-nonprod`.

**`pipelines/eim-prd.yml`:**
- One stage, Deploy_prd: `environment: prd`, `devopsEnvironment: eim-wtc-prd`, `ownsResources: true`, `components: gateway, backend, frontend, automation`.
- It uses `sc-eim-wtc-prd`, `eim-wtc-prd` and `packageSource: eim` (an `eim` run on `main`).
- It has no trigger and no PR trigger, and uses `lockBehavior: runLatest`.

**Parameters** on both pipelines, with matching defaults:
- `deployInfra`
- `applySettings`
- `runMigrations`
- `grantDatabaseAccess`, new, default `false`
- `deployCode`

**`pipelines/templates/deploy-environment.yml`:**
- Gains the `ownsResources` (boolean) and `grantDatabaseAccess` parameters.
- **Infrastructure step:** `infra, sql` when `ownsResources`, otherwise `infra`.
- **Database step:** runs when `runMigrations` (owners only) or `grantDatabaseAccess` is selected. It runs `myip` and primes the SQL token, then in a `try` runs `schema` and/or `sqlusers`, and in `finally` runs `myipremove`.
- **Secrets:** every `AzureCLI@2` step maps only `EIM_DEPLOY_CONFIG_OVERRIDES`.
- **Template guard:** `runMigrations` with `ownsResources: false` is a template-expansion error.

**Retired:** the DevOps environments `eim-wtc-nonprod` and `eim-wtc-prdshared` (manual clean-up).

*Alternative considered:* letting the script skip components that are outside a using configuration. Rejected: explicit rejection is safer, and the template knows the stage's role.

### D13. Production isolation

The new function `Get-IsolationProblems($AllConfigs)` replaces `Get-StampIsolationProblems`. It is pure, and preflight runs it over every configuration in the directory. It fails when:
- a production configuration `uses` a non-production configuration, or
- any resolved name of a production configuration equals any resolved name of a non-production configuration, compared case-insensitively. The queue prefix is excluded.

The name comparison also catches a non-production configuration that uses `prd`, because their names would be equal.

**Inherited non-production values.** `DeploymentConfigTests.NonProductionIdentifyingPaths` becomes:

```
entra.tenantId, entra.clientId, entra.audience,
automation.excludedUsers,
dashboard.simulationEnabled, dashboard.simulationQueuePrefix
```

The `names.*` paths, `sharedSuffix` and `gateway.legacyVerificationTargets` leave the list, because the base no longer holds them. A new test asserts that `deploy.base.json` has no `names` and no `sharedSuffix`.

### D14. Package

`New-DeploymentPackage.ps1` builds a frontend, `dashboard-frontend.<name>.zip`, for every packaged configuration that has no `uses` and whose Entra values are filled in. For this pipeline that means `bat` and `prd`. `test` gets none.

`Get-PackageReadme` drops the stamp, legacy, cutover and remove text, and describes owners and `uses`.

### D15. Existing `azql-wtc-sandbox` / `eimdb`

- **Entra admin.** The user sets the Entra admin on `azql-wtc-sandbox` (task 4.2), recommended to be a group holding the operators and the `sc-eim-wtc-nonprod` principal. The script never sets it: the server is not managed.
- **Authentication mode.** SQL authentication stays enabled (mixed mode). The old apps and tools that use SQL logins keep working until they are deleted. Turning on Entra-only authentication is a later, manual decision.
- **Secret variable.** `EIM_DEPLOY_EIMDB` goes. The migrator and `sqlusers` use the pipeline principal through the admin group.
- **First users.** The first `sqlusers` run against `eimdb` is a manual user run from the operator's machine (`-WhatIf` first, then on the user's go). After that, pipeline runs need `grantDatabaseAccess` and the `eim-wtc-bat` / `eim-wtc-test` approvals.
- **Firewall.** The `azql-wtc-sandbox` firewall is not changed by the script. `sql` and `verify` warn when the `asp-eim-wtc-bat` outbound IPs are not covered, and the user adds rules by hand (task 4.4).

### D16. Failure behaviour

This change touches no message processing, orchestration, routing or failure classification.
- `DefaultFailureClassifier` is unchanged, because no new exception type is introduced.
- A missing database user or an unreachable token endpoint surfaces as a `SqlException` in the same places as a SQL-authentication failure today, and is classified exactly as today.
- Service Bus redelivery and dead-lettering are unchanged.

**Idempotency of the deployment.** Every component can be rerun:
- creates happen only when a resource is missing
- `identity assign`, `set-policy`, `ALTER ROLE ADD MEMBER` and `GRANT` are idempotent
- the Key Vault secret is written only when it differs
- gated settings are written to the same values

**Failure halting.** A failed component stops the run (the existing rule). `myipremove` always runs in `finally`.

**Ordering.**
- `infra` runs before `sql`, which needs the outbound IPs.
- `settings` writes the secret before the app settings that reference it.
- In the pipeline, `schema` runs before code.
- A using configuration's preflight requires the owner's resources and secret.

**Schema.** There is no migration. Existing migrations 001 to 015 are untouched. The apps' new authentication needs no schema object.

## Risks / Trade-offs

- [Setting the Entra admin on the shared `azql-wtc-sandbox` affects every user of that server] → Mitigation:
  - This is an additive admin setting; SQL authentication stays on.
  - It is a manual user task, previewed with the exact `az sql server ad-admin create` command.
- [Wrong SID: client id and object id confused] → Mitigation:
  - The client id is read from the ARM identity endpoint.
  - `sqlusers` reads the user back.
  - The bat verification checks that the apps run queries without login failures in Application Insights.
- [The service connection's principal is not in the SQL admin group, so `schema` fails] → Mitigation:
  - The error message names the fix.
  - A manual setup task covers it.
  - Preflight fails early when the server has no Entra admin.
- [The workload-identity token expires during a long migration] → Mitigation: the token is primed at the start of the database step and cached for at least an hour. Migrations are small.
- [An unresolved Key Vault reference: firewall on the vault, missing access policy, or RBAC-mode vault] → Mitigation:
  - Preflight fails on an RBAC-mode vault.
  - `infra` sets the policies.
  - `verify` and `health` check reference status.
  - The Automation host refuses unresolved references.
- [The kept dashboard API gains an identity and a new `ConnectionStrings__EIMDB` value] → Mitigation:
  - This is additive.
  - Its database user is created before `settings` switches the value. Tasks order `sqlusers` before the first `settings` run.
- [The old apps still consume the `bat-*`/`test-*` queues] → Mitigation: the user stops `azf-eim-customer-wtc-{dev,bat,test}` and disables their triggers before the first Automation deployment (task 4.1).
- [An older package cannot be redeployed once the secret variables are gone] → Accepted: nothing has been deployed with the new tooling. Reverting means rerunning a stage of a run built after this change.
- [The `bat` gateway name changes from the earlier `azf-eim-gateway-wtc-nonprod` plan to `azf-eim-gateway-wtc-bat`] → No impact: that resource never existed. Consumers get new keys and the new base URL (task 4.6).
- [A new owner environment needs subscription-scope rights to create its resource group] → Documented in `devops-setup.md`. `bat` and `prd` reuse existing groups.

## Migration Plan

1. Archive `align-structure-and-deployment`, then `add-production-environment`. Implement this change. Then archive it, and rename `openspec/specs/resource-naming-cutover/` to `resource-naming`.
2. Manual preparation (user):
   - stop the old Automation apps
   - create the SQL admin group and set it on `azql-wtc-sandbox`
   - add the service connection principals to it
3. `eim` manual run, or local runs with `-WhatIf` first:
   - Deploy_bat with `deployInfra`, then `grantDatabaseAccess`, then `applySettings`, `runMigrations` and `deployCode`
   - then Deploy_test
4. Verify bat and test:
   - triggers enabled
   - queues draining
   - no SQL login failures
   - Key Vault references resolved
5. Manual follow-ups (user):
   - new gateway keys for consumers
   - move `wtc-dashboard-api-dev` to `asp-eim-wtc-bat`
   - delete the old resources, pipeline definitions and secret variables
6. Production (later, user): fill in the `prd` placeholders, then run `eim-prd` with infrastructure, users, settings, migrations and code. Go live by setting `automation.triggersEnabled` to `true`.

**Reverting:** rerun the Deploy stage of an earlier run made after this change. The schema only moves forward. Turning processing off means setting `automation.triggersEnabled` to `false`, or listing functions in `keepDisabled`, and running `settings`.

## Open Questions

1. **SQL admin groups.** Which Entra groups should be the SQL admin for non-production (`azql-wtc-sandbox`) and production (`sql-eim-wtc-prd`)? The design assumes one group per environment, holding the operators and the service connection principal. `deploy.prd.json` carries placeholders until the user names the production group. `bat` needs no config value, because the server exists.
   Deferred by the user (2026-10-25). The production `sql` step stays blocked by the placeholders until answered; nothing else waits on it.
2. **Service Bus connection.** Resolved (user, 2026-10-25): yes. The script copies the namespace key into the Key Vault secret `eim-servicebus`, and `EIM_DEPLOY_SERVICEBUS` is removed.
3. **Production SQL sizing.** Production SQL uses the Wastequip values: SKU `S0` and `Local` backup redundancy. Confirm, or set a larger SKU and `Geo` redundancy in `deploy.prd.json`.
