## Why

Ticket: `#5246`, on the branch `feature/5246/align-structure-and-deployment`.

Wilson has no production deployment. The #5246 change (`align-structure-and-deployment`) gives one script, one configuration layer and one promotion pipeline for non-production, and records "Production is out of scope: it will be a separate stamp that shares nothing with non-production". This change adds that stamp: a production configuration pair, a production pipeline that promotes the package already proven on bat and test, the production database seed, and the guard rails a production estate needs. The production dashboard must not be able to publish simulated messages.

## What Changes

- **Two new deployment configurations.** `deploy.prdshared.json` (scope `shared`, `sharedSuffix` `prd`) and `deploy.prd.json` (scope `environment`, `sharedSuffix` `prd`). Both reset every non-production value they would otherwise inherit from `deploy.base.json`, carry `"production": true`, and have no `legacy` section. The resource group is the existing `d365fsc-integration-prod`, named by the one `names` override; the subscription is inherited from the base (the same as non-production). All other names come from the pattern: `sbns-eim-wtc-prd`, `sql-eim-wtc-prd`, `sqldb-eim-wtc-prd`, `asp-eim-wtc-prd` (P0v3), `log-eim-wtc-prd`, `azf-eim-gateway-wtc-prd`, `app-eim-dashboard-api-wtc-prd`, `swa-eim-dashboard-wtc-prd`, `azf-eim-automation-wtc-prd`, and queues `prd-*`.
- **Script changes in `Deploy-Wilson.ps1`:**
  - The owning shared configuration is resolved from `sharedSuffix`, so messages name `prdshared` instead of assuming that the shared configuration is called after its suffix.
  - Preflight now also checks that the Service Bus namespace, SQL server and database exist.
  - Preflight checks that `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` point at the configured server, database and namespace. This stops non-production secrets from reaching production, and production secrets from reaching non-production.
  - Preflight checks that no production name is also a non-production name.
  - `remove` refuses any configuration marked `production`.
  - A shared-scope `cutover` with no legacy gateway is refused with a clear message.
- **Simulation switch, fail safe.**
  - The dashboard API gets a `Simulation:Enabled` setting. When it is absent or not `true`, the API does not expose `SimulationController`, `SimulationService` refuses to publish, and `/api/me` reports `features.simulation = false`.
  - The SPA hides the Simulation navigation entry and route, and the overview's Service Bus card.
  - The script writes `Simulation__Enabled` from `dashboard.simulationEnabled`: `true` in the base, `false` for production. It writes the `Simulation__*` connection string and queues only when simulation is enabled, and deletes stale ones when it is disabled.
- **Production pipeline `pipelines/eim-prd.yml`.**
  - Started manually only. It promotes the `package` artifact of a chosen `eim` run; it does not rebuild.
  - Stages are `Deploy_prdshared` (gateway, dashboard, migrations) then `Deploy_prd` (Automation). They use DevOps environments `eim-wtc-prdshared` and `eim-wtc-prd` with approvals, service connection `sc-eim-wtc-prd`, and variable group `eim-wtc-prd`.
  - `deploy-environment.yml` gains a package-source parameter.
  - The `eim` Build stage also packages `prdshared` and `prd`.
- **Database.** Migration `015_seed_prd_routes.sql` seeds the ten `PRD` routes with an insert-only MERGE, so it is safe on both the non-production `eimdb` and the production database. The address delete route is seeded disabled with `ONPAUSE = 'Hold'`, matching its paused state on BAT and TEST. `ENVIRONMENTTABLE` `PRD` rows (D365 credentials) are entered by runbook through the dashboard and are never committed.
- **Tests and docs.**
  - Update `DeploymentConfigTests`, `PipelineDefinitionTests`, `Test-DeployWilson.ps1` and the docs. The `deployment.md` decision "Production is out of scope" is replaced.
  - New tests: production isolation, the simulation switch, and the production pipeline.
- **Runbook (manual).**
  - Create the foundations.
  - Register the production Entra app.
  - Create the DevOps environments, service connection and variable group.
  - Seed `ENVIRONMENTTABLE`.
  - Run the first deployment.
  - Repoint D365 business events and Capture.
  - Go live with `cutover`.
  - Set up alerts.

## Capabilities

### New Capabilities
- `production-stamp`: covers four things:
  - the production configurations and their isolation from non-production
  - the production pipeline and its promotion of a tested package
  - the refusal to `remove` production resources
  - go-live through `cutover`

### Modified Capabilities
- `deployment-automation`:
  - The configuration list now includes `prdshared` and `prd`.
  - Shared names use `sharedSuffix`.
  - The owning shared configuration is resolved per stamp.
  - New requirements:
    - preflight checks foundation resources
    - preflight checks where the secrets point
    - the dashboard simulation setting
- `deployment-pipeline`:
  - The Build stage also packages `prdshared` and `prd`.
  - The package carries one frontend per shared configuration.
  - Secrets and the service connection are per stamp.
- `resource-naming-cutover`:
  - The dev-rejection scenario lists the new configurations.
  - A shared `cutover` without a legacy gateway is refused clearly.
- `schema-migration`: a migration applies to every stamp's database and must be safe on each.
- `integration-route-registry`: the `PRD` routes are seeded.
- `simulation-console`: simulation can be switched off, and it is off unless explicitly enabled.
- `dashboard-spa-shell`:
  - `/api/me` reports enabled features.
  - Navigation omits Simulation when it is off.
- `dashboard-overview`: the Service Bus card does not call the simulation endpoint when simulation is off.

## Impact

- **Script and config:**
  - `script/deployment/Deploy-Wilson.ps1`
  - `script/deployment/Test-DeployWilson.ps1`
  - `script/deployment/config/deploy.base.json`, plus the new `deploy.prdshared.json` and `deploy.prd.json`
  - `script/deployment/New-DeploymentPackage.ps1` (README text only)
- **Pipelines:**
  - `pipelines/eim.yml` (Build environments)
  - new `pipelines/eim-prd.yml`
  - `pipelines/templates/deploy-environment.yml` (package source)
  - `pipelines/README.md`
- **Dashboard API:**
  - `Program.cs`
  - new `Services/SimulationFeature.cs` (switch and controller feature provider)
  - `Services/SimulationService.cs`
  - `Controllers/MeController.cs`
  - `appsettings.json`, `appsettings.Development.json`
- **Frontend:**
  - `src/auth/useMe.ts`
  - `src/components/AppShell.tsx`
  - `src/router.tsx`
  - `src/features/dashboard/DashboardPage.tsx`
- **SQL:** `script/SQL/Migrations/015_seed_prd_routes.sql`, and `script/SQL/Migrations/002a_create_modifieddatetime_trigger.sql` (creates the trigger `003` needs on an empty database; design 7a)
- **Tests:**
  - `Wilson.Integration.Tests/DeploymentConfigTests.cs`
  - `Wilson.Integration.Tests/PipelineDefinitionTests.cs`
  - new `ARM.Integration.D365FSC.Gateway.Tests/SimulationSwitchTests.cs`
- **Docs:**
  - `docs/deployment/deployment.md`
  - `deploy-from-scratch.md`
  - `devops-setup.md`
  - `database-migrations.md`
  - `README.md`
  - new `docs/deployment/production-go-live.md`
- **Main session:** `CLAUDE.md` deployment bullet and `openspec/config.yaml` environments line.
- **Non-production rollout:** the first non-production run after this change must be a manual run with `applySettings`. Until then, the code-only drift check fails on the missing `Simulation__Enabled`, and that failure is deliberate.
- **External:**
  - production Entra app registration
  - Azure DevOps resources
  - D365 production business-event endpoints
  - gateway consumers, which get production keys
  - Capture production endpoint
- **Dependency:** this change builds on `align-structure-and-deployment`, under the same ticket (#5246) and on the same branch. That change is not yet archived, and several MODIFIED requirements here modify requirements it adds. Archive `align-structure-and-deployment` first.

## Non-goals

- Bicep or ARM templates, or any script component that creates the resource group, Service Bus namespace, SQL server or database. The resource group `d365fsc-integration-prod` already exists; the other three are created in it by runbook.
- A Key Vault, or a `secrets` component. `kv-eim-wtc-prd` is derived but nothing reads it.
- Moving the non-production values out of `deploy.base.json` into their own layer.
- Scripted alerts or dashboards. Alerts are a runbook step.
- A production `legacy` section, a production `rollback`, or any cutover from existing resources. Production has no predecessor.
- Copying gateway keys from non-production. Production consumers get new keys.
- Automatic deployment to production on merge.
- Removing or rewriting the non-production rows that migrations 001, 002 and 006 seed into every new database. The runbook covers what to review on the production database.
- Changing the Configuration page or the `SimulationEnvironment` configuration category.
- Any change to message processing, orchestrations, routing logic or failure classification.
