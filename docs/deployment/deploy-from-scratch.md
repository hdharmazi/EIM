# Deploying from scratch

How to stand this system up in an empty subscription. To add an environment to the existing estate, see
"Adding an environment" in [deployment.md](deployment.md); that list is much shorter.

There is no Bicep and no ARM template. Everything on the Azure side is a component of
`script/deployment/Deploy-Wilson.ps1`, so there is one way to deploy and one place that reads a configuration.
The normal route is the `eim` pipeline ([devops-setup.md](devops-setup.md)). This page uses the script by hand,
which makes each step visible. The pipeline runs the same commands.

This page stands up the non-production environments: `bat`, which owns the resources, and `test`, which uses
them. Production is the isolated configuration `prd` with its own pipeline (`eim-prd`); follow
[production-go-live.md](production-go-live.md) for it.

## What you need first

| Tool | Needed for |
|---|---|
| Azure CLI | `az login` with an account that can create the resource group and resources in it |
| PowerShell 7.2+ | running the scripts |
| .NET SDK 10 and Node 22 | building the package (`New-DeploymentPackage.ps1`) only. `Deploy-Wilson.ps1` deploys a package and never builds. |
| Entra rights | to create a security group and register an application, or someone who has them |

## What gets built

**Owned by `bat`** (a configuration without `uses`):

- the resource group, Service Bus namespace and Key Vault
- the SQL server (Entra-only authentication) and database, and the database migrations
- the App Service plan and Log Analytics workspace
- the D365 F&SC gateway
- the dashboard API and SPA
- bat's five Service Bus queues and its Automation Function App, storage and Insights

**Owned by `test`** (`"uses": "bat"`): its five queues and its Automation Function App, storage and Insights, on
bat's plan and database.

**Per environment, in the database:** the environment's rows in `ROUTETABLE` and `ENVIRONMENTTABLE`.

There is no development environment: development runs on a developer's machine against a local database (see
"Local development" in the repository `README.md`).

One database serves every non-production environment. Do not create an `eimdb_dev`; in the existing estate it is
a leftover that will be dropped by hand.

## 1. SQL admin group

The SQL server is created with Entra-only authentication and no SQL login, so it needs an Entra admin from the
start. Create an Entra security group, add yourself (and, for the pipeline, the service connection's principal),
and note its display name and object id.

## 2. Entra application registration

One registration serves both the SPA and the API:

1. Register an application.
2. Expose an API with the scope `user_impersonation`, and note the Application ID URI.
3. Add a Single-page application platform with the redirect URI `https://<static web app host>`. The host is
   known after step 6; add it then.
4. Note the tenant ID and the client ID.

Two registrations is the most common way to get a dashboard that loads and then fails every call.

## 3. Configuration

Edit `script/deployment/config/deploy.base.json`:

| Key | Value |
|---|---|
| `subscriptionId` | the subscription |
| `location` | the region |
| `entra.*` | from step 2 |
| `automation.excludedUsers` | the user GUIDs whose changes the Automation host ignores |

Edit `script/deployment/config/deploy.bat.json`:

| Key | Value |
|---|---|
| `names` | remove the overrides, which name the existing estate; every name then derives from the environment (`rg-eim-wtc-bat`, `sbns-eim-wtc-bat`, `kv-eim-wtc-bat`, `sql-eim-wtc-bat`, and so on) |
| `sql.entraAdmin`, `sql.entraAdminObjectId` | the group from step 1 |

`deploy.test.json` needs no change: it is `{ "environment": "test", "uses": "bat", ... }`.
[deployment.md](deployment.md) describes every key, the names and how the layers merge.

No configuration file holds a secret, and the script needs none: the apps reach SQL with their managed identities,
you reach it with your `az login`, and the Service Bus connection is copied into Key Vault by `settings`.

## 4. Build a package

```powershell
./script/deployment/New-DeploymentPackage.ps1 -Environment bat, test -Version local
```

The package lands in `artifacts/package/wtc-eim-deploy-local`. The SPA is built with the Entra values from
step 3.

## 5. Preview

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment bat -Component all, myip, myipremove -WhatIf -PackagePath artifacts/package/wtc-eim-deploy-local
```

Read the derived names and the planned actions. This is the cheapest moment to catch a typo. Preflight names every
unfilled placeholder.

## 6. Deploy bat

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment bat -Component all, myip, myipremove -PackagePath artifacts/package/wtc-eim-deploy-local
```

The components run in a fixed order:

1. `infra`: resource group, namespace, Key Vault, plan, workspace, queues, gateway, dashboard API, Standard static
   web app and Automation app, with their identities and Key Vault access
2. `sql`: the SQL server, with your group as Entra admin, the database, and a firewall rule for each app outbound IP
3. `settings`: the Key Vault secret `eim-servicebus`, then every app setting
4. `myip`, `schema` (every migration), `sqlusers` (a database user for each app identity), `myipremove`
5. `gateway`, `backend`, `frontend`, `automation`
6. `verify` and `health`

The script stops at the first failure and names the component. The gated queue and timer triggers follow
`automation.triggersEnabled`, which is `true` in `deploy.bat.json`.

## 7. Deploy test

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment test -Component all, myip, myipremove -PackagePath artifacts/package/wtc-eim-deploy-local
```

This creates test's queues and Automation app on bat's plan, writes its settings, and creates its database user.
Preflight fails until bat's resources and Key Vault secret exist.

## 8. Complete the environment data

The migrations create the tables and seed the status and routing defaults. Then:

- **`ROUTETABLE`**: confirm every environment is covered. A message with no route is dead-lettered with reason
  `NoRoute`, so a gap here loses traffic.
- **`ENVIRONMENTTABLE`**: add the real D365 connection profiles. The gateway resolves `D365FSC-GW/<environment>`
  from the request path, and the Automation host uses `D365FSC/<environment>`.

These values are stored in plaintext, and Environment Settings lets any dashboard admin reveal them. That is a
known, accepted state; see the standing constraints in [d365fsc-gateway.md](d365fsc-gateway.md).

## 9. Verify

```powershell
# Gateway: expect 401, which proves the platform is enforcing keys
Invoke-WebRequest "https://<gateway app>.azurewebsites.net/api/v1/d365fsc/PROBE/data/probe" `
    -Headers @{ "x-functions-key" = "not-a-key" } -SkipHttpErrorCheck | Select-Object StatusCode
```

Anything other than 401 means keys are not enforced. Then sign in to the dashboard. If it loads but every call
fails, check for two Entra registrations (step 2).

## 10. Onboard a gateway consumer

Only if third parties will call D365 through the gateway. The full procedure is in
[d365fsc-gateway.md](d365fsc-gateway.md). In short:

1. Create a host key on the gateway app, named for the consumer.
2. Create the consumer and its grants in the dashboard.
3. Register the key with **Set key**.

## Things that catch people out

- **Two Entra registrations**: the SPA loads and then every call fails.
- **An unseeded `ROUTETABLE`**: messages dead-letter with `NoRoute`, silently.
- **The shared plan**: the gateway and every non-production Automation app share one P0v3 instance, so load in one
  environment affects the others.
- **Triggers disabled**: an Automation app does nothing while `automation.triggersEnabled` is `false` or a function
  is in `keepDisabled`. `verify` shows the trigger states.
- **No database user**: an app whose identity has no user in the database fails at its first query. Run
  `sqlusers` (with `myip`) after an app is created or recreated.
- **Key Vault references**: `health` fails while `ServiceBus` or `Simulation__ServiceBusConnectionString` does not
  resolve; check the app's identity and the vault's access policies.
