## Context

Ticket **#5246**. The reference is Wastequip EIM (`C:\Users\Handoyo.Dharmazi\source\repos\ARM.Integration.Wastequip\EIM`), forked from Wilson. Its patterns are reused deliberately so the two repos stay mergeable:
- `Wastequip.Integration.Automation`, with `Functions/`, `Inbound/` and `Outbound/`
- `Wastequip.Integration.Orchestrations/Configuration/RequiredSettings.cs`
- `script/deployment/Deploy-Wastequip.ps1` and `New-DeploymentPackage.ps1`
- `pipelines/eim.yml` and `pipelines/templates/*.yml`

### Current state (Wilson)

**Automation host.** `ARM.Integration.EIM.Customer/` is a flat folder. The namespace `ARM.Integration.EIM.Customer` sits inside the engine's namespace tree. It contains:

| File | Function name (`nameof`) | Trigger |
|---|---|---|
| `QueueCaptureCustomer.cs` (+ static `QueueCaptureHandler.cs`) | `QueueCaptureCustomer` | SB `%CaptureToD365Queue%` |
| `QueueDataEventsHandler.cs` (285 lines; also holds `EntityRoutes`, `ResolveSourceId`) | `QueueDataEventsHandler` | SB `%D365FscDataEventQueue%` |
| `QueueMessageQueueBusinessEventHandler.cs` (269 lines) | `QueueMessageQueueBusinessEventHandler` | SB `%D365BusinessEventQueue%` |
| `AwaitingD365SweepHandler.cs` | `AwaitingD365SweepHandler` | timer `%AwaitingD365SweepSchedule%` |
| `ReleaseSweepHandler.cs` | `ReleaseSweepHandler` | timer `%ReleaseSweepSchedule%` |
| `RetrySweepHandler.cs` | `RetrySweepHandler` | timer `%RetrySweepSchedule%` |
| `PollSourceHandler.cs` | none: the whole `[Function]` method is commented out (a template) | — |

- Six functions are live. All SB triggers use `Connection = "ServiceBus"`.
- `PollSourceHandler` is an inactive template. Its class body is entirely commented out; nothing constructs it, and it has no DI registration (Functions classes are discovered by the source generator, which sees no `[Function]`). Its only references are:
  - a doc comment in `Wilson.Integration.Orchestrations/Orchestrations/SamplePoll_To_Target.cs`, itself an unregistered sample (not in `OrchestrationRegistry.CreateDefault()`)
  - a comment in the applied migration `006_seed_routetable.sql` (never edited)
  - archived OpenSpec changes (never edited)
  - the `PollSchedule` app setting, written by `Deploy-Eim.ps1` from `function.pollSchedule` in `deploy.{dev,bat,test}.json` and read by nothing live

  `EIMWatermark` (engine) and `WATERMARKTABLE` (baseline schema) were used only by the commented-out code. They stay, because they are engine code and schema shared with Wastequip. This change adds no schema change.
- `host.json` sets SB `maxConcurrentCalls: 1`, `prefetchCount: 0` and `autoCompleteMessages: false`. That makes each app a single sequential consumer per queue, which is the ordering guarantee the cutover must preserve.

**Csproj leftovers:**
- Docker properties and the `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` package
- `UserSecretsId`
- `InternalsVisibleTo ARM.Integration.EIM.Tests`
- `launchSettings.json`: `--port 7225` and a Docker profile, while `run.bat` and the README say 7071
- tracked `Properties/ServiceDependencies/**` and `Properties/serviceDependencies*.json`

**Tests.** `ARM.Integration.EIM.Tests` references EIM, Orchestrations and EIM.Customer.
- Client-dependent tests: `BusinessKeyRegistrationTests`, `CustomerAddressDeleteToCaptureTests`, `CustomerAddressSourceIdTests`, `D365UpdatePayloadBuilderTests`, `PartyContactToCaptureTests` and `EngineBoundaryTests`.
- Fixtures: `TestData/CustomerAddressDeleted.OnExternalUpdated.json`, `TestData/SourceEntities/*.json`, and the linked `script/SQL/Migrations/014_seed_customer_address_delete_route.sql`.
- Everything else is engine-only: BusinessEventReconcile, FailureClassifier, FailureOutcomeSelector, HoldGate, IngestGate, ReleaseSweep, RetryOptionsFactory, RetryScheduleCalculator and RouteResolver.

**Gateway.** `ARM.Integration.D365FSC.Gateway` provides `D365Data` and `D365Service` (HTTP, `AuthorizationLevel.Function`) and `IdempotencyPurge` (timer).
- It runs as the only app on Flex Consumption: `azf-d365fsc-gateway-wtc-nonprod`, with storage `stwtcd365gwnonprod` and Insights `appins-d365fsc-gateway-wtc-nonprod`.
- `GatewayOptions` is resolved lazily from DI.

**Deployment today:**
- `script/deployment/Deploy-Eim.ps1` with `config/deploy.{dev,bat,test}.json`. It has the `hostsGateway`/`hostsDashboard` flags and rebuilds from source on the operator's machine.
- Three YAML pipelines under `.azure-pipelines/workflows/deployment/` authenticate with publish profiles through `Deploy-ZipWithPublishProfile.ps1`.
- The Dashboard API (`wtc-dashboard-api-dev`) and SWA (`wtc-dashboard-frontend-dev`) serve every nonprod environment, but are named `-dev`.
- The plan `asp-appservice-p0v3-linux-dev` hosts the three Automation apps **and the dashboard API** (archived change `2026-08-28-consolidate-deployment-scripts`, design: "`asp-appservice-p0v3-linux-dev` carries the API alongside both Function Apps").

**Data.** One shared `eimdb` on `azql-wtc-sandbox` holds rows partitioned by an ENVIRONMENT column; `ENVIRONMENTTABLE` holds the D365 connection per environment. One Service Bus namespace `asb-eim-wtc-nonprod` holds the queues `{env}-d365fsc-dataevent`, `-d365fsc-businessevents`, `-d365fsc-to-capture`, `-capture-to-d365fsc` and `-capture-to-d365fsc-response`.

**Deployment configurations are not ENVIRONMENT values.** The database holds rows for the ENVIRONMENT values `DEV`, `DEV2`, `BAT` and `TEST`; route seeds such as migration 014 insert a row for each of the four, and the gateway serves any slug in `ENVIRONMENTTABLE` (the sample client uses `DEV2`). `DEV2` has no Function App, no queue set and no deployment configuration. This change therefore keeps two separate sets and never derives one from the other:
- the deployment configurations `nonprod`, `dev`, `bat` and `test`, which name Azure resources and pipeline stages
- the ENVIRONMENT values in `eimdb`, which the script never enumerates, validates or filters

Nothing in the script, configuration or tests assumes three (or four) ENVIRONMENT values. Anything that affects the database (migrations, a future SQL move) affects all ENVIRONMENT values at once, `DEV2` included.

### Constraints

- Function names must not change. Capture, D365 and operators refer to them, and `AzureWebJobs.<fn>.Disabled` gating relies on them.
- Never commit secrets. Decommissioning is manual and never done by the pipeline.
- Environments are dev → bat → test. There is no prod stage, no managed identity for EIMDB, and no Key Vault references.
- Do not edit archived changes or the active change `fix-address-source-business-key` (#5425).

## Goals / Non-Goals

**Goals:**
- **Phase 1:** the Wastequip-shaped host and test layout with identical runtime behaviour.
- **Phase 2:**
  - one build-once pipeline, `Build → Deploy_shared → Deploy_dev → Deploy_bat → Deploy_test`, driven by one script and layered configuration
  - the Automation apps, the gateway and their storage and Insights recreated under the naming pattern
  - the dashboard API and SWA deployed in place under their current names
  - the gateway on the shared App Service plan
  - a side-by-side cutover that keeps exactly one consumer per queue at any time, with rollback and a manual decommission

**Non-Goals:** see `proposal.md`. In short:
- No renaming of SQL, Service Bus, Key Vault, the resource group, the dashboard API or the dashboard SWA in this change.
- No managed identity, no Key Vault references, no prod.
- No behaviour, live function-name or queue changes. The only setting change is retiring the unused `PollSchedule` (D18).
- No SQL migrations.

## Decisions

### D1. Rename only the Automation host, to `Wilson.Integration.Automation`

The folder, csproj, `RootNamespace`/`AssemblyName` and namespace become `Wilson.Integration.Automation`, and `git mv` keeps the history. The launch profile is renamed `Wilson.Integration.Automation` with `--port 7071`.

The `ARM.Integration.*` projects keep their names, because they are shared with Wastequip and renaming them would break mergeability.

- **Why not keep `EIM.Customer`:** the name says "customer" but the host processes customers, contacts, addresses and business events. It also sits in the engine namespace, which is exactly what `EngineBoundaryTests` tries to prevent.
- **Function names stay** because they come from `nameof(<class>)` and the class names are kept. A new pin test (D4) fails if anyone renames a trigger class.
- **Consequences:**
  - Logger categories change from `ARM.Integration.EIM.Customer.*` to `Wilson.Integration.Automation.*`. Any App Insights query or alert that filters on the category must be updated; this is a risk with a runbook note.
  - The old namespace resolved `ARM.Integration.EIM.*` types implicitly as parent namespaces. The new one needs explicit usings, so add a `GlobalUsings.cs` in the Automation project with `global using ARM.Integration.EIM;` and the other engine namespaces it actually uses.
  - The publish output's assembly name changes. That is harmless, because the function metadata is regenerated.

**Alternative rejected: `Wilson.Integration.Functions`.** Wastequip uses `.Automation` for the SB and timer host, and the deployment naming (`azf-eim-automation-*`) should match the project.

### D2. Layout: thin triggers in `Functions/`, processing in `Inbound/` and `Outbound/`

The direction follows Wastequip's convention: Inbound is into D365, Outbound is out of D365.

| New file | Contents | From |
|---|---|---|
| `Functions/QueueCaptureCustomer.cs` | trigger only; calls `CaptureMessageProcessor` | `QueueCaptureCustomer.cs` |
| `Inbound/CaptureMessageProcessor.cs` | the body of the static `QueueCaptureHandler.ProcessAsync` (same signature, same `responseQueueSetting`) | `QueueCaptureHandler.cs` |
| `Functions/QueueMessageQueueBusinessEventHandler.cs` | trigger only; calls `BusinessEventReconciler` | split |
| `Inbound/BusinessEventReconciler.cs` | reconciliation of `SysMessageProcessorMessageProcessedBusinessEvent` | split |
| `Functions/QueueDataEventsHandler.cs` | trigger only, same constructor signature; calls `DataEventProcessor` | split |
| `Outbound/DataEventProcessor.cs` | `EntityRoutes`, `EntityRequiredAnyFields`, `ResolveSourceId`, `ExcludedUsers`/`DefaultEnvironment` handling, processing | split |
| `Functions/AwaitingD365SweepHandler.cs`, `ReleaseSweepHandler.cs`, `RetrySweepHandler.cs` | moved unchanged (they already delegate to the engine) | moved |
| `PollSourceHandler.cs` | **deleted** (D18) | `PollSourceHandler.cs` |
| `Program.cs`, `host.json`, `GlobalUsings.cs` | root | |

Rules:
- Every trigger class keeps its class name, `[Function(nameof(...))]`, binding expressions, `Connection` and `MessageActions` settlement semantics.
- A trigger class only binds, creates a logger scope and calls one processor method.
- Processors are plain classes. They are `internal` where the tests need them, with `InternalsVisibleTo Wilson.Integration.Tests`. Register them in DI only when they need DI; otherwise construct them in the trigger, matching what exists today.
- The `QueueDataEventsHandler` constructor `(ILoggerFactory, IReadOnlyDictionary<string, IOrchestration>, IngestGate, IFailureClassifier, RetryScheduleCalculator)` is unchanged, so `CustomerAddressDeleteToCaptureTests` keeps constructing it the same way.
- Tests that use `QueueDataEventsHandler.EntityRoutes` / `ResolveSourceId` switch to `DataEventProcessor.EntityRoutes` / `ResolveSourceId`. The member signatures are unchanged.

**Alternative rejected: move processing into `Wilson.Integration.Orchestrations`.** That library is also loaded by the Dashboard API for reprocessing, and the processors depend on Functions types (`ServiceBusReceivedMessage`, `ServiceBusMessageActions`). Wastequip keeps them in the host.

### D3. Tests: add `Wilson.Integration.Tests`, make `ARM.Integration.EIM.Tests` engine-only

**`Wilson.Integration.Tests`** (xUnit; same package versions as `ARM.Integration.EIM.Tests`: xunit 2.9.2, Test.Sdk 17.12.0, plus `Microsoft.Extensions.TimeProvider.Testing` 9.0.0 if a moved test needs it).
- References `Wilson.Integration.Automation`, `Wilson.Integration.Orchestrations`, `ARM.Integration.EIM` and `ARM.Integration.Core`.
- Moved tests: the six client-dependent tests listed in Context. Namespaces change to `Wilson.Integration.Tests`.
- Moved fixtures: `TestData/CustomerAddressDeleted.OnExternalUpdated.json` and `TestData/SourceEntities/*.json`, plus the linked `014` migration (same `Link` path).
- Also moved: `RecordingMessageActions` and any other helper used only by the moved tests. A helper used by both projects is duplicated rather than shared; it is test code and a shared test-utilities project is not worth it.

**`ARM.Integration.EIM.Tests`:**
- Drops its references to Orchestrations and Automation; keeps EIM and Core.
- Keeps its `TestData` used by the engine tests (the `SysMessageProcessor` fixtures).
- `ARM.Integration.EIM.csproj` keeps `InternalsVisibleTo ARM.Integration.EIM.Tests`. It adds `InternalsVisibleTo Wilson.Integration.Tests` only if a moved test needs engine internals; Luigi checks at compile time.

**`EngineBoundaryTests` moves with its assertions unchanged.** It asserts that:
- the engine does not reference the client
- the client references the engine
- the engine has no concrete orchestration
- every registered orchestration is client-owned
- registry keys equal type names

It sits in the client test project because it needs both assemblies. That matches Wastequip.

**Alternative rejected: keep a single test project.** An engine test project that references the client cannot prove the engine is client-free, and it makes merges from Wastequip conflict.

### D4. New tests that guard the refactor

1. **`AutomationFunctionNamesTests`.** Reflects over the public methods of `Wilson.Integration.Automation` that carry `Microsoft.Azure.Functions.Worker.FunctionAttribute`. It asserts the set of names equals exactly `{AwaitingD365SweepHandler, QueueCaptureCustomer, QueueDataEventsHandler, QueueMessageQueueBusinessEventHandler, ReleaseSweepHandler, RetrySweepHandler}`. It also asserts each SB trigger's queue expression and `Connection = "ServiceBus"`, and each timer's schedule expression, are unchanged, and that no type named `PollSourceHandler` exists in the assembly (D18).
   - The set stays at **six**. Deleting `PollSourceHandler` does not change it, because that class never registered a function.
   - The same list is the gated set in deployment config (D12). The test's failure message says to update `automation.gatedFunctions` in `deploy.base.json` when a function is added.
2. **`RequiredSettingsTests`.** `GetState` returns Missing, Present and UnresolvedKeyVaultReference. `EnsurePresent` throws `InvalidOperationException` naming every missing key and never a value. `Automation` groups contain the expected keys. Every required key is in `local.settings.sample.json`, and no retired setting (`PollSchedule`, `PollSource*`) is.
3. **`SecretScanTests`.** A port of Wastequip's, with:
   - root found by locating `ARM.Integration.Wilson.slnx`
   - extensions `.cs .json .ts .tsx .bat .ps1 .sql .md .yml .yaml .config .env`
   - skipped directories `bin obj node_modules dist .git .vs .azurite TestResults artifacts`; Wilson has gitignored `artifacts/` folders locally
   - `local.settings.*.json` skipped except `local.settings.sample.json`
   - patterns: Entra client secret shape, `SharedAccessKey=<non-placeholder>`, `AccountKey=<non-placeholder>`, `Password=`/`Pwd=` with a non-placeholder value
   - `archive` changes under `openspec/changes/archive` are in scope; if a historical file trips it, the fix is an allow-list entry with a comment, not editing the archive
4. **Gateway: `GatewayStartupTests`** (in `ARM.Integration.D365FSC.Gateway.Tests`). Building options from a configuration missing `EIMDB` or `Gateway:BaseUrl` throws at the eager build call, naming the key.

### D5. Fail-fast settings

**Automation**
- `Wilson.Integration.Orchestrations/Configuration/RequiredSettings.cs` ports Wastequip's `RequiredSettings`: `SettingState`, `GetState`, `Inspect`, and `EnsurePresent(IConfiguration, params IEnumerable<string>[] keyGroups)`.
- It adds a `Microsoft.Extensions.Configuration.Abstractions` package reference (10.0.x) to the Orchestrations csproj. That is harmless for the Dashboard API, which already has it transitively.
- Placement mirrors Wastequip, so a later Wilson API host can share it.
- Groups:
  - `Database = ["EIMDB"]`
  - `ServiceBus = ["ServiceBus"]`
  - `Queues = ["D365FscDataEventQueue", "D365BusinessEventQueue", "CaptureToD365Queue", "CaptureToD365QueueResponse", "D365ToCaptureQueue"]`
  - `Schedules = ["AwaitingD365SweepSchedule", "ReleaseSweepSchedule", "RetrySweepSchedule"]`
- `PollSchedule` and the `PollSource*` settings are not required; they are retired along with `PollSourceHandler` (D18). `ExcludedUsers` and `DefaultEnvironment` keep today's handling.
- `Program.cs` calls `RequiredSettings.EnsurePresent(builder.Configuration, Database, ServiceBus, Queues, Schedules)` before registering services, and reads `EIMDB` from `builder.Configuration` (same value).
- **Compatibility:** `Deploy-Eim.ps1` already sets all of these on every Automation app, so the deployed apps keep starting.

**Gateway.** `Program.cs` builds `var gatewayOptions = GatewayOptions.FromConfiguration(builder.Configuration);` before `Build()` and registers the instance. A missing `EIMDB` or `Gateway:BaseUrl` then stops startup with the existing message, instead of failing on the first request. There is no `RequiredSettings` dependency, because the gateway references only Core.

**Alternative rejected: validating inside each trigger.** A missing SB connection makes the host restart-loop on listener failures without naming the setting.

### D6. Local settings and the leftover cleanup

**Local settings**
- `Wilson.Integration.Automation/local.settings.sample.json` contains:
  - every key from D5, with placeholders `<...>` or local defaults: `UseDevelopmentStorage=true`, queue names `<your queue prefix>-d365fsc-dataevent` and so on (a developer points them at queues they own; no `dev2-` queues are implied), schedules
  - `FUNCTIONS_WORKER_RUNTIME`
  - `RetryMaxAttempts` and the other optional keys
  - `ExcludedUsers` and `DefaultEnvironment`
  - no `PollSchedule` or `PollSource*` keys (D18)
- `ARM.Integration.D365FSC.Gateway/local.settings.sample.json` contains `EIMDB`, `Gateway:BaseUrl` (`http://localhost:7072`), the optional keys, and `Host.LocalHttpPort: 7072`.
- Both csprojs get the Wastequip `LocalSettingsFile` property and the `None Include … Link="local.settings.json"` item, so `dotnet build -p:LocalSettings=mydev` copies `local.settings.mydev.json` as `local.settings.json` into the output.
  - `CopyToPublishDirectory Never` is kept.
  - `.gitignore` keeps excluding `local.settings.json` and adds `local.settings.*.json` with `!local.settings.sample.json`.

**Leftovers to remove**
- `.dockerignore` and the Docker csproj properties and package
- the `Container (Dockerfile)` launch profile
- `UserSecretsId`
- `Properties/ServiceDependencies/**` and `Properties/serviceDependencies*.json`
- `.appmod/`
- the `CRMSolutionProperties` block in `ARM.Integration.Wilson.slnx`

**Ports:** Automation `--port 7071`. The gateway stays on 7072, pinned through `Host:LocalHttpPort`.

### D7. Naming pattern and the old → new mapping

**Pattern:** `{type}-{workload}-{component}-{client}-{suffix}`.
- workload `eim`, client `wtc`
- suffix `<env>` (`dev`, `bat`, `test`) for per-environment resources and `nonprod` for shared ones
- `component` is omitted for resources that belong to the whole workload: plan, Log Analytics, and the would-be SQL, SB, KV and RG names. This follows Wastequip's `sql-eim-$k-$e`.
- **Storage** cannot contain hyphens and is limited to 24 characters, so it uses Wastequip's form `st{client}{workload}{shortcomponent}{suffix}`.

**Validation in `Get-ResourceNames`:**
- storage `^[a-z0-9]{3,24}$`
- Function App names ≤ 32 characters, because longer names are truncated into the host ID and collide

**Overrides:** every derived name can be overridden in `names` (Wastequip `$pick`).

**Per environment** (`{env}` ∈ dev, bat, test: the deployment configurations, not the ENVIRONMENT values in `eimdb`; `DEV2` has no Function App and so no row here):

| Resource | Old | New | Action |
|---|---|---|---|
| Automation Function App | `azf-eim-customer-wtc-{env}` | `azf-eim-automation-wtc-{env}` | recreate, then cutover |
| Automation storage | `stwtceimcustomer{env}` | `stwtceimauto{env}` (max 16 characters) | recreate |
| Automation Insights | `appins-eim-customer-wtc-{env}` | `appins-eim-automation-wtc-{env}` (workspace-based) | recreate |
| Service Bus queues | `{env}-d365fsc-dataevent`, `{env}-d365fsc-businessevents`, `{env}-d365fsc-to-capture`, `{env}-capture-to-d365fsc`, `{env}-capture-to-d365fsc-response` | unchanged | keep |

**Shared** (`nonprod`):

| Resource | Old | New | Action |
|---|---|---|---|
| App Service plan (P0v3 Linux) | `asp-appservice-p0v3-linux-dev` | `asp-eim-wtc-nonprod` | recreate |
| Log Analytics workspace | none | `log-eim-wtc-nonprod` | new |
| Gateway Function App | `azf-d365fsc-gateway-wtc-nonprod` (Flex) | `azf-eim-gateway-wtc-nonprod` (on `asp-eim-wtc-nonprod`, Always On) | recreate, then cutover |
| Gateway storage | `stwtcd365gwnonprod` | `stwtceimgwnonprod` | recreate |
| Gateway Insights | `appins-d365fsc-gateway-wtc-nonprod` | `appins-eim-gateway-wtc-nonprod` | recreate |
| Flex plan of the old gateway | auto-created, read from the app's `serverFarmId` | none | delete at decommission |
| Old App Service plan | `asp-appservice-p0v3-linux-dev` | none (replaced by `asp-eim-wtc-nonprod`) | delete at decommission, once empty |

**Kept, through `names` overrides** (D8; all approved as deferred):

| Resource | Current (kept) | Pattern name if renamed later |
|---|---|---|
| Resource group | `d365fsc-integration-nonprod` | `rg-eim-wtc-nonprod` |
| Service Bus namespace | `asb-eim-wtc-nonprod` | `sbns-eim-wtc-nonprod` |
| Key Vault | `kv-d365fsc-wtc-nonprod` | `kv-eim-wtc-nonprod` |
| SQL server | `azql-wtc-sandbox` | `sql-eim-wtc-nonprod` |
| Database | `eimdb` (plus `eimdb_dev` for local development) | `sqldb-eim-wtc-nonprod` |
| Dashboard API (App Service) | `wtc-dashboard-api-dev` | `app-eim-dashboard-api-wtc-nonprod` |
| Dashboard SWA | `wtc-dashboard-frontend-dev` | `swa-eim-dashboard-wtc-nonprod` |
| Region | `centralus` | — |

**Dashboard deployed in place.** The dashboard API and SWA keep their names, URLs, Entra redirect URI and linked backend. The script deploys code and settings to them in place: there is no new dashboard resource, no side-by-side cutover, no new SWA URL and no Entra change. The overrides `names.dashboardApi` and `names.staticWebApp` live in `deploy.nonprod.json`. Neither name is in any `legacy` section, so `remove` can never target them. The only hosting change is that the API moves from the old plan to `asp-eim-wtc-nonprod`, as a manual runbook step, so the old plan can be decommissioned (D8, D13).

**Gateway name: `azf-eim-gateway-wtc-nonprod` (approved).**
- A side-by-side cutover needs a new globally unique host name anyway. Reusing `azf-d365fsc-gateway-wtc-nonprod` would mean deleting the Flex app first, which means downtime for every consumer.
- It follows the pattern, with component `gateway`, and is 27 characters.
- The D365 target is still explicit in every route (`/api/v1/d365fsc/{env}/...`), so dropping `d365fsc` from the host name loses nothing.

**Alternative rejected: `azf-eim-d365gateway-wtc-nonprod`.** It is longer and inconsistent with the project's component naming, and gains nothing.

### D8. Stateful and kept resources: deferred renames (approved)

The user approved deferring every rename below. The cost of each, and what this change does instead:

| Resource | What a rename involves | Decision |
|---|---|---|
| **SQL server / `eimdb`** | db copy or geo-restore under the new server, a write freeze across every ENVIRONMENT value in the database (`DEV`, `DEV2`, `BAT`, `TEST`), logins and users, firewall rules, every `EIMDB` app setting, the local `eimdb_dev`, the DevOps secret, and `ENVIRONMENTTABLE` checks. Rows are shared by environment, so all environment values cut over at once. | **Deferred.** A separate change, ideally combined with the managed identity follow-up (new server, Entra-only auth). Keep the name via `names.sqlServer`/`names.sqlDatabase`; the script uses them only for the firewall (`myip`) and verification. |
| **Service Bus namespace** | Capture, the D365 data-event endpoint and the D365 business-event endpoint are configured outside this repo with the namespace's connection string. A rename means recreating all 15 queues, draining in-flight and dead-lettered messages, and coordinating three external parties per environment. | **Deferred, probably indefinitely.** `asb-eim-wtc-nonprod` already reads correctly; only the type prefix differs. |
| **Key Vault** | Nothing in Wilson reads it (`Deploy-Eim.ps1` says so). D365 business-event endpoints are probably configured to read the SB connection secret (`asb`) from it, so a rename means reconfiguring D365 business-event endpoints. Soft delete keeps the old name reserved for 90 days. | **Deferred.** The `keyvault` component is retired from the script; the vault is left untouched. Revisit when Key Vault references are introduced. |
| **Resource group** | Moving resources changes resource IDs. That breaks the SWA linked backend, service connection scopes, RBAC assignments, diagnostic settings and any saved queries. Splitting new resources into a new RG while SQL, SB and KV stay in the old one leaves the system spread over two RGs. | **Deferred.** Create every new resource in `d365fsc-integration-nonprod`. `azql-wtc-sandbox` is confirmed to be in the same RG, so one RG-scoped service connection covers everything. |
| **Dashboard API and SWA** | A new SWA host means a new URL for every dashboard user, a new Entra SPA redirect URI and a new linked backend; a new API means re-linking. | **Deferred.** Deployed in place under `wtc-dashboard-api-dev` / `wtc-dashboard-frontend-dev` through `names` overrides (D7). No Entra change, no URL change. |
| **Dashboard API plan** | `wtc-dashboard-api-dev` sits on `asp-appservice-p0v3-linux-dev`. While it stays there, the old plan cannot be deleted, and two P0v3 plans are billed. | **Move in place (manual runbook step), after all Automation cutovers and before `remove -Environment nonprod`.** The app keeps its name, URL and settings; only `serverFarmId` changes to `asp-eim-wtc-nonprod` (`az webapp update --set serverFarmId=<plan id>`), with one restart. A cross-plan move needs the same resource group, region and OS, and the same App Service webspace; both plans are in `d365fsc-integration-nonprod`, `centralus`, Linux. If Azure refuses the move (different webspace), the fallback is to leave the API on the old plan and scale that plan down; the `remove` guard keeps the plan while any app is on it. After the move, `verify` shows the API's outbound IPs; check the SQL firewall and rerun `health`. |
| **Gateway consumers / base URL** | Consumers call `https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net/api/...` with function or host keys. | **Do now.** Copy every named key to the new app with the same value (D11), so `GATEWAYCONSUMER.KEYHASH` still matches. Consumers change only the base URL during an overlap window. |
| **`GATEWAYCONSUMER.GATEWAYURL` / `VERIFIEDAPP`** | Rows may hold the old base URL (migration 013). `VERIFIEDAPP` records the old app name (informational). | **Do now (manual SQL).** Update rows equal to the old URL after the consumer confirms the switch. Re-verify in the dashboard if wanted. |
| **SQL firewall** | The new plan may have different outbound IPs from the old plan, and the new gateway certainly does (Flex used different egress). | **Do now (manual).** `verify` prints the outbound IPs of every app in scope. Add rules unless "Allow Azure services" is already on. |
| **`ENVIRONMENTTABLE` / `CONFIGURATIONTABLE`** | Hold D365 connection data per ENVIRONMENT value (`DEV`, `DEV2`, `BAT`, `TEST`). A row would need editing only if it contains one of the old app host names. | **Check (manual query) across all rows, not only the three deployed environments.** The runbook gives the `LIKE '%azf-d365fsc-gateway%' OR LIKE '%azf-eim-customer%'` check. |
| **CORS / Entra** | `Cors:AllowedOrigins` is empty: the SWA linked backend proxies `/api` same-origin. The SWA host is unchanged. | **Nothing to do.** |
| **Region** | n/a | keep `centralus` |

### D9. Shared vs per-environment ownership: scopes replace the `hosts*` flags

Configuration files live in `script/deployment/config/`:
- `deploy.base.json`: every value common to all four configs.
  - `client: "wtc"`, `workload: "eim"`, `location`, `subscriptionId` placeholder
  - `names` overrides for the kept resources (D8)
  - `appServicePlan.sku: "P0v3"`, `serviceBusNamespace`, `sqlServer`
  - `entra` (tenantId, clientId, audience, scope), placeholders where unknown
  - `gateway` (baseUrl derived unless overridden, maxRequestBytes 4194304, idempotencyRetention `1.00:00:00`, idempotencyPurgeSchedule `0 15 * * * *`, `legacyVerificationTargets`)
  - `automation` (schedules, batch sizes, retry tuning, `gatedFunctions` with the six names, `keepDisabled: []`, `retiredSettings: ["PollSchedule"]`, `excludedUsers` placeholder). There is no `pollSchedule` key.
  - `dashboard.simulationQueuePrefix: "bat"`, keeping today's `Simulation__*Queue` defaults, which were the bat queues because bat hosted the API
- `deploy.nonprod.json`:
  - `environment: "nonprod"`, `scope: "shared"`
  - `names.dashboardApi: "wtc-dashboard-api-dev"`, `names.staticWebApp: "wtc-dashboard-frontend-dev"` (kept, D7)
  - `legacy`: old gateway app, storage and Insights, and the old plan `asp-appservice-p0v3-linux-dev`. The dashboard is not listed: it is deployed in place, not replaced.
- `deploy.dev.json`, `deploy.bat.json`, `deploy.test.json`:
  - `environment: "<env>"`, `scope: "environment"`
  - `legacy`: old Automation app, storage and Insights
  - any per-environment override

**Merge order:** `deploy.base.json` → `deploy.<environment>.json` → the `-ConfigOverridesFile` path, if given → the JSON in `EIM_DEPLOY_CONFIG_OVERRIDES`, if set. Objects merge key by key; lists and scalars are replaced (Wastequip `Merge-ConfigObject`).

**Component ownership:**

| Component | `shared` (nonprod) | `environment` (dev, bat, test) |
|---|---|---|
| `preflight`, `verify`, `health` | yes | yes |
| `infra` | plan, Log Analytics, gateway app/storage/Insights; dashboard API and SWA **only if absent** (an existing dashboard resource is never recreated, moved or reconfigured by `infra`) | queues, Automation storage/Insights/app |
| `settings` | gateway, dashboard API | Automation |
| `schema`, `myip`, `myipremove` | yes | rejected |
| `gateway`, `backend`, `frontend` | yes | rejected |
| `automation` | rejected | yes |
| `cutover`, `rollback`, `remove` | gateway keys / legacy shared resources | Automation triggers / legacy per-environment resources |

- An explicit request for a component outside the config's scope is rejected with a message naming the scope that owns it.
- `all` expands to the components the scope owns. This replaces `hostsGateway`/`hostsDashboard`.
- Adding an environment means adding `deploy.<env>.json` with `scope: environment`, plus a pipeline stage. The script does not hold a fixed list of environment names: `-Environment` accepts any name with a matching config file, and nothing compares configuration names with ENVIRONMENT values in `eimdb`.

**Alternative rejected: keep the flags on bat.** The shared resources would still be deployed from whichever environment's stage happens to carry the flag. That is what produced `-dev` names on shared resources.

### D10. `Deploy-Wilson.ps1`

This replaces `Deploy-Eim.ps1`. It ports these Wastequip functions, adapted:
- `Invoke-Az` (with retry), `Merge-ConfigObject`, `Get-ResourceNames`/`$pick`
- `Invoke-FunctionDeploy`, `Invoke-PreflightComponent`, `Invoke-HealthComponent`
- `Invoke-CutoverComponent`, `Invoke-RemoveComponent`

**Parameters:**
- `-Environment`: the name of a configuration file `deploy.<name>.json` (today `nonprod`, `dev`, `bat`, `test`); not a hard-coded set
- `-Component` (string array; `all` allowed)
- `-WhatIf`
- `-ConfigOverridesFile`
- `-PackagePath`: defaults to the folder the script is in when run from a package. In repo mode, `artifacts/` must be produced first by `New-DeploymentPackage.ps1`; the script never runs `dotnet publish` itself.
- `-Confirm:$false` for `remove`

**Component order** (fixed, whatever order the caller passes):
1. `preflight`
2. `infra`
3. `settings`
4. `myip`
5. `schema`
6. `myipremove`
7. `gateway`
8. `backend`
9. `frontend`
10. `automation`
11. `verify`
12. `health`

`cutover`, `rollback` and `remove` are on-demand only. They are never part of `all`, and each must be requested alone.

**`preflight`** (always runs first when anything changes, and is the only work done under `-WhatIf` besides the preview):
- Fails on any `<...>` placeholder in a value the requested components need.
- Fails if `EIM_DEPLOY_EIMDB` or `EIM_DEPLOY_SERVICEBUS` is needed and missing. They are prompted for with `Read-Host -AsSecureString` when interactive and `TF_BUILD` is unset.
- Fails if a derived name violates the D7 rules, or a derived name equals a `legacy` name.
- Fails if the manifest's `automationQueueAndTimerFunctions` differs from `automation.gatedFunctions`.
- **Drift check** (only when code components are requested and `settings` is not): compares the setting names the config defines for each target app with what the app has.
  - A missing setting fails the run, naming the app and setting and saying that a manual run with `applySettings` is needed.
  - A different value warns.
  - A missing `AzureWebJobs.<fn>.Disabled` on an Automation app fails.
  - A setting listed in `automation.retiredSettings` that is still on an app warns (it is removed by the next `settings` run), and never fails.
  - Values are never printed.

**`infra`** (create if absent, then converge properties; idempotent):

*Shared scope*
- **Plan:** `az appservice plan create --is-linux --sku P0V3`.
- **Log Analytics:** `az resource create` for `Microsoft.OperationalInsights/workspaces`, avoiding the optional CLI extension prompt.
- **Gateway:**
  - storage: Standard_LRS, TLS 1.2, no blob public access
  - Insights: workspace-based, `az resource create` with `WorkspaceResourceId`
  - app: `az functionapp create --plan asp-eim-wtc-nonprod --runtime dotnet-isolated --runtime-version 10 --functions-version 4 --os-type Linux`
- **Dashboard API** (`names.dashboardApi`, kept as `wtc-dashboard-api-dev`):
  - If it exists, `infra` leaves it alone: no recreate, no plan move, no property change. `verify` reports its plan, Always On and outbound IPs.
  - Only if absent (disaster recovery or a new setup): `az webapp create --plan asp-eim-wtc-nonprod --runtime "DOTNETCORE:10.0"`, then `alwaysOn`, `httpsOnly`, `minTlsVersion 1.2`, `ftpsState Disabled`.
- **SWA** (`names.staticWebApp`, kept as `wtc-dashboard-frontend-dev`): left alone if it exists; created with `az staticwebapp create --sku Standard` only if absent.

*Environment scope*
- **Queues:** the five queues with today's properties (same code path as `Deploy-Eim.ps1` `Invoke-QueuesComponent`).
- **Automation:** storage, Insights and app as for the gateway.

*Every Function App, after create*
- `az functionapp config set --always-on true --ftps-state Disabled --min-tls-version 1.2 --http20-enabled false`
- `az functionapp update --set httpsOnly=true`
- Check `linuxFxVersion` is `DOTNET-ISOLATED|10.0`.

*Tags* on every created resource: `eim-workload=eim`, `eim-client=wtc`, `eim-scope=<shared|environment>`, `eim-environment=<nonprod|env>`. `remove` relies on them.

**`settings`**
- Writes the settings through a temp JSON file and `az ... appsettings set --settings @file`, as today. The file is deleted in `finally`.
- **Automation:** the `Deploy-Eim.ps1` list minus `PollSchedule` (FUNCTIONS_WORKER_RUNTIME, APPLICATIONINSIGHTS_CONNECTION_STRING, ServiceBus, EIMDB, the five queues, the three sweep schedules, the batch sizes, the retry tuning, `DefaultEnvironment` = the lower-case configuration name as today, ExcludedUsers), plus `AzureWebJobs.<fn>.Disabled=true` for each gated function **only when that setting is absent**.
  - Retired settings (`automation.retiredSettings`, today `PollSchedule`) are never written, and are deleted from the target app if present (`az functionapp config appsettings delete --setting-names`), naming each removed setting. See D18.
- **Gateway:**
  - `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated` (now allowed, since this is not Flex)
  - APPLICATIONINSIGHTS_CONNECTION_STRING, EIMDB
  - `Gateway__BaseUrl`: derived `https://azf-eim-gateway-wtc-nonprod.azurewebsites.net` unless `gateway.baseUrl` is set
  - `Gateway__MaxRequestBytes`, `Gateway__IdempotencyRetention`, `Gateway__IdempotencyPurgeSchedule`
- **Dashboard API** (in place, on `wtc-dashboard-api-dev`):
  - the `Deploy-Eim.ps1` list, unchanged in names: `ASPNETCORE_ENVIRONMENT`, `ConnectionStrings__EIMDB`, `AzureAd__*`, `Simulation__*` (queues from `dashboard.simulationQueuePrefix`), `Gateway__BaseUrl`, `Gateway__AppName`
  - the only value change is `Gateway__BaseUrl`/`Gateway__AppName`, which now name the new gateway
  - `Gateway__Applications__<n>__Name/BaseUrl` for each entry in `gateway.legacyVerificationTargets`. During the overlap this lists the old gateway, so keys of consumers still pointed there can be verified. Empty it after decommission.

**`schema`**
- Runs `artifacts/dbmigrator/ARM.Integration.DbMigrator.exe --timeout 1800` with `EIMDB_MIGRATOR_CONNECTION` set from `EIM_DEPLOY_EIMDB` in the child process environment only.
- Under `-WhatIf` it runs `--whatif`.
- **`myip` / `myipremove`:**
  - The SQL server name is parsed from the connection string, as today, and cross-checked against `names.sqlServer`.
  - The rule is named `eim-deploy-<BUILD_BUILDID>` in the pipeline, else `AllowDev-<USERNAME>`.
  - The agent IP comes from `https://api.ipify.org`.
  - `myipremove` deletes only that rule name.

**`gateway`, `automation` (Function Apps):** `Invoke-FunctionDeploy`.
1. `az functionapp stop`
2. `config-zip` with the package zip, `--timeout 900`
3. `az functionapp start` in `finally`
4. `syncfunctiontriggers` through `az rest`, retried 6 times at 20 s intervals

The `automation` component refuses to deploy if any gated function has no `AzureWebJobs.<fn>.Disabled` setting. This protects against a new app starting to consume before cutover.

**`backend`:** `az webapp deploy --type zip` with `dashboard-api.zip`, to the existing `wtc-dashboard-api-dev` (in place).

**`frontend`:**
- Unzips `dashboard-frontend.nonprod.zip`.
- `az staticwebapp backends link` to the dashboard API, re-checked every run. The existing SWA is already linked to `wtc-dashboard-api-dev`, so this is normally a no-op; the link survives the API's plan move because the app's resource ID does not change.
- `npx @azure/static-web-apps-cli deploy --deployment-token <from secrets list> --env production` to the existing `wtc-dashboard-frontend-dev`. Its URL and the Entra redirect URI are unchanged.
- The token goes through an environment variable, never the command line.

**`verify`** reports only and never fails. For each app in scope it prints:
- the default host and state
- `alwaysOn` and the plan
- the outbound IPs, for the SQL firewall
- the gated trigger states (Automation)
- the legacy app's state and trigger states, if the legacy app exists
- the SWA linked backend

**`health`** fails, naming the app and check, if:
- a Function App host does not report `Running` within 5 minutes (host status through `az rest` on `/admin/host/status` with the master key fetched in memory)
- the dashboard API answers ≥ 500 or does not answer
- the SWA does not serve `/`

**Secrets:**
- Never echoed and never placed on a command line.
- The settings files are temp files deleted in `finally`.
- The migrator connection is passed in the child process environment.
- All `az` calls that return keys or settings use `-o json` into variables, never `Write-Host`.

**Alternative rejected: Bicep for infra.** It is out of scope (non-goal), and Wastequip uses CLI create-if-absent. A later change can introduce it.

### D11. Gateway hosting moves to the shared plan

The gateway leaves Flex Consumption for `asp-eim-wtc-nonprod` with Always On. It is deployed by the same `Invoke-FunctionDeploy` as Automation.

**Why:**
- One deploy path.
- No cold starts. Always On keeps the host warm, which also keeps `IdempotencyPurge` firing.
- The Flex-only CLI quirks go away: no `FUNCTIONS_WORKER_RUNTIME`, empty `az functionapp show` projections, separate scale commands.
- Flex's separate plan billing goes away.

**What is lost:** Flex's `maximumInstanceCount`, which Deploy-Eim describes as "the only rate limiter this design has" (it protects the shared D365 tenant from a consumer burst). On a single-instance P0v3 plan, throughput is bounded by one 1-vCPU instance shared with five apps. That is a tighter bound, not a looser one, so D365 protection is not weakened. The trade-off is gateway latency under load (see Risks).

**Keys**
- Consumers authenticate with function or host keys, whose hash is stored in `GATEWAYCONSUMER.KEYHASH`.
- `cutover -Environment nonprod` copies every named key, with the same name and value, from the legacy gateway to the new one:
  - host-level `functionKeys`
  - function-level keys of `D365Data` and `D365Service`
- It excludes `_master`, `systemKeys` and the auto-generated `default` keys unless the operator passes `-IncludeDefaultKeys`.
- Commands: `az functionapp keys list/set` and `az functionapp function keys list/set`.
- Values stay in memory, and only key names are printed. The copy is idempotent.
- Consumers then change only the base URL. The old app keeps serving until decommission.

**`IdempotencyPurge`** runs on both apps during the overlap. That is harmless: it deletes expired rows, idempotently. The gateway is therefore not trigger-gated.

**Idempotency during overlap:** both apps share the idempotency table in `eimdb`, so a retried request sent to the other host is still deduplicated.

**Alternative rejected: a dedicated gateway plan.** It costs another plan for one lightweight app. It can be revisited if the CPU alert in the runbook fires.

### D12. Gated triggers, cutover and rollback (Automation)

**Gated set:** `automation.gatedFunctions` in `deploy.base.json` lists the six functions. A new Automation app is created with all of them disabled (D10 `settings`).

**`cutover -Environment <env>`** (manual, run by an operator) does the following, in order:
1. **Preconditions:**
   - The new app exists, is healthy (same checks as `health`) and has all gated settings.
   - The settings parity check against the legacy app passes:
     - A setting the legacy app has that the new app lacks fails the run. Platform settings, `WEBSITE_*`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, `AzureWebJobsStorage`, `AzureWebJobs.*.Disabled` and the names in `automation.retiredSettings` (`PollSchedule`, which every legacy app still carries) are ignored.
     - A different value warns, except `EIMDB`, `ServiceBus` and the queue settings: a difference there fails the run, because the new app must consume the same queues from the same namespace against the same database.
     - Only names are printed.
2. Read each gated function's effective state on the legacy app: the `isDisabled` property through `az functionapp function show`, falling back to the `AzureWebJobs.<fn>.Disabled` setting.
3. Set `AzureWebJobs.<fn>.Disabled=true` on the legacy app for every gated function. The app restarts.
4. Wait until every legacy function reports disabled, then wait the queue lock duration (60 s, read from the queue's `lockDuration`) so in-flight messages complete or are released.
5. On the new app, set `AzureWebJobs.<fn>.Disabled=false` only for the functions that were enabled on legacy (mirror). Print the resulting state table.

**If no legacy app is configured or it does not exist** (a future new environment), cutover enables every gated function except those in `automation.keepDisabled` (default empty).

**`rollback -Environment <env>`** is the exact reverse:
- Disable all gated functions on the new app.
- Wait for them to report disabled, plus the lock duration.
- Enable on legacy the functions that were enabled on the new app.
- It refuses if the legacy app no longer exists.

**Ordering guarantee:** at no point are the same function's triggers enabled on both apps. Disabling is confirmed before anything is enabled.

**Failure behaviour:**
- A failure at step 3 or 4 leaves both apps disabled. That is safe: messages wait in the queue and are picked up after rerun or rollback.
- The script prints the exact rerun and rollback commands.
- `cutover` is idempotent: rerunning it after success changes nothing.

**Gateway (`cutover -Environment nonprod`):** copies keys (D11) and prints the consumer and base-URL checklist. There are no triggers to gate. The dashboard has no cutover at all: it is deployed in place, its URL does not change, and only its `Gateway__*` settings point at the new gateway.

**Which ENVIRONMENT values move.** Cutover moves whatever the legacy app's queues carry. It does not look at ENVIRONMENT values, so rows for `DEV2` (or any other value) are handled by whichever app consumes the queues they arrive on, before and after cutover alike.

**Alternative rejected: deployment slots with swap.** Slots on a Linux plan double the app count per plan and do not solve the move from one app to another app. The cutover is a one-off.

### D13. Decommission: `remove` (manual only)

`remove -Environment <env|nonprod>` deletes only the resources named in that config's `legacy` section:
- per environment: the app, storage and Insights
- shared: the gateway app, its Flex plan (from `serverFarmId`), storage and Insights; and the old plan `asp-appservice-p0v3-linux-dev` **only if** no app remains on it. Until the dashboard API is moved off it (D8), `remove` keeps the plan and names `wtc-dashboard-api-dev` as the remaining app.
- never the dashboard API or SWA: they are not legacy, and a guard refuses any `legacy` entry equal to a `names` value in use

Guards:
- Refuses when `TF_BUILD` is set: the pipeline can never run it.
- Requires a prior `-WhatIf` run in the same session, recorded in a temp marker under `$env:TEMP` keyed by environment and date, plus `Read-Host` confirmation of the environment name. `-Confirm:$false` is rejected.
- Refuses if:
  - any legacy Automation gated function is enabled
  - a legacy name equals a current derived or overridden name
  - a resource is SQL, Service Bus, Key Vault, the resource group, a Log Analytics workspace or a static web app (a type allow-list: `Microsoft.Web/sites`, `Microsoft.Web/serverfarms`, `Microsoft.Storage/storageAccounts`, `Microsoft.Insights/components`)

Legacy resources are untagged, so `remove` targets them by explicit name and type, not by tag. This deviates from Wastequip, which deletes by tag. Tags apply to new resources only.

### D14. Pipeline: `pipelines/eim.yml`

**Structure** (modelled on Wastequip `pipelines/eim.yml`):
- `name: eim-$(Date:yyyyMMdd)$(Rev:.r)`
- `trigger`: `main`, with paths `Wilson.Integration.*`, `ARM.Integration.*`, `script/SQL`, `script/deployment` and `pipelines`, excluding `**/*.md`
- `pr`: `main`, same paths; Build only
- `lockBehavior: runLatest`
- **Parameters** (manual runs; all booleans): `deployInfra` (false), `applySettings` (false), `runMigrations` (false), `deployCode` (true)

**Stages:**

| Stage | DevOps environment | Approval | Exclusive lock | Steps |
|---|---|---|---|---|
| `Build` | — | — | — | `dotnet test ARM.Integration.Wilson.slnx -c Release` (trx published), frontend `npm ci` + `npm run lint`, `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`, `New-DeploymentPackage.ps1 -Environment nonprod,dev,bat,test`, then `Test-DeployWilson.ps1 -PackagePath <package>` (package content checks), publish artifact `package` |
| `Deploy_shared` | `eim-wtc-nonprod` | **yes** (approved) | yes | template `deploy-environment.yml` with `environment: nonprod`, `runMigrations` honoured, components `gateway, backend, frontend` |
| `Deploy_dev` | `eim-wtc-dev` | no | yes | `environment: dev`, components `automation` |
| `Deploy_bat` | `eim-wtc-bat` | yes | yes | `environment: bat`, components `automation` |
| `Deploy_test` | `eim-wtc-test` | yes | yes | `environment: test`, components `automation` |

- Every deploy stage's condition is `and(eq(dependencies.<previous>.result, 'Succeeded'), ne(variables['Build.Reason'], 'PullRequest'))`.
- The template's `runMigrations` parameter is passed only to `Deploy_shared`. The per-environment stages are passed `false`, because the environment scope rejects `schema`.

**`templates/deploy-environment.yml`** is a port of Wastequip's, with these changes:
- Steps:
  1. preflight + `-WhatIf` preview
  2. `infra` (if `deployInfra`)
  3. `settings` (if `applySettings`)
  4. `myip` → try `schema` finally `myipremove` (if `runMigrations`)
  5. code components (if `deployCode`)
  6. `verify` + `health`
- `UseNode@1` 22.x when `frontend` is in the components.
- No release tag step.
- Every `AzureCLI@2` step (pscore, `windows-latest`) maps the secrets explicitly:
  ```yaml
  env:
    EIM_DEPLOY_EIMDB: $(EIM_DEPLOY_EIMDB)
    EIM_DEPLOY_SERVICEBUS: $(EIM_DEPLOY_SERVICEBUS)
    EIM_DEPLOY_CONFIG_OVERRIDES: $(EIM_DEPLOY_CONFIG_OVERRIDES)
  ```
  Secret variables are not exposed to scripts otherwise.
- Variable group `eim-wtc-nonprod` (one group; all environments share the same database and namespace).
- Service connection `sc-eim-wtc-nonprod` for every stage.

**Pushback on the requested shape**
1. **`Deploy_shared` needs an approval**, and that approval then sits in front of dev on every merge. The shared stage changes the gateway (used by external consumers against BAT/TEST), the dashboard used by all environments, and on manual runs the one `eimdb` that every ENVIRONMENT value (`DEV`, `DEV2`, `BAT`, `TEST`) reads. Its blast radius is the union of bat and test, so it should not be less gated than bat.
   - **Approved:** the order stays `Build → Deploy_shared → Deploy_dev → Deploy_bat → Deploy_test`, with approval on `eim-wtc-nonprod`. Dev waits for one approval per merge.
2. **Code skew.** After `Deploy_shared`, the shared dashboard runs build N while bat and test Automation may still run build N-1 (until approved), or older if a stage is rejected. The dashboard's reprocessing uses its own copy of `OrchestrationRegistry`, so an orchestration change is visible in dashboard reprocessing for bat and test before their Automation is promoted. This already happens today, because the dashboard is shared, but the pipeline makes it routine. Mitigation: orchestration changes must be backward compatible across one build, the same rule as migrations (D16).
3. **Migrations in a run whose later stages are rejected.** The schema moves ahead of the code in bat and test. That is acceptable only because migrations must be expand-only and compatible with the previous build (D16).
4. **Rollback** means rerunning an earlier run's stage. Each run carries its own package, so code rollback is "Rerun stage" on the older run. Schema is forward-only.

**Service connection: one, `sc-eim-wtc-nonprod` (approved).**
- Workload identity federation, Contributor on RG `d365fsc-integration-nonprod`.
- `azql-wtc-sandbox` is confirmed to be in `d365fsc-integration-nonprod`, so the RG-scoped Contributor role also covers its firewall rules. No extra role assignment is needed.
- The SQL-auth connection string carries the database credentials.
- Per-environment connections would add no isolation, because every environment shares the RG, plan, database and namespace.

**Pipeline files (approved):**
- Delete `.azure-pipelines/workflows/deployment/*.yml` and `script/deployment/Deploy-ZipWithPublishProfile.ps1` in Phase 2. After the Phase 1 rename they would build a renamed project, and they target the legacy names.
- `.azure-pipelines/workflows/auto-ai-pull-request.yml` and `.azure-pipelines/README.md` move to `pipelines/auto-ai-pull-request.yml` and `pipelines/README.md`, content unchanged apart from paths.
- **Manual:**
  - Before merging Phase 2, tag the last pre-Phase-2 commit on `main` as `pre-5246-deploy`, so the legacy pipelines can still be run from that ref during the overlap if needed.
  - Repoint the auto-AI pipeline definition's YAML path.
  - Disable the three old definitions after the first successful new run; delete them after decommission.

### D15. `New-DeploymentPackage.ps1`

A port of Wastequip's.
- **Parameters:** `-Environment` (array, default `nonprod,dev,bat,test`), `-OutputPath`, `-Version`.
- **Output:** `artifacts/package/wtc-eim-deploy-<version>/` (`.gitignore` already covers `artifacts/`) and a zip of it.
- **Contents:**
  - `Deploy-Wilson.ps1`
  - `config/deploy.base.json` and `config/deploy.<env>.json` for each environment
  - `artifacts/wilson-automation.zip`, `artifacts/gateway.zip`, `artifacts/dashboard-api.zip`: `dotnet publish -c Release`, zipped with `System.IO.Compression.ZipFile` so `.azurefunctions` is kept
  - `artifacts/dashboard-frontend.nonprod.zip`: one SPA, built with `deploy.base.json` + `deploy.nonprod.json` Entra values, `VITE_API_SCOPE=api://<clientId>/<scope>` and `VITE_API_BASE_URL=''`, as Deploy-Eim does today
    - It is skipped with a warning if Entra values are placeholders. `frontend` then fails preflight.
  - `artifacts/dbmigrator/ARM.Integration.DbMigrator.exe`: self-contained `win-x64`, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`. The migrations are embedded resources, so the exe carries exactly the build's migrations.
  - `artifacts/manifest.json`: version, commit, builtUtc, environments, `automationQueueAndTimerFunctions` from `functions.metadata` (non-HTTP triggers), `gatewayFunctions`.
  - `README.txt`: the operator steps for running from the package.
- Warns when the working tree is dirty.
- Fails if any publish or npm step returns non-zero (`Invoke-Tool` via `System.Diagnostics.Process`, as in Wastequip).

### D16. Migration compatibility (schema-migration)

This change adds **no** SQL migration, so currently deployed code is unaffected.

What changes is when migrations run: once per pipeline run, in `Deploy_shared`, before any environment's code. From now on, every migration must work with both the new build and the build still running in later stages, and with every ENVIRONMENT value's rows, including `DEV2`, which has no deployment configuration of its own. That means expand/contract:
- add nullable columns or columns with defaults
- add tables and procedures
- remove or rename only in a later change, after every environment runs code that no longer uses the old shape

`docs/deployment/database-migrations.md` records this rule. The existing requirement "Migrations accompany the code that requires them" is unchanged.

### D17. Documentation

- **`docs/deployment/deployment.md`:** rewritten for `Deploy-Wilson.ps1`, components, scopes, config layering, secrets, `-WhatIf`, and running from the package.
- **`docs/deployment/devops-setup.md`** (new): the one-time checklist.
  - environments with approvals and locks
  - the service connection with RBAC
  - the variable group with secret flags
  - pipeline creation, the auto-AI path change
  - build service permissions
  - disabling the old definitions
- **`docs/deployment/resource-rename-cutover.md`** (new): the runbook.
  1. prerequisites and the `pre-5246-deploy` tag
  2. provision the shared resources (manual run with `deployInfra` + `applySettings`); the dashboard is updated in place
  3. provision per environment
  4. SQL firewall
  5. deploy
  6. verify
  7. per-environment cutover (dev → bat → test) with checks: the queue depth drains on the new app, the dashboard shows new rows, there are no failures in Insights
  8. gateway key copy and consumer switch window
  9. `GATEWAYURL` update
  10. App Insights query and alert updates (new logger categories and resource names), plus the plan CPU/memory alert
  11. moving `wtc-dashboard-api-dev` to `asp-eim-wtc-nonprod` (command, expected restart, fallback if Azure refuses, firewall and health re-check)
  12. rollback procedure
  13. decommission (`remove -WhatIf`, then `remove`)
  14. clean-up of the old pipelines
  15. the old → new name table, and the kept-name table
- **Docs and config also updated:**
  - `docs/deployment/d365fsc-gateway.md`: plan hosting and the new base URL
  - `docs/deployment/deploy-from-scratch.md` and `docs/deployment/deployment-plan.md`: point to the new script, or are marked superseded
  - `docs/azure-deployment-pipelines-setup.md`: replaced by a pointer to `devops-setup.md`
  - `docs/auto-ai-pull-request-setup.md`: the new YAML path
  - `docs/README.md`, `README.md` and `CLAUDE.md`
  - `ARM.Integration.D365FSC.SampleClient/README.md` and `appsettings.json`
  - `ARM.Integration.Dashboard.API/appsettings.Development.json`: the new gateway URL and app name
- The gateway test constants that use the old name are test data and stay unchanged.

### D18. Delete `PollSourceHandler` and retire `PollSchedule`

The user decided to delete the inactive template rather than move it.

**Code (Phase 1)**
- `git rm ARM.Integration.EIM.Customer/PollSourceHandler.cs` as part of the rename; it never reaches `Wilson.Integration.Automation/Functions/`.
- `Wilson.Integration.Orchestrations/Orchestrations/SamplePoll_To_Target.cs`: only its doc comment changes, to stop naming `PollSourceHandler` (it becomes "a template for a polling source; no polling trigger exists in this repository"). The class stays, because it is a sample orchestration, is not registered, and deleting it is outside this decision.
- Kept: `EIMWatermark` (engine, shared with Wastequip), `WATERMARKTABLE` (schema; no migration in this change), the comment in applied migration `006` (never edited), and archived changes (never edited).
- **Dependency check (done for this design):** a repository search for `PollSourceHandler`, `PollSchedule`, `PollSource*`, `SamplePoll` and `EIMWatermark` finds no live code, DI registration, route row, test, doc or frontend reference beyond those listed above. `Program.cs` does not mention it. The live function set is unchanged at six.

**Settings**
- `local.settings.sample.json` has no `PollSchedule` or `PollSource*` keys. `RequiredSettings` does not list them.
- Phase 1 leaves `Deploy-Eim.ps1` writing `PollSchedule` (it only changes project paths there). The setting is harmless and nothing reads it. Phase 2 deletes that script.
- Phase 2 config has no `pollSchedule`. `automation.retiredSettings: ["PollSchedule"]` in `deploy.base.json` drives three behaviours:
  - `settings` never writes a retired setting and deletes it from the target Automation app if present.
  - The preflight drift check warns, and never fails, when an app still has a retired setting.
  - The cutover parity check ignores retired settings, because every legacy app carries `PollSchedule` and the new apps do not.
- Legacy apps keep `PollSchedule` until they are decommissioned. The script never edits legacy app settings except the `AzureWebJobs.<fn>.Disabled` gates.

**Tests:** `AutomationFunctionNamesTests` asserts that no `PollSourceHandler` type exists; `RequiredSettingsTests` asserts the sample has no retired keys; `RepositoryLayoutTests` asserts the file is gone; `Test-DeployWilson.ps1` asserts the retired-settings behaviour of `settings`, drift and parity.

### D19. Testing the PowerShell, and delivery as two PRs

**Script tests.** Pester is not installed, and the tests must run on the build agent and for Luigi without extra modules. `script/deployment/Test-DeployWilson.ps1` is a dependency-free self-test:
- It parses `Deploy-Wilson.ps1`, `New-DeploymentPackage.ps1` and itself with `[System.Management.Automation.Language.Parser]::ParseFile` and fails on any parse error.
- It dot-sources both scripts. Each script guards its main body (`if ($MyInvocation.InvocationName -ne '.') { … }`), so dot-sourcing loads functions without running anything or calling `az`.
- It asserts the pure functions: name derivation, config merge, scope and order, gated and retired settings, cutover plan and parity, remove guards, the manifest's function extraction from a `functions.metadata` fixture, and the zip helper keeping `.azurefunctions/`.
- With `-PackagePath <folder>` it also checks a built package's contents. The `Build` stage runs it after `New-DeploymentPackage.ps1`. Locally, Luigi runs only the parameterless form.
- It exits non-zero on the first failed assertion.

The user has added `Bash(pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1:*)` to `.claude/settings.json`. Luigi has no permission to run `New-DeploymentPackage.ps1` or `Deploy-Wilson.ps1` directly, so the full packaging run is proven in the pipeline's `Build` stage and in the manual runbook.

**C# tests** in `Wilson.Integration.Tests` cover the non-PowerShell artefacts: config files (`DeploymentConfigTests`), pipeline YAML (`PipelineDefinitionTests`) and repository layout and stale names (`RepositoryLayoutTests`).

**Delivery.** Phase 1 ships as its own PR (`feature/5246/align-structure-and-deployment`, the rename, layout, tests and `PollSourceHandler` deletion) and is merged before Phase 2 starts. Phase 2 ships as a second PR (pipeline, scripts, config, docs). Each PR is green on its own: build, the three test projects, and (for Phase 2) the script self-test. Creating branches, commits and PRs stays with the user and the main session.

## Failure behaviour

**Message processing:** unchanged.
- Phase 1 moves code without altering classification (`DefaultFailureClassifier`), retries, idempotency (IngestGate duplicate probe), hold/release, or settlement (`autoCompleteMessages: false`, explicit complete, abandon and dead-letter in the same places).
- No new exception types are added. `RequiredSettings.EnsurePresent` throws at startup, before any message is received, so it is outside the classifier.

**Startup:**
- A missing required setting now stops the Automation host at startup with one message naming every missing key. Before, the first use failed.
- The gateway fails at startup instead of on the first request.
- Both are deployment-time faults and are caught by `health`.

**Deployment:**
- The script stops on the first failing component, names it and exits non-zero, as today.
- `Invoke-FunctionDeploy` always restarts the app in `finally`.
- `myipremove` always runs, in the pipeline's `finally`.

**Cutover:** see D12. Both-disabled is the failure-safe state, and messages wait in the queues. Duplicates are not possible, because disabling is confirmed before enabling, and `maxConcurrentCalls: 1` keeps per-app ordering.

**Ordering:** within an environment, exactly one consumer per queue at all times, before, during and after cutover.

**Idempotency of the scripts:** `infra`, `settings` (gated settings written only if absent), `cutover` and key copy can all be rerun safely.

## Risks / Trade-offs

- **Two consumers at once if someone enables triggers by hand.** Mitigations: gated settings, the `automation` deploy refusal, `cutover`'s confirm-then-enable sequence, and the runbook warning. `verify` shows both apps' trigger states.
- **Logger category and resource rename** break App Insights queries, alerts and workbooks that use `ARM.Integration.EIM.Customer` or the old Insights resources. Mitigation: a runbook step to inventory and update them. Old Insights data stays queryable until decommission.
- **P0v3 capacity** (1 vCPU, 4 GB) for five apps once the dashboard API has moved: three Automation apps, the gateway and the dashboard API.
  - Memory, at roughly 250–400 MB per .NET isolated host, is fine.
  - CPU is light in steady state (sequential SB consumers; about 12 timer firings per minute in total), but restarts during deploys and gateway bursts compete.
  - **Approved:** P0v3 with a CPU/memory alert above 80% (runbook), moving to P1v3 (a one-line `appServicePlan.sku` change) if it fires.
  - During the overlap the old plan (old Automation apps and the dashboard API), the new plan and the Flex app are all billed.
- **Dashboard API plan move.** Moving `wtc-dashboard-api-dev` between plans restarts it once, and Azure refuses the move if the two plans are in different webspaces. Fallback: keep the API on the old plan and scale that plan down; `remove` keeps any plan that still hosts an app. The move may also change the API's outbound IPs, so the runbook re-checks the SQL firewall and `health` afterwards.
- **Misleading `-dev` names stay on shared dashboard resources.** This is accepted (decision 2) and recorded as a deferred rename. `verify` and the docs label them as shared.
- **Gateway latency.** It moves from dedicated Flex instances to a shared single instance. Consumers may see higher latency under load; there are no cold starts. Watch Insights after cutover.
- **Shared-stage approval slows dev** (D14). Accepted by the user.
- **Project-rename churn.**
  - Developers' `local.settings.json` and `.csproj.user` stay in the old ignored folder, so they must be moved by hand. The README gives the step.
  - `ARM.Integration.EIM.Customer/artifacts`, `bin` and `obj` remain as untracked leftovers; delete locally.
  - Open branches touching `ARM.Integration.EIM.Customer/*` will conflict. Phase 1 lands quickly, as its own PR (D19).
- **External parties miss the gateway URL switch.** The old gateway keeps serving until decommission. Decommission only after Insights shows no requests to the old app for an agreed period (the runbook suggests 7 days).
- **New outbound IPs blocked by the SQL firewall** would cause the new apps to fail health. `verify` prints the IPs, and `health` catches it before cutover.
- **Placeholders in committed config** (subscription, Entra, excluded users) block the first pipeline run until they are filled. Preflight names each one. Non-secret values can be committed in a follow-up commit or supplied through `EIM_DEPLOY_CONFIG_OVERRIDES`.
- **Packaging is proven only in the pipeline.** Luigi cannot run `New-DeploymentPackage.ps1` locally (permission scope). Its helpers are unit-tested through `Test-DeployWilson.ps1`, and the full package check runs in the `Build` stage of the first Phase 2 pipeline run; a packaging bug surfaces there rather than in Luigi's loop.
- **Wastequip divergence that remains:** no Key Vault `secrets` component, no `sql`/`sqlusers`, no `-NameSuffix`, no release tags, remove-by-name instead of by tag, and kept (non-pattern) dashboard names. These are documented in `deployment.md` so future merges know where the scripts differ.

## Migration Plan

1. **Phase 1 PR** (Luigi): rename, layout, tests, settings, cleanup, `PollSourceHandler` deletion, and the updated old deploy paths. Merge.
   - `main` stays deployable with the old pipelines.
   - The #5425 deploy tasks can run at any time with the old pipelines.
2. **Manual:** tag `pre-5246-deploy` on `main` (the last commit before the Phase 2 merge). Complete `devops-setup.md`.
3. **Phase 2 PR** (Luigi): pipeline, scripts, config, docs. Merge. The merge run deploys code only and fails preflight until infrastructure exists. The first real run is manual, with `deployInfra` + `applySettings` + `deployCode`, and `runMigrations` off. The dashboard API and SWA receive code and settings in place; the only value change is the gateway they point at.
4. **Manual:** SQL firewall rules for the new outbound IPs. Rerun health.
5. **Manual cutover:** dev, verify for a day, then bat, then test. Each is `cutover -Environment <env>`.
6. **Manual:** gateway `cutover -Environment nonprod` (key copy), notify consumers, update `GATEWAYURL` rows as they switch. Disable the old pipeline definitions after the first successful new run.
7. **Manual:** move `wtc-dashboard-api-dev` to `asp-eim-wtc-nonprod`, then re-check the firewall and `health`.
8. **Manual decommission:** after the observation window, run `remove -WhatIf` then `remove` per environment and for `nonprod`. Empty `gateway.legacyVerificationTargets` and the `legacy` sections in a small follow-up commit. Delete the old pipeline definitions.

**Rollback:**
- Before decommission, `rollback -Environment <env>` returns consumption to the legacy app, and the legacy gateway is still running.
- The dashboard has no legacy copy. Its rollback is rerunning `Deploy_shared` of an earlier run (code), or reapplying settings with the old gateway in `EIM_DEPLOY_CONFIG_OVERRIDES` (`gateway.baseUrl` and app name).
- For code on the new apps, rerun the deploy stage of an earlier pipeline run.
- After decommission there is no rollback to the legacy resources. Recreate from the new pipeline instead.

## Resolved decisions (user, 2026-10-22)

1. SQL server and `eimdb`, the Service Bus namespace, the Key Vault and the resource group are **not** renamed; kept through `names` overrides and recorded as deferred (D7, D8).
2. The dashboard API and SWA are **not** renamed; kept as `wtc-dashboard-api-dev` / `wtc-dashboard-frontend-dev` and deployed in place, with no cutover, no new URL and no Entra change (D7, D8, D10).
3. The gateway becomes `azf-eim-gateway-wtc-nonprod` on the shared plan with Always On, with a side-by-side cutover (D7, D11).
4. One service connection, `sc-eim-wtc-nonprod` (D14).
5. Pipeline order `Build → Deploy_shared → Deploy_dev → Deploy_bat → Deploy_test`, with approval on `eim-wtc-nonprod` (D14).
6. Old pipelines: YAML deleted in Phase 2; `pre-5246-deploy` tag (manual) on the commit before; definitions disabled after the first successful new run and deleted at decommission (D14).
7. P0v3 with an 80% CPU/memory alert, moving to P1v3 if it fires (Risks).
8. `PollSourceHandler` is deleted in Phase 1 and `PollSchedule` is retired (D18).
9. `azql-wtc-sandbox` is in `d365fsc-integration-nonprod`; the single RG-scoped service connection is enough (D14).
10. The `pwsh` permission rule for `Test-DeployWilson.ps1` exists in `.claude/settings.json` (D19).
11. Phase 1 and Phase 2 ship as two PRs, Phase 1 first (D19).

## Open Questions

1. **Dashboard API plan move.** This design moves `wtc-dashboard-api-dev` onto `asp-eim-wtc-nonprod` (a manual step with one restart) so the old plan can be deleted. The alternative is to leave it on `asp-appservice-p0v3-linux-dev` and keep paying for that plan, or scale it down. Recommendation: move it.
2. **`SamplePoll_To_Target`.** It is unregistered and only mentions `PollSourceHandler` in a comment, so this change keeps the class and rewords the comment. Delete it too? Recommendation: keep it in this change and delete it in a separate clean-up if wanted.
