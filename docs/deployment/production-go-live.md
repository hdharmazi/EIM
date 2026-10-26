# Production go-live

The steps for standing up the production environment (configuration `prd`) and switching production traffic to
it. How production is built and kept apart from non-production is in
[deployment.md](deployment.md#production); the pipeline is [`pipelines/eim-prd.yml`](../../pipelines/eim-prd.yml).

Everything lives in the existing resource group **`d365fsc-integration-prod`** (centralus), in the same
subscription as `d365fsc-integration-nonprod`. Nothing in production is shared with non-production. Every name
below, except the resource group, is the pattern name the script derives; do not override them. The script creates
each resource when it is missing.

| Resource | Name | Created by `eim-prd`, `Deploy_prd` |
|---|---|---|
| Resource group | `d365fsc-integration-prod` | exists already |
| Service Bus namespace (Standard), queues | `sbns-eim-wtc-prd`, `prd-*` | `infra` |
| Key Vault (access policies) | `kv-eim-wtc-prd` | `infra` |
| Plan, Log Analytics | `asp-eim-wtc-prd`, `log-eim-wtc-prd` | `infra` |
| Gateway, dashboard API and SPA | `azf-eim-gateway-wtc-prd`, `app-eim-dashboard-api-wtc-prd`, `swa-eim-dashboard-wtc-prd` | `infra` |
| Automation app | `azf-eim-automation-wtc-prd` | `infra` |
| SQL server (Entra-only), database (`S0`, `Local` backups) | `sql-eim-wtc-prd`, `sqldb-eim-wtc-prd` | `sql` |

No secret is written in this document, in the repository or in a pipeline variable. The apps reach SQL with their
managed identities, and the Service Bus connection is kept in the Key Vault secret `eim-servicebus` by `settings`.

## 1. Non-production first

Production promotes a package that bat and test ran. Before any production work, `eim` must have deployed the same
tooling to bat and test, with migrations run: migration `015_seed_prd_routes.sql` adds ten inert `PRD` rows to
`eimdb`.

## 2. SQL admin group

1. Create an Entra security group for the production SQL admins. Add the production operators and the
   `sc-eim-wtc-prd` principal (created in step 3; add it then).
2. Commit its display name and object id in `deploy.prd.json`, replacing the placeholders:

   ```json
   "sql": {
     "entraAdmin": "<production sql entra admin group>",
     "entraAdminObjectId": "<production sql entra admin group object id>"
   }
   ```

   Neither value is secret. `sql` refuses to create the server while either is a placeholder.
3. The base sizing is `sql.sku` `S0` and `sql.backupRedundancy` `Local`. To change it, set those keys in
   `deploy.prd.json` before the first run; an existing database is never resized by the script.

## 3. Identity and DevOps

1. **Entra.** Register one production application for the SPA and the API, as in
   [deploy-from-scratch.md](deploy-from-scratch.md) step 2: expose `user_impersonation`, and add the SPA redirect
   URI `https://<swa-eim-dashboard-wtc-prd host>` once the static web app exists (step 4). Commit the tenant ID,
   client ID and audience, and `automation.excludedUsers`, in `deploy.prd.json`, replacing the `<production ...>`
   placeholders. None of them is secret, and the `Build` stage of `eim` needs them to build the production SPA.
   Until they are committed, the package has no `prd` SPA and the `frontend` step fails preflight.
2. **Azure DevOps.** Follow [devops-setup.md](devops-setup.md), section 9: environment `eim-wtc-prd` (production
   approvers, exclusive lock), service connection `sc-eim-wtc-prd` (Contributor on `d365fsc-integration-prod`
   only), the SQL admin group, variable group `eim-wtc-prd` restricted to `eim-prd`, and the `eim-prd` pipeline
   from `/pipelines/eim-prd.yml`.

## 4. First production run

Pick the package: a successful `eim` run from `main` whose `Deploy_test` stage succeeded, built after the Entra
values were committed. The pipeline resource has `branch: main`, so **Resources** offers the latest `eim` run from
`main` by default; pull request and feature runs also publish a package, so never pick one by hand. Preflight prints
the package version from its manifest; the approver checks it.

1. Run `eim-prd` with `deployCode` cleared and nothing else ticked, choosing that `eim` run under **Resources**.
   Permit the resources. It runs preflight only, and must pass the placeholder and isolation checks.
2. Run `eim-prd` again on the same `eim` run with `deployInfra`, `applySettings`, `runMigrations`,
   `grantDatabaseAccess` and `deployCode`. In one stage, in order, it:
   - creates the namespace, queues, Key Vault, plan, workspace, gateway, dashboard API, SPA and Automation app,
     with their identities and Key Vault access (`infra`)
   - creates `sql-eim-wtc-prd` with the admin group and `sqldb-eim-wtc-prd`, and allows the apps' outbound IPs
     (`sql`)
   - writes `eim-servicebus` and every app setting; every gated trigger is written disabled, because
     `automation.triggersEnabled` is `false` (`settings`)
   - runs every migration from `001` on `sqldb-eim-wtc-prd` and creates a database user for each app's identity
     (`schema`, `sqlusers`)
   - deploys the code, then `verify` and `health`
3. Check:

```powershell
# Every gated trigger is disabled until go-live
az functionapp config appsettings list -g d365fsc-integration-prod -n azf-eim-automation-wtc-prd `
    --query "[?starts_with(name, 'AzureWebJobs.')].{name:name, value:value}" -o table

# Simulation__Enabled=false, and no other Simulation__* setting
az webapp config appsettings list -g d365fsc-integration-prod -n app-eim-dashboard-api-wtc-prd `
    --query "[?starts_with(name, 'Simulation__')].name" -o tsv
```

`health` fails if a Key Vault reference does not resolve or an app does not start. The `sqlusers` output lists each
database user with its roles.

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
   ([d365fsc-gateway.md](d365fsc-gateway.md)). No key is copied from non-production.
6. **Simulation.** Check that the dashboard has no Simulation navigation entry, that `/simulation` shows "Simulation
   is not available in this environment", that the overview card shows "Simulation off", and that
   `POST /api/simulation/run` returns 404.

## 6. Go-live

1. Repoint the external systems to the production queues on `sbns-eim-wtc-prd`:
   - D365 F&SC production business events: `prd-d365fsc-businessevents` and `prd-d365fsc-dataevent`
   - Capture production: sends to `prd-capture-to-d365fsc`, reads `prd-d365fsc-to-capture` and
     `prd-capture-to-d365fsc-response`
2. Enable the triggers by configuration: set `automation.triggersEnabled` to `true` in `deploy.prd.json` (list any
   function that must stay off in `automation.keepDisabled`) and merge it. Then run `eim-prd` with `applySettings`
   on an `eim` run built from that commit, or from an operator machine:

```powershell
./Deploy-Wilson.ps1 -Environment prd -Component settings -WhatIf
./Deploy-Wilson.ps1 -Environment prd -Component settings
```

3. Watch the first messages complete in the dashboard's Message Queue.

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

If the plan alert fires, change the plan's SKU to `P1v3`. The plan was created by the script, so set
`appServicePlan.sku` in `deploy.prd.json` as well, so the configuration matches.

## Reverting

- Before go-live, delete nothing: leave `automation.triggersEnabled` `false` and point the external systems back.
- After go-live, stop processing by setting `automation.triggersEnabled` to `false` (or listing functions in
  `automation.keepDisabled`) and running `settings`.
- Earlier code is **Rerun stage** on an older `eim-prd` run. The schema only moves forward.
- The script deletes no Azure resource; retiring a production resource is a separate, reviewed change.
