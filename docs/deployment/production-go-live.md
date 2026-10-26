# Production go-live

The runbook for standing up the production stamp (configurations `prdshared` and `prd`) and switching
production traffic to it. How the stamp is built and kept apart from non-production is in
[deployment.md](deployment.md#production); the pipeline is [`pipelines/eim-prd.yml`](../../pipelines/eim-prd.yml).

Everything lives in the existing resource group **`d365fsc-integration-prod`** (centralus), in the same
subscription as `d365fsc-integration-nonprod`. Nothing here creates a resource group, and nothing in production is
shared with non-production. Every name below is the pattern name the script derives; do not override them.

| Resource | Name | Created by |
|---|---|---|
| Resource group | `d365fsc-integration-prod` | exists already |
| Service Bus namespace (Standard) | `sbns-eim-wtc-prd` | this runbook, step 2 |
| SQL server | `sql-eim-wtc-prd` | this runbook, step 2 |
| Database | `sqldb-eim-wtc-prd` | this runbook, step 2 |
| Plan, Log Analytics, gateway, dashboard API and SPA | `asp-eim-wtc-prd`, `log-eim-wtc-prd`, `azf-eim-gateway-wtc-prd`, `app-eim-dashboard-api-wtc-prd`, `swa-eim-dashboard-wtc-prd` | `eim-prd`, `Deploy_prdshared` (`infra`) |
| Queues and the Automation app | `prd-*`, `azf-eim-automation-wtc-prd` | `eim-prd`, `Deploy_prd` (`infra`) |

No secret is written in this document or in the repository. Connection strings go only into the `eim-wtc-prd`
variable group, marked secret.

## 1. Non-production first

The change that introduced production also added the dashboard setting `Simulation__Enabled`. Before any
production work:

1. Run `eim` manually with `applySettings` and `deployCode`. `wtc-dashboard-api-dev` gets
   `Simulation__Enabled=true` before the new API code starts, so simulation stays on for non-production.
2. Run `eim` with `runMigrations` (in the same run or a later one). Migration `015_seed_prd_routes.sql` adds ten
   inert `PRD` rows to `eimdb`. Check that the BAT, DEV, DEV2 and TEST row counts did not change.

## 2. Foundations

Confirm the group exists and that you have Contributor on it. Do not create it.

```powershell
az group show --name d365fsc-integration-prod --query "{name:name, location:location}" -o table
```

Create the namespace, the SQL server and the database inside it:

```powershell
az servicebus namespace create --resource-group d365fsc-integration-prod --name sbns-eim-wtc-prd `
    --location centralus --sku Standard

az sql server create --resource-group d365fsc-integration-prod --name sql-eim-wtc-prd --location centralus `
    --admin-user <sql admin login> --admin-password <sql admin password, from the password manager>
az sql server ad-admin create --resource-group d365fsc-integration-prod --server-name sql-eim-wtc-prd `
    --display-name <production SQL admin group> --object-id <its object id>

az sql db create --resource-group d365fsc-integration-prod --server sql-eim-wtc-prd --name sqldb-eim-wtc-prd `
    --collation SQL_Latin1_General_CP1_CI_AS --service-objective S0
```

- Service Bus must be Standard: the design relies on dead-lettering.
- The collation must be the case-insensitive `SQL_Latin1_General_CP1_CI_AS`, the same as `eimdb`. Route lookups
  depend on it: the Automation host sends ENVIRONMENT `prd` and the routes say `PRD`.
- SQL authentication follows the non-production model: the deploy and app connection strings use a contained SQL
  user, and the server also gets an Entra admin. Record the SKU, backup retention and admin choices in the
  change record; the values above are the minimum.
- Create the contained user in `sqldb-eim-wtc-prd` (connected as the Entra admin) and build the two connection
  strings for step 3: `Server=tcp:sql-eim-wtc-prd.database.windows.net,1433;Initial Catalog=sqldb-eim-wtc-prd;...`
  and the `RootManageSharedAccessKey` (or a dedicated) connection string of `sbns-eim-wtc-prd`. Never paste them
  anywhere but the variable group.

Preflight checks that all four exist, and that the secrets point at `sql-eim-wtc-prd`, `sqldb-eim-wtc-prd` and
`sbns-eim-wtc-prd`, so a typo or a non-production secret fails before anything changes.

## 3. Identity and DevOps

1. **Entra.** Register one production application for the SPA and the API, as in
   [deploy-from-scratch.md](deploy-from-scratch.md) step 2: expose `user_impersonation`, add the SPA redirect URI
   `https://<swa-eim-dashboard-wtc-prd host>` once the static web app exists (step 4). Commit the tenant ID, client
   ID and audience, and `automation.excludedUsers`, in `deploy.prdshared.json` and `deploy.prd.json`, replacing
   the `<production ...>` placeholders. None of them is secret, and the `Build` stage of `eim` needs them to build
   the production SPA. Until they are committed, the package has no `prdshared` SPA and the `frontend` step fails
   preflight.
2. **Azure DevOps.** Follow [devops-setup.md](devops-setup.md), section 9: environments `eim-wtc-prdshared` and
   `eim-wtc-prd` (production approvers, exclusive lock), service connection `sc-eim-wtc-prd` (Contributor on
   `d365fsc-integration-prod` only), variable group `eim-wtc-prd` restricted to `eim-prd`, and the `eim-prd`
   pipeline from `/pipelines/eim-prd.yml`.

## 4. First production run

Pick the package: a successful `eim` run from `main` whose `Deploy_test` stage succeeded, built after the Entra
values were committed. The pipeline resource has `branch: main`, so **Resources** offers the latest `eim` run from
`main` by default; pull request and feature runs also publish a package, so never pick one by hand. Preflight prints
the package version from its manifest; the approver checks it.

1. Run `eim-prd` with `deployCode` cleared and nothing else ticked, choosing that `eim` run under **Resources**.
   Permit the resources. It runs preflight only, and must pass the foundation, secret-target and isolation
   checks.
2. Run `eim-prd` again with `deployInfra`, `applySettings`, `runMigrations` and `deployCode`, on the same `eim`
   run. `Deploy_prdshared` creates the plan, workspace, gateway, dashboard API and SPA, writes their settings, runs
   every migration from `001` on `sqldb-eim-wtc-prd`, and deploys the code. `Deploy_prd` creates the queues and
   `azf-eim-automation-wtc-prd`, with every queue and timer trigger disabled.
3. Check:

```powershell
# Every gated trigger is disabled until go-live
az functionapp config appsettings list -g d365fsc-integration-prod -n azf-eim-automation-wtc-prd `
    --query "[?starts_with(name, 'AzureWebJobs.')].{name:name, value:value}" -o table

# Simulation__Enabled=false, and no other Simulation__* setting
az webapp config appsettings list -g d365fsc-integration-prod -n app-eim-dashboard-api-wtc-prd `
    --query "[?starts_with(name, 'Simulation__')].name" -o tsv
```

4. From an operator machine, confirm that `remove` is refused for both production configurations:

```powershell
./Deploy-Wilson.ps1 -Environment prdshared -Component remove -WhatIf
./Deploy-Wilson.ps1 -Environment prd -Component remove -WhatIf
```

`health` fails if the apps cannot reach the database. `verify` lists their outbound IPs; add a firewall rule on
`sql-eim-wtc-prd` for each one (or allow Azure services), then rerun `health`.

## 5. Data

Sign in to the production dashboard (`swa-eim-dashboard-wtc-prd`).

1. **`ENVIRONMENTTABLE`.** On the Environments page, enter the `PRD` profiles: `D365FSC` / `PRD` for the
   Automation host and `D365FSC-GW` / `PRD` for the gateway. They hold D365 credentials and are never entered by
   migration or commit.
2. **`USERTABLE`.** Migration `001` seeds one developer admin. Add the production admins on the Users page, then
   deactivate the seeded row if it is not wanted there:

```sql
SELECT EMAIL, DISPLAYNAME, ROLE, ISACTIVE FROM dbo.USERTABLE ORDER BY CREATEDDATETIME;

-- Only if the seeded developer admin should not have production access
UPDATE dbo.USERTABLE SET ISACTIVE = 0 WHERE EMAIL = '<seeded developer email>';
```

3. **Routes.** Confirm the ten `PRD` routes on the Route Control page, or:

```sql
SELECT ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE, ENABLED, ONPAUSE
FROM dbo.ROUTETABLE WHERE ENVIRONMENT = 'PRD' ORDER BY SOURCESYSTEM, SOURCEENTITY;
```

   Nine are enabled. The address delete route (`mserp_wtc_custpostaladdressdeletedv2entity`) is disabled with
   `Hold`, so deletes are held, not dead-lettered, until Capture confirms the delete contract; enable it on the
   Route Control page then.
4. **Inert rows.** `001`, `002` and `006` also seeded a sample `ENVIRONMENTTABLE` row, `SimulationEnvironment`
   configuration rows and the BAT/DEV/DEV2/TEST routes. Leave them: no production message carries those
   ENVIRONMENT values, and simulation is off.
5. **Gateway consumers.** Onboard each consumer on `azf-eim-gateway-wtc-prd` with a new host key and **Set key**
   ([d365fsc-gateway.md](d365fsc-gateway.md)). No key is copied from non-production; `cutover` for `prdshared` is
   refused for that reason.
6. **Simulation.** Check that the dashboard has no Simulation navigation entry, that `/simulation` shows "Simulation
   is not available in this environment", that the overview card shows "Simulation off", and that
   `POST /api/simulation/run` returns 404.

## 6. Go-live

1. Repoint the external systems to the production queues on `sbns-eim-wtc-prd`:
   - D365 F&SC production business events: `prd-d365fsc-businessevents` and `prd-d365fsc-dataevent`
   - Capture production: sends to `prd-capture-to-d365fsc`, reads `prd-d365fsc-to-capture` and
     `prd-capture-to-d365fsc-response`
2. Enable the triggers from an operator machine. With no legacy app, `cutover` enables every gated trigger except
   those in `automation.keepDisabled`:

```powershell
./Deploy-Wilson.ps1 -Environment prd -Component cutover -WhatIf
./Deploy-Wilson.ps1 -Environment prd -Component cutover
```

3. Watch the first messages complete in the dashboard's Message Queue.

There is no `rollback` for `prd`: there is no legacy app to move the triggers back to.

## 7. Alerts

Created by hand in `d365fsc-integration-prod`, with the production action group:

```powershell
$rg = 'd365fsc-integration-prod'
$actionGroup = '<production action group id>'

# Plan CPU and memory above 80%
$plan = az appservice plan show -g $rg -n asp-eim-wtc-prd --query id -o tsv
az monitor metrics alert create -g $rg -n asp-eim-wtc-prd-cpu-80 --scopes $plan `
    --condition "avg CpuPercentage > 80" --window-size 15m --evaluation-frequency 5m --action $actionGroup
az monitor metrics alert create -g $rg -n asp-eim-wtc-prd-memory-80 --scopes $plan `
    --condition "avg MemoryPercentage > 80" --window-size 15m --evaluation-frequency 5m --action $actionGroup

# Function host availability, for both Function Apps
foreach ($app in 'azf-eim-gateway-wtc-prd', 'azf-eim-automation-wtc-prd') {
    $id = az functionapp show -g $rg -n $app --query id -o tsv
    az monitor activity-log alert create -g $rg -n "$app-resource-health" --scope $id `
        --condition category=ResourceHealth --action-group $actionGroup
}

# Dead-lettered messages on each prd-* queue
$namespace = az servicebus namespace show -g $rg -n sbns-eim-wtc-prd --query id -o tsv
foreach ($queue in 'prd-d365fsc-dataevent', 'prd-d365fsc-businessevents', 'prd-d365fsc-to-capture',
                   'prd-capture-to-d365fsc', 'prd-capture-to-d365fsc-response') {
    az monitor metrics alert create -g $rg -n "$queue-deadletter" --scopes $namespace `
        --condition "max DeadletteredMessages > 0 where EntityName includes $queue" `
        --window-size 15m --evaluation-frequency 5m --action $actionGroup
}

# Dashboard API HTTP 5xx
$api = az webapp show -g $rg -n app-eim-dashboard-api-wtc-prd --query id -o tsv
az monitor metrics alert create -g $rg -n app-eim-dashboard-api-wtc-prd-5xx --scopes $api `
    --condition "total Http5xx > 0" --window-size 15m --evaluation-frequency 5m --action $actionGroup
```

If the plan alert fires, change the plan's SKU to `P1v3`, and set `appServicePlan.sku` in both production
configuration files so the next `infra` run keeps it.

## Rollback

- Before go-live, delete nothing: leave the Automation triggers disabled (or disable them again) and point the
  external systems back.
- After go-live, code rollback is **Rerun stage** on an older `eim-prd` run. The schema only moves forward.
- `remove` never runs against production; retiring a production resource is a separate, reviewed change.
- On non-production, `dashboard.simulationEnabled` is the only behavioural change of step 1, and a settings run
  flips it back.
