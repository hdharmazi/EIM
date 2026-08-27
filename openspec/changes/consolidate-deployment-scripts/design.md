## Context

Ten scripts in `Script/Deploy-Manual/` implement two independent deployment architectures into the same resource group, `d365fsc-integration-nonprod`.

```
  PATH A: CONTAINER APPS                   PATH B: APP SERVICE
  AzureDeploy (no extension)               AzureDeploy.AppService.ps1
  AzureDeploy.Update.ps1                   AzureDeploy.AppService.UpdateAzure.ps1
                                           AzureDeploy.Frontend.ps1
                                           AzureDeploy.API.ps1
                                           AzureDeploy.Function.EIM.Customer.dev1.ps1

  API   -> containerapp dashboard-api      API   -> webapp wtc-dashboard-api-dev
  Front -> containerapp + nginx            Front -> Static Web App, linked backend
  Func  -> containerapp eim-customer       Func  -> azf-eim-customer-wtc-<env>
  build -> az acr build (server side)      build -> dotnet publish / npm build + zip

  Entra: 5e8a5341 / 317063cf               Entra: 446b6761 / 5d1e26b3
         scope access_as_user                     scope user_impersonation
```

Path B has been selected as authoritative.

Observations that shape this design:

- **The same credentials appear seven times.** `AzureDeploy` lines 26–27, `AzureDeploy.AppService.ps1` lines 36 and 38, `AzureDeploy.AppService.API.config.ps1` line 1, and `AzureDeploy.Function.EIM.Customer.dev1.ps1` lines 23–24 all carry the same SQL password and Service Bus root key, committed.
- **Resource naming is already consistently patterned**, just duplicated rather than derived: `azf-eim-customer-wtc-<env>`, `stwtceimcustomer<env>`, `asp-appservice-p0v3-linux-<env>`, `<env>-capture-to-d365fsc`.
- **`AzureDeploy.Function.EIM.Customer.dev1.ps1` is the only genuinely idempotent script**, checking for each resource before creating it. It is the quality bar for the consolidated script.
- **`Deploy-ServiceBusQueue-AzCli.ps1` is already correct**: fully parameterised, idempotent, no hardcoded values. It is reused rather than rewritten.
- **The dashboard is singular while Functions are per-environment.** One `wtc-dashboard-api-dev` and one `wtc-dashboard-frontend-dev` exist, against three Function Apps (`bat`, `test`, `dev1`). This is consistent with the dashboard reading `ENVIRONMENTTABLE` and observing all environments from one deployment.
- **`bat` and `test` share the `-dev` App Service Plan** in `AzureDeploy.AppService.ps1`, while `dev1` has its own. Plan allocation is currently inconsistent.

## Goals / Non-Goals

**Goals:**

- One command deploys any component to any non-production environment.
- No credential appears in any committed file.
- Running the script twice produces no unintended change.
- Adding an environment is a configuration entry, not a new script.
- The correct order of operations is encoded in the script rather than remembered.
- A failed step stops the deployment and reports which step failed.
- The design does not preclude adding production later.

**Non-Goals:**

- **Production deployment.** Production will use a different resource group and Service Bus namespace, so it is a distinct exercise rather than another value of `-Environment`. Supported environments are `dev1`, `bat`, and `test`. Decision 8 records what this change does to keep production addable later.
- Renaming the shared dashboard resources. They are suffixed `-dev` but serve all non-production environments; `-nonprod` would be accurate. Renaming would mean recreating live resources and is deliberately deferred.
- Converting deployment to declarative infrastructure as code. The existing Bicep files under `Script/Bicep/` are not adopted or extended here; this change consolidates the imperative scripts that are actually used.
- Provisioning the SQL server or the Service Bus namespace. Both are assumed to exist, as they are today. Only queues and schema are managed.
- CI/CD pipeline definition. The script is designed to be pipeline-invocable, but wiring it into Azure DevOps is separate.
- Blue/green deployment, slots, or automated rollback.
- Changing Entra app registrations. This change consolidates configuration to whichever registration is authoritative; it does not create or modify registrations.
- Local development workflow. `run.bat` and `run-dashboard.bat` are unaffected, and the Vite dev server continues to proxy `/api` to the locally running API.

## Decisions

### 1. App Service is authoritative; the Container Apps path and its container artifacts are removed

Keeping both means every change must be made twice, and the Entra divergence demonstrates that this already fails in practice — the re-registration reached Path B only.

Removal rather than deprecation: a dormant second path in the repository will be found and run by someone eventually, and it deploys into the same resource group as the live one.

The container artifacts go with it. `docker-compose.yml`, the three Dockerfiles, and `nginx.conf.template` support only the Container Apps path and a local container workflow that is not in use. Local development runs through `run.bat` and `run-dashboard.bat`, which start the API with `dotnet run` and the frontend with the Vite dev server proxying `/api`. Nothing in the working set depends on the container artifacts.

One caveat is carried into Decision 13: `nginx.conf.template` is the only place in the repository that records how to make Server-Sent Events survive a reverse proxy. That knowledge is preserved as documentation before the file is deleted.

### 2. One entry point, parameterised by environment and component

```
  Deploy-Eim.ps1
    -Environment  dev1 | bat | test | prod        (required)
    -Component    all | queues | schema | api | function | frontend
    -WhatIf
```

*Alternative:* keep small per-component scripts and add a coordinating wrapper. Rejected — that is close to the current state, and the current state is the problem. The component switch preserves the ability to deploy one piece without preserving eight files.

### 3. All resource names derive from the environment through one function

```
  function Get-ResourceNames($Environment) {
      FunctionApp    = "azf-eim-customer-wtc-$env"
      StorageAccount = "stwtceimcustomer$env"
      AppServicePlan = "asp-appservice-p0v3-linux-$env"
      QueuePrefix    = "$env-"
      ...
  }
```

The naming patterns already exist; they are simply repeated by hand today. Centralising them means adding an environment cannot produce a mismatched name, and it removes the copy-a-file-and-edit-it workflow that produced the current sprawl.

Where a name cannot be derived — an existing resource that does not follow the pattern — it is an explicit override in that environment's configuration file rather than a special case in code.

### 4. Secrets live in gitignored per-environment configuration

```
  Script/config/deploy.example.json     committed, no real values
  Script/config/deploy.dev1.json        gitignored
  Script/config/deploy.prod.json        gitignored
```

The script fails with a clear message if the file for the requested environment is missing, and never falls back to a default or embedded value.

*Alternative:* read from environment variables only. Rejected as the primary mechanism because a dozen values are needed and setting them by hand before each run reintroduces the error the change is meant to remove. Environment variables remain supported as an override so a pipeline can supply values without a file.

*Alternative:* Azure Key Vault. The better end state, and it depends on the deferred Managed Identity work. The configuration file is structured so its values can later be sourced from Key Vault without changing the script's interface.

### 5. Every step is idempotent and existence-checked

Each resource is queried before creation and created only if absent; configuration and code deployment are applied unconditionally, since both are naturally convergent. This follows `AzureDeploy.Function.EIM.Customer.dev1.ps1`, which already does exactly this.

A `-WhatIf` switch reports the actions that would be taken without performing them, which matters most for the first production run.

### 6. Deployment order is fixed and encoded

```
  queues  ->  schema  ->  api  ->  function  ->  frontend
```

- **Queues before everything**: the Function App fails to start listening if its queues are absent.
- **Schema before application code**: application code assuming a column that does not exist fails at runtime, whereas schema ahead of code is backward-compatible given additive migrations.
- **API before frontend**: the Static Web App links the API as a backend, so the API must exist.
- **Frontend last**: its build bakes in configuration and it is the operator-visible surface, so it changes only once everything behind it is in place.

Running `-Component all` executes this sequence. A failure at any step aborts the remainder with a non-zero exit code.

### 7. The dashboard is deployed once, not per environment

The dashboard API and frontend observe all environments through `ENVIRONMENTTABLE`, which is why only one of each exists today against three Function Apps. Treating them as per-environment resources would create dashboards that each show the same data.

The configuration therefore distinguishes **per-environment components** (queues, schema, Function App, storage) from **shared components** (dashboard API, frontend). Deploying the dashboard is not scoped by `-Environment`; requesting it with an environment that is not the designated dashboard host is an error rather than a silent second deployment.

The shared dashboard covers non-production only. Production, when added, gets its own dashboard in its own resource group, so "shared" means shared across non-production rather than shared across everything.

This asymmetry is the least obvious thing about the current setup and the easiest to get wrong when consolidating.

### 8. Production is excluded, but the design keeps it addable

Production will use a different resource group and Service Bus namespace, so it is not simply another value of `-Environment`. Including it now would mean designing against unknown resource names and an unbuilt environment.

Two consequences are carried anyway, because retrofitting them is more expensive than allowing for them:

- **Resource group and Service Bus namespace are configuration values, not constants**, even though every current environment shares the same ones. This is the single assumption that would otherwise have to be unpicked throughout the naming function later.
- **The shared dashboard is scoped to non-production** rather than being treated as globally singular (Decision 7).

A production gate, confirmation prompt, and separate approval path are deferred with production itself.

### 9. Entra configuration is applied consistently, and the local registration is retained

Three variants exist today across two registrations and two scope names:

```
  local development   5e8a5341 / 317063cf   user_impersonation   (appsettings.json, .env.local)
  Container Apps      5e8a5341 / 317063cf   access_as_user       (being deleted)
  App Service         446b6761 / 5d1e26b3   user_impersonation   (authoritative)
```

The first is confirmed deliberate: it is the local-development registration, and the committed defaults in `ARM.Integration.Dashboard.API/appsettings.json` are correct as they stand. They are documented as such rather than changed, so they are not mistaken for drift by the next reader.

The scope-name divergence resolves itself: `access_as_user` appears only in the Container Apps scripts, which this change deletes. Both surviving paths use `user_impersonation`.

For deployed environments, the script reads tenant, client, and scope from the environment's configuration file and applies them to both the frontend build arguments and the API's `AzureAd` settings, so the two cannot drift.

### 10. Service Bus queue provisioning is reused, not rewritten

`Deploy-ServiceBusQueue-AzCli.ps1` is already parameterised, idempotent, and free of hardcoded values. It is invoked as the `queues` component rather than being absorbed, so it remains independently usable.

### 11. One App Service Plan is shared by all non-production environments

The plan hosts the dashboard API web app and every environment's Function App, `dev1` included. `AzureDeploy.AppService.ps1` already does this for `bat` and `test`: `asp-appservice-p0v3-linux-dev` carries the API alongside both Function Apps. Only `dev1` diverges, having been given `asp-appservice-p0v3-linux-dev1` by a separate script, and it is brought onto the shared plan.

Plan name is therefore a **configuration value, not a derived name** — it is one of the few resources that does not follow the per-environment pattern, and deriving it would produce a plan per environment, which is the opposite of the intent.

**Migrating `dev1`.** Both plans are in the same resource group and region, so the Function App can be moved between them. The move restarts the app, so it is performed when no drain or reconciliation is in flight. Once empty, `asp-appservice-p0v3-linux-dev1` is deleted; leaving an idle Premium v3 plan in place continues to bill.

**Contention is the accepted cost.** All apps on a plan share its instances, so the dashboard API and three Function Apps run on the same compute. A heavy release drain in one environment can affect responsiveness in another, and no environment scales independently.

This is worth revisiting once `add-entity-hold-release` lands. That change adds a release sweep and a retry sweep to every Function App, on top of the existing queue triggers and the awaiting-D365 sweep. Three environments' worth of additional timers on the smallest Premium v3 size may make the shared plan the wrong trade, and the symptom would be timers firing late rather than an obvious failure.

### 12. Production is a separate stamp, not another environment value

Production shares nothing: its own resource group, Service Bus namespace, App Service Plan, dashboard, and database. It is not another value of `-Environment` layered onto the same infrastructure.

This reinforces Decision 8. The two carried assumptions — resource group and Service Bus namespace as configuration values, and the dashboard scoped to non-production rather than treated as globally singular — are exactly what allow a production stamp to be added later as configuration plus a second shared dashboard, rather than as a rework of the naming function.

It also means the shared-plan decision above is explicitly a non-production one. Production sizing is an independent decision.

### 13. Reverse-proxy streaming configuration is preserved as documentation before deletion

`nginx.conf.template` contains the only record in the repository of how to make the simulation console's Server-Sent Events work through a reverse proxy:

```nginx
location /api/ {
    proxy_buffering off;         # events must be forwarded as produced
    proxy_cache off;
    proxy_read_timeout 3600s;    # a run may outlast a default request timeout
}
```

`SimulationController.Run` additionally sets `X-Accel-Buffering: no`, which is an nginx-specific hint and has no effect on a proxy that is not nginx.

These settings are captured in the deployment documentation before the file is removed, because they state the requirements streaming imposes on any proxy — not merely how one particular proxy was configured. If Server-Sent Events need fixing on the Static Web App path, this is the starting point, and it would otherwise be lost.

## Risks / Trade-offs

- **Removing the Container Apps path breaks anyone relying on it** -> Confirm no environment is served by Container Apps before deletion. The resources can remain deployed while the scripts are removed; deleting the scripts does not delete infrastructure.
- **Centralised naming breaks on resources that do not follow the pattern** -> Per-environment explicit overrides, with the script reporting which names were derived and which were overridden. The shared App Service Plan is the known case and is configured rather than derived.
- **A shared App Service Plan couples environments** -> Accepted deliberately for cost (Decision 11). Load in one environment can affect another, and no environment scales independently. Recorded so the symptom is attributed correctly rather than investigated as a defect.
- **Migrating `dev1` between plans restarts its Function App** -> Performed when no drain or reconciliation is in flight, and verified afterwards that queue triggers resume and no message was left mid-processing.
- **The emptied `dev1` plan continues to bill if forgotten** -> Deleted explicitly as a task rather than assumed to disappear with the Function App.
- **Simulation SSE may not survive the Static Web App linked backend, and no proven alternative exists** -> Server-Sent Events have only ever been exercised through the Vite development proxy. Neither nginx nor the Static Web App proxy has been verified, so the Container Apps path is not a fallback. Verified early, before any script is deleted, with the direct-to-API and polling alternatives pre-identified.
- **Deployed simulation may already be broken** -> If Server-Sent Events are used only locally, a failure on the deployed path could predate this change. Verification is therefore diagnostic as well as protective, and a failure found is not necessarily a regression this change introduced.
- **Deleting the container artifacts loses reverse-proxy streaming knowledge** -> Captured as documentation first (Decision 13).
- **A gitignored configuration file is easy to lose** -> A committed example documents every required key, and the script names the missing key explicitly rather than failing obscurely. Values also exist in Azure app settings and can be recovered from there.
- **Deleting scripts loses working knowledge encoded in them** -> The consolidated script is verified to reproduce each deleted script's behaviour before removal, and deletions happen last.
- **The `bat` and `test` shared App Service Plan does not fit a derived name** -> Plan allocation is resolved explicitly during implementation: either each environment gets its own plan, or plan name becomes a configuration value rather than a derived one. This is a cost decision as much as a naming one.

## Migration Plan

1. **Extract configuration.** Create `deploy.example.json` and per-environment files from the values currently embedded in the scripts. Verify each against the live Azure app settings rather than trusting the scripts.
2. **Build the script with `-WhatIf` only.** No mutating calls. Confirm the derived names match every existing resource exactly.
3. **Deploy one component to the lowest environment.** Start with `function` against `dev1`, the best-understood path, and compare the result with the existing script's output.
4. **Add remaining components** against the lowest environment: queues, api, frontend.
5. **Extend to `bat` and `test`**, resolving the shared App Service Plan question.
6. **Add the schema component**, once `add-schema-migration-runner` provides the runner. Until then it is a no-op that reports it was skipped.
7. **Delete superseded scripts**, Container Apps path last.
8. **Document** the configuration format and the procedure for onboarding a new environment.

**Rollback.** Until step 7, every existing script still works and the new script is additive. After step 7, the deleted scripts remain in version control history. No infrastructure is destroyed at any point.

## Open Questions

- **Does simulation SSE work through the Static Web App linked backend?** Server-Sent Events have only been exercised through the Vite development proxy. The `proxy_buffering off` and `proxy_read_timeout 3600s` settings in `nginx.conf.template` were never exercised in a deployed environment, so the Container Apps path is not a proven fallback. `SimulationController.Run`'s `X-Accel-Buffering: no` header is nginx-specific and likely inert on the Static Web App proxy. This may already be failing in the deployed environment today.
- **Does the shared plan hold once the hold and release sweeps exist?** `add-entity-hold-release` adds two timer sweeps per Function App. Across three environments on one Premium v3 instance alongside the dashboard API, that may exceed what the shared plan supports comfortably. Revisited after that change lands rather than pre-emptively sized now.
