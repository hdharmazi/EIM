# Deployment

Deployment is one script, `script/deployment/Deploy-Wilson.ps1`, run by two pipelines: `pipelines/eim.yml` for
non-production and `pipelines/eim-prd.yml` for production. The script reads a configuration from
`script/deployment/config/` and takes application code from a package built by
`script/deployment/New-DeploymentPackage.ps1`. There is no Bicep or ARM template in this repository; everything on
the Azure side is a component of the script.

| Document | For |
|---|---|
| this file | how the script, configuration and pipelines work |
| [devops-setup.md](devops-setup.md) | the one-time Azure DevOps setup (environments, service connections, variable groups, SQL admin group) |
| [production-go-live.md](production-go-live.md) | standing up the production environment and going live |
| [database-migrations.md](database-migrations.md) | migrations, and the expand/contract rule |
| [d365fsc-gateway.md](d365fsc-gateway.md) | the D365 F&SC gateway: consumers, keys, hosting |
| [deploy-from-scratch.md](deploy-from-scratch.md) | standing the system up in an empty subscription |

## The usual path: the pipeline

`pipelines/eim.yml` builds once and promotes the same package through every non-production configuration:

```
Build -> Deploy_bat -> Deploy_test
```

| Stage | DevOps environment | Approval | Owns resources | Deploys (code) |
|---|---|---|---|---|
| `Build` | | | | tests, script self-test, package, package checks, publishes artifact `package` |
| `Deploy_bat` | `eim-wtc-bat` | yes | yes | `gateway, backend, frontend, automation` |
| `Deploy_test` | `eim-wtc-test` | yes | no (uses bat) | `automation` |

There is no development stage: development runs on a developer's machine (see "Local development" in the
repository `README.md`).

- A merge to `main` deploys code only.
- Infrastructure, app settings, migrations and database access run only on a manual run, when you tick
  `deployInfra`, `applySettings`, `runMigrations` or `grantDatabaseAccess`. `deployCode` (on by default) controls
  the code step.
- Each deploy stage runs only if the previous stage succeeded, and never for a pull request. A rejected
  `Deploy_bat` stops `Deploy_test`.
- `lockBehavior: runLatest`: a newer run waiting at an environment replaces an older one that was never
  approved.
- Migrations run only in a stage that owns its resources (`Deploy_bat`), before any code. `runMigrations` on a
  stage with `ownsResources: false` is a template error. Migrations must be expand-only; see
  [database-migrations.md](database-migrations.md).
- Each run carries its own package, so returning to earlier code is "Rerun stage" on an older run made with this
  tooling. The schema only moves forward.

Each deploy stage runs, in order:

1. preflight with a `-WhatIf` preview
2. `infra`, plus `sql` for an owner (if `deployInfra`)
3. `settings` (if `applySettings`)
4. the database step (if `runMigrations` or `grantDatabaseAccess`): `myip`, then the SQL token is primed, then
   `schema` and/or `sqlusers`, and `myipremove` in a `finally`
5. the code components
6. `verify` and `health`

A manual run with `deployCode` cleared and nothing else ticked deploys nothing: every stage runs preflight only and
skips `verify` and `health`.

The `Build` stage packages every configuration, `bat,test,prd`, but `eim.yml` deploys only the non-production ones.
The production configuration travels in the same package so that `eim-prd.yml` can promote it (see
[Production](#production)).

## Configurations: owners and `uses`

`-Environment` names a configuration file, `script/deployment/config/deploy.<name>.json`. There is one file per
environment, plus `deploy.base.json`:

| Configuration | `uses` | Owns |
|---|---|---|
| `bat` | | resource group, Service Bus namespace, Key Vault, SQL server and database, App Service plan, Log Analytics workspace, gateway, dashboard API and static web app, database migrations, its queues and its Automation app |
| `test` | `bat` | its queues and its Automation storage, Insights and Function App (on bat's plan) |
| `prd` | | the same as `bat`, for production, all of it separate |

**Owner.** A configuration without `uses` owns every resource it names, and may run every component.

**`uses`.** A configuration with `"uses": "<owner>"` borrows the owner's resource group, namespace, SQL server and
database, plan, gateway and dashboard, and by default its vault and workspace. It owns its queues and its Automation
app, and optionally its Key Vault and Log Analytics workspace (see below). It may
run only `preflight`, `infra`, `settings`, `myip`, `sqlusers`, `myipremove`, `automation`, `verify` and `health`;
asking for `sql`, `schema`, `gateway`, `backend` or `frontend` fails, naming the owner, before any Azure call.

**Locked and overridable names.** Ten borrowed names are locked: `resourceGroup`, `serviceBusNamespace`,
`sqlServer`, `sqlDatabase`, `appServicePlan`, `gatewayApp`, `gatewayStorage`, `gatewayInsights`, `dashboardApi` and
`staticWebApp`. The other two, `keyVault` and `logAnalytics`, serve only the configuration's own Automation app, so
a configuration with `uses` may set them in `names` to make that vault or workspace its own:

```json
{ "environment": "test", "uses": "bat", "names": { "keyVault": "kv-eim-wtc-test" }, "automation": { "triggersEnabled": true } }
```

`infra` then creates the vault or workspace in the owner's resource group, tagged with the configuration's
environment. The plan stays locked because an Automation app on the owner's plan keeps the owner's outbound
addresses, so the owner's SQL firewall still covers it. The owner's dashboard API and gateway are unaffected: they
keep the owner's vault and workspace. A blank override, or one equal to the owner's name, borrows the owner's.

Deployer rights on an owned vault: `preflight` reads it and `infra` creates it and sets its access policies, both
covered by Contributor on the resource group. `settings` needs secret `get` and `set` on it; that is automatic when
the pipeline's principal created the vault (`az keyvault create` grants the creator a policy), and a vault that
existed before needs an access policy for the deployer by hand. The other components need nothing on the vault. An
owned workspace needs nothing beyond Contributor on the resource group. With an owned vault, the configuration's old
access policy on the owner's vault stays in place, because the script removes nothing; remove it by hand if wanted.

Preflight also fails when:

- `uses` names a configuration that does not exist, the configuration itself, or a configuration that itself has
  `uses` (no chains)
- a production configuration uses a non-production one
- the configuration overrides a locked name
- an owner resource it needs (resource group, namespace, vault, plan, workspace, SQL server or database), or the
  owner's Key Vault secret `eim-servicebus`, does not exist yet: run `infra`, `sql` and `settings` for the owner
  first. A vault or workspace the configuration owns is not needed yet: preflight notes that `infra` will create it,
  and the owner's secret is not checked when the vault is owned.

There is no `dev` configuration, so `-Environment dev` fails as an unknown configuration.

**Deployment configurations are not ENVIRONMENT values.** The rows in `eimdb` use ENVIRONMENT values `DEV`,
`DEV2`, `BAT` and `TEST`. `DEV` and `DEV2` have rows but no Function App and no configuration; their rows stay.
Nothing in the script or tests reads or assumes the database's values. A message for any ENVIRONMENT value is
handled by whichever Automation app consumes the queue it arrives on.

The script holds no list of environments. Any name with a matching `deploy.<name>.json` is accepted.

### Configuration layering

Merged in this order, later layers winning:

1. `deploy.base.json`: everything common to all configurations
2. `deploy.<name>.json`: `environment`, `uses`, `production`, `names` overrides and other overrides
3. the file given by `-ConfigOverridesFile`
4. the JSON in the environment variable `EIM_DEPLOY_CONFIG_OVERRIDES` (the pipeline sets it from the variable
   group)

Objects merge key by key. Lists and plain values are replaced, so an override of
`automation.gatedFunctions` must list every function, and one of `serviceBusQueues` every queue. A configuration that sets the removed keys `scope` or the
old shared name suffix fails to load, naming the file.

`deploy.base.json` holds no names. It holds the non-production values that are not names: Entra, excluded users
and the simulation settings. `deploy.prd.json` resets each of them explicitly. **Every new key in
`deploy.base.json` must be judged for production:** if it identifies a non-production resource or behaviour, reset
it in `deploy.prd.json`. `DeploymentConfigTests` guards the keys known today; it cannot know about a key added
later.

### Dashboard simulation

`dashboard.simulationEnabled` (base: `true`; production: `false`) controls the dashboard's Simulation console.
`settings` for an owner always writes `Simulation__Enabled` to the dashboard API:

- `true`: it also writes `Simulation__ServiceBusConnectionString` (a Key Vault reference) and the four queue
  settings, with the queues of `dashboard.simulationQueuePrefix` (`bat`). A blank or placeholder prefix throws
  before anything is written.
- `false`: it writes none of them, and deletes any other `Simulation__*` setting the app still has.

The API reads `Simulation:Enabled` fail-safe: only `true` turns simulation on. Absent, empty or anything else
means off, and then every `/api/simulation/*` route returns 404 and the SPA hides the console.

Values written as `<...>` are placeholders (subscription, Entra, excluded users, the production SQL admin group).
Preflight names every placeholder the requested components need and stops before changing anything. Fill them in a
commit (none of them is secret) or through `EIM_DEPLOY_CONFIG_OVERRIDES`. **The Entra values must be committed** if
the pipeline is to deploy the SPA: the package, including the SPA, is built in the `Build` stage, which does not
read the variable group.

### No deployment secrets

The script needs no secret and the pipelines map none. Every `AzureCLI@2` step maps only
`EIM_DEPLOY_CONFIG_OVERRIDES`, which holds no secret. No configuration file holds a secret, and a test fails the
build if one does.

- **SQL.** The apps connect with their system-assigned managed identity:
  `Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<database>;Authentication=Active Directory Managed Identity;Encrypt=True;...`.
  The value is not secret. It is written as `EIMDB` on the Automation app and the gateway, and as
  `ConnectionStrings__EIMDB` on the dashboard API. `schema` and `sqlusers` sign in as the deployer (the Azure CLI
  identity, which in the pipeline is the service connection's principal). The deployer must be in the SQL server's
  Entra admin group; see [devops-setup.md](devops-setup.md).
- **Service Bus.** `settings` for an owner reads the namespace's `RootManageSharedAccessKey` connection string and
  writes it to the Key Vault secret `eim-servicebus`, only when it is missing or different. The value passes
  through a temporary file that is deleted afterwards, and is never printed. `ServiceBus` on the Automation app and
  `Simulation__ServiceBusConnectionString` on the dashboard API are Key Vault references,
  `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/eim-servicebus/)`. A configuration with
  `uses` references its owner's secret. One that owns its vault gets its own copy of `eim-servicebus`, read from
  the owner's namespace by its own `settings`. After a namespace key rotation, run `settings` for both the owner
  and that configuration.

`infra` gives every deployed app a system-assigned identity and secret `get` on the Key Vault through an access
policy. A vault in RBAC-authorization mode fails preflight, naming the vault. `verify` reports the Key Vault
reference status of each app, and `health` fails when a reference is not `Resolved`.

## Running the script by hand

You need PowerShell 7.2+ (`pwsh`), the Azure CLI (logged in with Contributor on the resource group; an isolated
environment whose resource group does not exist yet needs rights to create it) and, for `frontend`, Node.js. You do
not need the .NET SDK: the script never runs `dotnet publish`; it deploys a package.

```powershell
# Build a package (needs the .NET 10 SDK and Node 22)
./script/deployment/New-DeploymentPackage.ps1 -Environment bat, test -Version local

# Preview: derived names and intended actions, changes nothing
./script/deployment/Deploy-Wilson.ps1 -Environment bat -Component all -WhatIf -PackagePath artifacts/package/wtc-eim-deploy-local

# One or more components. They always run in the fixed order, not the order given.
./script/deployment/Deploy-Wilson.ps1 -Environment bat -Component gateway, backend -PackagePath artifacts/package/wtc-eim-deploy-local

# Database work needs the firewall rule; myipremove always runs last
./script/deployment/Deploy-Wilson.ps1 -Environment test -Component myip, sqlusers, myipremove -WhatIf -PackagePath artifacts/package/wtc-eim-deploy-local
```

From an extracted package (the pipeline artifact, or the zip) the script sits next to `artifacts/`, so
`-PackagePath` is not needed. The package's `README.txt` lists the operator steps.

| Parameter | |
|---|---|
| `-Environment` | configuration name (`^[a-z0-9]+$`); required |
| `-Component` | one or more components, or `all` |
| `-ConfigOverridesFile` | JSON merged over the configuration, before `EIM_DEPLOY_CONFIG_OVERRIDES` |
| `-PackagePath` | the package folder (holding `artifacts/manifest.json`); defaults to the script's folder |
| `-WhatIf` | preview; changes nothing and opens no SQL connection |

### Components

Always run in this order. A configuration with `uses` may run only those marked "both".

| Component | For | Does |
|---|---|---|
| `preflight` | both | Fails, changing nothing, on unfilled placeholders the requested components need, a wrong `uses`, a production name shared with a non-production configuration, a missing or non-boolean `automation.triggersEnabled`, a `keepDisabled` entry outside `gatedFunctions`, an invalid `serviceBusQueues` (see [Service Bus queues](#service-bus-queues)), missing package artifacts, or a package whose queue/timer functions differ from `automation.gatedFunctions`. Also fails on a Key Vault in RBAC mode; for a configuration with `uses`, on a missing owner resource or owner secret; and, when `schema` or `sqlusers` is requested, on a SQL server with no Entra admin. When code is deployed without `settings`, also fails on settings the configuration defines that an app lacks, and warns on differing values and retired settings. Names only, never values. Added automatically before any component that changes something. |
| `infra` | owner | resource group, Service Bus namespace, Key Vault, plan, Log Analytics, its queues, gateway, dashboard API, static web app and Automation, each created only when missing; identities and Key Vault access policies for every deployed app |
| `infra` | `uses` | its queues and its Automation storage, Insights and app (on the owner's plan, Insights in the owner's workspace); the app's identity and access policy on the owner's vault. With an owned `keyVault` or `logAnalytics`: that vault or workspace, created in the owner's resource group when missing, Insights in the owned workspace and the access policy on the owned vault. An existing Automation Insights linked to another workspace is not moved: `infra` warns, and it must be relinked or recreated by hand |
| `sql` | owner | a missing SQL server (Entra-only authentication, Entra admin from `sql.entraAdmin`) and database; the firewall rules on a server it manages; a report, and warnings, for an existing server |
| `settings` | owner | the Key Vault secret `eim-servicebus`, then the gateway, dashboard API (see [Dashboard simulation](#dashboard-simulation)) and Automation app settings |
| `settings` | `uses` | its Automation app settings; first, with an owned `keyVault`, the Key Vault secret `eim-servicebus` in that vault |
| `myip` | both | adds this machine's (or agent's) public IP to the SQL server |
| `schema` | owner | runs the packaged `ARM.Integration.DbMigrator.exe` as the deployer (`--whatif` under `-WhatIf`) |
| `sqlusers` | both | a contained database user for each deployed app's identity, with `db_datareader`, `db_datawriter` and `EXECUTE` |
| `myipremove` | both | deletes the rule `myip` added |
| `gateway` | owner | zip-deploys `gateway.zip` |
| `backend` | owner | zip-deploys `dashboard-api.zip` to the dashboard API, in place |
| `frontend` | owner | deploys the SPA to the static web app, in place, and links the API as its backend |
| `automation` | both | zip-deploys `wilson-automation.zip`; refused while any gated setting is missing |
| `verify` | both | report only: host, state, Always On, plan, outbound IPs, gated trigger states, Key Vault references, SWA backend |
| `health` | both | fails unless each Function App host is `Running` within 5 minutes, the dashboard API answers below HTTP 500, the static web app serves its root (200 to 399) and the Key Vault references resolve |

`all` expands to the components the configuration may run, except `myip` and `myipremove`.

The script stops at the first failing component, names it and exits non-zero. Function App deploys stop the
app, deploy, and start it again in a `finally`, then sync triggers.

**The script deletes no Azure resource.** Its only deletions are the app settings it owns (retired settings and
stale `Simulation__*` and `Gateway__Applications__*` settings), the `myip` firewall rule, and stale
`app-outbound-*` rules on a SQL server it manages.

### Create if missing, use as found

A resource the script creates is tagged `eim-workload`, `eim-client` and `eim-environment` (the configuration that
created it). A resource with an `eim-environment` tag is *managed*: `infra` keeps its Function App baseline (Always
On, HTTPS only, TLS 1.2, FTPS disabled, `DOTNET-ISOLATED|10.0`), the dashboard API site configuration and, for a
SQL server, the firewall rules.

A resource the script did not create is used as found: no SKU, tier, plan, tag, firewall, authentication or site
configuration change. The only additions are the system-assigned identity on every app the configuration deploys,
and secret `get` for those identities on the Key Vault. This is how `bat` keeps the existing non-production
resources, including the dashboard API `wtc-dashboard-api-dev`.

### The SQL server: `sql`

For a missing server, `sql` creates it with Entra-only authentication and the Entra admin group from
`sql.entraAdmin` and `sql.entraAdminObjectId` (`sql.entraAdminType`, default `Group`); both must be filled in.
There is no SQL admin login and no password. A missing database is created with `sql.sku` and
`sql.backupRedundancy` (base: `S0`, `Local`).

On a managed server, `sql.firewall` decides the rules: `appOutboundIps` (the default) keeps one
`app-outbound-<a-b-c-d>` rule for each outbound IP of the configuration's apps and deletes stale ones;
`azureServices` ensures `AllowAllWindowsAzureIps`. When no outbound IPs are found, the rules are left as they are,
with a warning.

An existing server the script did not create (`azql-wtc-sandbox`) is only reported: its Entra admin, whether
Entra-only authentication is on, and whether every app outbound IP is covered. A missing Entra admin or an uncovered
IP is a warning naming the server; the fix is manual.

### Database users: `sqlusers`

`sqlusers` signs in as the deployer with an Azure CLI token for `https://database.windows.net/`. For each app
identity the configuration deploys (owner: gateway, dashboard API and its Automation app; `uses`: its Automation
app) it reads the identity's client id and, if the user is missing, runs
`CREATE USER [<app name>] WITH SID = <client id>, TYPE = E`, then adds `db_datareader` and `db_datawriter` and
grants `EXECUTE`. Every step is idempotent. It never drops a user: a user with the app's name but another identity
(the app was recreated) fails, naming the user, and must be dropped by hand. It prints each user with its roles.

`sqlusers` writes to the database. In the pipeline it runs only when `grantDatabaseAccess` is ticked, behind the
stage approval. Under `-WhatIf` it lists the users and roles and opens no SQL connection. Run it before the first
`settings` that switches an app to managed-identity SQL access.

### The SQL firewall: `myip` and `myipremove`

`myip` adds a rule for the caller's public IP to the configuration's SQL server (`names.sqlServer`). The rule is
`eim-deploy-<BUILD_BUILDID>` in the pipeline and `AllowDev-<username>` by hand. The pipeline always runs
`myipremove` in a `finally`, because the agent's IP changes per run. `myipremove` on its own runs without
preflight, so a failing preflight cannot leave the rule open. Run `myipremove` yourself when you are done.

## Names

Pattern: `{type}-{workload}-{component}-{client}-{environment}`, workload `eim`, client `wtc`, environment the
configuration's own `environment`. Storage accounts cannot have hyphens, so they use
`st{client}{workload}{shortcomponent}{environment}`. A non-blank `names.<key>` wins over the pattern; a blank one
falls back to it. A configuration with `uses` takes the owner's names for everything it borrows. For its
overridable names (`keyVault`, `logAnalytics`), a blank `names.<key>` borrows the owner's name; it does not derive
one from the pattern.

| Key | Pattern |
|---|---|
| `resourceGroup` | `rg-{w}-{c}-{e}` |
| `serviceBusNamespace` | `sbns-{w}-{c}-{e}` |
| `keyVault` | `kv-{w}-{c}-{e}` |
| `sqlServer`, `sqlDatabase` | `sql-{w}-{c}-{e}`, `sqldb-{w}-{c}-{e}` |
| `appServicePlan`, `logAnalytics` | `asp-{w}-{c}-{e}`, `log-{w}-{c}-{e}` |
| `gatewayApp`, `gatewayStorage`, `gatewayInsights` | `azf-{w}-gateway-{c}-{e}`, `st{c}{w}gw{e}`, `appins-{w}-gateway-{c}-{e}` |
| `dashboardApi`, `staticWebApp` | `app-{w}-dashboard-api-{c}-{e}`, `swa-{w}-dashboard-{c}-{e}` |
| `automationApp`, `automationStorage`, `automationInsights` | `azf-{w}-automation-{c}-{e}`, `st{c}{w}auto{e}`, `appins-{w}-automation-{c}-{e}` |
| `queuePrefix` | `{e}` |

Preflight rejects a storage name that is not `^[a-z0-9]{3,24}$`, a Function App name over 32 characters (longer
names collide in the host ID), an invalid Key Vault name and an invalid SQL server name.

Resolved names:

| Resource | `bat` | `test` (uses bat) | `prd` |
|---|---|---|---|
| Resource group | `d365fsc-integration-nonprod` (override) | bat's | `d365fsc-integration-prod` (override) |
| Service Bus namespace | `asb-eim-wtc-nonprod` (override) | bat's | `sbns-eim-wtc-prd` |
| Key Vault | `kv-d365fsc-wtc-nonprod` (override) | bat's | `kv-eim-wtc-prd` |
| SQL server, database | `azql-wtc-sandbox`, `eimdb` (overrides) | bat's | `sql-eim-wtc-prd`, `sqldb-eim-wtc-prd` |
| Plan, workspace | `asp-eim-wtc-bat`, `log-eim-wtc-bat` | bat's | `asp-eim-wtc-prd`, `log-eim-wtc-prd` |
| Gateway | `azf-eim-gateway-wtc-bat`, `stwtceimgwbat`, `appins-eim-gateway-wtc-bat` | bat's | `azf-eim-gateway-wtc-prd`, `stwtceimgwprd`, `appins-eim-gateway-wtc-prd` |
| Dashboard | `wtc-dashboard-api-dev`, `wtc-dashboard-frontend-dev` (overrides) | bat's | `app-eim-dashboard-api-wtc-prd`, `swa-eim-dashboard-wtc-prd` |
| Automation | `azf-eim-automation-wtc-bat`, `stwtceimautobat`, `appins-eim-automation-wtc-bat` | `azf-eim-automation-wtc-test`, `stwtceimautotest`, `appins-eim-automation-wtc-test` | `azf-eim-automation-wtc-prd`, `stwtceimautoprd`, `appins-eim-automation-wtc-prd` |
| Queues | `bat-*` | `test-*` | `prd-*` |

The queues of each prefix are listed in `serviceBusQueues`; see [Service Bus queues](#service-bus-queues).

**The dashboard is deployed in place.** `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` serve every
non-production environment despite the `-dev` suffix. The script deploys code and settings to them where they are:
no new dashboard resource, no new URL, no Entra change.

## Service Bus queues

`serviceBusQueues` in `deploy.base.json` lists the queues every configuration creates and binds to, in the same
model as the Wastequip scripts. Each entry has these keys and no others:

| Key | Default | Meaning |
|---|---|---|
| `name` | (required) | The queue is `<queuePrefix>-<name>`, e.g. `bat-d365fsc-dataevent`. Lowercase letters and digits, separated by single hyphens. |
| `setting` | none | The Automation app setting that receives the resolved queue name, e.g. `D365FscDataEventQueue`. An entry without one creates the queue and writes no setting. |
| `requiresSession` | `false` | Create the queue with sessions enabled. |
| `duplicateDetection` | `true` | Create the queue with duplicate detection, history window `PT5M`. |

The shipped list holds the five queues of today, with no flags: `d365fsc-dataevent` (`D365FscDataEventQueue`),
`d365fsc-businessevents` (`D365BusinessEventQueue`), `d365fsc-to-capture` (`D365ToCaptureQueue`),
`capture-to-d365fsc` (`CaptureToD365Queue`) and `capture-to-d365fsc-response` (`CaptureToD365QueueResponse`).
`DeploymentConfigTests` pins it and checks that every Automation `ServiceBusTrigger` binds to a setting in it. The
dashboard simulation takes its four queue settings from the list of the configuration that owns
`dashboard.simulationQueuePrefix` (see [Dashboard simulation](#dashboard-simulation)).

**Creation.** `infra` creates only the queues that are missing, with a 1 GB maximum size, 14-day default TTL,
60-second lock duration, maximum delivery count 10, dead-lettering on expiration and no partitioning. It adds
`--enable-session true` when `requiresSession` is true, and `--duplicate-detection true
--duplicate-detection-history-time-window PT5M` unless `duplicateDetection` is false. The `-WhatIf` label shows a
non-default choice: `create queue prd-x (sessions)`, `create queue prd-x (no duplicate detection)`. Queues are not
tagged.

**Existing queues are never changed.** A queue that already exists gets no step at all, whatever its properties:
no update, no recreate, no warning. Sessions and duplicate detection cannot be changed on an existing queue in
Service Bus, so check a new entry's flags in the `-WhatIf` preview before the first `infra` run. Queue deletion is
deliberately not a component: a queue holds undelivered messages.

**Sessions need a session-aware function.** Wilson's triggers are not session-enabled. A queue with
`requiresSession: true` can only be consumed by a function whose trigger sets `IsSessionsEnabled = true`; set the
flag only together with such a function.

**Preflight validates the list** before any Azure call, naming the configuration and the entry. It fails when the
list is missing, not a list or empty; an entry is not an object or has an unknown key (e.g. `duplicateDetecton`);
a name is blank or invalid, or resolves to more than 260 characters; two entries resolve to the same queue name or
name the same setting (both case-insensitive); a setting is not a valid name; a flag is not the JSON value `true` or
`false`; or any of the five settings the Automation app requires (`RequiredSettings.Queues`) has no entry.

**Adding a queue.** Normally add the entry to `deploy.base.json`, in the same change as the function that consumes
it and the code that reads its setting; `infra` then creates `<prefix>-<name>` in every environment and `settings`
writes the setting. Only when one environment must differ, copy the whole list into its `deploy.<name>.json`: like
every list, `serviceBusQueues` in an environment file (or an override) replaces the base list, it does not merge
with it.

**Renaming a queue creates a new one.** Changing a `name` makes `infra` create the new queue and `settings` point
the setting at it; the old queue is left as it is, with any messages still in it. Drain or delete it by hand.

## Hosting

The P0v3 Linux plan `asp-eim-wtc-bat` carries the gateway and the bat and test Automation apps. The kept dashboard
API stays on the plan it is on until it is moved by hand. The environments share compute, so load in one can affect
another; if the plan runs hot, change `appServicePlan.sku` to `P1v3`. Production has its own P0v3 plan,
`asp-eim-wtc-prd`.

The gateway can be shared because the D365 environment arrives in the request path. The Automation host cannot:
its Service Bus triggers bind to one environment's queues at startup.

## Gated triggers

`automation.gatedFunctions` lists the Automation host's six queue and timer functions. A test keeps the list
equal to the functions in the code. `settings` writes `AzureWebJobs.<fn>.Disabled` for every one of them on every
run, overwriting changes made in the portal:

- `automation.triggersEnabled` (a JSON boolean; base `false`, `bat` and `test` `true`, `prd` explicitly `false`)
  sets them all to `false` (enabled) or `true` (disabled).
- A function listed in `automation.keepDisabled` stays disabled either way.

`automation` refuses to deploy while any gated setting is missing ("run `settings` first"). The preflight drift
check fails on a missing gated setting and warns on a differing value. To stop processing in an environment, set
`automation.triggersEnabled` to `false` (or list functions in `keepDisabled`) and run `settings`.

`automation.retiredSettings` (today `PollSchedule`) are never written, are deleted from the Automation app by
`settings`, and only warn in the preflight drift check.

## Adding an environment

1. Add `script/deployment/config/deploy.<env>.json` with `"environment": "<env>"` and
   `"automation": { "triggersEnabled": true }`. Then either:
   - add `"uses": "bat"` to share bat's resources (it gets only its own queues and Automation app), or
     optionally its own Key Vault and Log Analytics workspace through `names.keyVault` and `names.logAnalytics`
     (see [Configurations: owners and `uses`](#configurations-owners-and-uses)), or
   - leave `uses` out for a fully isolated environment. It derives `rg-eim-wtc-<env>`, `sbns-eim-wtc-<env>`,
     `kv-eim-wtc-<env>`, `sql-eim-wtc-<env>` and so on, and needs `sql.entraAdmin`, `sql.entraAdminObjectId` and
     rights to create its resource group (or a `names.resourceGroup` override to an existing one). Check that every
     derived name stays valid.
2. Add a `Deploy_<env>` stage to `pipelines/eim.yml` after the last one, with `ownsResources` set to whether it
   owns its resources (and `runMigrations: false` when it does not), and a DevOps environment `eim-wtc-<env>`. Add
   the name to the `environments` list of the `Build` stage.
3. Run the pipeline manually with `deployInfra`, then `grantDatabaseAccess`, then `applySettings` (and
   `runMigrations` for an owner). The gated triggers follow `automation.triggersEnabled`.
4. Add the environment's rows to `ROUTETABLE` (a message with no route is dead-lettered with reason `NoRoute`),
   and its D365 profiles to `ENVIRONMENTTABLE`.

No script change.

## Production

Production is the configuration `prd`, with `"production": true`. It owns everything it uses and shares nothing
with non-production: its own Service Bus namespace, Key Vault, SQL server and database, plan, gateway, dashboard and
Entra registration, in the existing resource group `d365fsc-integration-prod`. Standing it up and going live:
[production-go-live.md](production-go-live.md).

**Pipeline.** `pipelines/eim-prd.yml`, manual only, one stage:

```
Deploy_prd (eim-wtc-prd)
```

It has no `Build` stage. It promotes the `package` artifact of an `eim` run, picked under **Resources** on the
manual run: a run from `main` whose `Deploy_test` stage succeeded, so production gets exactly the binaries bat and
test ran. The resource has `branch: main`, so the default offered is the latest `eim` run from `main`, never a pull
request or feature run. The stage uses the service connection `sc-eim-wtc-prd` and the variable group
`eim-wtc-prd`, and has the same parameters as `eim.yml`. See [pipelines/README.md](../../pipelines/README.md).

**Names.** Only `names.resourceGroup` is overridden. Everything else uses the pattern (see [Names](#names)), and
`infra` and `sql` create whatever is missing.

**Isolation rules.**

- `deploy.prd.json` resets every non-production value it inherits from the base: Entra, excluded users and
  simulation. `DeploymentConfigTests` fails when one leaks.
- No name in use by a production configuration may be in use by a non-production one, and a production
  configuration may not use a non-production one. Preflight checks this across every configuration, and so do the
  tests.
- `automation.triggersEnabled` is explicitly `false` until go-live. Going live is setting it to `true` in
  `deploy.prd.json` and running `settings`.

**Simulation off.** `dashboard.simulationEnabled` is `false`, so the production dashboard API gets
`Simulation__Enabled=false` and no other `Simulation__*` setting. The API removes the simulation routes (404) and
the SPA hides the console. See [Dashboard simulation](#dashboard-simulation).

## Differences from the Wastequip scripts

`Deploy-Wilson.ps1` and `New-DeploymentPackage.ps1` are ports of the Wastequip EIM scripts. Where they differ,
on purpose:

- One configuration per environment with optional `uses`, instead of a name suffix.
- No Key Vault `secrets` component. The only secret, `eim-servicebus`, is copied from the namespace by `settings`.
- `sql` creates no SQL admin login: the server is Entra-only, and `sqlAdminLogin` is not supported.
- `sqlusers` creates users `WITH SID ... TYPE = E` rather than `FROM EXTERNAL PROVIDER`, so the SQL server needs
  no Directory Readers role.
- Existing resources the script did not create are used as found, with no configuration change.
- No `-NameSuffix`.
- No release tags.
- The dashboard keeps its non-pattern names in bat and is deployed in place.

Keep this list current when merging changes from Wastequip.

## Decisions

**Production is an isolated environment.** It shares nothing with non-production: its own resource group, Service
Bus namespace, plan, dashboard, gateway, database and pipeline (see [Production](#production)). It is built from
base plus explicit resets in `deploy.prd.json`, guarded by tests.

**`eimdb_dev` is not a deployment target.** Every non-production environment uses `eimdb`. The `eimdb_dev`
catalog on the same server is a leftover of the old development setup; it will be dropped by hand later. Local
development uses a local SQL Server instead (see "Local database" in
[database-migrations.md](database-migrations.md) and "Local development" in the repository `README.md`). Until
`eimdb_dev` is gone, remember that a connection string that looks right can still point at the wrong catalog on
the shared server, and the failure is silent because both have the same schema. Check `Initial Catalog`, not only
the server.

**Two Entra registrations exist, and the difference is intentional.** The deployed registration (tenant
`446b6761`, client `5d1e26b3`) is the one the configuration carries. The pair in
`ARM.Integration.Dashboard.API/appsettings.json` (`5e8a5341` / `317063cf`) is for local development only. Getting
these backwards produces a SPA that loads and then fails every API call, because the API validates tokens
against a registration they were not issued for.

**Queues are created, never changed.** As in Wastequip, `infra` creates missing queues from `serviceBusQueues` and
leaves existing ones exactly as they are, with no drift warning; see [Service Bus queues](#service-bus-queues).

**Containers are not used.** The Container Apps path (`docker-compose.yml`, the Dockerfiles,
`nginx.conf.template`) was removed. If containers come back, the reverse proxy in front of the API must keep
three settings the deleted `nginx.conf.template` had, because the simulation console streams Server-Sent Events:
`proxy_buffering off`, `proxy_cache off` and `proxy_read_timeout 3600s`. The same need applies to the Static Web
App linked backend, which has never been exercised with SSE. If the simulation console arrives all at once or is
cut off, that is the cause.
