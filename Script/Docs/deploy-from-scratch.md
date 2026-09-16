# Deploying from scratch

Standing up this system in an empty subscription. If you are adding an environment to an estate that
already exists, you want the "Onboarding a new environment" section of [deployment.md](deployment.md)
instead — it is a much shorter list.

There is no Bicep and no ARM template. Everything is a component of `Script/Deploy-Eim.ps1`, so there
is one way to deploy and one place that reads an environment's configuration.

## What you need first

| | |
|---|---|
| Azure CLI | `az login`, and an account that can create resource groups |
| .NET SDK 10 | builds the API, the Functions host and the gateway |
| Node 20+ | builds the SPA |
| SQL access | an account that can create a database and run DDL |
| Entra rights | to register an application, or someone who has them |

The script refuses to start if `az` is missing or not logged in, rather than failing halfway.

## The shape of what you are building

Three things are **shared** by every non-production environment:

- the dashboard API and SPA
- the D365 F&SC gateway
- the App Service Plan they sit on

Everything else is **per environment**: Service Bus queues, the Functions host, its storage account,
and the rows in `ROUTETABLE` and `ENVIRONMENTTABLE`.

One database, `eimdb`, is shared by every non-production environment. `eimdb_dev` exists for local
development only; nothing deployed points at it.

The first environment you create is doing double duty — it is both an ordinary environment *and* the
host of the shared resources. Pick it deliberately.

## 1. Azure foundations

These are the pieces the script expects to already exist. It provisions what belongs to a
deployment, not the account-level scaffolding around it.

```powershell
az group create --name <rg> --location <region>

az servicebus namespace create --resource-group <rg> --name <namespace> --sku Standard

az appservice plan create --resource-group <rg> --name <plan> --sku P0v3 --is-linux
```

Standard tier Service Bus is required — the design uses sessions and dead-lettering.

Then the SQL server and an empty `eimdb`:

```powershell
az sql server create --resource-group <rg> --name <sql-server> --location <region> `
    --admin-user <admin> --admin-password <password>

az sql db create --resource-group <rg> --server <sql-server> --name eimdb --service-objective S0
```

Finally, the two dashboard resources. **The script does not create these**, by design: a linked
backend needs the Standard SKU, and creating one silently on the wrong tier produces a dashboard that
loads and then fails every API call.

```powershell
az webapp create --resource-group <rg> --plan <plan> --name <api-app> --runtime "DOTNETCORE:10.0"

az staticwebapp create --resource-group <rg> --name <spa-app> --sku Standard
```

## 2. Entra application registration

One registration serves both the SPA and the API. Using two is the single most common way to get a
dashboard that loads and then fails every call, because the API validates tokens against a
registration they were not issued for.

1. Register an application.
2. Expose an API with scope `user_impersonation`; note the Application ID URI.
3. Add a Single-page application platform with redirect URI `https://<spa-host>`.
4. Note the tenant id and client id.

## 3. Configuration

```powershell
Copy-Item Script\config\deploy.example.json Script\config\deploy.<env>.json
```

`deploy.example.json` documents every key inline and is the only one committed — the rest hold SQL
and Service Bus credentials and are gitignored. Keep it that way.

For your first environment:

| Key | Value |
|---|---|
| `environment` | must match `-Environment`, or the script refuses |
| `resourceGroup`, `location`, `appServicePlan`, `serviceBusNamespace` | from step 1 |
| `secrets.*` | the two connection strings |
| `dashboard.hostsDashboard` | **true** — this environment owns the shared dashboard |
| `dashboard.apiAppName`, `frontendAppName` | from step 1 |
| `gateway.hostsGateway` | **true** — this environment owns the shared gateway |
| `gateway.baseUrl` | `https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net` |
| `entra.*` | from step 2 |
| `function.*` | timer schedules; every one must be non-empty |

`gateway.hostsGateway` is checked for *presence*, not truth. Absent and false both read as falsy, so
a forgotten flag would silently mean no environment deploys the gateway.

## 4. Open the SQL firewall

```powershell
.\Script\Deploy-Eim.ps1 -Environment <env> -Component firewall
```

Adds your current public IP as a rule named for you. Not part of `all`, because a deployment that
quietly widens network access is not something to run by default. The command to remove the rule is
printed when it is created.

## 5. Check before you deploy

```powershell
.\Script\Deploy-Eim.ps1 -Environment <env> -Component all -WhatIf
```

Read the derived names against what you created in step 1. This is the cheapest moment to catch a
typo; after this, resources start existing.

## 6. Deploy

```powershell
.\Script\Deploy-Eim.ps1 -Environment <env> -Component all
```

Order is deliberate: `queues`, `schema`, `api`, `function`, `gateway`, `frontend`. Queues and schema
come first because a Functions host that starts against a missing queue or an un-migrated database
fails on its first trigger rather than at deploy time, which is far harder to diagnose. The SPA is
last because it only reads what the rest produces.

The script stops at the first failure and names the component. It does not continue, because a
half-deployed environment reporting success is worse than a stop.

## 7. Seed the database

Two tables need rows before anything works. Neither is created by deployment, because both hold
decisions rather than infrastructure.

**`ROUTETABLE`** — one row per message route for the environment. A message arriving with no route is
dead-lettered with reason `NoRoute`, so a gap here is lost traffic rather than an untidy table.

**`ENVIRONMENTTABLE`** — the D365 connection profiles. The gateway resolves `D365FSC-GW/<environment>`
from the environment in the request path, and the capture pipeline uses `D365FSC/<environment>`.

Connection strings are held here in plaintext, and Environment Settings lets any dashboard admin
reveal them. That is a known and accepted state, not an oversight — see the standing constraints in
[d365fsc-gateway.md](d365fsc-gateway.md).

## 8. Verify

```powershell
# Dashboard
az staticwebapp show --name <spa-app> --resource-group <rg> --query defaultHostname -o tsv

# Gateway — expect 401, which proves the platform is enforcing keys
Invoke-WebRequest "https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net/api/v1/d365fsc/PROBE/data/probe" `
    -Headers @{ "x-functions-key" = "not-a-key" } -SkipHttpErrorCheck | Select-Object StatusCode
```

A 401 from the gateway is the result you want. Anything else means keys are not being enforced.

Then sign in to the dashboard. If it loads but every call fails, the cause is almost always two Entra
registrations instead of one — go back to step 2.

## 9. Onboard a gateway consumer

Only if third parties will call D365 through the gateway. Full procedure in
[d365fsc-gateway.md](d365fsc-gateway.md); briefly:

1. Create a **host key** on the gateway app named for the consumer.
2. Create the consumer and its grants in the dashboard.
3. Register the key with **Set key**.

Keys do not cross applications, and a consumer holds one key. Both matter when you later move a
consumer between gateway apps.

## Optional: Key Vault

```powershell
.\Script\Deploy-Eim.ps1 -Environment <env> -Component keyvault
```

Provisions an RBAC-authorised vault and stores the Service Bus and SQL connection strings.

**Nothing reads from it.** Connection strings remain in `ENVIRONMENTTABLE` and app settings in
plaintext, so this creates a second copy of each secret that must be rotated alongside the first.
Run it only if you are picking up the deferred Key Vault work; otherwise it adds a rotation hazard
and no protection.

The vault uses RBAC, so being able to create it does not mean being able to write secrets into it.
If that step fails with 403, the component prints the role assignment you need.

## Adding the next environment

Much shorter — the shared resources already exist:

1. Add the name to the `ValidateSet` on `-Environment` in `Script/Deploy-Eim.ps1`.
2. Copy `deploy.example.json` and fill it in, with **both** `hostsDashboard` and `hostsGateway` false.
3. `-Component all` — it skips the shared components and reports which it skipped.
4. Seed `ROUTETABLE`, and `ENVIRONMENTTABLE` if the gateway is to serve the environment.

No new gateway, no new dashboard, no template to edit.

## Things that will bite you

- **Two Entra registrations.** A SPA that loads and then fails every call. One registration, used by
  both the SPA build and the API.
- **A missing timer schedule.** An empty `function.*` value stops the trigger binding, and that takes
  down the entire Functions host — not just the timer. The script checks for this, loudly.
- **An unseeded `ROUTETABLE`.** Messages dead-letter with `NoRoute`. Silent unless you look.
- **The shared App Service Plan.** Every non-production environment's Functions host and the
  dashboard share compute. Load in one environment affects the others.
- **`maximumInstanceCount` on the gateway.** The only rate limiter in the design, now shared across
  every non-production environment, and the gateway shares a D365 tenant with the capture pipeline. A
  load test in one environment can throttle another and degrade order processing.
