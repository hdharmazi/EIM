## Why

Ticket: **#5246** (branch `feature/5246/align-structure-and-deployment`).

The deployment tooling from `align-structure-and-deployment` and `add-production-environment` is built around "stamps". A stamp has a shared configuration (`nonprod`, `prdshared`) that owns the gateway, dashboard, plan and migrations, a `sharedSuffix`, and a scope per configuration. On top of that sits a "legacy" concept: cutover, rollback, remove, key copy and `legacy` sections.

The user decided on 2026-10-25 to drop both concepts before the first deployment with the new tooling. Nothing new exists in Azure yet.

- Each `deploy.<env>.json` describes one environment.
- An environment may borrow another environment's resources with `uses`.
- SQL access moves to Entra ID: apps use managed identities, and no SQL password exists anywhere.
- Old resources are a one-time manual clean-up, and the script deletes no Azure resource.

This change replaces the superseded `remove-legacy-cutover` change and folds in its whole scope.

## What Changes

- **BREAKING (configuration model):**
  - Remove the `scope` and `sharedSuffix` keys, the stamp concept and the shared configurations. Delete `deploy.nonprod.json` and `deploy.prdshared.json`.
  - The configurations become `bat`, `test` and `prd`.
  - A configuration without `uses` owns every component.
  - A configuration with `"uses": "<env>"` takes the target's resource group, Service Bus namespace, Key Vault, SQL server and database, plan, Log Analytics, gateway and dashboard. It deploys only its own queues, its Automation app and that app's settings and database user.
  - `test` uses `bat`.
- **BREAKING (naming):**
  - Every derived name uses the environment as its suffix: `{type}-{workload}-{component}-{client}-{env}`, and storage `st{client}{workload}{short}{env}`.
  - The resource group is derived too, as `rg-{workload}-{client}-{env}`.
  - Any name can be overridden under `names`. The non-production overrides move from `deploy.base.json` to `deploy.bat.json`. They cover the resource group, Service Bus namespace, Key Vault, SQL server and database, and the dashboard API and static web app.
  - The plan, Log Analytics workspace and gateway for `bat` are derived (`asp-eim-wtc-bat`, `log-eim-wtc-bat`, `azf-eim-gateway-wtc-bat`).
- **Create if missing, otherwise use as found:**
  - Owner configurations create every missing resource: resource group, Service Bus namespace and queues, Key Vault, SQL server and database, plan, Log Analytics, gateway, dashboard API and static web app, and the Automation app.
  - An existing resource that the script did not create is used as found: its SKU and configuration do not change.
  - The only additive exceptions are system-assigned identities on the apps the script deploys, and Key Vault `get` access policies for those identities.
- **BREAKING (SQL authentication):**
  - A new `sql` component creates a missing SQL server with an Entra admin from configuration, Entra-only authentication and TLS 1.2. It creates a missing database with the configured SKU and backup redundancy, and manages firewall rules on servers it created. This mirrors Wastequip.
  - A new `sqlusers` component creates a contained database user, `WITH SID ... TYPE = E`, for each app identity the configuration deploys, with db_datareader, db_datawriter and EXECUTE.
  - The apps receive managed-identity connection strings.
  - The migrator authenticates as the deployer's Entra identity.
  - `EIM_DEPLOY_EIMDB` is removed.
  - `ARM.Integration.Core` gains `Microsoft.Data.SqlClient.Extensions.Azure`, which SqlClient 7 needs for Entra connection strings.
- **BREAKING (Service Bus secret):**
  - `settings` stores the namespace connection string in the Key Vault as `eim-servicebus`.
  - The apps reference it through `@Microsoft.KeyVault(...)`.
  - `EIM_DEPLOY_SERVICEBUS` is removed, so the variable groups hold no secret.
- **BREAKING (pipelines):**
  - `eim.yml` runs Build, then Deploy_bat, then Deploy_test.
  - `eim-prd.yml` has one stage, Deploy_prd, that promotes the package of an `eim` run on `main`.
  - A new manual parameter, `grantDatabaseAccess` (default off), runs `sqlusers` behind the stage approval.
  - The DevOps environments `eim-wtc-nonprod` and `eim-wtc-prdshared` are retired.
- **Production:**
  - `deploy.prd.json` folds in `prdshared`.
  - Preflight fails when `prd` uses a non-production configuration, or when any name resolved for `prd` equals a name resolved for a non-production configuration.
  - The non-production identifying keys are still reset in `prd`.
- **Folded in from `remove-legacy-cutover`:**
  - **BREAKING:** remove the `cutover`, `rollback` and `remove` components, `-IncludeDefaultKeys`, and every `legacy` key and verification target.
  - Add `automation.triggersEnabled`: `false` in the base, `true` in `bat` and `test`, and explicitly `false` in `prd`.
  - `settings` writes every `AzureWebJobs.<fn>.Disabled` on every run. `keepDisabled` stays.
  - Preflight validates the trigger configuration, and the code-only drift check warns on a differing trigger value.
  - Delete `docs/deployment/resource-rename-cutover.md` and every link to it.
- Delete the `openspec/changes/remove-legacy-cutover/` folder (never committed).

## Capabilities

### New Capabilities

- `sql-identity-access`: the SQL server and database are created with an Entra admin, the apps connect with managed identities through contained users, and the migrator uses the deployer's Entra identity. Also covers how the existing `azql-wtc-sandbox`/`eimdb` is handled.

### Modified Capabilities

- `deployment-automation`:
  - per-environment configurations with `uses` replace scopes and stamps
  - environment-suffixed naming
  - create-if-missing and use-as-found rules
  - the `sql` and `sqlusers` components
  - the Key Vault-held Service Bus secret
  - no secret variables
  - no on-demand components
  - the script deletes no Azure resource
- `deployment-pipeline`:
  - stages Build, Deploy_bat, Deploy_test
  - the `grantDatabaseAccess` parameter
  - no secret variables
  - the trigger checks and trigger drift warning in preflight
  - one frontend per dashboard owner
- `resource-naming-cutover`:
  - remove the cutover, rollback, key-copy, remove and legacy requirements
  - the trigger state follows configuration
  - `bat` names and the dashboard-in-place rule
- `production-stamp`:
  - one `prd` configuration with an isolation check
  - a single-stage `eim-prd`
  - go-live by configuration
  - the "never removed" requirement is removed
- `schema-migration`:
  - migrations run from the owner stages (`Deploy_bat`, `Deploy_prd`)
  - the migrator authenticates with Entra
  - no `EIM_DEPLOY_EIMDB`

## Impact

- **Scripts:**
  - `script/deployment/Deploy-Wilson.ps1`: configuration model, names, `uses`, infrastructure plan, `sql`, `sqlusers`, Key Vault, settings, triggers, legacy removal.
  - `script/deployment/New-DeploymentPackage.ps1`: frontends per dashboard owner, package README.
  - `script/deployment/Test-DeployWilson.ps1`.
- **Config:**
  - Edit `script/deployment/config/deploy.{base,bat,test,prd}.json`.
  - Delete `deploy.nonprod.json` and `deploy.prdshared.json`.
- **Pipelines:** `pipelines/eim.yml`, `pipelines/eim-prd.yml`, `pipelines/templates/deploy-environment.yml`, `pipelines/README.md`.
- **Product code:** `ARM.Integration.Core/ARM.Integration.Core.csproj` gains one package reference. No C# logic changes. `DefaultFailureClassifier` is unchanged, because no new exception type is introduced.
- **Tests:**
  - `Wilson.Integration.Tests`: `DeploymentConfigTests`, `PipelineDefinitionTests`, `ProductionPipelineDefinitionTests`, `RepositoryLayoutTests`, plus a new SQL authentication provider test.
  - `Test-DeployWilson.ps1`.
- **Docs:**
  - Edit `docs/deployment/*.md`, `README.md`, `docs/README.md`, `script/README.md`, `CLAUDE.md` (Deployment bullet), `openspec/config.yaml` (context line) and `ARM.Integration.Wilson.slnx`.
  - Delete `docs/deployment/resource-rename-cutover.md`.
- **Azure (manual, user, previewed with `-WhatIf`):**
  - Set an Entra admin on `azql-wtc-sandbox`. SQL authentication stays enabled.
  - Add the service connection principals to the SQL admin group.
  - Run the first `sqlusers` against `eimdb`.
  - Stop the old Automation apps before the first deployment.
  - Remove `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` from the variable groups.
  - Issue new gateway keys.
  - Move the dashboard API to `asp-eim-wtc-bat`.
  - Delete the old resources and pipeline definitions by hand.
- **Other changes:** this change's deltas assume `align-structure-and-deployment` and then `add-production-environment` are archived first. `fix-address-source-business-key` is unaffected.

## Non-goals

- Renaming or recreating any existing resource: `d365fsc-integration-nonprod`, `asb-eim-wtc-nonprod` and its queues, `kv-d365fsc-wtc-nonprod`, `azql-wtc-sandbox`/`eimdb`, `wtc-dashboard-api-dev`, `wtc-dashboard-frontend-dev`, `d365fsc-integration-prod`.
- Changing the SKU, tier, firewall or authentication mode of any existing resource the script did not create. On `azql-wtc-sandbox`, Entra-only authentication is not enabled; SQL authentication stays for the old apps and tools until they are deleted.
- Service Bus or Key Vault access by managed identity through RBAC data roles. The service connection is Contributor and cannot assign roles.
- Script-driven deletion of any Azure resource, database user, queue or row.
- Deleting or draining the `dev-*` queues or the `DEV`/`DEV2` rows.
- Changing the engine sweeps, which stay global across ENVIRONMENT values.
- Removing unrelated uses of "legacy" or "rollback":
  - `EnvironmentStore.cs`, `D365FSCServiceCollectionExtensions.cs`, `SysMessageBusinessEventsContract.cs`, `JsonValueEditor.tsx`, `scenarios.ts`
  - `MessageQueueRepository.RollbackAsync`, `RetryScheduleCalculatorTests`
  - the schema rollback text in `database-migrations.md`
  - the `async-orchestration-result`, `gateway-consumer-console` and `d365-business-event-reconcile` specs
- Removing the dashboard API's `Gateway:Applications` verification feature or `automation.retiredSettings`.
- Local development: developers keep a local SQL Server `eimdb` with SQL or Windows authentication.
- Any SQL migration. Applied migrations 001 to 015 are not edited.
- Editing the `align-structure-and-deployment` or `add-production-environment` change folders.
