# Deployment

Non-production deployment is one script, `Script/Deploy-Eim.ps1`, driven by one configuration file
per environment. It replaces the ten scripts that used to live in `Script/Deploy-Manual/`.

## Deploying

```powershell
# Preview. Reports the derived names and intended actions, changes nothing.
./Script/Deploy-Eim.ps1 -Environment bat -Component all -WhatIf

# One component
./Script/Deploy-Eim.ps1 -Environment bat -Component function

# Several. Order is enforced, not taken from the order given.
./Script/Deploy-Eim.ps1 -Environment bat -Component queues,schema,function
```

Environments are `dev1`, `bat` and `test`. Components are:

| Component | Does |
|---|---|
| `queues` | Ensures the environment's five Service Bus queues |
| `schema` | Runs database migrations via `ARM.Integration.DbMigrator` |
| `api` | Publishes and zip-deploys the dashboard API, and applies its app settings |
| `function` | Ensures plan, storage and Function App, applies settings, publishes and deploys |
| `frontend` | Builds the SPA, links the API as the static web app's backend, deploys |

`all` runs every component in the order above. That order is deliberate: a Function App that starts
against a missing queue or an un-migrated database fails on its first trigger rather than at deploy
time, which is far harder to diagnose. The dashboard goes last because it only reads what the rest
produces.

The script halts on the first failure, names the component that failed, and exits non-zero. It does
not continue past a broken step, because a half-deployed environment reporting success is worse
than a stop.

`api` and `frontend` are shared across all non-production environments, so they are refused unless
the environment's configuration sets `dashboard.hostsDashboard`. Refused rather than skipped: asking
to deploy the shared dashboard from an environment that does not own it means the request was wrong,
and quietly ignoring it hides that.

Neither `api` nor `frontend` provisions its resource. Both fail if the web app or static web app is
missing, because a linked backend needs the Standard SKU and creating one silently on the wrong tier
produces a dashboard that loads and then fails every call.

## Configuration

`Script/config/deploy.<environment>.json`, one per environment. `deploy.example.json` documents
every key and is the only one committed — the rest carry the SQL and Service Bus credentials and are
gitignored.

| Key | Required | Notes |
|---|---|---|
| `environment` | yes | Must match `-Environment`, or the script refuses |
| `subscriptionId` | no | Selected before any action when set |
| `resourceGroup`, `location` | yes | Configuration, not constants, so an environment in another region can be added |
| `appServicePlan` | yes | Shared across environments — see below |
| `serviceBusNamespace` | yes | Queue names are derived from the environment prefix |
| `names.functionApp`, `names.storageAccount`, `names.queuePrefix` | no | Override the derived name; omit or null to derive |
| `secrets.eimdbConnectionString` | yes | Used by `schema`, `api` and `function` |
| `secrets.serviceBusConnectionString` | yes | |
| `dashboard.hostsDashboard` | yes | Exactly one environment should be true |
| `dashboard.apiAppName`, `dashboard.frontendAppName` | yes | Shared resources |
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

### Timer schedules must be present

Every `function.*` schedule is checked non-empty before it is applied. A timer trigger whose
schedule setting is missing fails to bind, and that failure takes down the entire Function host, not
just the timer. This is a silent failure worth converting into a loud one.

## Onboarding a new environment

1. Add the environment name to the `ValidateSet` on `-Environment` in `Script/Deploy-Eim.ps1`.
2. Copy `deploy.example.json` to `deploy.<environment>.json` and fill it in. Leave `names` null
   unless a resource does not follow the pattern.
3. Set `dashboard.hostsDashboard` to false unless this environment is to own the shared dashboard.
4. Run with `-WhatIf` and check every derived name against the resources that actually exist.
5. Run `queues`, then `schema`, then `function`.
6. Add the environment's rows to `ROUTETABLE`. A message arriving with no route is dead-lettered
   with reason `NoRoute`, so a seeding gap is lost traffic rather than an untidy table.

## Decisions

**The App Service Plan is shared** across all non-production environments, carrying the dashboard
API and every environment's Function App. This is a deliberate cost decision, and it means those
environments share compute — load in one can affect another.

**The shared dashboard resources are suffixed `-dev`** (`wtc-dashboard-api-dev`,
`wtc-dashboard-frontend-dev`) but serve every non-production environment, not just a development
one. Renaming them to `-nonprod` is deferred: the rename is cosmetic and would need coordinated
changes to the linked backend and the Entra redirect URIs.

**Two Entra registrations exist and the difference is intentional.** The deployed registration is
tenant `446b6761` / client `5d1e26b3`, and it is what these configuration files carry. The pair in
`ARM.Integration.Dashboard.API/appsettings.json` — `5e8a5341` / `317063cf` — is for local
development only, and lives in `appsettings.Development.json` for the values that vary. Getting
these backwards produces a SPA that loads and then fails every API call, because the API validates
against a registration the token was not issued for.

**Production is out of scope here.** It shares nothing with non-production and will be a separate
stamp: its own resource group, Service Bus namespace, App Service Plan, dashboard, and database.
Do not extend these configuration files to cover it.

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
