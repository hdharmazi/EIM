# Deployment

Deployment is one script, `script/deployment/Deploy-Wilson.ps1`, run by two pipelines: `pipelines/eim.yml` for
non-production and `pipelines/eim-prd.yml` for production. The script reads a configuration from
`script/deployment/config/` and takes application code from a package built by
`script/deployment/New-DeploymentPackage.ps1`. There is no Bicep or ARM template in this repository; everything on
the Azure side is a component of the script.

| Document | For |
|---|---|
| this file | how the script, configuration and pipelines work |
| [devops-setup.md](devops-setup.md) | the one-time Azure DevOps setup (environments, service connections, variable groups) |
| [resource-rename-cutover.md](resource-rename-cutover.md) | the #5246 move from the old resource names to the new ones |
| [production-go-live.md](production-go-live.md) | standing up the production stamp and going live |
| [database-migrations.md](database-migrations.md) | migrations, and the expand/contract rule |
| [d365fsc-gateway.md](d365fsc-gateway.md) | the D365 F&SC gateway: consumers, keys, hosting |
| [deploy-from-scratch.md](deploy-from-scratch.md) | standing the system up in an empty subscription |

## The usual path: the pipeline

`pipelines/eim.yml` builds once and promotes the same package through every configuration:

```
Build -> Deploy_shared (nonprod) -> Deploy_bat -> Deploy_test
```

| Stage | DevOps environment | Approval | Deploys (code) |
|---|---|---|---|
| `Build` | | | tests, script self-test, package, package checks, publishes artifact `package` |
| `Deploy_shared` | `eim-wtc-nonprod` | yes | `gateway, backend, frontend` |
| `Deploy_bat` | `eim-wtc-bat` | yes | `automation` |
| `Deploy_test` | `eim-wtc-test` | yes | `automation` |

There is no development stage: development runs on a developer's machine (see "Local development" in the
repository `README.md`). A merge therefore waits for two approvals, shared then bat, before any Automation code
is deployed.

- A merge to `main` deploys code only.
- Infrastructure, app settings and migrations run only on a manual run, when you tick `deployInfra`,
  `applySettings` or `runMigrations`. `deployCode` (on by default) controls the code step.
- Each deploy stage runs only if the previous stage succeeded, and never for a pull request. A rejected
  `Deploy_shared` stops every environment.
- `lockBehavior: runLatest`: a newer run waiting at an environment replaces an older one that was never
  approved.
- Migrations run once per run, in `Deploy_shared`, before any environment's code. They must be expand-only;
  see [database-migrations.md](database-migrations.md).
- Each run carries its own package, so code rollback is "Rerun stage" on an older run. The schema only
  moves forward.

Each deploy stage runs, in order: preflight with a `-WhatIf` preview, `infra` (if ticked), `settings` (if
ticked), `myip`, `schema`, `myipremove` (if ticked, shared stage only), the code components, then `verify`
and `health`. A manual run with `deployCode` cleared and nothing else ticked deploys nothing: every stage runs
preflight only and skips `verify` and `health`.

The `Build` stage packages every configuration, `nonprod,bat,test,prdshared,prd`, but `eim.yml` deploys only the
non-production ones. The production configurations travel in the same package so that `eim-prd.yml` can promote
it (see [Production](#production)).

## Configurations and scopes

`-Environment` names a configuration file, `script/deployment/config/deploy.<name>.json`. Today there are five, in
two stamps:

| Configuration | Stamp | Scope | Owns |
|---|---|---|---|
| `nonprod` | non-production | `shared` | App Service plan, Log Analytics workspace, gateway, dashboard API and SPA, database migrations |
| `bat`, `test` | non-production | `environment` | the five Service Bus queues, and the Automation storage, Insights and Function App |
| `prdshared` | production | `shared` | the same as `nonprod`, for production |
| `prd` | production | `environment` | the same as `bat`, for production |

A stamp is the set of configurations with the same `sharedSuffix` (`nonprod` or `prd`). Each stamp has exactly
one `shared` configuration; preflight fails, naming them, when a stamp has none or more than one. Messages that
name the owning configuration (for example "`gateway` is owned by the shared configuration 'prdshared'") are
resolved from the stamp, not from the suffix.

There is no `dev` configuration, so `-Environment dev` fails as an unknown configuration. The legacy dev
Automation app, `azf-eim-customer-wtc-dev`, is retired with no replacement: it is listed under
`legacy.retiredAutomationApps` in `deploy.nonprod.json`, reported by `verify -Environment nonprod`, and deleted by
`remove -Environment nonprod` (see [resource-rename-cutover.md](resource-rename-cutover.md)).

**Deployment configurations are not ENVIRONMENT values.** The rows in `eimdb` use ENVIRONMENT values `DEV`,
`DEV2`, `BAT` and `TEST`. `DEV` and `DEV2` have rows but no new Function App and no configuration; the legacy dev
app is retired in runbook step 7 and deleted at decommission (step 13). Their rows stay.
Nothing in the script or tests reads or assumes the database's values. A message for any ENVIRONMENT value is
handled by whichever Automation app consumes the queue it arrives on.

The script holds no list of environments. Any name with a matching `deploy.<name>.json` is accepted.

### Configuration layering

Merged in this order, later layers winning:

1. `deploy.base.json`: everything common to all configurations
2. `deploy.<name>.json`: `environment`, `scope`, overrides and the `legacy` section
3. the file given by `-ConfigOverridesFile`
4. the JSON in the environment variable `EIM_DEPLOY_CONFIG_OVERRIDES` (the pipeline sets it from the variable
   group)

Objects merge key by key. Lists and plain values are replaced, so an override of
`automation.gatedFunctions` must list every function.

`deploy.base.json` holds the non-production values (resource group, namespace, SQL server, legacy verification
targets, simulation prefix). The production files reset each of them explicitly. **Every new key in
`deploy.base.json` must be judged for production:** if it identifies a non-production resource or behaviour, reset
it in `deploy.prdshared.json` and `deploy.prd.json`. `DeploymentConfigTests` guards the keys known today; it cannot
know about a key added later.

### Dashboard simulation

`dashboard.simulationEnabled` (base: `true`; production: `false`) controls the dashboard's Simulation console.
`settings` for a shared configuration always writes `Simulation__Enabled` to the dashboard API:

- `true`: it also writes `Simulation__ServiceBusConnectionString` and the four queue settings, with the queues of
  `dashboard.simulationQueuePrefix` (`bat`). A blank or placeholder prefix throws before anything is written.
- `false`: it writes none of them, and deletes any other `Simulation__*` setting the app still has.

The API reads `Simulation:Enabled` fail-safe: only `true` turns simulation on. Absent, empty or anything else
means off, and then every `/api/simulation/*` route returns 404 and the SPA hides the console. Because the
preflight drift check expects `Simulation__Enabled`, the first code-only run after this setting was introduced
fails until a run with `applySettings` has written it.

Values written as `<...>` are placeholders (subscription, Entra, excluded users). Preflight names every
placeholder the requested components need and stops before changing anything. Fill them in a commit (none of
them is secret) or through `EIM_DEPLOY_CONFIG_OVERRIDES`. **The Entra values must be committed** if the
pipeline is to deploy the SPA: the package, including the SPA, is built in the `Build` stage, which does not
read the variable group.

### Secrets

Two secrets, and only two places they come from:

| Variable | Holds |
|---|---|
| `EIM_DEPLOY_EIMDB` | the `eimdb` SQL connection string |
| `EIM_DEPLOY_SERVICEBUS` | the Service Bus namespace connection string |

The script reads them from the environment, or prompts for them (input hidden) when run interactively outside
a pipeline. It writes them to Azure through temporary files that are deleted afterwards, and never prints them
or puts them on a command line. No configuration file holds a secret, and a test fails the build if one does.

In the pipeline they are secret variables in the `eim-wtc-nonprod` variable group (production: `eim-wtc-prd`).
Secret variables reach a script only when mapped, so every `AzureCLI@2` step maps them through `env:`.

Preflight checks that each secret points at the configured resources: the server and `Initial Catalog` (or
`Database`) of `EIM_DEPLOY_EIMDB` must equal `names.sqlServer` and `names.sqlDatabase`, and the namespace in the
`Endpoint` of `EIM_DEPLOY_SERVICEBUS` must equal `names.serviceBusNamespace`. A mismatch names the variable, the
parsed name and the expected name, never the connection string. This catches a non-production secret in the
production group (and the reverse), and a connection string to `eimdb_dev`.

## Running the script by hand

You need PowerShell 7.2+ (`pwsh`), the Azure CLI (logged in with Contributor on the resource group) and, for
`frontend`, Node.js. You do not need the .NET SDK: the script never runs `dotnet publish`; it deploys a package.

```powershell
# Build a package (needs the .NET 10 SDK and Node 22)
./script/deployment/New-DeploymentPackage.ps1 -Environment nonprod, bat -Version local

# Preview: derived names and intended actions, changes nothing
./script/deployment/Deploy-Wilson.ps1 -Environment bat -Component all -WhatIf -PackagePath artifacts/package/wtc-eim-deploy-local

# One or more components. They always run in the fixed order, not the order given.
./script/deployment/Deploy-Wilson.ps1 -Environment nonprod -Component gateway, backend -PackagePath artifacts/package/wtc-eim-deploy-local
```

From an extracted package (the pipeline artifact, or the zip) the script sits next to `artifacts/`, so
`-PackagePath` is not needed. The package's `README.txt` lists the operator steps.

| Parameter | |
|---|---|
| `-Environment` | configuration name (`^[a-z0-9]+$`) |
| `-Component` | one or more components, or `all` |
| `-ConfigOverridesFile` | JSON merged over the configuration, before `EIM_DEPLOY_CONFIG_OVERRIDES` |
| `-PackagePath` | the package folder (holding `artifacts/manifest.json`); defaults to the script's folder |
| `-IncludeDefaultKeys` | `cutover` for `nonprod` only: also copy the generated `default` gateway keys |
| `-WhatIf` | preview; changes nothing |

### Components

Always run in this order:

| Component | Scope | Does |
|---|---|---|
| `preflight` | both | Fails, changing nothing, on unfilled placeholders, missing secrets, a legacy name equal to a name in use, missing package artifacts, or a package whose queue/timer functions differ from `automation.gatedFunctions`. Also fails when the resource group, Service Bus namespace, SQL server or database does not exist (only the group is reported when it is missing); when a secret points at a different server, catalog or namespace than the configuration names; when a name used by a production configuration is also used by a non-production one; and when the stamp has no single shared configuration. When code is deployed without `settings`, also fails on settings the configuration defines that an app lacks, and warns on differing values and retired settings. Names only, never values. Added automatically before any component that changes something. |
| `infra` | shared | plan, Log Analytics, gateway storage/Insights/app; the dashboard API and static web app **only if absent** |
| `infra` | environment | the five queues; Automation storage, Insights and app. Requires the shared plan, workspace and Service Bus namespace. |
| `settings` | shared | gateway and dashboard API app settings (including `Simulation__Enabled`; see [Dashboard simulation](#dashboard-simulation)) |
| `settings` | environment | Automation app settings; gated triggers written disabled only when absent; retired settings deleted |
| `myip` | shared | adds this machine's (or agent's) public IP to the SQL server |
| `schema` | shared | runs the packaged `ARM.Integration.DbMigrator.exe` (`--whatif` under `-WhatIf`) |
| `myipremove` | shared | deletes the rule `myip` added |
| `gateway` | shared | zip-deploys `gateway.zip` |
| `backend` | shared | zip-deploys `dashboard-api.zip` to the dashboard API, in place |
| `frontend` | shared | deploys the SPA to the static web app, in place, and links the API as its backend |
| `automation` | environment | zip-deploys `wilson-automation.zip`; refused while any gated setting is missing |
| `verify` | both | report only: host, state, Always On, plan, outbound IPs, gated trigger states, SWA backend |
| `health` | both | fails unless each Function App host is `Running` within 5 minutes, the dashboard API answers below HTTP 500 and the static web app serves its root (200 to 399) |

`all` expands to the components the configuration's scope owns. Asking for a component outside the scope is
refused, naming the scope that owns it (for example `schema` for `bat`, or `automation` for `nonprod`).

The script stops at the first failing component, names it and exits non-zero. Function App deploys stop the
app, deploy, and start it again in a `finally`, then sync triggers.

On demand only, never part of `all`, and each run on its own:

| Component | Environment scope | Shared scope |
|---|---|---|
| `cutover` | moves the gated triggers from the legacy Automation app to the new one; with no legacy app (`prd`) enables every gated trigger except `automation.keepDisabled` | copies the named gateway host keys and `D365Data`/`D365Service` function keys to the new gateway; refused when there is no legacy gateway (`prdshared`) |
| `rollback` | the reverse of `cutover`; refused once the legacy app is gone, and when there is no legacy app (`prd`) | refused: consumers roll back by switching their base URL |
| `remove` | deletes the resources listed under `legacy`; refused for a production configuration | the same, for the shared legacy resources and the retired dev Automation app; refused for a production configuration |

`cutover`, `rollback` and `remove` are described in [resource-rename-cutover.md](resource-rename-cutover.md).
`remove` runs only from an operator machine (never when `TF_BUILD` is set), needs a `remove -WhatIf` run the same
day on the same machine, and asks you to type the configuration name. It refuses while any gated function is
enabled on the configuration's legacy Automation app or on a retired Automation app; a stopped app still counts
until its `AzureWebJobs.<fn>.Disabled` settings say disabled.

### The SQL firewall: `myip` and `myipremove`

`myip` adds a rule for the caller's public IP to the SQL server named in `EIM_DEPLOY_EIMDB`. The server is
cross-checked against `names.sqlServer`, so a rule cannot be opened on one server while deploying to another.
The rule is `eim-deploy-<BUILD_BUILDID>` in the pipeline and `AllowDev-<username>` by hand. The pipeline always
runs `myipremove` in a `finally`, because the agent's IP changes per run. `myipremove` runs without preflight,
so a failing preflight cannot leave the rule open. Run `myipremove` yourself when you are done.

## Names

Pattern: `{type}-{workload}-{component}-{client}-{suffix}`, workload `eim`, client `wtc`. The suffix is the
configuration name for per-environment resources and the stamp's `sharedSuffix` (`nonprod` or `prd`) for shared
ones. Storage accounts cannot have
hyphens, so they use `st{client}{workload}{shortcomponent}{suffix}`. The script rejects a storage name that is
not `^[a-z0-9]{3,24}$` and a Function App name over 32 characters (longer names collide in the host ID).

| Resource | Name |
|---|---|
| App Service plan (P0v3, Linux) | `asp-eim-wtc-nonprod` |
| Log Analytics workspace | `log-eim-wtc-nonprod` |
| Gateway Function App, storage, Insights | `azf-eim-gateway-wtc-nonprod`, `stwtceimgwnonprod`, `appins-eim-gateway-wtc-nonprod` |
| Automation Function App, storage, Insights | `azf-eim-automation-wtc-{env}`, `stwtceimauto{env}`, `appins-eim-automation-wtc-{env}` |
| Service Bus queues | `{env}-d365fsc-dataevent`, `{env}-d365fsc-businessevents`, `{env}-d365fsc-to-capture`, `{env}-capture-to-d365fsc`, `{env}-capture-to-d365fsc-response` |

Every derived name can be overridden under `names`. These resources keep their current names through
overrides, and their renames are deferred:

| Resource | Kept name | Pattern name, if renamed later |
|---|---|---|
| Resource group | `d365fsc-integration-nonprod` | `rg-eim-wtc-nonprod` |
| Service Bus namespace | `asb-eim-wtc-nonprod` | `sbns-eim-wtc-nonprod` |
| Key Vault | `kv-d365fsc-wtc-nonprod` | `kv-eim-wtc-nonprod` |
| SQL server | `azql-wtc-sandbox` | `sql-eim-wtc-nonprod` |
| Database | `eimdb` | `sqldb-eim-wtc-nonprod` |
| Dashboard API | `wtc-dashboard-api-dev` | `app-eim-dashboard-api-wtc-nonprod` |
| Dashboard static web app | `wtc-dashboard-frontend-dev` | `swa-eim-dashboard-wtc-nonprod` |

**The dashboard is deployed in place.** `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` serve every
non-production environment despite the `-dev` suffix. The script deploys code and settings to them where they
are: no new dashboard resource, no new URL, no Entra change. `infra` creates them only if they are missing and
never recreates, renames or re-plans them. Neither is ever a `legacy` resource, so `remove` cannot target them.
`verify` labels them as shared.

New resources are tagged. Queues are not tagged.

## Hosting

One P0v3 Linux plan, `asp-eim-wtc-nonprod`, carries the gateway, the two Automation apps (bat, test) and (after
the runbook's plan move) the dashboard API. Every Function App gets Always On, HTTPS only, TLS 1.2, FTPS disabled
and `DOTNET-ISOLATED|10.0`, converged on every `infra` run. The environments share compute, so load in one can
affect another. A plan CPU/memory alert above 80% is part of the runbook; if it fires, change
`appServicePlan.sku` to `P1v3`. Production has its own P0v3 plan, `asp-eim-wtc-prd`, for `prdshared` and `prd`.

The gateway can be shared because the D365 environment arrives in the request path. The Automation host cannot:
its Service Bus triggers bind to one environment's queues at startup.

## Gated triggers

`automation.gatedFunctions` lists the Automation host's six queue and timer functions. A test keeps the list
equal to the functions in the code. `settings` writes `AzureWebJobs.<fn>.Disabled=true` for each one only when
the setting is absent, so a new Automation app starts with every trigger off, and a rerun never re-disables an
app that has been cut over. `cutover` turns them on. `automation` refuses to deploy while any gated setting is
missing.

`automation.retiredSettings` (today `PollSchedule`) are never written, are deleted from the Automation app by
`settings`, and only warn in the preflight drift check.

## Adding an environment

1. Add `script/deployment/config/deploy.<env>.json` with `"environment": "<env>"` and `"scope": "environment"`.
   Leave out `legacy`; a new environment has nothing to replace.
2. Add a `Deploy_<env>` stage to `pipelines/eim.yml` after the last one, with `runMigrations: false`, and a DevOps
   environment `eim-wtc-<env>`. Add the name to the `environments` list of the `Build` stage.
3. Run the pipeline manually with `deployInfra` and `applySettings`.
4. Run `Deploy-Wilson.ps1 -Environment <env> -Component cutover`. With no legacy app it enables every gated
   trigger except those in `automation.keepDisabled`.
5. Add the environment's rows to `ROUTETABLE` (a message with no route is dead-lettered with reason `NoRoute`),
   and its D365 profiles to `ENVIRONMENTTABLE`.

No script change, no new gateway and no new dashboard.

## Production

Production is a separate stamp: configurations `prdshared` (shared scope) and `prd` (environment scope), with
`"production": true` and `sharedSuffix` `prd`. It shares nothing with non-production: its own resource group,
Service Bus namespace, SQL server and database, plan, gateway, dashboard and Entra registration. Standing it up
and going live: [production-go-live.md](production-go-live.md).

**Pipeline.** `pipelines/eim-prd.yml`, manual only:

```
Deploy_prdshared (eim-wtc-prdshared) -> Deploy_prd (eim-wtc-prd)
```

It has no `Build` stage. It promotes the `package` artifact of an `eim` run, picked under **Resources** on the
manual run: a run from `main` whose `Deploy_test` stage succeeded, so production gets exactly the binaries bat and
test ran. The resource has `branch: main`, so the default offered is the latest `eim` run from `main`, never a pull
request or feature run. Both stages use the service connection `sc-eim-wtc-prd` and the variable group `eim-wtc-prd`;
migrations run only in `Deploy_prdshared`, behind its approval. See [pipelines/README.md](../../pipelines/README.md).

**Names.** Only `names.resourceGroup` is overridden, to the existing group `d365fsc-integration-prod`. Everything
else uses the pattern:

| Resource | Name |
|---|---|
| Service Bus namespace | `sbns-eim-wtc-prd` |
| SQL server, database | `sql-eim-wtc-prd`, `sqldb-eim-wtc-prd` |
| App Service plan, Log Analytics | `asp-eim-wtc-prd`, `log-eim-wtc-prd` |
| Gateway Function App, storage, Insights | `azf-eim-gateway-wtc-prd`, `stwtceimgwprd`, `appins-eim-gateway-wtc-prd` |
| Dashboard API, static web app | `app-eim-dashboard-api-wtc-prd`, `swa-eim-dashboard-wtc-prd` |
| Automation Function App, storage, Insights | `azf-eim-automation-wtc-prd`, `stwtceimautoprd`, `appins-eim-automation-wtc-prd` |
| Service Bus queues | `prd-d365fsc-dataevent`, `prd-d365fsc-businessevents`, `prd-d365fsc-to-capture`, `prd-capture-to-d365fsc`, `prd-capture-to-d365fsc-response` |

The script never creates the resource group, namespace, SQL server or database; the runbook does, and preflight
fails until they exist.

**Isolation rules.**

- The production files reset every non-production value they inherit from the base: names, Entra, excluded
  users, legacy verification targets and simulation. `DeploymentConfigTests` fails when one leaks.
- No name in use by a production configuration may be in use by a non-production one. Preflight checks this
  across every configuration, and so do the tests.
- The secrets must point at the production server, database and namespace (preflight secret-target check).
- There is no `legacy` section. `remove` refuses every configuration with `"production": true`, before any Azure
  call and even with `-WhatIf`. `cutover` for `prdshared` and `rollback` for `prd` are refused: there is no
  legacy gateway or Automation app. `cutover` for `prd` is the go-live switch that enables the gated triggers.

**The `production` flag.** `deploy.base.json` sets `"production": false`; `prdshared` and `prd` set `true`. The
script reads it for the `remove` refusal and the isolation check, and the package README for its notes.

**Simulation off.** `dashboard.simulationEnabled` is `false`, so the production dashboard API gets
`Simulation__Enabled=false` and no other `Simulation__*` setting. The API removes the simulation routes (404) and
the SPA hides the console. See [Dashboard simulation](#dashboard-simulation).

## Differences from the Wastequip scripts

`Deploy-Wilson.ps1` and `New-DeploymentPackage.ps1` are ports of the Wastequip EIM scripts. Where they differ,
on purpose:

- No Key Vault `secrets` component. Nothing in Wilson reads Key Vault; secrets come from the two environment
  variables.
- No `sql` or `sqlusers` component. The SQL server and database exist and are kept.
- No `-NameSuffix`.
- No release tags.
- `remove` deletes by explicit name and type from the `legacy` section, not by tag, because the legacy resources
  are untagged.
- The dashboard keeps its non-pattern names and is deployed in place.
- `cutover` and `rollback` move gated triggers between two apps, and `cutover` for `nonprod` copies gateway keys.

Keep this list current when merging changes from Wastequip.

## Decisions

**Production is a separate stamp.** It shares nothing with non-production: its own resource group, Service Bus
namespace, plan, dashboard, gateway, database and pipeline (see [Production](#production)). It is built from base
plus explicit resets in `deploy.prdshared.json` and `deploy.prd.json`, guarded by tests. If a third stamp appears,
introduce a stamp layer (`deploy.stamp.<sharedSuffix>.json`) between the base and the configuration rather than
adding more resets.

**`eimdb_dev` is not a deployment target.** Every non-production environment uses `eimdb`. The `eimdb_dev`
catalog on the same server is a leftover of the old development setup; it will be dropped by hand later, outside
this change. Local development uses a local SQL Server instead (see "Local database" in
[database-migrations.md](database-migrations.md) and "Local development" in the repository `README.md`). Until
`eimdb_dev` is gone, remember that a connection string that looks right can still point at the wrong catalog on
the shared server, and the failure is silent because both have the same schema. Check `Initial Catalog`, not only
the server.

**Two Entra registrations exist, and the difference is intentional.** The deployed registration (tenant
`446b6761`, client `5d1e26b3`) is the one the configuration carries. The pair in
`ARM.Integration.Dashboard.API/appsettings.json` (`5e8a5341` / `317063cf`) is for local development only. Getting
these backwards produces a SPA that loads and then fails every API call, because the API validates tokens
against a registration they were not issued for.

**Queues.** New queues get a 1 GB maximum size, 14-day default TTL, 60-second lock duration, maximum delivery
count 10, dead-lettering on expiration and no partitioning. Queue deletion is deliberately not a component: a
queue holds undelivered messages.

**Containers are not used.** The Container Apps path (`docker-compose.yml`, the Dockerfiles,
`nginx.conf.template`) was removed. If containers come back, the reverse proxy in front of the API must keep
three settings the deleted `nginx.conf.template` had, because the simulation console streams Server-Sent Events:
`proxy_buffering off`, `proxy_cache off` and `proxy_read_timeout 3600s`. The same need applies to the Static Web
App linked backend, which has never been exercised with SSE. If the simulation console arrives all at once or is
cut off, that is the cause.
