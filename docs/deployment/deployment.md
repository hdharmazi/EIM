# Deployment

Non-production deployment is one script, `script/deployment/Deploy-Eim.ps1`, driven by one configuration file
per environment. It replaces the scripts that used to live in `script/Deploy-Manual/`, and the
Bicep templates that used to live alongside them. There is no Bicep or ARM template in this
repository — everything Azure-side is a component of this script.

Standing the system up in an empty subscription is a different job from deploying into one that
exists: see [deploy-from-scratch.md](deploy-from-scratch.md).

## Deploying

```powershell
# Preview. Reports the derived names and intended actions, changes nothing.
./script/deployment/Deploy-Eim.ps1 -Environment bat -Component all -WhatIf

# One component
./script/deployment/Deploy-Eim.ps1 -Environment bat -Component function

# Several. Order is enforced, not taken from the order given.
./script/deployment/Deploy-Eim.ps1 -Environment bat -Component queues,schema,function
```

Environments are `dev`, `bat` and `test`. Components are:

| Component | Does |
|---|---|
| `queues` | Ensures the environment's five Service Bus queues |
| `schema` | Runs database migrations via `ARM.Integration.DbMigrator` |
| `api` | Publishes and zip-deploys the dashboard API, and applies its app settings |
| `function` | Ensures plan, storage and Function App, applies settings, publishes and deploys |
| `gateway` | Ensures storage, the Flex Consumption app and Application Insights, applies settings, publishes and deploys the D365 F&SC gateway |
| `frontend` | Builds the SPA, links the API as the static web app's backend, deploys |

`all` runs every component in the order above. That order is deliberate: a Function App that starts
against a missing queue or an un-migrated database fails on its first trigger rather than at deploy
time, which is far harder to diagnose. The dashboard goes last because it only reads what the rest
produces.

### `firewall`, on demand only

| Component | Does |
|---|---|
| `firewall` | Adds this machine's public IP to the environment's SQL server, as a rule named for the current user |

Run it when `schema` cannot reach the database:

```powershell
.\script\deployment\Deploy-Eim.ps1 -Environment bat -Component firewall
```

It is **not** part of `all`, and deliberately so. Opening a SQL firewall is a developer convenience,
not a deployment step, and a deployment that quietly widens network access is the kind of thing
nobody notices until it is audited.

The server name is read from `secrets.eimdbConnectionString` rather than configured separately, so a
rule cannot be opened on one server while deploying against another. The rule is named
`AllowDev-<username>` rather than something shared, so rules can be attributed and removed; the
command to remove it is printed when it is created.

The script halts on the first failure, names the component that failed, and exits non-zero. It does
not continue past a broken step, because a half-deployed environment reporting success is worse
than a stop.

`api`, `frontend` and `gateway` are shared across all non-production environments. Naming one
explicitly against an environment that does not host it is refused rather than skipped: the request
was wrong, and quietly ignoring it hides that. `all` is different — it means "everything that belongs
here" rather than a claim about any particular component — so it drops the shared components an
environment does not host and says which it dropped. Without that, `all` would only ever work against
the one environment hosting everything.

`api` and `frontend` are gated on `dashboard.hostsDashboard`, `gateway` on `gateway.hostsGateway`.
The two are separate because the reasons differ: the dashboard needs the shared App Service Plan, and
the gateway is on Flex Consumption and needs nothing from it.

Neither `api` nor `frontend` provisions its resource. Both fail if the web app or static web app is
missing, because a linked backend needs the Standard SKU and creating one silently on the wrong tier
produces a dashboard that loads and then fails every call.

## Configuration

`script/deployment/config/deploy.<environment>.json`, one per environment. These files are tracked so
resource names and non-secret settings can be reviewed. Keep the SQL and Service Bus fields as
placeholders and supply their values through `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` at runtime.

| Key | Required | Notes |
|---|---|---|
| `environment` | yes | Must match `-Environment`, or the script refuses |
| `subscriptionId` | no | Selected before any action when set |
| `resourceGroup`, `location` | yes | Configuration, not constants, so an environment in another region can be added |
| `appServicePlan` | yes | Shared across environments — see below |
| `serviceBusNamespace` | yes | Queue names are derived from the environment prefix |
| `names.functionApp`, `names.storageAccount`, `names.functionInsights`, `names.queuePrefix` | no | Override the derived name; omit or null to derive |
| `secrets.eimdbConnectionString` | yes | Used by `schema`, `api`, `function` and `gateway`. Every non-production environment points at `eimdb` |
| `secrets.serviceBusConnectionString` | yes | |
| `dashboard.hostsDashboard` | yes | Exactly one environment should be true |
| `dashboard.apiAppName`, `dashboard.frontendAppName` | yes | Shared resources |
| `gateway.hostsGateway` | yes | Exactly one environment should be true. Checked for presence, not truth — a forgotten flag would silently mean nobody deploys the gateway |
| `gateway.baseUrl` | yes | The shared gateway's own public URL, used to rewrite `@odata.nextLink` and `Location` so callers can follow them |
| `gateway.alwaysReadyInstances`, `maximumInstanceCount`, `instanceMemoryMB` | yes | Flex Consumption scale bounds |
| `gateway.maxRequestBytes`, `idempotencyRetention`, `idempotencyPurgeSchedule` | yes | Request limit and idempotency store retention |
| `gateway.appInsightsName` | no | Omit or null to derive `appins-d365fsc-gateway-wtc-nonprod` |
| `entra.tenantId`, `clientId`, `audience`, `scope` | yes | One registration for both the SPA build and the API |
| `function.*` | yes | Timer schedules and retry tuning |

A missing key fails with that key named, before anything is touched.

`EIM_DEPLOY_SUBSCRIPTION_ID`, `EIM_DEPLOY_RESOURCE_GROUP`, `EIM_DEPLOY_EIMDB` and
`EIM_DEPLOY_SERVICEBUS` override the file, so a pipeline can run without one on disk.

### Derived names

| Resource | Pattern |
|---|---|
| Function App | `azf-eim-customer-wtc-{environment}` |
| Storage account | `stwtceimcustomer{environment}` |
| Queue prefix | `{environment}-` |

The App Service Plan is **not** derived. It is a shared configuration value.

The gateway is not derived per environment either. Its app, storage and Application Insights are
fixed at `azf-d365fsc-gateway-wtc-nonprod`, `stwtcd365gwnonprod` and
`appins-d365fsc-gateway-wtc-nonprod`, because one gateway serves every non-production environment.

### Timer schedules must be present

Every `function.*` schedule is checked non-empty before it is applied. A timer trigger whose
schedule setting is missing fails to bind, and that failure takes down the entire Function host, not
just the timer. This is a silent failure worth converting into a loud one.

## Onboarding a new environment

Only queues, storage, the Function App and the database schema are per environment. The dashboard and
the gateway already exist and are reused, so adding an environment provisions three resources and
touches no infrastructure template.

1. Add the environment name to the `ValidateSet` on `-Environment` in `script/deployment/Deploy-Eim.ps1`.
2. Copy `deploy.example.json` to `deploy.<environment>.json` and fill it in. Leave `names` null
   unless a resource does not follow the pattern.
3. Set `dashboard.hostsDashboard` and `gateway.hostsGateway` to false. A new environment reuses both
   shared applications; it does not get its own.
4. Run with `-WhatIf` and check every derived name against the resources that actually exist.
5. Run `queues`, then `schema`, then `function`. Running `all` is also safe — it skips the shared
   components this environment does not host and reports which it skipped.
6. Add the environment's rows to `ROUTETABLE`. A message arriving with no route is dead-lettered
   with reason `NoRoute`, so a seeding gap is lost traffic rather than an untidy table.
7. Add the environment's D365 profile to `ENVIRONMENTTABLE` if the gateway is to serve it. The shared
   gateway resolves the environment from the request path, so this is the only gateway-side step.

### Key Vault, on demand only

| Component | Does |
|---|---|
| `keyvault` | Ensures an RBAC-authorised vault and stores the Service Bus and SQL connection strings |

**Nothing reads from this vault.** Connection strings are still held in `ENVIRONMENTTABLE` and app
settings in plaintext, and Environment Settings lets any dashboard admin reveal them. Running this
component therefore creates a *second* copy of each secret that must be rotated alongside the first,
and protects nothing on its own. It exists so the capability survives — the Key Vault work is
deferred, not abandoned — and is excluded from `all` so nobody provisions it by accident.

The vault uses RBAC, so control-plane rights to create it do not grant data-plane rights to write
secrets into it. If that step fails with 403, the component prints the role assignment needed.

### Queues

Queue provisioning is `script/deployment/Deploy-Eim.ps1 -Component queues`, and nothing else. All
five names derive from the environment prefix. The component verifies that the configured Service
Bus namespace exists, skips queues that already exist, and creates only missing queues.

New queues use a 1 GB maximum size, 14-day default TTL, 60-second lock duration, maximum delivery
count 10, dead-lettering on expiration, and no partitioning. Queue creation is implemented directly
in `Deploy-Eim.ps1`; there is no separate queue helper to keep in sync.

The Bicep templates under `script/Superseded/` did this before and hard-code `bat-` and `test-`
queue names. They are kept for history only — one of them declares a list of queues it never actually
creates, so reading it to find out what exists gives the wrong answer.

Queue *deletion* has deliberately not been made a component. A queue holds undelivered messages, and
a deletion step sitting next to provisioning steps is one mistyped environment away from discarding
live traffic.

### Key Vault is not implemented

`script/deployment/Deploy-Eim.ps1 -Component keyvault` provisions a vault and stores connection strings, but nothing
reads from it. The capability is kept because the work is deferred rather than abandoned; running it
on its own achieves nothing except a second copy of each secret.

## Decisions

**The App Service Plan is shared** across all non-production environments, carrying the dashboard
API and every environment's Function App. This is a deliberate cost decision, and it means those
environments share compute — load in one can affect another.

**The shared dashboard resources are suffixed `-dev`** (`wtc-dashboard-api-dev`,
`wtc-dashboard-frontend-dev`) but serve every non-production environment, not just a development
one. Renaming them to `-nonprod` is deferred: the rename is cosmetic and would need coordinated
changes to the linked backend and the Entra redirect URIs.

**The gateway is shared, and named `-nonprod` because it could be.** One
`azf-d365fsc-gateway-wtc-nonprod` serves dev, bat and test. It can be shared because the D365
environment arrives in the request path and is resolved against `ENVIRONMENTTABLE` at request time, so
nothing about the deployment is environment-specific. `Wilson.Integration.Automation` cannot be shared for the opposite
reason: its Service Bus triggers bind to environment-specific queues at startup.

The name was chosen before the resource existed, rather than inheriting the `-dev` suffix the
dashboard now regrets. That leaves two conventions in one estate, which is deliberate — the
alternative was propagating a known mistake for the sake of matching it.

Two consequences worth knowing. `maximumInstanceCount` is the only rate limiter in the gateway's
design, and it is now a bound shared by every non-production environment, so a load test in one can
throttle another. And because Azure Functions keys do not cross applications, a consumer holding a key
for a per-environment gateway must be issued a key on the shared app and re-registered before it can
use it.

**Two Entra registrations exist and the difference is intentional.** The deployed registration is
tenant `446b6761` / client `5d1e26b3`, and it is what these configuration files carry. The pair in
`ARM.Integration.Dashboard.API/appsettings.json` — `5e8a5341` / `317063cf` — is for local
development only, and lives in `appsettings.Development.json` for the values that vary. Getting
these backwards produces a SPA that loads and then fails every API call, because the API validates
against a registration the token was not issued for.

**Production is out of scope here.** It shares nothing with non-production and will be a separate
stamp: its own resource group, Service Bus namespace, App Service Plan, dashboard, gateway, and
database. Do not extend these configuration files to cover it.

**`eimdb_dev` is not a deployment target.** Every non-production environment uses `eimdb`. The
`eimdb_dev` catalog on the same SQL server is for local development only, so local work does not
disturb anyone testing against the sandbox. No `deploy.*.json` should ever point at it.

Because the two databases share a server, a connection string that looks right can still be aimed at
the wrong catalog — and the failure is silent, because both have the same schema. Check `Initial
Catalog`, not just the server name.

**Containers are not used.** The Container Apps deployment path and its artifacts — `docker-compose.yml`,
the three Dockerfiles, and `nginx.conf.template` — were removed. Two complete and incompatible
deployment paths existed for the same logical apps, both maintained, with no signal in git history
about which was current. App Service was kept because it is what the running environments use.
Local development is `run.bat` plus the Vite development proxy.

Reintroducing containers would mean rebuilding those artifacts from scratch. If that happens, the
reverse proxy in front of the API **must** preserve three things that the deleted `nginx.conf.template`
got right, because the simulation console streams Server-Sent Events:

- `proxy_buffering off` — buffering holds events until the response completes, so a live progress
  console arrives all at once at the end
- `proxy_cache off`
- `proxy_read_timeout 3600s` — a full simulation run outlasts any default read timeout, and the
  connection is dropped mid-stream without it

The same requirement applies to the Static Web App linked backend, which has never been exercised
with SSE. If the simulation console arrives all at once or is cut off, that is the cause, and it is
a pre-existing gap rather than a regression.
