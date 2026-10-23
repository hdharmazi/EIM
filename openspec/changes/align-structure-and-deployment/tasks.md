## Phase 1: structure (Luigi, PR 1). Finish and verify section 2, and merge PR 1, before starting Phase 2.

## 1. Phase 1: Implementation

- [x] 1.1 Rename the project with `git mv`: `ARM.Integration.EIM.Customer/` → `Wilson.Integration.Automation/` and `ARM.Integration.EIM.Customer.csproj` → `Wilson.Integration.Automation.csproj`.
  - Set `RootNamespace`/`AssemblyName` to `Wilson.Integration.Automation`.
  - Change every `namespace ARM.Integration.EIM.Customer` to `Wilson.Integration.Automation[.Functions|.Inbound|.Outbound]`.
  - Add `Wilson.Integration.Automation/GlobalUsings.cs` with the engine namespaces the code uses (design D1).
  - Proven by `dotnet build ARM.Integration.Wilson.slnx` and test 2.2.
- [x] 1.2 Clean up `Wilson.Integration.Automation/Wilson.Integration.Automation.csproj`:
  - Remove `DockerFastModeProjectMountDirectory`, `DockerDefaultTargetOS`, `UserSecretsId` and the `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` package.
  - Replace `InternalsVisibleTo ARM.Integration.EIM.Tests` with `InternalsVisibleTo Wilson.Integration.Tests`.
  - Add the `LocalSettingsFile` property and the linked `None` item (design D6).
  - Proven by the build and by test 2.6.
- [x] 1.3 Reorganise into `Functions/`, `Inbound/` and `Outbound/` exactly as the D2 table describes:
  - `Functions/{QueueCaptureCustomer,QueueDataEventsHandler,QueueMessageQueueBusinessEventHandler,AwaitingD365SweepHandler,ReleaseSweepHandler,RetrySweepHandler}.cs`
  - `Inbound/CaptureMessageProcessor.cs` (replaces `QueueCaptureHandler.cs`)
  - `Inbound/BusinessEventReconciler.cs`
  - `Outbound/DataEventProcessor.cs`

  Keep class names, `[Function(nameof(...))]`, bindings, `Connection = "ServiceBus"`, constructor signatures and settlement calls unchanged. Proven by tests 2.2 and 2.3 and the moved behaviour tests in 2.1.
- [x] 1.4 Delete the inactive polling template (design D18):
  - `git rm` `PollSourceHandler.cs` (it is not moved into `Functions/`).
  - In `Wilson.Integration.Orchestrations/Orchestrations/SamplePoll_To_Target.cs`, reword only the doc comment so it no longer names `PollSourceHandler`. Keep the class.
  - Do not touch `EIMWatermark`, `WATERMARKTABLE`, `script/SQL/Migrations/006_*` or any archived change.

  Proven by tests 2.2 (no `PollSourceHandler` type, six functions) and 2.6 (file absent, no reference in `SamplePoll_To_Target.cs`).
- [x] 1.5 `Properties/launchSettings.json`:
  - Rename the profile to `Wilson.Integration.Automation`, with `commandLineArgs` `--port 7071`.
  - Delete the `Container (Dockerfile)` profile.

  Proven by test 2.6 (inspects the file) and a manual `func start` check by the reviewer.
- [x] 1.6 Delete the leftovers with `git rm`:
  - `.dockerignore`
  - `Wilson.Integration.Automation/Properties/ServiceDependencies/**` and `Wilson.Integration.Automation/Properties/serviceDependencies*.json`
  - `.appmod/`
  - the `CRMSolutionProperties` `<Properties>` block in `ARM.Integration.Wilson.slnx`

  Proven by test 2.6.
- [x] 1.7 Add `Wilson.Integration.Orchestrations/Configuration/RequiredSettings.cs`, a port of Wastequip's:
  - `SettingState`, `GetState`, `Inspect` and `EnsurePresent`
  - groups `Database`, `ServiceBus`, `Queues` and `Schedules` (design D5); no `PollSchedule` or `PollSource*` keys
  - a `Microsoft.Extensions.Configuration.Abstractions` 10.0.x package reference in `Wilson.Integration.Orchestrations.csproj`

  Proven by test 2.4.
- [x] 1.8 In `Wilson.Integration.Automation/Program.cs`, call `RequiredSettings.EnsurePresent(builder.Configuration, RequiredSettings.Database, RequiredSettings.ServiceBus, RequiredSettings.Queues, RequiredSettings.Schedules)` before service registration, and read `EIMDB` from `builder.Configuration`. Proven by test 2.4 (`EnsurePresent_MissingServiceBus_NamesSetting`).
- [x] 1.9 In `ARM.Integration.D365FSC.Gateway/Program.cs`, build `GatewayOptions.FromConfiguration(builder.Configuration)` before `Build()` and register the instance. Proven by test 2.5.
- [x] 1.10 Add the local settings samples (design D6):
  - `Wilson.Integration.Automation/local.settings.sample.json` and `ARM.Integration.D365FSC.Gateway/local.settings.sample.json`, placeholders only
  - Automation sample queue values use the `<your queue prefix>-` form (for example `<your queue prefix>-d365fsc-dataevent`); no `PollSchedule` or `PollSource*` keys
  - the `LocalSettingsFile` switch in `ARM.Integration.D365FSC.Gateway.csproj`
  - in both project `.gitignore` files, ignore `local.settings.*.json` except `!local.settings.sample.json`

  Proven by tests 2.4 (sample covers required keys, no retired keys) and 2.7 (secret scan).
- [x] 1.11 Create the `Wilson.Integration.Tests/` project:
  - xUnit 2.9.2, Test.Sdk 17.12.0
  - references Automation, Orchestrations, EIM and Core
  - `TestData\**\*.json` as content, plus the linked `..\script\SQL\Migrations\014_seed_customer_address_delete_route.sql` as `TestData\Migrations\...`

  Move with `git mv` from `ARM.Integration.EIM.Tests/`:
  - `BusinessKeyRegistrationTests.cs`, `CustomerAddressDeleteToCaptureTests.cs`, `CustomerAddressSourceIdTests.cs`, `D365UpdatePayloadBuilderTests.cs`, `PartyContactToCaptureTests.cs`, `EngineBoundaryTests.cs`
  - their helpers (e.g. `RecordingMessageActions`)
  - `TestData/CustomerAddressDeleted.OnExternalUpdated.json` and `TestData/SourceEntities/*.json`

  Update the references from `QueueDataEventsHandler.EntityRoutes`/`ResolveSourceId` to `DataEventProcessor`. Proven by test 2.1.
- [x] 1.12 Trim `ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`:
  - Remove the Orchestrations and Automation project references and the linked 014 migration.
  - Keep EIM, Core and the remaining `TestData`.
  - Add `InternalsVisibleTo Wilson.Integration.Tests` to `ARM.Integration.EIM/ARM.Integration.EIM.csproj` only if 1.11 needs engine internals.

  Proven by test 2.1 (`dotnet test ARM.Integration.EIM.Tests`) and test 2.3.
- [x] 1.13 Update `ARM.Integration.Wilson.slnx`: replace the `ARM.Integration.EIM.Customer` project entry with `Wilson.Integration.Automation`, and add `Wilson.Integration.Tests`. Proven by `dotnet build ARM.Integration.Wilson.slnx`.
- [x] 1.14 Update the references to the old project name and ports:
  - `run.bat`: window title `"Wilson Automation"`, `cd Wilson.Integration.Automation`, `--launch-profile Wilson.Integration.Automation`, comment on 7071/7072
  - `run-dashboard.bat`, if it references the project
  - `.vscode/settings.json` (`azureFunctions.projectSubpath`/`deploySubpath`) and `.vscode/tasks.json` (`cwd`)
  - `.claude/agents/luigi.md` and `.claude/agents/bowser.md`: test commands for both test projects
  - `.claude/settings.json`: check only; no path references today
  - `CLAUDE.md`: the project table, `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj` and the Stack line
  - `README.md`: the project table, ports and local settings steps, including moving an existing `local.settings.json` and `-p:LocalSettings=<name>`
  - `openspec/config.yaml` context: `Wilson.Integration.Automation`

  Proven by test 2.6 (stale-name search).
- [x] 1.15 Keep `main` deployable with the old path until Phase 2 lands: update only the project path, publish path and labels in `script/deployment/Deploy-Eim.ps1` and `.azure-pipelines/workflows/deployment/deploy-eim-customer-nonprod.yml` to `Wilson.Integration.Automation`. Resource names and the settings list (including the harmless `PollSchedule`) stay unchanged. Proven by test 2.6 and by review of the diff (Luigi has no permission to run `pwsh` against this script).
- [x] 1.16 Update `docs/**` that name the old project or its path:
  - `docs/deployment/database-migrations.md`, `docs/deployment/deployment.md`, `docs/deployment/deployment-plan.md`
  - `docs/azure-deployment-pipelines-setup.md` and `.azure-pipelines/README.md`

  Do NOT edit `openspec/changes/archive/**` or `openspec/changes/fix-address-source-business-key/**`. Proven by test 2.6.

## 2. Phase 1: Tests (gate: all green before PR 1 is raised)

- [x] 2.1 The moved tests pass unchanged in assertions, in `Wilson.Integration.Tests`, and `EngineBoundaryTests` passes. Command: `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`.
- [x] 2.2 Add `Wilson.Integration.Tests/AutomationFunctionNamesTests.cs`. It reflects over `[Function]` methods in the Automation assembly and asserts:
  - exactly the six names
  - each SB queue expression and `Connection = "ServiceBus"`
  - each timer schedule expression
  - no type named `PollSourceHandler` exists in the assembly

  It fails naming `automation.gatedFunctions` in `script/deployment/config/deploy.base.json` (spec `integration-host-layout`, "Function names are stable" and "No inactive trigger templates").
- [x] 2.3 Add `Wilson.Integration.Tests/ProjectReferenceTests.cs`. It asserts that `ARM.Integration.EIM.Tests.csproj` has no reference to `Wilson.*` projects (spec "Engine and client tests are separate").
- [x] 2.4 Add `Wilson.Integration.Tests/RequiredSettingsTests.cs`, covering:
  - `GetState`: Present, Missing and UnresolvedKeyVaultReference
  - `EnsurePresent` naming all missing keys and no value
  - every key in the Automation groups present in `Wilson.Integration.Automation/local.settings.sample.json`
  - the sample has no `PollSchedule` key and no key starting with `PollSource`
- [x] 2.5 Add `ARM.Integration.D365FSC.Gateway.Tests/GatewayStartupTests.cs`. Configuration missing `EIMDB` and configuration missing `Gateway:BaseUrl` each throw from `GatewayOptions.FromConfiguration`, naming the key (spec "Fail-fast configuration validation").
- [x] 2.6 Add `Wilson.Integration.Tests/RepositoryLayoutTests.cs` (repo root located via `ARM.Integration.Wilson.slnx`). It asserts:
  - No tracked text file outside `openspec/changes/archive/**` and `openspec/changes/fix-address-source-business-key/**` contains `ARM.Integration.EIM.Customer`.
  - `.dockerignore`, `.appmod` and `Properties/ServiceDependencies` are absent.
  - `PollSourceHandler.cs` exists nowhere in the tree, and `SamplePoll_To_Target.cs` does not contain `PollSourceHandler`.
  - The slnx has no `CRMSolutionProperties`.
  - Automation's `launchSettings.json` has `--port 7071` and no `Docker` profile.
  - Both csprojs declare `CopyToPublishDirectory Never` for local settings.
- [x] 2.7 Add `Wilson.Integration.Tests/SecretScanTests.cs`, a port of Wastequip's with Wilson's skip list including `artifacts` (design D4). It passes on the current tree.
- [x] 2.8 Run `dotnet build ARM.Integration.Wilson.slnx`, then the affected test projects. All must pass before PR 1 is raised and before section 3 starts:
  - `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`
  - `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`
  - `dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj`

## Phase 2: deployment (Luigi, PR 2). Start only after PR 1 is merged.

## 3. Phase 2: Implementation

- [x] 3.1 Add the configuration files `script/deployment/config/deploy.base.json`, `deploy.nonprod.json`, `deploy.dev.json`, `deploy.bat.json` and `deploy.test.json`, with the shape in design D9:
  - `environment`, `scope`, `gateway`, `dashboard`, `entra`
  - `names` overrides for the kept SQL server, database, Service Bus namespace, Key Vault and resource group; in `deploy.nonprod.json` also `names.dashboardApi: "wtc-dashboard-api-dev"` and `names.staticWebApp: "wtc-dashboard-frontend-dev"`
  - `automation.gatedFunctions` (six), `automation.keepDisabled: []`, `automation.retiredSettings: ["PollSchedule"]`, and no `pollSchedule` key
  - `legacy` sections from the D7 tables: per environment the old Automation app, storage and Insights; in `nonprod` the old gateway app, storage and Insights and the old plan `asp-appservice-p0v3-linux-dev`. No dashboard resource is listed as legacy.
  - `<...>` placeholders for the subscription, Entra and excluded users, and no secrets

  Delete `deploy.example.json`. Proven by test 4.1.
  - Note (Phase 2b): `deploy.dev.json` is deleted and the legacy dev entries move to `deploy.nonprod.json` in 6.2.
- [x] 3.2 Add `script/deployment/Deploy-Wilson.ps1`. Requirements:
  - Port from Wastequip `Deploy-Wastequip.ps1`: `Invoke-Az`, `Merge-ConfigObject`, `Get-ResourceNames`/`$pick`, preflight with the drift check, `Invoke-FunctionDeploy` (stop, `config-zip --timeout 900`, start in `finally`, `syncfunctiontriggers` 6×20 s), health, cutover and remove.
  - Parameters as in D10. `-Environment` accepts any configuration that has a `deploy.<name>.json`; there is no hard-coded list of environments, and nothing reads or assumes the database's ENVIRONMENT values (`DEV`, `DEV2`, `BAT`, `TEST`).
  - Scope checks (D9). The fixed component order. `-WhatIf`.
  - Secrets only from `EIM_DEPLOY_EIMDB`/`EIM_DEPLOY_SERVICEBUS`, or a secure prompt when interactive.
  - Settings written via temp files deleted in `finally`.
  - Main body guarded so the file can be dot-sourced by 4.2: `if ($MyInvocation.InvocationName -ne '.') { ... }`.

  Proven by tests 4.2 and 4.3.
- [x] 3.3 In `Deploy-Wilson.ps1`, implement the `infra` component for both scopes, with the decisions in a pure planning function that takes the existing-resource state as input:
  - shared: plan, Log Analytics workspace, gateway app, storage and Insights on the plan
  - shared, dashboard: create the dashboard API (on the plan) and the Standard SWA only if absent; never recreate, rename, re-plan or delete an existing `wtc-dashboard-api-dev` or `wtc-dashboard-frontend-dev`
  - environment: the five queues; Automation storage, Insights and app
  - every created app: Always On, HTTPS only, TLS 1.2, FTPS disabled, `DOTNET-ISOLATED|10.0`
  - tags on every created resource
  - no Flex commands

  Proven by test 4.2 (planned `az` calls for `nonprod` with and without existing dashboard resources, and for `dev`).
  - Note (Phase 2b): the environment-scope fixture moves from `dev` to `bat` in 7.1.
- [x] 3.4 In `Deploy-Wilson.ps1`, implement the `settings` component:
  - Automation: the Deploy-Eim list minus `PollSchedule`, plus gated `AzureWebJobs.<fn>.Disabled=true` only when absent; every name in `automation.retiredSettings` is never written and is deleted from the target app if present, naming each removed setting
  - gateway (adds `FUNCTIONS_WORKER_RUNTIME`)
  - dashboard API, applied in place to `names.dashboardApi`: the Deploy-Eim list; `Gateway__BaseUrl`/`Gateway__AppName` for the new gateway; `Gateway__Applications__<n>__*` from `gateway.legacyVerificationTargets`; `Simulation__*` from `dashboard.simulationQueuePrefix`
  - the preflight drift check reports a retired setting on an app as a warning and never fails on it

  Proven by test 4.2 (`Get-GatedSettingsToWrite` keeps existing values; settings names per app match the D10 list; retired-settings write, delete and drift behaviour).
- [x] 3.5 In `Deploy-Wilson.ps1`, implement:
  - `myip`/`myipremove`: rule `eim-deploy-<BUILD_BUILDID>` or `AllowDev-<USERNAME>`, server parsed from the connection string and cross-checked with `names.sqlServer`
  - `schema`: packaged `ARM.Integration.DbMigrator.exe --timeout 1800`, `--whatif` under `-WhatIf`, `EIMDB_MIGRATOR_CONNECTION` in the child environment only

  Proven by test 4.2 (scope rejection of `schema` for `dev`) and test 4.3.
  - Note (Phase 2b): the scope-rejection fixture moves to `bat` in 7.1; `AllowDev-<USERNAME>` (an operator's machine) is unchanged.
- [x] 3.6 In `Deploy-Wilson.ps1`, implement the code components:
  - `gateway` and `automation` via `Invoke-FunctionDeploy`; `automation` refuses when gated settings are missing
  - `backend`: `az webapp deploy --type zip` to `names.dashboardApi` (in place)
  - `frontend`: unzip the SPA, `az staticwebapp backends link` to `names.dashboardApi`, SWA CLI deploy to `names.staticWebApp` (in place, existing hostname) with the token via an environment variable
  - `verify` (report only: host, state, Always On, plan, outbound IPs, gated states for new and legacy, SWA backend; labels the dashboard resources as shared)
  - `health` (host `Running` within 5 minutes, API < 500, SWA root)

  Proven by test 4.2 (refusal logic and resolved target names) and test 4.3.
- [x] 3.7 In `Deploy-Wilson.ps1`, implement:
  - `cutover`/`rollback` for the environment scope: pure `Get-CutoverPlan` (mirror legacy states; no legacy → all except `keepDisabled`) and `Test-CutoverParity` (missing fails; `EIMDB`/`ServiceBus`/queue differences fail; others warn; names in `automation.retiredSettings` ignored; names only); disable, confirm, wait for the lock duration, then enable
  - `cutover` for `nonprod`: copy named host keys and `D365Data`/`D365Service` function keys, excluding master and system keys and `default` unless `-IncludeDefaultKeys`; key names only in output. No dashboard cutover.
  - `remove`: pure `Test-RemoveAllowed` covering `TF_BUILD`, the prior `-WhatIf` marker, the confirmation prompt, enabled legacy triggers, a legacy name equal to any derived or overridden name in use, the type allow-list (`Microsoft.Web/sites`, `Microsoft.Web/serverfarms`, `Microsoft.Storage/storageAccounts`, `Microsoft.Insights/components`), and a plan that still hosts an app (named in the output; the other entries still proceed)

  Proven by test 4.2.
- [x] 3.8 Add `script/deployment/New-DeploymentPackage.ps1`, a port of Wastequip's (design D15):
  - package name `wtc-eim-deploy-<version>`
  - zips `wilson-automation.zip`, `gateway.zip`, `dashboard-api.zip`, `dashboard-frontend.nonprod.zip`, built with `ZipFile`
  - self-contained single-file `dbmigrator/ARM.Integration.DbMigrator.exe` for win-x64
  - `manifest.json` with `automationQueueAndTimerFunctions` and `gatewayFunctions`
  - `README.txt`
  - the function extraction from `functions.metadata` and the zip helper as pure functions, with the main body guarded so the file can be dot-sourced by 4.4

  Proven by test 4.4 and by the first pipeline Build stage (5.4).
- [x] 3.9 Add the pipeline:
  - `pipelines/eim.yml`: parameters, trigger and pr paths, `lockBehavior: runLatest`, stages `Build → Deploy_shared → Deploy_dev → Deploy_bat → Deploy_test` with `Succeeded` conditions and no deploy on PR
  - `pipelines/templates/build-package.yml`: .NET 10, Node 22, `dotnet test ARM.Integration.Wilson.slnx -c Release` with trx, frontend `npm ci` and `npm run lint`, `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`, `New-DeploymentPackage.ps1 -Environment nonprod,dev,bat,test`, then `Test-DeployWilson.ps1 -PackagePath <package>`, and publish of the `package` artifact only after both checks pass
  - `pipelines/templates/deploy-environment.yml`: preflight+WhatIf, infra, settings, myip/try schema/finally myipremove, code, then verify+health (skipped when nothing was deployed); `env:` mapping of `EIM_DEPLOY_EIMDB`, `EIM_DEPLOY_SERVICEBUS` and `EIM_DEPLOY_CONFIG_OVERRIDES`; variable group `eim-wtc-nonprod`; service connection `sc-eim-wtc-nonprod`

  Proven by test 4.5.
  - Note (Phase 2b): `Deploy_dev` is removed and the Build stage packages `nonprod,bat,test` in 6.1.
- [x] 3.10 Move `.azure-pipelines/workflows/auto-ai-pull-request.yml` → `pipelines/auto-ai-pull-request.yml` and `.azure-pipelines/README.md` → `pipelines/README.md`, rewritten for the new pipeline. Delete:
  - `.azure-pipelines/workflows/deployment/*.yml`
  - `script/deployment/Deploy-Eim.ps1`
  - `script/deployment/Deploy-ZipWithPublishProfile.ps1`

  Update the `/script/` folder entries in `ARM.Integration.Wilson.slnx` (`Deploy-Wilson.ps1`, `New-DeploymentPackage.ps1`, `Test-DeployWilson.ps1`, configs). Proven by test 4.5.
- [x] 3.11 Update the code-side gateway references to the new gateway:
  - `ARM.Integration.Dashboard.API/appsettings.Development.json` (`Gateway:BaseUrl`, `AppName`)
  - `ARM.Integration.D365FSC.SampleClient/appsettings.json`, `GatewayOptions.cs` doc comment and `README.md`

  The gateway test constants stay unchanged. Proven by test 4.6.
- [x] 3.12 Write the deployment docs (design D17):
  - `docs/deployment/deployment.md`: rewrite, including the kept names, the dashboard deployed in place, the remaining Wastequip divergences, and that deployment configurations are not ENVIRONMENT values (`DEV2` has no Function App)
  - `docs/deployment/devops-setup.md`: new
  - `docs/deployment/resource-rename-cutover.md`: new, with the 15-step runbook from D17 (including the dashboard API plan move with its fallback and the plan CPU/memory alert; no Entra step), the old → new table and the kept-name table
  - update `docs/deployment/d365fsc-gateway.md`, `docs/deployment/database-migrations.md` (expand/contract rule, shared-stage migrations, every ENVIRONMENT value including `DEV2`), `docs/deployment/deploy-from-scratch.md` and `docs/deployment/deployment-plan.md` (pointer, or a superseded note)
  - update `docs/azure-deployment-pipelines-setup.md` (pointer), `docs/auto-ai-pull-request-setup.md`, `docs/README.md`, `README.md` and `CLAUDE.md` (deployment section)

  Proven by test 4.6.
  - Note (Phase 2b): every `dev` stage, environment and cutover reference in these docs is replaced in 6.6, which also adds the local development and retire-legacy-dev sections.

## 4. Phase 2: Tests

- [x] 4.1 Add `Wilson.Integration.Tests/DeploymentConfigTests.cs`. It asserts:
  - Each `script/deployment/config/deploy.*.json` parses.
  - `environment` matches the file name, and `scope` is `shared` for `nonprod` and `environment` for the others.
  - `automation.gatedFunctions` equals the reflected function set from 2.2.
  - `automation.retiredSettings` contains `PollSchedule`, and no config has a `pollSchedule` key.
  - `deploy.nonprod.json` has `names.dashboardApi` = `wtc-dashboard-api-dev` and `names.staticWebApp` = `wtc-dashboard-frontend-dev`.
  - No `legacy` name equals any `names` value or derived name in the D7 tables, and no `legacy` section lists a dashboard resource.
  - No config has a `secrets` section or a connection-string-shaped value.
  - Note (Phase 2b): the expected configurations become `base`, `bat`, `nonprod`, `test` and the legacy-name checks read `retiredAutomationApps` in 7.2.
- [x] 4.2 Add `script/deployment/Test-DeployWilson.ps1`: a dependency-free self-test that dot-sources `Deploy-Wilson.ps1`, throws on the first failed assertion and exits non-zero. It asserts:
  - `Get-ResourceNames` for `nonprod`, `dev`, `bat` and `test` equals the D7 tables, and `names` overrides win (the dashboard resolves to `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev`).
  - The storage and Function App length rules reject a long environment name.
  - An unknown configuration name is rejected naming it, and a new `deploy.<name>.json` fixture is accepted without code changes.
  - `Merge-ConfigObject` merges objects and replaces lists and scalars, with the `EIM_DEPLOY_CONFIG_OVERRIDES` layer last.
  - Out-of-scope components are rejected (`schema`/`gateway`/`backend`/`frontend` for `dev`; `automation` for `nonprod`), and `all` expands per scope.
  - Requested components are put in the fixed order.
  - The `infra` plan for `nonprod` creates no dashboard resource when both exist, creates them when absent, and never issues a plan change or delete for them.
  - `Get-GatedSettingsToWrite` writes `true` only when absent.
  - Retired settings: the Automation settings to write exclude `PollSchedule`; the settings to delete are `PollSchedule` only when the app has it; the drift check returns a warning, not a failure, for it.
  - `Get-CutoverPlan` mirrors legacy states, and with no legacy uses `keepDisabled`.
  - `Test-CutoverParity` fails on a missing setting or a queue/`EIMDB`/`ServiceBus` difference, warns on others, ignores `PollSchedule` on the legacy side, and never puts values in the messages.
  - `Test-RemoveAllowed` refuses under `TF_BUILD`, without the WhatIf marker, with legacy triggers enabled, on a legacy name equal to a name in use (e.g. `wtc-dashboard-api-dev`), on a disallowed type (including a static web app), and keeps a plan still in use while naming the remaining app.

  Run: `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`.
  - Note (Phase 2b): the `dev` fixtures (names, scope rejection, infra plan, settings and drift) move to `bat`, and the retired-app guard assertions are added, in 7.1.
- [x] 4.3 Check that `Deploy-Wilson.ps1`, `New-DeploymentPackage.ps1` and `Test-DeployWilson.ps1` parse with no errors (`[System.Management.Automation.Language.Parser]::ParseFile`), included as the first assertion in `Test-DeployWilson.ps1`. Also add an assertion that the script text never passes `EIM_DEPLOY_*` values as `az` arguments: a regex over the script for `--settings \$` or a connection string interpolated into an `az` command line. Run as in 4.2.
- [x] 4.4 Extend `Test-DeployWilson.ps1` for packaging (design D19):
  - Dot-source `New-DeploymentPackage.ps1`. The function extraction over a committed fixture `script/deployment/testdata/functions.metadata` returns exactly the six non-HTTP functions.
  - The zip helper, run on a temp folder that contains `.azurefunctions/`, produces a zip that still contains `.azurefunctions/`.
  - With `-PackagePath <folder>` only: `wilson-automation.zip` contains `.azurefunctions/` and a `functions.metadata` listing exactly the six gated functions; `manifest.json` lists the six; `dbmigrator/ARM.Integration.DbMigrator.exe` exists. Failures name the zip and the missing entry or function.

  Luigi runs only the parameterless form (`pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`). The `-PackagePath` form is proven in the pipeline Build stage (3.9, checked in 5.4).
- [x] 4.5 Add `Wilson.Integration.Tests/PipelineDefinitionTests.cs`, text and YAML assertions over `pipelines/eim.yml` and `pipelines/templates/*.yml`:
  - stage names and the `dependsOn` order
  - DevOps environments `eim-wtc-nonprod/dev/bat/test`
  - Note (Phase 2b): the stage order, environments and Build packaging list drop `dev` in 7.3.
  - `runMigrations` passed true-able only to `Deploy_shared`
  - the Build template runs `Test-DeployWilson.ps1` before packaging and `Test-DeployWilson.ps1 -PackagePath` after `New-DeploymentPackage.ps1`, both before the artifact is published
  - no `ManualValidation`, `PublishProfile` or `Deploy-ZipWithPublishProfile`
  - every `AzureCLI@2` step maps `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` via `env:`
  - `.azure-pipelines/` absent

  Uses `YamlDotNet` if needed, as a test-only package.
- [x] 4.6 Extend `RepositoryLayoutTests` (2.6). Outside `openspec/changes/**`, `docs/deployment/resource-rename-cutover.md` (which lists legacy names on purpose), `script/deployment/config/*.json` (`legacy` sections) and `ARM.Integration.D365FSC.Gateway.Tests/**`, no tracked file references `azf-d365fsc-gateway-wtc-nonprod`, `Deploy-Eim.ps1` or `Deploy-ZipWithPublishProfile.ps1`. The kept names `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` are allowed.
- [x] 4.7 Run the final checks:
  - `dotnet build ARM.Integration.Wilson.slnx`
  - `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`
  - `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`
  - `dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj`
  - `npm --prefix ARM.Integration.Dashboard.Frontend run lint`
  - `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`

  All green before PR 2 is raised.

## Phase 2b: remove dev from deployment (Luigi, same branch, one commit before push). Design D20. Sections 6 and 7 come before the manual section 5 runs; section numbers are kept so existing references stay valid.

## 6. Phase 2b: Implementation

- [x] 6.1 Pipeline, in `pipelines/eim.yml`:
  - Delete the `Deploy_dev` stage.
  - `Deploy_bat`: `dependsOn: Deploy_shared` and `condition: and(eq(dependencies.Deploy_shared.result, 'Succeeded'), ne(variables['Build.Reason'], 'PullRequest'))`. Keep `devopsEnvironment: eim-wtc-bat`, `runMigrations: false` and its components.
  - Build stage: `environments: nonprod,bat,test`.
  - Header comment: environments `eim-wtc-nonprod` (approval + lock), `eim-wtc-bat` and `eim-wtc-test` (approval + lock); no `eim-wtc-dev`.

  In `pipelines/templates/build-package.yml`, drop `dev` from the `environments` parameter comment (and default, if it has one). Proven by test 7.3 (spec `deployment-pipeline`, "No dev stage", "Bat waits for its own approval after shared", "A failed stage stops promotion").
- [x] 6.2 Configuration:
  - `git rm script/deployment/config/deploy.dev.json`, and remove its `<File Path="script/deployment/config/deploy.dev.json" />` entry from `ARM.Integration.Wilson.slnx`.
  - In `script/deployment/config/deploy.nonprod.json`, set `legacy` exactly to the D20 block: add `retiredAutomationApps: ["azf-eim-customer-wtc-dev"]` and the `azf-eim-customer-wtc-dev` (sites, no `removePlan`), `stwtceimcustomerdev` (storage) and `appins-eim-customer-wtc-dev` (Insights) entries before `asp-appservice-p0v3-linux-dev`.
  - Do not change `deploy.base.json`, `deploy.bat.json` or `deploy.test.json`.

  Proven by tests 7.1 and 7.2 (spec `resource-naming-cutover`, "The legacy dev Automation app is retired without a replacement").
- [x] 6.3 `script/deployment/Deploy-Wilson.ps1`, the remove guard:
  - Add the pure function `Get-TriggerGuardApps($Config)`, returning the distinct, non-empty names from `legacy.automationApp` and `legacy.retiredAutomationApps`.
  - `Invoke-RemoveComponent`: for each guard app that exists, read `Get-EffectiveFunctionStates` and pass every enabled function as `<app>/<function>` in `EnabledLegacyFunctions`. A stopped app with enabled settings still counts as enabled.
  - `Test-RemoveAllowed`: keep its parameters; its refusal text becomes "a legacy or retired Automation app still has enabled gated functions: <app>/<fn>, ...; run 'cutover' (legacy) or disable them (retired; see the runbook)".
  - `Get-LegacyNames`: also return `legacy.retiredAutomationApps`.
  - `cutover` and `rollback` do not read `retiredAutomationApps`.

  Proven by test 7.1 (scenarios "Retired app still consuming", "Retired app stopped but triggers enabled", "Retired app disabled", "Retired Insights already absent", "Legacy still consuming").
- [x] 6.4 `script/deployment/Deploy-Wilson.ps1`, verify and text:
  - `Invoke-VerifyComponent`, shared scope: for each name in `legacy.retiredAutomationApps`, `Write-AppReport` labelled "retired Automation (no replacement)" plus `Write-GatedReport`; print "not found" when the app does not exist. Report only, never fails.
  - Help text, `.EXAMPLE` blocks and the unknown-configuration hint use `bat`/`test` instead of `dev`. Keep the firewall rule name `AllowDev-<USERNAME>`.

  Proven by test 7.1 (the verify plan for `nonprod` lists the retired app; parse check; scenario "Dev configuration rejected").
- [x] 6.5 `script/deployment/New-DeploymentPackage.ps1`: default `-Environment` becomes `@('nonprod', 'bat', 'test')`; update the `.EXAMPLE` and the generated `README.txt` text so neither names `dev`. Proven by test 7.1 (AST check of the default) and by the Build stage in 5.4.
- [x] 6.6 Docs, replacing every `dev` stage, DevOps environment, Automation app and cutover reference with `bat`/`test`, and keeping the `DEV`/`DEV2` ENVIRONMENT-value text:
  - `docs/deployment/deployment.md`: stage diagram and stage table without `Deploy_dev`; configurations `nonprod`, `bat`, `test`; the `eimdb_dev` paragraph says it is not a deployment target and points to local development.
  - `docs/deployment/devops-setup.md`: no `eim-wtc-dev` row; delete `eim-wtc-dev` if it was created.
  - `docs/deployment/resource-rename-cutover.md`: provisioning text without `Deploy_dev`; replace the dev cutover with the "Retire legacy dev" step from D20 (disable the six triggers with `az functionapp config appsettings set`, `verify -Environment nonprod`, check `dev-*` queue depth with `az servicebus queue show --query countDetails`, leave the app until `remove -Environment nonprod`); bat then test cutover; decommission order `bat`, `test`, `nonprod`; old → new table marks the dev row "retired, no replacement".
  - `docs/deployment/deploy-from-scratch.md`: configurations and stages without `dev`; `eimdb_dev` wording as above.
  - `docs/deployment/database-migrations.md`: shared-stage text without `dev`; new "Local database" section (local SQL Server or LocalDB, `CREATE DATABASE eimdb`, `EIMDB_MIGRATOR_CONNECTION=Server=localhost;Database=eimdb;Integrated Security=true;TrustServerCertificate=true`, `dotnet run --project ARM.Integration.DbMigrator`, optional `--whatif`; only merged migrations reach the shared `eimdb`).
  - `pipelines/README.md`: stage list without `Deploy_dev`.
  - `README.md`: new "Local development" section (local SQL `eimdb` via the migrator, Azurite, settings from the samples, a local `ENVIRONMENTTABLE` row never committed, never consume `bat-*`/`test-*` queues, never point a queue-consuming local host at the shared `eimdb`, `dev-*` queues as the default local target) and remove `dev` from the deployment summary.
  - `CLAUDE.md`: the Deployment bullet's order becomes `Build → Deploy_shared → Deploy_bat → Deploy_test`.

  Proven by test 7.5 and review.
- [x] 6.7 Local settings samples: in `Wilson.Integration.Automation/local.settings.sample.json` and `ARM.Integration.D365FSC.Gateway/local.settings.sample.json`, change the `EIMDB` placeholder to `<local SQL Server eimdb connection string>`. No key is added or removed; `AzureWebJobsStorage` stays `UseDevelopmentStorage=true`. Proven by test 7.4 and by `SecretScanTests` (2.7).

## 7. Phase 2b: Tests

- [x] 7.1 Update `script/deployment/Test-DeployWilson.ps1`:
  - Move every `dev` fixture to `bat`: the `Get-ResourceNames` expectations, the scope rejection of `schema`/`gateway`/`backend`/`frontend`, the environment-scope `infra` plan (`azf-eim-automation-wtc-bat`), the Automation settings and the drift fixtures. The legacy-dev entries in the `Test-RemoveAllowed` fixtures may stay, as they are now `nonprod` legacy entries.
  - New assertions:
    - the configuration names are exactly `bat`, `nonprod`, `test`, and `Get-DeployConfig 'dev'` throws naming `dev`
    - `Get-ResourceNames` for every configuration never yields `azf-eim-automation-wtc-dev`, `stwtceimautodev` or `appins-eim-automation-wtc-dev`
    - `Get-TriggerGuardApps` returns exactly `azf-eim-customer-wtc-dev` for `nonprod` (it has no `legacy.automationApp`, and the legacy gateway is not a guard app), exactly `azf-eim-customer-wtc-bat` for `bat`, and no duplicates or blanks for a fixture that lists the same app in both keys and an empty string
    - `Test-RemoveAllowed` with `EnabledLegacyFunctions = @('azf-eim-customer-wtc-dev/QueueCaptureCustomer')` refuses, and the message contains `azf-eim-customer-wtc-dev/QueueCaptureCustomer` and `retired`
    - `Get-LegacyNames` for `nonprod` includes `azf-eim-customer-wtc-dev`, `stwtceimcustomerdev` and `appins-eim-customer-wtc-dev`
    - the `New-DeploymentPackage.ps1` `-Environment` default, read from its AST, is `nonprod`, `bat`, `test`

  Run: `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`.
- [x] 7.2 Update `Wilson.Integration.Tests/DeploymentConfigTests.cs`:
  - `EnvironmentConfigurations` = `bat`, `test`; the expected file set = `base`, `bat`, `nonprod`, `test`.
  - `LegacyNames` also reads `legacy.retiredAutomationApps`.
  - New: every `retiredAutomationApps` entry is also a `Microsoft.Web/sites` entry in the same `legacy.resources`; `deploy.nonprod.json` lists `azf-eim-customer-wtc-dev` as retired; no config file has `environment: dev`.
- [x] 7.3 Update `Wilson.Integration.Tests/PipelineDefinitionTests.cs`:
  - `StageOrder` = `Build`, `Deploy_shared`, `Deploy_bat`, `Deploy_test`; `Deploy_bat` depends on `Deploy_shared` with a `Succeeded` condition.
  - Remove the `Deploy_dev` `InlineData` and the `Deploy_dev` entry in the migrations loop.
  - The Build stage contains `environments: nonprod,bat,test`.
  - New: no file under `pipelines/` contains `Deploy_dev` or `eim-wtc-dev`.
- [x] 7.4 Add a sample-settings test, in `Wilson.Integration.Tests/RequiredSettingsTests.cs` or a new `LocalSettingsSampleTests.cs`: for both samples, `AzureWebJobsStorage` is `UseDevelopmentStorage=true`, `EIMDB` is a `<...>` placeholder containing `local`, and no value contains `azql-wtc-sandbox` or starts with `bat-` or `test-` (spec `integration-host-layout`, "Samples target local services").
- [x] 7.5 Extend `Wilson.Integration.Tests/RepositoryLayoutTests.cs`: outside `openspec/changes/**`, `Wilson.Integration.Tests/**` and `script/deployment/Test-DeployWilson.ps1`, no tracked file contains `Deploy_dev`, `eim-wtc-dev`, `deploy.dev.json`, `azf-eim-automation-wtc-dev`, `stwtceimautodev` or `appins-eim-automation-wtc-dev` (spec `resource-naming-cutover`, "No dev Automation resources").
- [x] 7.6 Run the final checks, all green before the branch is pushed:
  - `dotnet build ARM.Integration.Wilson.slnx`
  - `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`
  - `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`
  - `dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj`
  - `npm --prefix ARM.Integration.Dashboard.Frontend run lint`
  - `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`

## Phase 3: deploy and verify (MANUAL, the user's runbook; not Luigi's). Follow `docs/deployment/resource-rename-cutover.md`.

## 5. Deploy and verify (manual)

- [ ] 5.1 MANUAL: before merging PR 2, tag the last pre-Phase-2 commit on `main` as `pre-5246-deploy`. Finish or run any remaining #5425 deploy tasks with the old pipelines if wanted.
- [ ] 5.2 MANUAL: complete the Azure DevOps setup from `docs/deployment/devops-setup.md`:
  - environments `eim-wtc-nonprod`, `eim-wtc-bat` and `eim-wtc-test`, each with approval + exclusive lock. Do not create `eim-wtc-dev`; if it already exists, delete it (Phase 2b, D20).
  - service connection `sc-eim-wtc-nonprod`: workload identity, Contributor on `d365fsc-integration-nonprod` (this also covers the `azql-wtc-sandbox` firewall, which is in the same resource group)
  - variable group `eim-wtc-nonprod` with secret `EIM_DEPLOY_EIMDB`, secret `EIM_DEPLOY_SERVICEBUS`, and `EIM_DEPLOY_CONFIG_OVERRIDES`
  - create the pipeline from `pipelines/eim.yml`
  - repoint the auto-AI PR pipeline to `pipelines/auto-ai-pull-request.yml`
- [ ] 5.3 MANUAL: commit the Entra tenant/client/audience values to `deploy.base.json` (the Build stage has no variable group, so only committed values reach the SPA build). Supply the subscription and `ExcludedUsers` by a commit or through `EIM_DEPLOY_CONFIG_OVERRIDES`. Check that preflight passes in every stage with a manual run that has `deployCode` cleared and nothing else selected (verify and health are skipped on such a run). That run checks the subscription, names, resource group and gated functions only; confirm the Entra values from the absence of an "entra is incomplete" warning in the Build log, and `excludedUsers` and the secrets in the 5.4 preview step.
- [ ] 5.4 MANUAL: start a manual run with `deployInfra` and `applySettings` selected, `deployCode` on and `runMigrations` off, and approve each stage. Check:
  - the Build stage ran `Test-DeployWilson.ps1` and `Test-DeployWilson.ps1 -PackagePath` green, and the package contains `deploy.{base,nonprod,bat,test}.json` and no `deploy.dev.json`
  - the stages were `Deploy_shared`, `Deploy_bat`, `Deploy_test` (no `Deploy_dev`), each waiting for its approval
  - the new plan, Log Analytics workspace, gateway and the `bat` and `test` Automation apps exist, with the Automation triggers disabled; no `azf-eim-automation-wtc-dev`, `stwtceimautodev` or `appins-eim-automation-wtc-dev` exists
  - no new dashboard resource was created; `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` received code and settings in place, and the dashboard opens and signs in at its existing URL
  - `PollSchedule` is absent from the new Automation apps
- [ ] 5.5 MANUAL: run `verify` and note the outbound IPs. Add SQL firewall rules on `azql-wtc-sandbox` unless Azure services are already allowed. Rerun `health`.
- [ ] 5.6 MANUAL: query `ENVIRONMENTTABLE` and `CONFIGURATIONTABLE` across all rows (every ENVIRONMENT value, including `DEV2`) for the old host names, as given in the runbook, and update any rows found.
- [ ] 5.7 MANUAL: retire legacy dev (runbook "Retire legacy dev"; D20). There is no dev cutover and no new dev app.
  - Before: record the active and dead-letter counts of the five `dev-*` queues (`az servicebus queue show --query countDetails`).
  - Disable the six gated functions on `azf-eim-customer-wtc-dev` (`AzureWebJobs.<fn>.Disabled=true` app settings; stopping the app is not enough for `remove`).
  - Run `Deploy-Wilson.ps1 -Environment nonprod -Component verify` and check that the retired app shows every gated function disabled.
  - Drain what is left on the `dev-*` queues with a local host (task 5.14), or record that losing it is accepted. Do not delete the queues or any `DEV`/`DEV2` row.
  - Set the dashboard Simulation `DEV` row inactive in the dashboard's configuration screen (resolved question 3). Capture dev and the D365 dev endpoints may keep publishing to `dev-*` (resolved question 2); if the queue depth keeps growing, ask their owners to stop.
- [ ] 5.8 MANUAL: run `Deploy-Wilson.ps1 -Environment bat -Component cutover`. Verify that the queues drain on the new app, the dashboard shows new rows, and there are no failures in `appins-eim-automation-wtc-bat`. Observe for one day; run `rollback` if needed. Then repeat for `test` with the same verification.
- [ ] 5.9 MANUAL: run `Deploy-Wilson.ps1 -Environment nonprod -Component cutover` to copy the gateway keys, then:
  - Notify the gateway consumers of the new base URL `https://azf-eim-gateway-wtc-nonprod.azurewebsites.net`.
  - Update the `GATEWAYCONSUMER.GATEWAYURL` rows as consumers confirm the switch.
  - Update the App Insights queries and alerts to the new resources and the `Wilson.Integration.Automation` logger categories.
  - Add a plan CPU and memory alert above 80% on `asp-eim-wtc-nonprod` (move to P1v3 if it fires).
- [ ] 5.10 MANUAL: disable the three old pipeline definitions after the first successful new run.
- [ ] 5.11 MANUAL: after the Automation and gateway cutovers, move `wtc-dashboard-api-dev` to `asp-eim-wtc-nonprod` as the runbook describes (`az webapp update --set serverFarmId=<plan id>`; one restart). If Azure refuses the move, keep it on `asp-appservice-p0v3-linux-dev` and scale that plan down. Afterwards run `verify`, re-check the SQL firewall for the API's outbound IPs, run `health`, and check dashboard sign-in and reprocessing.
- [ ] 5.12 MANUAL: after the observation window (suggested 7 days with no requests to the legacy gateway), decommission from an operator machine:
  - Run `remove -WhatIf`, then `remove`, for `bat`, `test` and `nonprod`. The `nonprod` run also deletes the retired legacy dev resources (`azf-eim-customer-wtc-dev`, `stwtceimcustomerdev`, and `appins-eim-customer-wtc-dev` if it exists); it refuses if any of the retired app's gated functions was re-enabled. Confirm the old plan was deleted, or was kept (and named) because the dashboard API is still on it.
  - Delete the old pipeline definitions.
- [ ] 5.13 MANUAL: follow-up commit, either by Luigi on request or by the user. Empty `gateway.legacyVerificationTargets` and the `legacy` sections in the config (including `legacy.retiredAutomationApps` in `deploy.nonprod.json`), remove the legacy-name exceptions from 4.6, and archive this change with `/opsx:archive`.
- [ ] 5.14 MANUAL (each developer): set up local development as `README.md` "Local development" describes. Create `eimdb` on a local SQL Server (`CREATE DATABASE eimdb`), run `ARM.Integration.DbMigrator` against it, add a local `ENVIRONMENTTABLE` row (never committed), start Azurite, and copy the samples to `local.settings.json`. Point a local host only at the `dev-*` queues (resolved question 1), never at `bat-*`/`test-*` and never, while consuming queues, at the shared `eimdb`. `eimdb_dev` is dropped later by hand, outside this change (resolved question 4).
