## Context

`align-structure-and-deployment` (#5246) is not yet archived. It leaves the repository with:

- One script, `script/deployment/Deploy-Wilson.ps1`.
- A layered configuration: `deploy.base.json`, then `deploy.<name>.json`, then an overrides file, then `EIM_DEPLOY_CONFIG_OVERRIDES`. Objects merge per key, and lists and values are replaced.
- One pipeline, `pipelines/eim.yml`: `Build`, then `Deploy_shared` (nonprod), then `Deploy_bat`, then `Deploy_test`.

`deploy.base.json` holds non-production values:

- `sharedSuffix` `nonprod`
- `names.resourceGroup` `d365fsc-integration-nonprod`, `serviceBusNamespace` `asb-eim-wtc-nonprod`, `keyVault` `kv-d365fsc-wtc-nonprod`, `sqlServer` `azql-wtc-sandbox`, `sqlDatabase` `eimdb`
- `gateway.legacyVerificationTargets` naming the old nonprod gateway
- `dashboard.simulationQueuePrefix` `bat`
- Entra and subscription placeholders, which will be filled with the non-production values

The user's decisions for production:

- Production is a new, separate stamp that shares nothing with non-production.
- Production has no simulation: the production dashboard must not be able to publish simulated messages.
- The production App Service plan is P0v3.
- Production uses the existing, empty resource group `d365fsc-integration-prod` (centralus). The script does not create a resource group. Checked on 2026-10-23: the group holds no resources and is in the same subscription as `d365fsc-integration-nonprod`.
- The ticket is #5246, on the branch `feature/5246/align-structure-and-deployment`.

Facts from the code that shape the design:

- `Get-ResourceNames` uses an override only when it is not blank, so `""` falls back to the pattern name.
- `Resolve-Components` and `Get-InfraPlan` name the owning shared configuration as `$config.sharedSuffix`. That works only because `nonprod` is both the suffix and the configuration name. For production the suffix is `prd`, but the shared configuration is `prdshared`.
- `Get-DashboardApiSettings` always writes `Simulation__ServiceBusConnectionString` and the four simulation queues.
- `SimulationController` and `SimulationService` are registered unconditionally (`ARM.Integration.Dashboard.API/Program.cs` lines 17-18).
- `remove` deletes only `legacy.resources`, so a configuration without `legacy` deletes nothing. Nothing marks a configuration as one that must never be targeted.
- `Invoke-MyIpComponent` cross-checks the SQL server named in `EIM_DEPLOY_EIMDB` against `names.sqlServer`. No other check ties a secret to the configured resources.
- The Automation `DefaultEnvironment` is the configuration name in lower case (`prd`). Messages carry it as ENVIRONMENT, and route and environment lookups are case-insensitive, so `prd` matches `PRD` rows.
- Migrations 001, 002 and 006 seed non-production rows into every new database:
  - a sample `ENVIRONMENTTABLE` row
  - `SimulationEnvironment` configuration rows
  - a developer `USERTABLE` admin
  - 36 BAT/DEV/DEV2/TEST routes

  Applied migrations are never edited.
- The non-production `eimdb` serves DEV, DEV2, BAT and TEST.

## Goals / Non-Goals

**Goals:**
- Stand up a production stamp with the existing script, without forking it.
- Make sharing with non-production hard to do by accident: no inherited values, no shared names, and no secrets pointing at the other stamp.
- Promote to production only a package that `eim` already deployed to bat and test.
- Make simulation impossible on production, and off by default anywhere the setting is missing.
- Keep non-production behaviour unchanged, apart from one new setting (`Simulation__Enabled=true`).

**Non-Goals:** see the proposal. In short:

- no infrastructure-as-code for the foundations
- no Key Vault
- no scripted alerts
- no cleanup of the seeded non-production rows by migration
- no change to message processing

## Decisions

### 1. Production configuration: base plus explicit resets (layering option A)

`deploy.prdshared.json` and `deploy.prd.json` inherit from `deploy.base.json`. Each file explicitly resets every non-production value:

```json
{
  "environment": "prdshared",            // "prd" in deploy.prd.json
  "scope": "shared",                     // "environment" in deploy.prd.json
  "production": true,
  "sharedSuffix": "prd",
  "names": { "resourceGroup": "d365fsc-integration-prod", "serviceBusNamespace": "", "keyVault": "", "sqlServer": "", "sqlDatabase": "" },
  "entra": { "tenantId": "<production dashboard entra tenant id>", "clientId": "<production dashboard entra client id>", "audience": "<production api://... or client id>" },
  "gateway": { "legacyVerificationTargets": [] },
  "automation": { "excludedUsers": "<production semicolon separated user guids>" },
  "dashboard": { "simulationEnabled": false, "simulationQueuePrefix": "" }
}
```

(The comments are for this document only. JSON files carry none.)

`subscriptionId` is not reset. `d365fsc-integration-prod` is in the same subscription as `d365fsc-integration-nonprod`, so production inherits the base value. The stamps are kept apart by resource group, names, service connection and secrets, not by subscription.

`deploy.base.json` gains `"production": false` and `"dashboard": { "simulationEnabled": true, ... }`, so both keys are visible in one place.

What carries the isolation is a set of tests that fail when a base value leaks into a production configuration (`DeploymentConfigTests`, section 7), not reviewer attention:

- Every production configuration's derived and overridden names are disjoint from every non-production configuration's names.
- `names.*` resolve to the pattern names, except `names.resourceGroup`, which is `d365fsc-integration-prod`.
- There is no `legacy` section.
- `gateway.legacyVerificationTargets` is empty.
- `dashboard.simulationEnabled` is `false`.
- `entra.clientId`, `entra.audience` and `automation.excludedUsers` are not equal to the base values, unless both are placeholders.

`entra.tenantId` may equal the base value, because both stamps can live in one tenant.

Alternatives:
- **B. Move every non-production value from the base into `deploy.nonprod.json`, `deploy.bat.json` and `deploy.test.json`.** Rejected:
  - It duplicates the names block in three files.
  - It churns the in-flight #5246 files.
  - It loses "a value the same for every configuration is defined once in the base" (deployment-pipeline, "Environment configuration is layered on a shared base").
- **C. A stamp layer, `deploy.stamp.<sharedSuffix>.json`, between the base and the configuration.** This is the cleanest long-term model. Deferred, because it changes `Get-DeployConfig`, `Get-ConfigurationNames` filtering, packaging (`New-DeploymentPackage.ps1` copies only base plus the named files) and the self-test, all on top of an unarchived change. The resets plus tests give the same safety for two files. If a third stamp appears, do option C then.

### 2. The shared configuration is resolved from `sharedSuffix`

New pure function `Get-SharedConfigurationName($Config, [string]$Directory)`. It returns the single configuration with `scope = shared` and the same `sharedSuffix`. Every other configuration with that suffix belongs to the same stamp.

It is used in:

- `Resolve-Components` (the "owned by the shared configuration '…'" message)
- the environment-scope example list ("owned by the environment configurations (e.g. bat, test)"), which becomes the actual environment configurations of the stamp
- `Get-InfraPlan`'s `require` message

Preflight fails if a configuration's stamp has zero shared configurations, or more than one. Main resolves this name once, after `Get-DeployConfig`, and passes it to `Resolve-Components`.

Alternative: name the shared configuration `prd` and the environment `prdenv`, or similar. Rejected, because the user's naming is `prdshared` plus `prd`, and the Automation `DefaultEnvironment` is the configuration name, so the environment configuration must be `prd`.

### 3. Foundations by runbook; preflight proves they exist and that the secrets point at them

The script still does not create the resource group, Service Bus namespace, SQL server or database. The resource group already exists (`d365fsc-integration-prod`). The other three are stateful, rarely created, and need decisions the script cannot make (SQL admin model, backup and retention, SKU). `docs/deployment/production-go-live.md` gives the exact `az` commands, with the names written out, to create them inside `d365fsc-integration-prod`. The database is created with the same collation as `eimdb` (case-insensitive, `SQL_Latin1_General_CP1_CI_AS`), because route lookups rely on case-insensitive comparison.

To catch typos and wrong secrets, preflight gains three checks that apply to every configuration:

1. **Foundations exist.** In addition to the resource group (existing check), the Service Bus namespace `names.serviceBusNamespace`, the SQL server `names.sqlServer` and the database `names.sqlDatabase` must exist in the resource group. These are read-only `Test-Az` calls (`servicebus namespace show`, `sql server show`, `sql db show`). A missing one is reported by name.
2. **Secrets point at the configured resources.** This runs whenever a secret is required (that is, when `Get-RequiredSecrets` is not empty).
   - Data Source and Initial Catalog: from `EIM_DEPLOY_EIMDB`, the server must equal `names.sqlServer` and `Initial Catalog` (or `Database`) must equal `names.sqlDatabase`.
   - Endpoint: in `EIM_DEPLOY_SERVICEBUS`, the host `sb://<namespace>.servicebus.windows.net` must equal `names.serviceBusNamespace`.
   - Messages name the variable, the parsed server, catalog or namespace (resource names, not secrets) and the expected name, and never the connection string. This reuses `Get-SqlServerFromConnection` and adds `Get-SqlCatalogFromConnection` and `Get-ServiceBusNamespaceFromConnection` as pure functions.
   - The same check catches the known non-production trap of a connection string pointing at `eimdb_dev`.
3. **Stamp isolation.** No name in use by a configuration with `production = true` may also be in use by a configuration with `production = false`. The check uses `Get-NamesInUse` (derived and overridden) across all configurations. It is pure and needs no Azure call.

Failure behaviour: all three add to the existing preflight `problems` list. Preflight fails closed and makes no change ("Pre-flight check failed; nothing was changed"). Under `-WhatIf` it still runs them, because they are read-only.

Alternatives:
- **An opt-in `foundation` component that creates the four resources.** Rejected for now. Creating a SQL server needs an admin decision, and a typo would create a wrong resource rather than fail. The check-only path fails instead.
- **Committing the production names as overrides.** Rejected for everything except the resource group, which already exists under a non-pattern name. The other pattern names are already right, and an override is where typos come from. A typo in the resource-group override fails the existing resource-group check in preflight.

### 4. Production resources are never removed by the script

`Test-RemoveAllowed` gains a `-Production` switch. When it is set, `remove` refuses before any Azure call: "remove never runs against a production configuration". `Invoke-RemoveComponent` passes `$Config.production -eq $true`. The refusal comes first, so even `remove -WhatIf` refuses for production.

Shared-scope `cutover` with a blank `legacy.gatewayApp` refuses with "'<name>' has no legacy gateway; there are no keys to copy". Today it fails with "The legacy gateway '' does not exist". Environment-scope `rollback` with a blank `legacy.automationApp` refuses with "'<name>' has no legacy Automation app to roll back to; disable triggers with the runbook instead".

Environment-scope `cutover` for `prd` keeps its current no-legacy behaviour: it enables every gated trigger except `automation.keepDisabled`. That is the production go-live switch.

### 5. Simulation switch: off unless explicitly on, enforced by the API

API (`ARM.Integration.Dashboard.API`):

- **Configuration value.** `Simulation:Enabled`. `SimulationFeature.IsEnabled(IConfiguration)` returns `true` only when the value parses as boolean `true`. Absent, empty, `false` and unparseable all mean off.
  - `appsettings.json`: `"Enabled": false`.
  - `appsettings.Development.json`: `"Simulation": { "Enabled": true }`, so local development keeps the console.
- **Controller removed.** `SimulationControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>` removes `SimulationController` from the controller feature when simulation is off. Every `/api/simulation/*` route then returns 404. It is registered with `AddControllers().ConfigureApplicationPartManager(...)`.
- **Services not registered.** `SimulationService` and `SalesOrderExcelParser` are registered only when simulation is on.
- **Second guard.** `SimulationService` keeps its own check: `RunAsync` yields a single error event ("Simulation is disabled in this deployment") and creates no Service Bus client when `Simulation:Enabled` is not `true`, even if a connection string is present.
- **Identity.** `MeController` takes `IConfiguration` and adds `features = { simulation = SimulationFeature.IsEnabled(config) }` to `GET /api/me`.

Frontend (`ARM.Integration.Dashboard.Frontend/src`):

- `auth/useMe.ts`: `Me` gains `features: { simulation: boolean }`, and there is a `useFeature("simulation")` helper. A missing `features` (an older API) means `false`.
- `components/AppShell.tsx`: the Simulation navigation entry is shown only when the feature is on.
- `router.tsx`: the `simulation` route renders `<SimulationPage />` when on, and a "Simulation is not available in this environment" panel when off. A deep link therefore does not show a broken page.
- `features/dashboard/DashboardPage.tsx`: `useSimulationConfig` is enabled only when the feature is on. Otherwise the Service Bus card shows "Simulation off" and does not call `/api/simulation/config`.

Script (`Get-DashboardApiSettings`):

- It always writes `Simulation__Enabled` (`true` or `false`) from `dashboard.simulationEnabled`.
- When enabled, it also writes `Simulation__ServiceBusConnectionString` and the four queue settings. It throws if `simulationQueuePrefix` is blank or a placeholder.
- When disabled, it writes none of them. `settings` deletes any `Simulation__*` setting other than `Simulation__Enabled` that is present on the dashboard API (`Get-StaleSimulationSettings`), using the same pattern as `Get-StaleVerificationTargetSettings`.
- The preflight drift check expects `Simulation__Enabled`.

Fail-safe direction. Every failure mode leaves simulation off:

- a missing setting
- an old frontend against a new API (the routes are gone)
- a new frontend against an old API (`features` is missing, so it is treated as off)

The non-production consequence is that new API code without the new setting has simulation off. The drift check already turns that into a preflight failure on code-only runs, so the first non-production rollout is a manual run with `applySettings` and `deployCode` (Migration Plan, step 1).

Alternatives:
- **Frontend-only hiding (a build-time `VITE_` flag).** Rejected. The requirement is that the dashboard must not be able to publish. Only the API can enforce that.
- **Leave `Simulation__ServiceBusConnectionString` blank on production.** `SimulationService` already refuses to run without one. Rejected as the only guard, for three reasons:
  - The parse endpoint stays exposed.
  - The setting is implicit.
  - Anyone who later fills in the connection string turns simulation on.
- **An authorization policy (Admin only).** Rejected. It restricts who can simulate; it does not prevent simulation.

### 6. Production pipeline: separate `pipelines/eim-prd.yml` that promotes an `eim` package

```yaml
trigger: none
pr: none
resources:
  pipelines:
    - pipeline: eim          # alias
      source: eim            # the eim pipeline definition
      branch: main           # only packages built from main
      trigger: none
parameters: deployInfra, applySettings, runMigrations (false), deployCode (true)   # same as eim.yml
lockBehavior: runLatest
stages:
  - Deploy_prdshared: deploy-environment.yml, environment prdshared, devopsEnvironment eim-wtc-prdshared,
      serviceConnection sc-eim-wtc-prd, variableGroup eim-wtc-prd, packageSource eim,
      runMigrations: ${{ parameters.runMigrations }}, components gateway, backend, frontend (when deployCode)
  - Deploy_prd (dependsOn Deploy_prdshared): environment prd, devopsEnvironment eim-wtc-prd,
      same connection, group and source, runMigrations false, components automation
```

`pipelines/templates/deploy-environment.yml` gains a `packageSource` parameter (default `current`):

- With `current`, the step stays `download: current` and the package is at `$(Pipeline.Workspace)/package`.
- With a pipeline-resource alias, it uses `download: <alias>`, artifact `package`, and the package is at `$(Pipeline.Workspace)/<alias>/package`.

Every `Deploy-Wilson.ps1` path in the template uses one template expression for the package root. The defaults for `serviceConnection` and `variableGroup` stay non-production. `eim-prd.yml` always passes the production values explicitly, and a test asserts it does.

`pipelines/eim.yml`'s `Build` stage packages `nonprod,bat,test,prdshared,prd`. `Deploy-Wilson.ps1` in a package reads only the packaged configuration files, so the production run needs them in the `eim` artifact.

The SPA for `prdshared` is built only when its Entra values are committed. Until then:

- `New-DeploymentPackage.ps1` warns and skips it, which is existing behaviour.
- A production `frontend` fails preflight on the missing artifact, which fails closed.

Approvals: `eim-wtc-prdshared` and `eim-wtc-prd` both require production approvers and hold an exclusive lock. The `Deploy_prdshared` approval gates production migrations, which satisfies "Production migration is gated".

Which package is promoted: on a manual run, the operator picks the `eim` run under **Resources**. The runbook requires a successful `eim` run from `main` whose `Deploy_test` stage succeeded. Preflight prints the package version from the manifest, so the approver sees it.

Alternatives:
- **Production stages appended to `eim.yml`.** Rejected:
  - Every merge would queue two more pending approvals, since `lockBehavior: runLatest` only replaces stale waits.
  - The non-production service connection and variable group would sit next to the production ones in one definition.
  - A production deploy would be tied to a non-production run's lifetime.
- **`eim-prd.yml` builds its own package.** Rejected. It deploys a build that never ran on bat and test.

### 7. Database: migration `015_seed_prd_routes.sql`

This migration inserts the ten `PRD` routes with the same insert-only `MERGE` shape as 006 and 014:

- the nine routes from 006, `ENABLED = 1`, `ONPAUSE = 'Hold'`
- the delete route `D365FSC` / `mserp_wtc_custpostaladdressdeletedv2entity` / `CustomerAddressDelete_To_Capture`, with `ENABLED = 0` and `ONPAUSE = 'Hold'`. Production deletes are held, not dead-lettered as `NoRoute`, until the Capture team confirms the delete contract (the same reason BAT and TEST are paused).

The parity check is filtered to `ENVIRONMENT = N'PRD'`. It always requires the 10 rows and every type in the registered set. The seeded state (enabled, Hold, type) is checked only for rows still carrying `MODIFIEDBY = 'Migrations.015_seed_prd_routes.sql'`, so a re-run after an operator change succeeds. It uses `THROW 50015`. It never counts the whole table, because 006 already did that and the non-production table has more rows.

Compatibility:

- **Non-production `eimdb`.** Adds 10 rows for an ENVIRONMENT value no non-production message carries. The previous build ignores them. It is expand-only and safe to apply before or after the code.
- **New production database.** 001 to 014 run first (with `002a` creating the trigger `003` needs, see 7a; 014's parity is environment-filtered). Then 015 adds `PRD`. All orchestration types are already registered in the deployed build, so `RouteRegistryValidator` passes.
- **Re-run.** `WHEN NOT MATCHED` only, so it is idempotent. Operator changes to `PRD` rows are never overwritten.

`ENVIRONMENTTABLE` rows for `PRD` (`D365FSC/PRD` for Automation, `D365FSC-GW/PRD` for the gateway) hold D365 credentials. They are entered through the dashboard's Environments page by runbook, never by migration or commit.

Alternative: a runbook SQL script for the `PRD` routes. Rejected. Migrations are the reviewed, journaled path, and a missed route silently dead-letters traffic.

The migration review checklist in `docs/deployment/database-migrations.md` gains "a per-environment seed lists `PRD` as well as `BAT`, `DEV`, `DEV2`, `TEST`" and "the migration is safe on both the non-production and production databases".

#### 7a. A new database migrates from 001 with no manual step: `002a_create_modifieddatetime_trigger.sql`

Found during implementation (task 6.16). `003_backfill_truncated_sourceentity.sql` runs `ALTER TABLE dbo.MESSAGEQUEUETABLE DISABLE TRIGGER [MODIFIEDDATETIME]`. That trigger is created only by the repeatable `R__TR_MESSAGEQUEUETABLE_MODIFIEDDATETIME.sql`, and repeatables run after every versioned script. `eimdb` already had the trigger when `003` ran, so the defect never showed. On an empty `sqldb-eim-wtc-prd`, `003` fails and the production migration stops there.

`003` is applied and is never edited. The fix (approved by the user) is a new versioned script inserted between `002` and `003`:

- `002a_create_modifieddatetime_trigger.sql` creates `dbo.MODIFIEDDATETIME` on `dbo.MESSAGEQUEUETABLE` only when `OBJECT_ID(N'dbo.MODIFIEDDATETIME', N'TR')` is null, through `EXEC(N'CREATE TRIGGER ...')`, with the repeatable's body.
- **Order.** DbUp (dbup-core 6.1.1) sorts scripts with `ScriptNameComparer` over `StringComparer.Ordinal`, so `Migrations.002_…` < `Migrations.002a_…` < `Migrations.003_…`. The DbMigrator csproj embeds `..\script\SQL\Migrations\*.sql`, so the file needs no project change.
- **Existing `eimdb`.** The trigger exists, so `002a` is a no-op there and is only journaled. DbUp applies an unjournaled script whatever its position, so `002a` is simply the one pending script on the next `runMigrations`.
- **Ownership.** The repeatable keeps owning the definition and re-applies it with `CREATE OR ALTER` after every run.
- **Naming.** `002a` is the only allowed exception to the next-number rule, and `database-migrations.md` says so. It is safe only because it is a no-op wherever `003` has already run. No test enforces `NNN_` names, so no test changes.

Alternatives:
- **Edit `003` to skip the `DISABLE TRIGGER` when the trigger is missing.** Rejected. It edits an applied migration, which the repository forbids: `eimdb` would never run the edited version, and the two databases would carry different `003` texts under one journal name.
- **A manual step in the production runbook (create the trigger before the first migration).** Rejected. It is easy to miss, it is not journaled, and it makes "every migration runs unchanged on every database" depend on an operator.
- **Use the next number (`016`).** Impossible: it would run after `003`, which is the script that fails.

### 8. Secrets, identity and external systems

- **Variable group `eim-wtc-prd`.** It holds `EIM_DEPLOY_EIMDB` (secret, `Initial Catalog=sqldb-eim-wtc-prd`), `EIM_DEPLOY_SERVICEBUS` (secret, the `sbns-eim-wtc-prd` connection string) and optionally `EIM_DEPLOY_CONFIG_OVERRIDES`. Pipeline permissions are restricted to `eim-prd`.
- **Service connection `sc-eim-wtc-prd`.** Workload identity federation, Contributor on `d365fsc-integration-prod` only, and authorised for `eim-prd` only.
- **Production Entra app registration.** One registration for the SPA and API (same model as non-production). The SPA redirect URI is the `swa-eim-dashboard-wtc-prd` host, and the API exposes `user_impersonation`. The values are committed in `deploy.prdshared.json`. They are not secret, and the Build stage needs them to build the SPA.
- **External repointing (runbook).**
  - D365 production business events go to `prd-d365fsc-businessevents` and `prd-d365fsc-dataevent`.
  - Capture production goes to `prd-capture-to-d365fsc` and reads `prd-d365fsc-to-capture` and `prd-capture-to-d365fsc-response`.
  - Gateway consumers get new host keys on `azf-eim-gateway-wtc-prd` through the dashboard's **Set key**. There is no key copy from non-production.

### 9. Alerts and hygiene (runbook only)

Alerts are created by hand in `d365fsc-integration-prod`:

- `asp-eim-wtc-prd` CPU or memory above 80%
- Function host availability for both Function Apps
- dead-letter message count above 0 on each `prd-*` queue
- dashboard API HTTP 5xx

The runbook also covers the production database after the first migration:

- Review the `USERTABLE` developer admin seeded by 001. Add the production admins and deactivate the seeded row if it is not wanted.
- Leave the non-production `ENVIRONMENTTABLE`, `SimulationEnvironment` and route rows in place. They are inert on production, because no production message carries those ENVIRONMENT values and simulation is off.

## Failure behaviour

This change does not touch message processing. Terminal and transient classification, retries, idempotency and ordering are unchanged, and `DefaultFailureClassifier` is not affected. No new exception type enters the engine.

Deployment and runtime failure modes:

| Failure | Behaviour |
|---|---|
| Production placeholder unfilled | Preflight fails naming the path. Nothing changes. |
| Foundation resource missing or misspelled | Preflight fails naming the resource. Nothing changes. |
| Non-production secret in the production group, or the reverse | Preflight fails naming the variable and the mismatched server, catalog or namespace. Nothing changes. |
| Production name equal to a non-production name | Preflight fails naming both configurations and the name. |
| Two shared configurations with one suffix | Preflight fails naming them. |
| `remove` against production | Refused before any Azure call. |
| `Simulation__Enabled` absent on an API | Simulation off: 404 on `/api/simulation/*`, and the navigation entry is hidden. |
| `simulationEnabled` true with a blank prefix | `settings` throws before writing anything. |
| Code-only run on non-production before the rollout run | Preflight drift fails naming `Simulation__Enabled` on the dashboard API. The fix is a manual run with `applySettings`. |
| `prdshared` SPA not built (Entra placeholders) | `frontend` fails preflight on the missing artifact. |
| `Deploy_prdshared` rejected or failed | `Deploy_prd` does not start. |
| 015 parity mismatch | The migration throws 50015, and DbUp does not journal it. |

## Risks / Trade-offs

- **[Risk] A future base value leaks into production.** For example, someone adds a non-production value to `deploy.base.json` that production must not use.
  - Mitigation: the isolation tests cover today's known keys.
  - Mitigation: a design note in `deployment.md` says every new base key must be judged for production.
  - Option C (a stamp layer) remains the structural fix.
- **[Risk] Simulation turns off on non-production if new API code reaches it without the setting.**
  - Mitigation: the preflight drift check blocks code-only runs until `applySettings` has run.
  - Mitigation: the rollout step is explicit.
  - Even when it happens, the result is the safe direction.
- **[Risk] Operators promote an `eim` run that never reached `test`.** Mitigation: the runbook rule, and preflight printing the package version. A pipeline check that the source run's `Deploy_test` succeeded is not automated (see open question 5).
- **[Risk] `azf-eim-automation-wtc-prdshared` (derived but unused for the shared scope) is exactly 32 characters.** The 32-character check passes. Any longer shared configuration name would fail `Get-ResourceNames` for every run, because preflight loads all configurations.
  - Mitigation: a self-test asserts the `prdshared` names resolve.
- **[Trade-off] Non-production rows exist in the production database** (from 001, 002 and 006). They are inert, and removing them would need a migration that branches on database identity. Accepted, and reviewed in the runbook.
- **[Trade-off] Committing production Entra IDs in the repository.** They are identifiers, not secrets, and this is the same model as non-production.
- **[Risk] Archive order.** Several MODIFIED requirements here modify requirements that `align-structure-and-deployment` adds. Archiving this change first fails. Mitigation: proposal and tasks both state the order.

## Migration Plan

1. **Non-production first.** Merge this change, then run `eim` manually with `applySettings` and `deployCode`.
   - `wtc-dashboard-api-dev` gets `Simulation__Enabled=true` before the new API code starts, so simulation never goes off on non-production.
   - Run `runMigrations` in the same run, or a later one, to apply 015 to `eimdb`. 015 is independent of code order.
2. **Foundations (runbook).** Confirm `d365fsc-integration-prod` exists and the deployer has Contributor on it. Then create, inside it:
   - `sbns-eim-wtc-prd` (Standard)
   - `sql-eim-wtc-prd` with an Entra admin
   - `sqldb-eim-wtc-prd` with collation `SQL_Latin1_General_CP1_CI_AS`
3. **Identity and DevOps (runbook).**
   - Register the production Entra app and commit its values in `deploy.prdshared.json`.
   - Create `sc-eim-wtc-prd`, `eim-wtc-prd` (variable group), and the environments `eim-wtc-prdshared` and `eim-wtc-prd` with approvals.
   - Create the `eim-prd` pipeline from `/pipelines/eim-prd.yml`.
4. **First production run.** Use `eim-prd` with `deployCode` cleared (preflight only, and permit the resources). Then run with `deployInfra`, `applySettings`, `runMigrations` and `deployCode`. Automation triggers start disabled.
5. **Data (runbook).**
   - Enter the `PRD` `ENVIRONMENTTABLE` profiles through the dashboard.
   - Review `USERTABLE`.
   - Confirm the 10 `PRD` routes.
   - Onboard gateway consumers with new keys.
6. **Go-live.** Repoint D365 business events and Capture to the `prd-*` queues, then run `Deploy-Wilson.ps1 -Environment prd -Component cutover` from an operator machine to enable the triggers. Set up the alerts.

**Rollback.**

- Before go-live, delete nothing: disable the Automation triggers and repoint the external systems back.
- After go-live, code rollback is "Rerun stage" on an older `eim-prd` run.
- The schema only moves forward.
- On non-production, setting `dashboard.simulationEnabled` is the only behavioural change, and it can be flipped back with a settings run.

## Open Questions

None.

Resolved (2026-10-23):

1. **Ticket.** #5246, on the existing branch `feature/5246/align-structure-and-deployment`.
2. **Resource group.** Use the existing `d365fsc-integration-prod`; never create one.
3. **`PRD` delete route at go-live.** Seeded disabled with Hold, as designed. It is enabled in the Routes console when Capture confirms the delete contract.
4. **Seeded developer admin in the production `USERTABLE`.** The migration stays as is. The runbook adds the production admins and deactivates the seeded row if it is not wanted.
5. **Seeded non-production rows on production** (`SimulationEnvironment`, the sample `ENVIRONMENTTABLE` row, the BAT/DEV/DEV2/TEST routes), and the `PRD` routes that 015 adds to the non-production `eimdb`. Left in place on both databases: they are inert, and every migration stays identical on every database.
6. **Promote versus rebuild.** Promote, as designed. A pipeline check that the source `eim` run's `Deploy_test` succeeded may come later.
7. **Production subscription.** The same subscription as non-production (where `d365fsc-integration-prod` already is). `subscriptionId` is inherited from the base.
8. **Key Vault.** Not created. `kv-eim-wtc-prd` is derived but nothing reads it.
9. **SQL authentication.** The same model as non-production: the deploy and app connection strings are secret variables in `eim-wtc-prd`, using a contained SQL user. `sql-eim-wtc-prd` also gets an Entra admin. The runbook records the choice.
