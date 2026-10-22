# Deployment

Non-production deployment is one script, `script/deployment/Deploy-Wilson.ps1`, run by one pipeline,
`pipelines/eim.yml`. The script reads a configuration from `script/deployment/config/` and takes application code
from a package built by `script/deployment/New-DeploymentPackage.ps1`. There is no Bicep or ARM template in this
repository; everything on the Azure side is a component of the script.

| Document | For |
|---|---|
| this file | how the script, configuration and pipeline work |
| [devops-setup.md](devops-setup.md) | the one-time Azure DevOps setup (environments, service connection, variable group) |
| [resource-rename-cutover.md](resource-rename-cutover.md) | the #5246 move from the old resource names to the new ones |
| [database-migrations.md](database-migrations.md) | migrations, and the expand/contract rule |
| [d365fsc-gateway.md](d365fsc-gateway.md) | the D365 F&SC gateway: consumers, keys, hosting |
| [deploy-from-scratch.md](deploy-from-scratch.md) | standing the system up in an empty subscription |

## The usual path: the pipeline

`pipelines/eim.yml` builds once and promotes the same package through every configuration:

```
Build -> Deploy_shared (nonprod) -> Deploy_dev -> Deploy_bat -> Deploy_test
```

| Stage | DevOps environment | Approval | Deploys (code) |
|---|---|---|---|
| `Build` | | | tests, script self-test, package, package checks, publishes artifact `package` |
| `Deploy_shared` | `eim-wtc-nonprod` | yes | `gateway, backend, frontend` |
| `Deploy_dev` | `eim-wtc-dev` | no | `automation` |
| `Deploy_bat` | `eim-wtc-bat` | yes | `automation` |
| `Deploy_test` | `eim-wtc-test` | yes | `automation` |

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

## Configurations and scopes

`-Environment` names a configuration file, `script/deployment/config/deploy.<name>.json`. Today there are four:

| Configuration | Scope | Owns |
|---|---|---|
| `nonprod` | `shared` | App Service plan, Log Analytics workspace, gateway, dashboard API and SPA, database migrations |
| `dev`, `bat`, `test` | `environment` | the five Service Bus queues, and the Automation storage, Insights and Function App |

**Deployment configurations are not ENVIRONMENT values.** The rows in `eimdb` use ENVIRONMENT values `DEV`,
`DEV2`, `BAT` and `TEST`. `DEV2` has rows but no Function App and no configuration. Nothing in the script or
tests reads or assumes the database's values. A message for any ENVIRONMENT value is handled by whichever
Automation app consumes the queue it arrives on.

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

In the pipeline they are secret variables in the `eim-wtc-nonprod` variable group. Secret variables reach a
script only when mapped, so every `AzureCLI@2` step maps them through `env:`.

## Running the script by hand

You need PowerShell 7.2+ (`pwsh`), the Azure CLI (logged in with Contributor on the resource group) and, for
`frontend`, Node.js. You do not need the .NET SDK: the script never runs `dotnet publish`; it deploys a package.

```powershell
# Build a package (needs the .NET 10 SDK and Node 22)
./script/deployment/New-DeploymentPackage.ps1 -Environment nonprod, dev -Version local

# Preview: derived names and intended actions, changes nothing
./script/deployment/Deploy-Wilson.ps1 -Environment dev -Component all -WhatIf -PackagePath artifacts/package/wtc-eim-deploy-local

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
| `preflight` | both | Fails, changing nothing, on unfilled placeholders, missing secrets, a legacy name equal to a name in use, missing package artifacts, or a package whose queue/timer functions differ from `automation.gatedFunctions`. When code is deployed without `settings`, also fails on settings the configuration defines that an app lacks, and warns on differing values and retired settings. Names only, never values. Added automatically before any component that changes something. |
| `infra` | shared | plan, Log Analytics, gateway storage/Insights/app; the dashboard API and static web app **only if absent** |
| `infra` | environment | the five queues; Automation storage, Insights and app. Requires the shared plan, workspace and Service Bus namespace. |
| `settings` | shared | gateway and dashboard API app settings |
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
refused, naming the scope that owns it (for example `schema` for `dev`, or `automation` for `nonprod`).

The script stops at the first failing component, names it and exits non-zero. Function App deploys stop the
app, deploy, and start it again in a `finally`, then sync triggers.

On demand only, never part of `all`, and each run on its own:

| Component | Environment scope | Shared scope |
|---|---|---|
| `cutover` | moves the gated triggers from the legacy Automation app to the new one | copies the named gateway host keys and `D365Data`/`D365Service` function keys to the new gateway |
| `rollback` | the reverse of `cutover`; refused once the legacy app is gone | refused: consumers roll back by switching their base URL |
| `remove` | deletes the resources listed under `legacy` | the same, for the shared legacy resources |

`cutover`, `rollback` and `remove` are described in [resource-rename-cutover.md](resource-rename-cutover.md).
`remove` runs only from an operator machine (never when `TF_BUILD` is set), needs a `remove -WhatIf` run the same
day on the same machine, and asks you to type the configuration name.

### The SQL firewall: `myip` and `myipremove`

`myip` adds a rule for the caller's public IP to the SQL server named in `EIM_DEPLOY_EIMDB`. The server is
cross-checked against `names.sqlServer`, so a rule cannot be opened on one server while deploying to another.
The rule is `eim-deploy-<BUILD_BUILDID>` in the pipeline and `AllowDev-<username>` by hand. The pipeline always
runs `myipremove` in a `finally`, because the agent's IP changes per run. `myipremove` runs without preflight,
so a failing preflight cannot leave the rule open. Run `myipremove` yourself when you are done.

## Names

Pattern: `{type}-{workload}-{component}-{client}-{suffix}`, workload `eim`, client `wtc`. The suffix is the
configuration name for per-environment resources and `nonprod` for shared ones. Storage accounts cannot have
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

One P0v3 Linux plan, `asp-eim-wtc-nonprod`, carries the gateway, the three Automation apps and (after the
runbook's plan move) the dashboard API. Every Function App gets Always On, HTTPS only, TLS 1.2, FTPS disabled
and `DOTNET-ISOLATED|10.0`, converged on every `infra` run. The environments share compute, so load in one can
affect another. A plan CPU/memory alert above 80% is part of the runbook; if it fires, change
`appServicePlan.sku` to `P1v3`.

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

**Production is out of scope.** It shares nothing with non-production and will be a separate stamp: its own
resource group, Service Bus namespace, plan, dashboard, gateway and database. Do not extend these configurations
to cover it.

**`eimdb_dev` is not a deployment target.** Every non-production environment uses `eimdb`. The `eimdb_dev`
catalog on the same server is for local development only. Because the two share a server, a connection string
that looks right can still point at the wrong catalog, and the failure is silent because both have the same
schema. Check `Initial Catalog`, not only the server.

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
