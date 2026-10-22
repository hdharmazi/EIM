# Deploying from scratch

How to stand this system up in an empty subscription. To add an environment to the existing estate, see
"Adding an environment" in [deployment.md](deployment.md); that list is much shorter.

There is no Bicep and no ARM template. Everything on the Azure side is a component of
`script/deployment/Deploy-Wilson.ps1`, so there is one way to deploy and one place that reads a configuration.
The normal route is the `eim` pipeline ([devops-setup.md](devops-setup.md)). This page uses the script by hand,
which makes each step visible. The pipeline runs the same commands.

Production is out of scope. It will be a separate stamp with its own configurations; do not reuse these
(see "Decisions" in [deployment.md](deployment.md)).

## What you need first

| Tool | Needed for |
|---|---|
| Azure CLI | `az login` with an account that can create resources in the resource group |
| PowerShell 7.2+ | running the scripts |
| .NET SDK 10 and Node 22 | building the package (`New-DeploymentPackage.ps1`) only. `Deploy-Wilson.ps1` deploys a package and never builds. |
| SQL access | an account that can create a database and run DDL |
| Entra rights | to register an application, or someone who has them |

## What gets built

**Shared**, owned by the `nonprod` configuration:

- the App Service plan and Log Analytics workspace
- the D365 F&SC gateway
- the dashboard API and SPA
- the database migrations

**Per environment**, owned by `dev`, `bat` and `test`:

- the five Service Bus queues
- the Automation Function App, its storage and its Insights
- the environment's rows in `ROUTETABLE` and `ENVIRONMENTTABLE`

One database, `eimdb`, serves every non-production environment. `eimdb_dev` exists for local development only,
and nothing deployed points at it.

## 1. Azure foundations

The script creates what belongs to a deployment, but not these:

```powershell
az group create --name <resource group> --location centralus

az servicebus namespace create --resource-group <resource group> --name <namespace> --sku Standard

az sql server create --resource-group <resource group> --name <sql server> --location centralus `
    --admin-user <admin> --admin-password <sql password>
az sql db create --resource-group <resource group> --server <sql server> --name eimdb --service-objective S0
```

Use the Standard tier of Service Bus; the design relies on dead-lettering. The database starts empty, and the
`schema` component creates everything in it. No application creates schema at startup.

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
| `names.*` | the resource group, Service Bus namespace, SQL server and database from step 1. Remove a key to use the derived pattern name instead. |
| `entra.*` | from step 2 |
| `automation.excludedUsers` | the user GUIDs whose changes the Automation host ignores |

In a new estate there is nothing to replace, so remove the `legacy` section from each configuration file and
empty `gateway.legacyVerificationTargets`. The
dashboard names in `deploy.nonprod.json` (`names.dashboardApi`, `names.staticWebApp`) can be removed too, so that
`infra` creates the dashboard under the pattern names. [deployment.md](deployment.md) describes every key and how
the layers merge.

No configuration file holds a secret. Supply the two connection strings through the environment, or type them
when prompted:

```powershell
$env:EIM_DEPLOY_EIMDB = '<eimdb connection string>'
$env:EIM_DEPLOY_SERVICEBUS = '<service bus connection string>'
```

## 4. Build a package

```powershell
./script/deployment/New-DeploymentPackage.ps1 -Environment nonprod, dev, bat, test -Version local
```

The package lands in `artifacts/package/wtc-eim-deploy-local`. The SPA is built with the Entra values from
step 3.

## 5. Preview

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment nonprod -Component all -WhatIf -PackagePath artifacts/package/wtc-eim-deploy-local
```

Read the derived names and the planned actions. This is the cheapest moment to catch a typo. Preflight names every
unfilled placeholder and missing secret.

## 6. Deploy the shared resources

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment nonprod -Component all -PackagePath artifacts/package/wtc-eim-deploy-local
```

The components run in a fixed order: `infra` (plan, workspace, gateway, dashboard API, Standard static web app),
`settings`, `myip`, `schema`, `myipremove`, `gateway`, `backend`, `frontend`, `verify` and `health`. The script
stops at the first failure and names the component.

`health` fails if the apps cannot reach the database. `verify` lists their outbound IPs; add a firewall rule for
each one, or allow Azure services on the SQL server, then rerun `health`:

```powershell
az sql server firewall-rule create -g <resource group> -s <sql server> -n <rule name> `
    --start-ip-address <ip> --end-ip-address <ip>
```

## 7. Deploy each environment

```powershell
foreach ($name in 'dev', 'bat', 'test') {
    ./script/deployment/Deploy-Wilson.ps1 -Environment $name -Component all -PackagePath artifacts/package/wtc-eim-deploy-local
}
```

This creates the queues and the Automation app, with every queue and timer trigger disabled. Turn them on with
`cutover`. With no legacy app, it enables every gated trigger except those in `automation.keepDisabled`:

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment dev -Component cutover
```

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
- **The shared plan**: every non-production Function App and the dashboard API share one P0v3 instance, so load
  in one environment affects the others.
- **Triggers left disabled**: a new Automation app does nothing until `cutover` runs. `verify` shows the trigger
  states.
- **Key Vault**: there is no component for it, because nothing reads from it.
