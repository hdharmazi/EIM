Ticket: `#5246`. Use the existing branch `feature/5246/align-structure-and-deployment`.

Depends on `align-structure-and-deployment` (same ticket, same branch):

- Implement this change on top of the `align-structure-and-deployment` work already on the branch.
- Archive `align-structure-and-deployment` first, then this change. Several MODIFIED requirements here modify requirements that change adds.

Sections 1 to 6 are Luigi's. Section 7 is for the main session. Section 8 is manual and is not Luigi's.

## 1. Implementation: configuration

- [x] 1.1 Add `"production": false` and, under `dashboard`, `"simulationEnabled": true` to `script/deployment/config/deploy.base.json`. Keep `"simulationQueuePrefix": "bat"`.
  - Proven by tests 5.1 and 5.2.
- [x] 1.2 Add `script/deployment/config/deploy.prdshared.json` exactly as shown in design decision 1: `environment` `prdshared`, `scope` `shared`, and `production` `true`.
  - `sharedSuffix` is `prd`.
  - These are `<production ...>` placeholders: `entra.tenantId`, `entra.clientId`, `entra.audience` and `automation.excludedUsers`.
  - `subscriptionId` is not set; it is inherited from the base (production is in the same subscription).
  - `names.resourceGroup` is `d365fsc-integration-prod`. Blank `serviceBusNamespace`, `keyVault`, `sqlServer` and `sqlDatabase`.
  - `gateway.legacyVerificationTargets` is `[]`, and `dashboard` is `{ "simulationEnabled": false, "simulationQueuePrefix": "" }`.
  - No `legacy` section, and no secret.
  - Proven by tests 5.1 and 5.2.
- [x] 1.3 Add `script/deployment/config/deploy.prd.json`: the same content as 1.2, with `environment` `prd` and `scope` `environment`.
  - Proven by tests 5.1 and 5.2.

## 2. Implementation: Deploy-Wilson.ps1

- [x] 2.1 Add the pure function `Get-SharedConfigurationName($Config, [string]$Directory)`. It returns the one configuration with scope `shared` and the same `sharedSuffix`, and throws naming the suffix and the matches when there are zero or more than one (design decision 2).
  - Main: resolve it once after `Get-DeployConfig` and pass it to `Resolve-Components`.
  - In `Resolve-Components`, the environment-scope example list becomes the stamp's environment configurations (add a parameter for them) instead of the literal "bat, test".
  - In `Get-InfraPlan`, the `require` message names the shared configuration instead of `$Config.sharedSuffix`.
  - Files: `script/deployment/Deploy-Wilson.ps1`.
  - Proven by test 6.2.
- [x] 2.2 Add the pure functions `Get-SqlCatalogFromConnection` (reads `Initial Catalog=` or `Database=`) and `Get-ServiceBusNamespaceFromConnection` (reads `Endpoint=sb://<ns>.servicebus.windows.net`). Both throw without echoing the input.
  - Add the pure function `Get-SecretTargetProblems($Secrets, $Names, [string[]]$Required)`. It returns one message per mismatch, naming the variable, the parsed name and the expected name.
  - Call it from `Invoke-PreflightComponent` whenever `Get-RequiredSecrets` is not empty and the secrets resolved.
  - Files: `Deploy-Wilson.ps1`.
  - Proven by test 6.3.
- [x] 2.3 Add the pure function `Get-FoundationProblems($Names, [scriptblock]$Exists)`. It checks the resource group, Service Bus namespace, SQL server and database.
  - In `Invoke-PreflightComponent`, replace the lone resource-group check with this function, passing a probe that calls `Test-Az group show`, `servicebus namespace show`, `sql server show` and `sql db show`.
  - Skip the namespace, server and database probes when the resource group is missing, and report only the group.
  - Files: `Deploy-Wilson.ps1`.
  - Proven by test 6.4 (with a stub probe, because the self-test forbids `az`).
- [x] 2.4 Add the pure function `Get-StampIsolationProblems($Configs)`. For every name in `Get-NamesInUse` of a configuration with `production` true that is also in use by a configuration with `production` false, it returns a problem naming both configurations and the name.
  - Call it from `Invoke-PreflightComponent` with every configuration it already loads.
  - Files: `Deploy-Wilson.ps1`.
  - Proven by test 6.5.
- [x] 2.5 `Test-RemoveAllowed` gains `[bool]$Production`. When it is true, the function adds the refusal "remove never runs against a production configuration".
  - `Invoke-RemoveComponent` passes `$Config.production -eq $true` to the early call, so the refusal happens before any Azure call, with or without `-WhatIf`.
  - Files: `Deploy-Wilson.ps1`.
  - Proven by test 6.6.
- [x] 2.6 In `Invoke-CutoverComponent`:
  - Shared scope with a blank `legacy.gatewayApp`: throw "'<environment>' has no legacy gateway; there are no keys to copy. Nothing was changed."
  - Environment-scope `rollback` with a blank `legacy.automationApp`: throw "'<environment>' has no legacy Automation app to roll back to. Nothing was changed."
  - Both checks happen before any Azure call.
  - Files: `Deploy-Wilson.ps1`.
  - Proven by test 6.7 (extract the checks into a pure function `Get-CutoverRefusal($Config, [bool]$Rollback)`).
- [x] 2.7 `Get-DashboardApiSettings` always writes `Simulation__Enabled` (`'true'`/`'false'`) from `dashboard.simulationEnabled`.
  - It writes `Simulation__ServiceBusConnectionString` and the four queue settings only when enabled.
  - When enabled with a blank or placeholder `dashboard.simulationQueuePrefix`, it throws naming the key.
  - Add `Get-StaleSimulationSettings($Settings, $Current)`, which returns `Simulation__*` names present on the app and absent from `$Settings`.
  - Delete those names in the shared-scope `settings` component, alongside the stale `Gateway__Applications__*` deletion.
  - Add `dashboard` to `Get-RequiredConfigPaths` for shared `settings` (already present). Make sure `dashboard.simulationQueuePrefix` `""` is not reported as a placeholder.
  - Files: `Deploy-Wilson.ps1`.
  - Proven by test 6.8.
- [x] 2.8 `New-DeploymentPackage.ps1`: make the README text name the shared configurations from the packaged configurations instead of the literal `nonprod` and `d365fsc-integration-nonprod`, and list `prdshared`/`prd` when packaged. Keep the default `-Environment` as `nonprod, bat, test`.
  - Proven by test 6.9.

## 3. Implementation: pipelines

- [x] 3.1 `pipelines/templates/deploy-environment.yml`: add the parameter `packageSource` (default `current`).
  - Use `download: ${{ parameters.packageSource }}` with artifact `package`.
  - Every `Deploy-Wilson.ps1` path uses `$(Pipeline.Workspace)/package` for `current` and `$(Pipeline.Workspace)/<packageSource>/package` otherwise, through one template expression (for example a job variable `packageRoot`).
  - Proven by test 5.4.
- [x] 3.2 `pipelines/eim.yml`: the `Build` stage's `environments` becomes `nonprod,bat,test,prdshared,prd`. Nothing else changes.
  - Proven by test 5.3.
- [x] 3.3 Add `pipelines/eim-prd.yml` per design decision 6:
  - `trigger: none` and `pr: none`
  - `resources.pipelines` alias `eim`, with source `eim` and `trigger: none`
  - the same four parameters as `eim.yml`
  - `lockBehavior: runLatest`
  - stage `Deploy_prdshared`: `prdshared`, `eim-wtc-prdshared`, runMigrations parameter, `gateway, backend, frontend`
  - stage `Deploy_prd`: `dependsOn: Deploy_prdshared`, `prd`, `eim-wtc-prd`, `runMigrations: false`, `automation`
  - both stages: `serviceConnection: sc-eim-wtc-prd`, `variableGroup: eim-wtc-prd`, `packageSource: eim`, and `condition: succeeded()`
  - Proven by test 5.4.
- [x] 3.4 `pipelines/README.md`: describe `eim-prd.yml`, the package promotion, and the production DevOps resources.
  - Proven by review, and by test 5.4's file-name assertions.

## 4. Implementation: dashboard API, frontend and SQL

- [x] 4.1 Add `ARM.Integration.Dashboard.API/Services/SimulationFeature.cs`, which holds:
  - `public static bool IsEnabled(IConfiguration)`, which is `true` only when `bool.TryParse(config["Simulation:Enabled"])` yields `true`
  - `SimulationControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>`, which removes `SimulationController` when it is disabled

  In `Program.cs`:
  - Register the provider through `AddControllers().ConfigureApplicationPartManager(...)`.
  - Register `SimulationService` and `SalesOrderExcelParser` only when simulation is enabled.

  Proven by tests 6.10 and 6.11.
- [x] 4.2 `ARM.Integration.Dashboard.API/Services/SimulationService.cs`: `RunAsync` first checks `SimulationFeature.IsEnabled`. When it is off, it yields one error event, "Simulation is disabled in this deployment", and returns before creating any Service Bus client.
  - Proven by test 6.12.
- [x] 4.3 `ARM.Integration.Dashboard.API/Controllers/MeController.cs`: inject `IConfiguration` and add `features = new { simulation = SimulationFeature.IsEnabled(config) }` to the response.
  - Proven by test 6.13.
- [x] 4.4 Set `"Enabled": false` in the `Simulation` section of `ARM.Integration.Dashboard.API/appsettings.json`. Add `"Simulation": { "Enabled": true }` to `appsettings.Development.json`, with no other value.
  - Proven by test 6.14.
- [x] 4.5 Frontend:
  - `src/auth/useMe.ts`: `Me.features?: { simulation?: boolean }`, and `useFeature(name)` returns `false` when the value is missing.
  - `src/components/AppShell.tsx`: show the Simulation navigation entry only when `useFeature("simulation")` is true.
  - `src/router.tsx`: the `simulation` route renders `SimulationPage` when the feature is on, and a `SimulationUnavailable` notice (new, `src/features/simulation/SimulationUnavailable.tsx`) when it is off.
  - `src/features/dashboard/DashboardPage.tsx`: run `useSimulationConfig` with `enabled: simulationOn` (add an `enabled` option to `useSimulationConfig` in `src/features/simulation/api.ts`), and show "Simulation off" on the card when it is off.
  - Proven by test 6.15 and the manual check 8.10.
- [x] 4.6 Add `script/SQL/Migrations/015_seed_prd_routes.sql`, following design decision 7 and the shape of 014:
  - an insert-only `MERGE` of ten `PRD` rows, with `MODIFIEDBY` `Migrations.015_seed_prd_routes.sql`
  - nine enabled rows and the delete route disabled, all with `ONPAUSE 'Hold'`
  - a `PRD`-filtered parity check with `THROW 50015` (count = 10 and all types in the registered list always; enabled state, `Hold` and type only for rows still `MODIFIEDBY` 015)
  - a header comment stating that it is safe on both the non-production and production databases, and needs no code ordering

  Do not edit any earlier migration.

  Proven by test 6.16 and the deploy check 8.2.
- [x] 4.7 Docs:
  - `docs/deployment/deployment.md`:
    - Replace the "Production is out of scope" decision with "Production is a separate stamp".
    - Add `prdshared`/`prd` to the configurations table.
    - Add a "Production" section: pipeline, isolation rules, the `production` flag, and simulation off.
    - Document `dashboard.simulationEnabled`.
    - Add the new preflight checks to the components table.
    - Add a note that every new base key must be judged for production.
  - `docs/deployment/deploy-from-scratch.md`: replace lines 11-12 with a pointer to the runbook.
  - `docs/deployment/devops-setup.md`: add the production environments, `sc-eim-wtc-prd`, the `eim-wtc-prd` group and the `eim-prd` pipeline.
  - `docs/deployment/database-migrations.md`: add the two checklist items from design decision 7, and describe the production database.
  - `README.md`: add a deployment overview line for production.
  - New `docs/deployment/production-go-live.md`: the runbook in design "Migration Plan" steps 2 to 6, with the exact `az` commands and pattern names, the alerts from decision 9, and the `USERTABLE` review.
  - Proven by review.

## 5. Tests: configuration and pipelines (Wilson.Integration.Tests)

- [x] 5.1 Update `Wilson.Integration.Tests/DeploymentConfigTests.cs`:
  - `The_expected_configurations_exist` expects `base, bat, nonprod, prd, prdshared, test`.
  - The scope theory maps `nonprod` and `prdshared` to `shared`, and `bat`, `test` and `prd` to `environment`.
  - `DerivedNames()` includes the production names from the `production-stamp` spec.
  - `No_secrets_in_configuration` covers the new files.
- [x] 5.2 Add the production isolation tests to `DeploymentConfigTests.cs`. Each one is a scenario of the `production-stamp` spec:
  - `Production_configurations_are_exactly_prdshared_and_prd` (merged `production` flag)
  - `Production_names_resolve_to_the_pattern` (resource group `d365fsc-integration-prod` is the only `names` override; `sql-eim-wtc-prd`, `sqldb-eim-wtc-prd`, `sbns-eim-wtc-prd` are derived)
  - `Production_and_nonproduction_names_are_disjoint`
  - `Production_does_not_inherit_nonproduction_values` (`names.resourceGroup` differs from the base; `entra.clientId`, `entra.audience` and `automation.excludedUsers` differ from the base unless both are placeholders; `gateway.legacyVerificationTargets` empty)
  - `Production_has_no_legacy_section`
  - `Production_simulation_is_off_and_nonproduction_on`
  - `Production_plan_is_P0v3`
  - `Each_stamp_has_one_shared_configuration`
- [x] 5.3 Update `Wilson.Integration.Tests/PipelineDefinitionTests.cs`:
  - `Build` packages `nonprod,bat,test,prdshared,prd`.
  - `eim.yml` names no `Deploy_prdshared`, `Deploy_prd`, `sc-eim-wtc-prd`, `eim-wtc-prd` or `eim-wtc-prdshared`.
  - The template's defaults stay `sc-eim-wtc-nonprod`/`eim-wtc-nonprod`.
  - The step-order and secret-mapping tests accept the `packageRoot` path.
- [x] 5.4 Add a test class `ProductionPipelineDefinitionTests` (in `PipelineDefinitionTests.cs` or a new file in `Wilson.Integration.Tests`) for `pipelines/eim-prd.yml`:
  - `trigger: none` and `pr: none`
  - the pipeline resource `eim`, with no `Build` stage and no `dotnet` or `npm` step
  - stages exactly `Deploy_prdshared`, `Deploy_prd`, with `Deploy_prd` depending on `Deploy_prdshared`
  - each stage passes `sc-eim-wtc-prd`, `eim-wtc-prd`, `packageSource: eim` and its DevOps environment
  - `runMigrations` is passed through only in `Deploy_prdshared`
  - `lockBehavior: runLatest`
  - no `nonprod` service connection or group

  Also test that the template downloads `${{ parameters.packageSource }}` and that every script path uses the package root.

## 6. Tests: script self-test and dashboard API

- [x] 6.1 `script/deployment/Test-DeployWilson.ps1`:
  - Update the configuration list (line 124) to `bat, nonprod, prd, prdshared, test`, and the dev-rejection message (line 127).
  - Add the `prdshared`/`prd` name assertions (the shared-names block; `azf-eim-automation-wtc-prdshared` resolves at 32 characters).
- [x] 6.2 Self-test `Get-SharedConfigurationName`:
  - `prd` resolves to `prdshared` and `bat` to `nonprod`.
  - A duplicate shared configuration in a temporary directory throws naming both.
  - `Resolve-Components @('gateway') 'environment' 'prdshared'` names `prdshared`.
  - `Resolve-Components @('automation') 'shared' 'prdshared'` names `prd`.
- [x] 6.3 Self-test `Get-SecretTargetProblems` with fake connection strings that contain no real values:
  - wrong server
  - wrong catalog (`eimdb_dev`)
  - wrong namespace
  - all matching (no problems)
  - the messages contain no `Password=`, `SharedAccessKey=` or full connection string
- [x] 6.4 Self-test `Get-FoundationProblems` with a stub probe:
  - all present: no problems
  - each one missing: named
  - resource group missing: only the group is reported
- [x] 6.5 Self-test `Get-StampIsolationProblems`:
  - the real configurations give no problems
  - a copy of `prdshared` with `names.sqlServer = azql-wtc-sandbox` reports that name and both configurations
- [x] 6.6 Self-test `Test-RemoveAllowed -Production $true`:
  - It refuses with the production message even with `-Preview $true`, a marker and the right confirmation.
  - The existing non-production cases are unchanged.
- [x] 6.7 Self-test `Get-CutoverRefusal`:
  - `prdshared` cutover: refused (no legacy gateway).
  - `prd` rollback: refused (no legacy app).
  - `prd` cutover: not refused.
  - `nonprod` cutover and `bat` rollback: not refused.
- [x] 6.8 Self-test the dashboard settings:
  - For `nonprod`, `Get-DashboardApiSettings` includes `Simulation__Enabled=true` and the four `bat-*` queues.
  - For `prdshared` it includes `Simulation__Enabled=false` and no other `Simulation__*` name.
  - Enabled with a blank prefix throws naming `dashboard.simulationQueuePrefix`.
  - `Get-StaleSimulationSettings` returns `Simulation__ServiceBusConnectionString` for `prdshared` and nothing for `nonprod`.
  - The drift check fails when `Simulation__Enabled` is missing.
- [x] 6.9 Self-test `Get-PackageReadme`. With `nonprod, bat, test, prdshared, prd`, the README names `prdshared` and `prd`. Keep the default-environments assertion (line 609).
- [x] 6.10 Add `ARM.Integration.D365FSC.Gateway.Tests/SimulationSwitchTests.cs`. `SimulationFeature.IsEnabled` is a theory over:
  - absent: false
  - `""`: false
  - `false`: false
  - `yes`: false
  - `true` and `True`: true
- [x] 6.11 In `SimulationSwitchTests.cs`, build an `ApplicationPartManager` with the Dashboard API assembly, the default `ControllerFeatureProvider` and `SimulationControllerFeatureProvider`, and populate a `ControllerFeature`:
  - disabled: no `SimulationController`, and `MeController` still present
  - enabled: `SimulationController` present
- [x] 6.12 In `SimulationSwitchTests.cs`, call `SimulationService.RunAsync` with `Simulation:Enabled` absent and a fake connection string set. It yields exactly one error event containing "disabled" and creates no Service Bus client (the connection string is syntactically invalid, so any client creation would throw).
- [x] 6.13 In `SimulationSwitchTests.cs`, call `MeController.Get()` with a test `ClaimsPrincipal`:
  - `features.simulation` is `false` when the setting is absent and `true` when it is `true`
  - `name`, `roles` and `dbUserValidated` are unchanged
- [x] 6.14 In `SimulationSwitchTests.cs`, read `appsettings.json` (`Simulation:Enabled` is `false`) and `appsettings.Development.json` (`true`) from the API project folder.
- [x] 6.15 Run `npm --prefix ARM.Integration.Dashboard.Frontend run lint` (`tsc --noEmit`). It passes with the optional `features` field. There is no frontend unit-test runner, so behaviour is checked in 8.10.
- [x] 6.16 Run the migrations against a local SQL Server twice:
  - Run `ARM.Integration.DbMigrator` against a fresh local `eimdb` (`EIMDB_MIGRATOR_CONNECTION`). All of 001 to 015 (including `002a`) and then the repeatables apply with no manual step, and `SELECT ENVIRONMENT, SOURCEENTITY, ENABLED, ONPAUSE FROM dbo.ROUTETABLE WHERE ENVIRONMENT = 'PRD'` returns the ten expected rows.
  - Re-run the migrator. Nothing applies, and there is no error.
  - Enable the `PRD` delete route as an operator would (`ENABLED = 1`, `MODIFIEDBY = 'operator'`), then execute the body of 015 again. It succeeds and leaves that row unchanged.
  - Record the result in the PR description.
- [x] 6.16a Add `script/SQL/Migrations/002a_create_modifieddatetime_trigger.sql` (design 7a), so that `003` finds the `MODIFIEDDATETIME` trigger on a database created empty:
  - It creates `dbo.MODIFIEDDATETIME` on `dbo.MESSAGEQUEUETABLE` only when `OBJECT_ID(N'dbo.MODIFIEDDATETIME', N'TR')` is null, with the body of `R__TR_MESSAGEQUEUETABLE_MODIFIEDDATETIME.sql`. It is a no-op on `eimdb`.
  - Confirm from `ARM.Integration.DbMigrator` and dbup-core that `Migrations.002a_` sorts between `002_` and `003_`, and that the csproj glob embeds it.
  - Do not edit 001 to 014.
  - `docs/deployment/database-migrations.md`: explain the insert, why it exists, and that it is the only exception to the next-number rule.
  - Proven by 6.16 (no manual trigger step) and the `schema-migration` scenario "A new database migrates from 001 with no manual step".
- [x] 6.17 Final check. Run all of the following:
  - `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`
  - `dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj`
  - `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`
  - `npm --prefix ARM.Integration.Dashboard.Frontend run lint`
  - `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`

  All pass, including `RepositoryLayoutTests` (no dev names, no retired names).

## 7. Main session (not Luigi)

- [x] 7.1 Edit the `CLAUDE.md` deployment bullet. There are now two pipelines: `pipelines/eim.yml` (Build, then Deploy_shared, Deploy_bat, Deploy_test) and `pipelines/eim-prd.yml` (Deploy_prdshared, then Deploy_prd, promoting an `eim` package).
  - Configurations: `deploy.base.json` plus `deploy.<name>.json` for `nonprod`, `bat`, `test`, `prdshared` and `prd`.
  - Add a pitfall: a new key in `deploy.base.json` must be reset in the production files if it identifies non-production. `DeploymentConfigTests` guards the known keys.
  - Add a pitfall: per-environment seed migrations must include `PRD`.
- [x] 7.2 Edit the `openspec/config.yaml` context line "Environments: ...". Make it: "Environments: local (local SQL eimdb, Azurite, dev-* queues), bat, test (non-production stamp, shared nonprod dashboard and gateway), and prd (separate production stamp, no simulation)."

## 8. Deploy and verify (manual, not Luigi)

- [ ] 8.1 After merge, run `eim` manually with `applySettings` and `deployCode`. Check `wtc-dashboard-api-dev` has `Simulation__Enabled=true`, and that the Simulation console still runs on non-production.
- [ ] 8.2 Run `eim` manually with `runMigrations`. Check that 015 is journaled on `eimdb`, that there are ten `PRD` rows, and that the BAT/DEV/DEV2/TEST row counts are unchanged.
- [ ] 8.3 Confirm `d365fsc-integration-prod` exists (do not create a resource group). Then create the production foundations in it from `docs/deployment/production-go-live.md`:
  - `sbns-eim-wtc-prd` (Standard)
  - `sql-eim-wtc-prd`
  - `sqldb-eim-wtc-prd` (collation `SQL_Latin1_General_CP1_CI_AS`)
- [ ] 8.4 Register the production Entra app (SPA and API, `user_impersonation`). Commit its tenant, client and audience, plus `automation.excludedUsers`, in `deploy.prdshared.json` and `deploy.prd.json` (a small follow-up PR; none is secret).
- [ ] 8.5 Create the DevOps resources:
  - environments `eim-wtc-prdshared` and `eim-wtc-prd` (approvals and exclusive lock)
  - service connection `sc-eim-wtc-prd` (Contributor on `d365fsc-integration-prod` only)
  - variable group `eim-wtc-prd` (`EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS`, both secret), restricted to `eim-prd`
  - the `eim-prd` pipeline from `/pipelines/eim-prd.yml`
- [ ] 8.6 Run `eim-prd` with `deployCode` cleared (preflight only) against an `eim` run from `main` whose `Deploy_test` succeeded. Permit the resources. Preflight passes the foundation, secret-target and isolation checks.
- [ ] 8.7 Run `eim-prd` with `deployInfra`, `applySettings`, `runMigrations` and `deployCode`. Check:
  - `verify` and `health` pass
  - every gated trigger on `azf-eim-automation-wtc-prd` is disabled
  - `app-eim-dashboard-api-wtc-prd` has `Simulation__Enabled=false` and no other `Simulation__*` setting
- [ ] 8.8 Run `remove -WhatIf` for `prd` and for `prdshared` from an operator machine. Both are refused.
- [ ] 8.9 Production data:
  - Enter the `PRD` `ENVIRONMENTTABLE` profiles (`D365FSC`, `D365FSC-GW`) through the dashboard.
  - Add the production admins and review the seeded `USERTABLE` row.
  - Confirm the ten `PRD` routes, with the delete route paused.
  - Onboard the gateway consumers with new keys.
- [ ] 8.10 Sign in to the production dashboard and check:
  - no Simulation navigation entry
  - `/simulation` shows the unavailable notice
  - the overview card shows "Simulation off"
  - `POST /api/simulation/run` returns 404
- [ ] 8.11 Go-live:
  - Repoint the D365 production business events and Capture production to the `prd-*` queues.
  - Run `Deploy-Wilson.ps1 -Environment prd -Component cutover`.
  - Watch the first messages complete in the dashboard.
- [ ] 8.12 Create the alerts from design decision 9 in `d365fsc-integration-prod`.
