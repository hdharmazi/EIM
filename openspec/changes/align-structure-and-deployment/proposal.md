## Why

Ticket: **#5246**.

Wastequip EIM (`ARM.Integration.Wastequip/EIM`) was forked from Wilson and has since gained a cleaner structure:
- a client-named automation host with thin triggers
- a client test project separate from the engine tests
- fail-fast settings and committed sample settings
- one build-once, promote-in-order pipeline driven by a single script, layered configuration and a resource naming pattern

Wilson has drifted the other way:
- `ARM.Integration.EIM.Customer` mixes triggers with processing logic, and engine and client tests share one project.
- Docker, Service Dependencies and `.appmod` leftovers remain.
- Deployment runs through three per-component YAML pipelines. They rebuild from source and authenticate with publish profiles.
- Azure resource names follow no common pattern (`wtc-dashboard-api-dev` serves every environment; `asp-appservice-p0v3-linux-dev` hosts nonprod).
- The gateway is the only Function App on Flex Consumption, with its own deploy path.

Aligning now keeps the two code bases mergeable. It also gives Wilson one reviewed path to bat and test, with names that say what each resource is.

The `dev` environment is not a deployment target (user decision, 2026-10-22: "Remove dev environment as deployment, since it only exists on my local"). Development happens on the developer's machine against a local SQL Server, Azurite and a locally run DbMigrator. Phase 2 was first written and implemented with a `dev` stage; Phase 2b below removes it before anything is pushed or provisioned.

## What Changes

### Phase 1: structure (code only; ends with build and tests green)

- **BREAKING (repo layout):** rename `ARM.Integration.EIM.Customer` to `Wilson.Integration.Automation`. This covers the folder, csproj, root namespace and launch profile, plus every reference to it:
  - the slnx and project references
  - `InternalsVisibleTo`
  - `run.bat` and `.vscode`
  - `.claude/agents`, `CLAUDE.md` and `README.md`
  - `docs/**` and `openspec/config.yaml`
  - the current `Deploy-Eim.ps1` and old YAML project paths, so `main` stays deployable until Phase 2 lands

  Function names (`[Function(nameof(...))]`) do not change.
- Reorganise the host with identical behaviour:
  - `Functions/` holds the six thin trigger classes under unchanged class names.
  - `Inbound/` holds Capture → D365 processing: `CaptureMessageProcessor` and `BusinessEventReconciler`.
  - `Outbound/` holds D365 → Capture processing: `DataEventProcessor`.
- Delete the inactive `PollSourceHandler` template (its `[Function]` was already commented out, so the six live functions are unchanged). Its `PollSchedule` setting is retired: it is absent from the samples and the new deploy config, and the deploy script removes it from new apps and ignores it in drift and cutover checks. `SamplePoll_To_Target` keeps its class and loses the doc reference.
- Add a `Wilson.Integration.Tests` project and move the client-dependent tests into it. These are the orchestration tests, the Automation host tests, `EngineBoundaryTests`, their fixtures and the linked migration `014`. `ARM.Integration.EIM.Tests` becomes engine-only.
- Add new tests:
  - one that pins the six Automation function names (and that `PollSourceHandler` is gone)
  - `RequiredSettingsTests`
  - `SecretScanTests`
- Add a fail-fast required-settings check:
  - Automation uses `RequiredSettings.EnsurePresent`.
  - The gateway builds `GatewayOptions` eagerly at startup.
- Add `local.settings.sample.json` (placeholders only) and the `-p:LocalSettings=<name>` switch to Automation and the Gateway.
- Remove the leftovers:
  - the Docker properties, package and launch profile, and `.dockerignore`
  - `Properties/ServiceDependencies/*` and `serviceDependencies*.json`
  - `.appmod/`
  - the `CRMSolutionProperties` block in the slnx
  - the port mismatch: Automation now listens on 7071, as `run.bat` and the README already say

### Phase 2: deployment, correctly named resources, cutover

- **BREAKING (Azure resource names):** adopt the pattern `{type}-{workload}-{component}-{client}-{suffix}`.
  - Workload is `eim` and client is `wtc`.
  - The suffix is `<env>` for per-environment resources and `nonprod` for shared ones.
  - The new names are derived by the script, which keeps a `names` override.
  - Recreated under the new names: the Automation Function Apps, the gateway Function App, their storage and Application Insights, the App Service plan, and a new Log Analytics workspace.
  - Kept under their current names through overrides (approved as deferred): SQL server and `eimdb`, the Service Bus namespace and queues, the Key Vault, the resource group, the dashboard API `wtc-dashboard-api-dev` and the dashboard SWA `wtc-dashboard-frontend-dev`.
  - The dashboard is deployed in place: no new dashboard resource, no cutover, no new URL and no Entra change. Its API moves to the new plan in a manual runbook step so the old plan can be deleted.
- Deployment configurations (`nonprod`, `bat`, `test`; `dev` is removed in Phase 2b) are kept separate from the ENVIRONMENT values in `eimdb` (`DEV`, `DEV2`, `BAT`, `TEST`). Nothing in the script or tests enumerates or assumes the database's environment values.
- **BREAKING (gateway hosting):** the gateway moves from Flex Consumption to the shared App Service plan with Always On, and becomes `azf-eim-gateway-wtc-nonprod`.
  - Its existing named host keys are copied across, so consumers change only the base URL.
- Add `pipelines/eim.yml` with the templates `build-package.yml` and `deploy-environment.yml`.
  - It builds, tests and packages once, then runs `Build → Deploy_shared → Deploy_bat → Deploy_test`, with approval on the shared environment and on bat. (Phase 2 shipped a `Deploy_dev` stage between shared and bat; Phase 2b removes it.)
  - Manual-run checkboxes: `deployInfra`, `applySettings`, `runMigrations`, `deployCode`.
  - Approvals and exclusive locks live on the Azure DevOps environments.
  - It uses one ARM service connection, `sc-eim-wtc-nonprod`. Publish profiles and Kudu Basic auth are dropped.
- **BREAKING (deployment):** replace `Deploy-Eim.ps1` with `Deploy-Wilson.ps1`. It provides:
  - the fixed component order `preflight, infra, settings, myip, schema, myipremove, gateway, backend, frontend, automation, verify, health`
  - `-WhatIf`
  - a settings drift check on code-only runs
  - stop → zip deploy → start → trigger sync for every Function App
  - gated triggers, plus `cutover`, `rollback` and a manual-only `remove` for the legacy resources
- The configuration becomes layered: `deploy.base.json`, `deploy.nonprod.json` (shared components) and `deploy.{bat,test}.json` (Phase 2 also shipped `deploy.dev.json`; Phase 2b deletes it).
  - The `EIM_DEPLOY_CONFIG_OVERRIDES` variable carries overrides.
  - The EIMDB and Service Bus connection strings come only from secret variables (`EIM_DEPLOY_EIMDB`, `EIM_DEPLOY_SERVICEBUS`).
  - The `hostsGateway`/`hostsDashboard` flags are removed.
- Add `New-DeploymentPackage.ps1`. It builds one versioned package containing:
  - the script and config
  - the app zips, keeping `.azurefunctions`
  - the shared SPA build
  - a self-contained win-x64 `ARM.Integration.DbMigrator.exe`
  - a manifest
- Add `Test-DeployWilson.ps1`, a dependency-free self-test of the scripts' logic, run by Luigi and by the pipeline's Build stage (which also checks the built package).
- Migrations run once per run, in `Deploy_shared`, behind `runMigrations`. The agent IP is added to the SQL firewall and removed in try/finally.
- Delete the old pipelines:
  - `.azure-pipelines/workflows/deployment/*.yml`
  - `Deploy-ZipWithPublishProfile.ps1`
  - `deploy.example.json`

  Move `auto-ai-pull-request.yml` and the pipeline README to `pipelines/`, with unchanged behaviour.
- Update the docs:
  - `docs/deployment/*`, `docs/azure-deployment-pipelines-setup.md`, `docs/auto-ai-pull-request-setup.md`, `docs/README.md` and `README.md`
  - add a one-time Azure DevOps setup checklist
  - add `docs/deployment/resource-rename-cutover.md` (provision, settings, deploy, cutover, verify, rollback, decommission)
- Manual (user runbook, not Luigi):
  - the Azure DevOps setup and the `pre-5246-deploy` tag
  - provisioning the new resources
  - gateway consumer updates
  - cutover per environment
  - moving the dashboard API to the new plan
  - decommissioning the legacy resources

### Phase 2b: remove dev from deployment (local development only)

- **BREAKING (pipeline):** delete the `Deploy_dev` stage. The order becomes `Build → Deploy_shared → Deploy_bat → Deploy_test`. `Deploy_bat` depends on `Deploy_shared` and runs only when it succeeded. bat keeps its approval and exclusive lock on the `eim-wtc-bat` DevOps environment, so a merge now waits for two approvals (shared, then bat) before code reaches bat.
- The Build stage packages `nonprod,bat,test`, and `New-DeploymentPackage.ps1` defaults to the same list.
- Delete `script/deployment/config/deploy.dev.json`. `Deploy-Wilson.ps1 -Environment dev` is then rejected as an unknown configuration, so `azf-eim-automation-wtc-dev`, `stwtceimautodev` and `appins-eim-automation-wtc-dev` are never created.
- Retire the legacy dev resources with no replacement, through the existing manual `remove` component:
  - Move the legacy dev entries (`azf-eim-customer-wtc-dev`, `stwtceimcustomerdev`, `appins-eim-customer-wtc-dev`) into the `legacy.resources` of `deploy.nonprod.json`.
  - Add `legacy.retiredAutomationApps: ["azf-eim-customer-wtc-dev"]` there. `remove` refuses while any gated function on a retired app is enabled, exactly as it does for `legacy.automationApp`. Every other `remove` safeguard is unchanged.
  - `verify` on the shared configuration also reports the gated trigger states of each retired app.
- The `dev-*` Service Bus queues and the `DEV`/`DEV2` rows in `eimdb` are left alone. The pipeline neither creates nor deletes them. ENVIRONMENT values are still never enumerated by the script.
- Local development is documented: a local SQL Server `eimdb` created by running `ARM.Integration.DbMigrator` locally, Azurite for `AzureWebJobsStorage`, and the local settings samples pointing at them. A developer's host must not consume a queue that a deployed Automation app consumes.
- Update the tests (`Test-DeployWilson.ps1`, `DeploymentConfigTests`, `PipelineDefinitionTests`) to the three configurations, and the docs, `README.md`, `CLAUDE.md` and `pipelines/README.md` to the new stage order.
- Manual: do not create the `eim-wtc-dev` DevOps environment (or delete it if it exists); disable the legacy dev triggers, check the `dev-*` queues, then remove the legacy dev resources with the nonprod `remove`.
### Delivery

Both phases ship on one branch. Phase 1 is its own commit, and Phase 2 follows on the same branch. Phase 2b is a third commit on the same branch, made before the branch is pushed, so no environment is ever provisioned or deployed with a `dev` stage. The manual runbook (tasks section 5) runs after Phase 2b.

## Non-goals

- Managed identity for EIMDB, Key Vault references for app settings, or a prod stage. These are separate follow-ups.
- Renaming the SQL server, the `eimdb` database, the Service Bus namespace and its queues, the Key Vault, the resource group, the dashboard API or the dashboard SWA. They are kept via `names` overrides and recorded as deferred; see design D7 and D8.
- Adding a deployment configuration, Function App or queues for the `DEV2` environment value.
- Any Azure resource, DevOps environment or pipeline stage for `dev`. Development is local only.
- Deleting or renaming the `dev-*` Service Bus queues, or the `DEV`/`DEV2` rows in `ROUTETABLE`, `ENVIRONMENTTABLE` or any other table. No ENVIRONMENT value changes.
- Changing the engine sweeps. The release and retry sweeps are global across ENVIRONMENT values, so the bat and test apps keep sweeping `DEV`/`DEV2` held and retry rows; that behaviour is unchanged.
- Provisioning a local Service Bus emulator or a local D365 stub.
- Renaming `ARM.Integration.D365FSC.Gateway`, `ARM.Integration.EIM`, `ARM.Integration.Core`, `ARM.Integration.Dashboard.*` or `ARM.Integration.DbMigrator`.
- Changing any live `[Function]` name, app setting name (other than retiring the unused `PollSchedule`), queue name, route, orchestration or message behaviour.
- Deleting `SamplePoll_To_Target`, `EIMWatermark` or `WATERMARKTABLE`.
- Any SQL migration. This change adds no schema.
- Deployment slots, Bicep/ARM templates, per-environment dashboards or gateways.
- Pipeline-driven deletion of any Azure resource. Decommissioning is always manual.
- Wastequip-only features: the Key Vault `secrets` component, `sqlusers`, `-NameSuffix` test copies, `resubmit`, and release tags.

## Capabilities

### New Capabilities
- `deployment-pipeline`: the build-once, promote-in-order Azure DevOps pipeline. Covers:
  - the shared stage, then bat and test (no dev stage)
  - approvals and locks
  - manual-only infrastructure, settings and migrations
  - preflight with drift check, and the health check
  - layered configuration and secret handling
  - the deployment package
- `integration-host-layout`: the Wilson Automation host layout. Covers:
  - stable function names, and no inactive trigger templates
  - the engine/client test split
  - fail-fast required settings for both Function hosts
  - sample local settings and the secret scan
  - local run ports
- `resource-naming-cutover`: the naming pattern, the old → new mapping, the kept names, and gated triggers. Covers:
  - side-by-side cutover and rollback with a single queue consumer at any time
  - the dashboard deployed in place
  - manual-only decommissioning of the legacy resources, including retiring the legacy dev Automation app with no replacement

### Modified Capabilities
- `deployment-automation`:
  - The entry point becomes `Deploy-Wilson.ps1` with the new component order.
  - Names derive from the `{type}-{workload}-{component}-{client}-{suffix}` pattern, with overrides for kept resources.
  - Shared components are owned by the `nonprod` configuration instead of the `hosts*` flags.
  - The gateway runs on the shared App Service plan with Always On, replacing the Flex hosting profile.
  - Gateway settings gain `FUNCTIONS_WORKER_RUNTIME`.
  - Schema becomes a shared component.
  - Retired application settings are removed and ignored by checks.
- `schema-migration`: the explicit deployment step runs once per pipeline run, from the shared stage, behind a temporary firewall rule. Migrations stay compatible with the code still running in later stages.

## Impact

- **Code:**
  - `ARM.Integration.EIM.Customer/` becomes `Wilson.Integration.Automation/`; `PollSourceHandler.cs` is deleted.
  - New `Wilson.Integration.Tests/`.
  - `ARM.Integration.EIM.Tests/` is trimmed to engine-only.
  - New `Wilson.Integration.Orchestrations/Configuration/RequiredSettings.cs`; `SamplePoll_To_Target.cs` doc comment.
  - Gateway changes: `Program.cs`, `.csproj`, and the new `local.settings.sample.json`.
  - `ARM.Integration.EIM/ARM.Integration.EIM.csproj` (InternalsVisibleTo).
  - `ARM.Integration.Wilson.slnx`.
- **Scripts and config:**
  - Under `script/deployment/`: `Deploy-Wilson.ps1`, `New-DeploymentPackage.ps1`, `Test-DeployWilson.ps1` and `config/deploy.{base,nonprod,bat,test}.json`.
  - Removed: `Deploy-Eim.ps1`, `Deploy-ZipWithPublishProfile.ps1`, `deploy.example.json` and (Phase 2b) `config/deploy.dev.json`.
- **Pipelines:** new `pipelines/**`. `.azure-pipelines/**` is removed. Phase 2b removes `Deploy_dev` from `pipelines/eim.yml`.
- **Azure (manual runbook):**
  - New resources: `asp-eim-wtc-nonprod` (P0v3) hosting `azf-eim-gateway-wtc-nonprod` and `azf-eim-automation-wtc-{bat,test}`; `log-eim-wtc-nonprod`; storage and Insights per new app. No `*-dev` Automation app, storage or Insights is created.
  - `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` are updated in place. The API moves to `asp-eim-wtc-nonprod` (one restart).
  - The legacy Automation apps, the legacy gateway with its Flex plan, their storage and Insights, and the emptied old plan are deleted manually after cutover. The legacy dev Automation app is retired with no replacement: its triggers are disabled and it is removed with the nonprod `remove`.
- **External:**
  - Gateway consumers change the base URL (same keys).
  - Dashboard users and the Entra app registration are unaffected.
  - The SQL firewall needs the new plan's outbound IPs, unless Azure services are already allowed.
  - Capture, D365 data events and business events are unaffected, because the Service Bus namespace and queues are kept.
  - Every ENVIRONMENT value in `eimdb`, including `DEV2`, keeps its rows: the database, queues and gateway routes are unchanged. Once the legacy dev app is retired, nothing deployed consumes the `dev-*` queues; anything Capture or D365 still publishes there waits for a local host or expires (14-day TTL, dead-lettered on expiry). Held and retry rows for `DEV`/`DEV2` are still swept by the bat and test apps, because the sweeps are global.
- **Azure DevOps (manual):**
  - environments `eim-wtc-nonprod`, `eim-wtc-bat` and `eim-wtc-test` (no `eim-wtc-dev`; delete it if it was already created)
  - service connection `sc-eim-wtc-nonprod`
  - variable group `eim-wtc-nonprod`
  - a new pipeline for `pipelines/eim.yml`
  - an updated YAML path for the auto-AI PR pipeline
  - the old definitions disabled, then deleted after decommission
- **Open change #5425 (`fix-address-source-business-key`):** its code is merged and only its manual deploy tasks remain. Its archived spec delta does not overlap this change. Run its remaining deploy tasks with the current pipelines before Phase 2 cutover, or with the new pipeline afterwards. Do not edit it.
- **Developers:** move any `local.settings.json` from the old folder to `Wilson.Integration.Automation/`. Local Automation listens on 7071. Development runs against a local SQL Server `eimdb` (created with the DbMigrator) and Azurite; only finished, backward-compatible migrations reach the shared `eimdb`, through the pipeline's `runMigrations`.
