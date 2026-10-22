# Resource rename and cutover (#5246)

The one-off runbook for moving the non-production estate from the old resource names to the new ones, and from
the old pipelines to [`pipelines/eim.yml`](../../pipelines/eim.yml). How the script and pipeline work is in
[deployment.md](deployment.md).

This file names the old resources on purpose. Elsewhere in the repository a test fails if they appear.

**What changes**

- Each environment's Automation Function App is recreated under a new name on a new plan, and its queue and
  timer triggers move to it with `cutover`.
- The gateway is recreated on the new plan with Always On. Its consumers' keys are copied, so consumers change
  only the base URL.
- The dashboard is **not** replaced. `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` keep their names,
  URLs, Entra registration and linked backend, and receive code and settings in place. The API later moves to
  the new plan (step 11). Dashboard users and the Entra app registration are unaffected; there is no Entra step.
- The SQL server and `eimdb`, the Service Bus namespace and its queues, the Key Vault and the resource group keep
  their names.

**Run commands from** the repository root, or from an extracted package (then drop the `script/deployment/`
prefix). You need PowerShell 7.2+, the Azure CLI logged in with Contributor on `d365fsc-integration-nonprod`,
and, for anything that writes settings, the two secrets in `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` (or
type them at the hidden prompt). `cutover`, `rollback` and `remove` need neither the secrets nor a package.

## 1. Prerequisites

- [ ] The Azure DevOps setup in [devops-setup.md](devops-setup.md) is complete.
- [ ] Before the #5246 deployment change is merged, tag the last commit on `main` before it:
      `git tag pre-5246-deploy <commit>` and push the tag. The old pipeline definitions can run from that tag
      during the overlap.
- [ ] Any pending deployment with the old pipelines (for example #5425) is finished, or deliberately left to the
      new pipeline.
- [ ] The non-secret placeholders in `script/deployment/config/deploy.base.json` are filled: `subscriptionId`,
      `entra.*` and `automation.excludedUsers`, by a commit or through `EIM_DEPLOY_CONFIG_OVERRIDES`. The Entra
      values must be committed for the pipeline to build the SPA. Check with a manual run that has `deployCode`
      cleared and nothing else ticked: it runs preflight only, in every stage, and skips verify and health.
      Preflight must pass in every stage. That run checks the subscription, names, resource group and the
      package's gated functions only; it does not check the Entra values, `excludedUsers` or the secrets. Look for
      an "entra is incomplete" warning in the Build log, and check the rest in the preview step of the next run.

## 2. Provision the shared resources

Start a **manual run** of `eim` with:

| Parameter | Value |
|---|---|
| `deployInfra` | ticked |
| `applySettings` | ticked |
| `runMigrations` | cleared (this change adds no migration) |
| `deployCode` | ticked |

Approve `Deploy_shared`. It creates `asp-eim-wtc-nonprod`, `log-eim-wtc-nonprod`, `stwtceimgwnonprod`,
`appins-eim-gateway-wtc-nonprod` and `azf-eim-gateway-wtc-nonprod`, writes the gateway and dashboard API settings,
and deploys the gateway, the dashboard API and the SPA.

The dashboard is updated in place. Its only setting change is the gateway it points at: `Gateway__BaseUrl` and
`Gateway__AppName` now name the new gateway, and `Gateway__Applications__0__*` names the old one, so the
dashboard can verify keys against both during the overlap.

- [ ] No new dashboard resource was created.
- [ ] The dashboard opens and signs in at its existing URL.

## 3. Provision each environment

In the same run, `Deploy_dev`, then (approved) `Deploy_bat` and `Deploy_test` create, per environment, the
storage `stwtceimauto{env}`, Insights `appins-eim-automation-wtc-{env}` and the Function App
`azf-eim-automation-wtc-{env}` on `asp-eim-wtc-nonprod`, write its settings, and deploy the Automation code. The
queues already exist and are kept.

Every queue and timer trigger on a new Automation app starts **disabled** (`AzureWebJobs.<fn>.Disabled=true`).
The old app keeps consuming the queues until `cutover`. Never enable a trigger by hand: two apps consuming the
same queue break per-environment ordering.

- [ ] Each new Automation app has all six gated triggers disabled.
- [ ] `PollSchedule` is absent from the new Automation apps.

## 4. SQL firewall

The new plan and the new gateway may use outbound IPs the SQL firewall does not allow yet.

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment nonprod -Component verify
./script/deployment/Deploy-Wilson.ps1 -Environment dev -Component verify
```

`verify` prints each app's outbound IPs. Unless **Allow Azure services and resources to access this server** is
already on for `azql-wtc-sandbox`, add a rule per IP:

```powershell
az sql server firewall-rule create -g d365fsc-integration-nonprod -s azql-wtc-sandbox `
    -n <rule name, e.g. asp-eim-wtc-nonprod-1> --start-ip-address <ip> --end-ip-address <ip>
```

## 5. Deploy

If a stage failed in step 2 or 3 (most often `health`, before the firewall rules existed), rerun the failed
stage of the same run. A rerun is safe: `infra` creates only what is missing, and `settings` never re-disables
a trigger that is already set.

From now on, a merge to `main` deploys code only.

## 6. Verify

```powershell
foreach ($name in 'nonprod', 'dev', 'bat', 'test') {
    ./script/deployment/Deploy-Wilson.ps1 -Environment $name -Component verify
    ./script/deployment/Deploy-Wilson.ps1 -Environment $name -Component health
}
```

- [ ] Every Function App: state Running, Always On, plan `asp-eim-wtc-nonprod`.
- [ ] Gated triggers: disabled on each new Automation app, enabled as before on each old one.
- [ ] The static web app's linked backend is `wtc-dashboard-api-dev`.
- [ ] `health` passes for every configuration.

Then check the data the database holds about the old apps, across **every** row, whatever its ENVIRONMENT value
(`DEV`, `DEV2`, `BAT`, `TEST`), not only the three deployed configurations. The query returns keys only, because
the values hold credentials:

```sql
SELECT N'ENVIRONMENTTABLE' AS [TABLE], [SYSTEM] AS [KEY1], [ENVIRONMENT] AS [KEY2]
FROM dbo.ENVIRONMENTTABLE
WHERE [VALUE] LIKE '%azf-d365fsc-gateway%' OR [VALUE] LIKE '%azf-eim-customer%'
UNION ALL
SELECT N'CONFIGURATIONTABLE', [CATEGORY], [NAME]
FROM dbo.CONFIGURATIONTABLE
WHERE [VALUE] LIKE N'%azf-d365fsc-gateway%' OR [VALUE] LIKE N'%azf-eim-customer%';
```

- [ ] No rows, or each row found is updated to the new host name.

## 7. Cut over each environment: dev, then bat, then test

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment dev -Component cutover -WhatIf   # preview
./script/deployment/Deploy-Wilson.ps1 -Environment dev -Component cutover
```

`cutover`:

1. Checks the new app: it exists, its host is Running and it has every gated setting.
2. Compares settings with the old app. A setting the old app has and the new one lacks fails the run. A
   difference in `EIMDB`, `ServiceBus` or a queue name fails the run; other differences warn. Platform settings
   and `PollSchedule` are ignored. Only setting names are printed.
3. Records which triggers are enabled on the old app (`%TEMP%/wtc-eim-cutover-<env>.json`), disables them all,
   and waits until every one reports disabled.
4. Waits the longest queue lock duration (read from the queues; 60 s today), so in-flight messages complete or
   are released.
5. Enables on the new app exactly the triggers that were enabled on the old one, and prints a state table.

If it fails at step 3 or later, both apps may be left with every trigger disabled. That is safe: messages wait in
the queues. The error prints the commands to rerun or to roll back; a rerun uses the recorded states. Rerunning
after success changes nothing.

Checks, for each environment:

- [ ] The queues drain on the new app (Service Bus active message counts fall back to normal).
- [ ] The dashboard shows new message rows for the environment.
- [ ] No failures in `appins-eim-automation-wtc-{env}`.
- [ ] Observe dev for one day before bat; then bat before test.

## 8. Gateway: copy the keys and switch the consumers

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment nonprod -Component cutover -WhatIf   # key names only
./script/deployment/Deploy-Wilson.ps1 -Environment nonprod -Component cutover
```

This copies every named host key and every `D365Data`/`D365Service` function key from
`azf-d365fsc-gateway-wtc-nonprod` to `azf-eim-gateway-wtc-nonprod`, with the same name and value. It skips the
master key, system keys and the generated `default` keys (add `-IncludeDefaultKeys` to copy `default` too). Only
key names are printed. A rerun copies nothing new.

Because the values are the same, each consumer's stored `KEYHASH` still matches, and consumers change only the
base URL:

- old: `https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net`
- new: `https://azf-eim-gateway-wtc-nonprod.azurewebsites.net`

- [ ] Notify every gateway consumer of the new base URL and an agreed switch window.
- [ ] The old gateway keeps serving, with the same keys, until step 13.

## 9. Update `GATEWAYURL`

As each consumer confirms the switch:

```sql
-- Consumers pinned to the old gateway
SELECT CONSUMERID, GATEWAYURL, VERIFIEDAPP
FROM dbo.GATEWAYCONSUMER
WHERE GATEWAYURL LIKE N'%azf-d365fsc-gateway-wtc-nonprod%';

UPDATE dbo.GATEWAYCONSUMER
SET GATEWAYURL = N'https://azf-eim-gateway-wtc-nonprod.azurewebsites.net'
WHERE CONSUMERID = N'<consumer id>'
  AND GATEWAYURL = N'https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net';
```

A consumer with no `GATEWAYURL` uses the dashboard's configured gateway, which is now the new one; nothing to
update. `VERIFIEDAPP` records the app a key was last verified against and is informational; use **Set key** in the
dashboard to re-verify against the new app if wanted.

Also repeat the `ENVIRONMENTTABLE`/`CONFIGURATIONTABLE` check from step 6 if any row was changed since.

## 10. App Insights queries, alerts and the plan alert

The Automation logger categories moved from the previous project namespace (renamed in #5246, Phase 1) to
`Wilson.Integration.Automation`, and the telemetry now goes to the new Insights resources:

| Old | New |
|---|---|
| `appins-eim-customer-wtc-{env}` | `appins-eim-automation-wtc-{env}` (workspace-based, in `log-eim-wtc-nonprod`) |
| `appins-d365fsc-gateway-wtc-nonprod` | `appins-eim-gateway-wtc-nonprod` |

- [ ] Update saved queries, workbooks and alert rules that name the old category or the old resources. The old
      data stays queryable until step 13.
- [ ] Add a CPU and a memory alert above 80% on the plan:

```powershell
$plan = az appservice plan show -g d365fsc-integration-nonprod -n asp-eim-wtc-nonprod --query id -o tsv
az monitor metrics alert create -g d365fsc-integration-nonprod -n asp-eim-wtc-nonprod-cpu-80 --scopes $plan `
    --condition "avg CpuPercentage > 80" --window-size 15m --evaluation-frequency 5m --action <action group id>
az monitor metrics alert create -g d365fsc-integration-nonprod -n asp-eim-wtc-nonprod-memory-80 --scopes $plan `
    --condition "avg MemoryPercentage > 80" --window-size 15m --evaluation-frequency 5m --action <action group id>
```

If an alert fires, set `appServicePlan.sku` to `P1v3` in `deploy.base.json` and change the plan's SKU
(`az appservice plan update -g d365fsc-integration-nonprod -n asp-eim-wtc-nonprod --sku P1V3`).

## 11. Move the dashboard API to the new plan

After every Automation cutover (step 7) and the gateway key copy (step 8), and before `remove` for `nonprod`.
While `wtc-dashboard-api-dev` stays on `asp-appservice-p0v3-linux-dev`, that plan cannot be deleted and two P0v3
plans are billed.

```powershell
$plan = az appservice plan show -g d365fsc-integration-nonprod -n asp-eim-wtc-nonprod --query id -o tsv
az webapp update -g d365fsc-integration-nonprod -n wtc-dashboard-api-dev --set serverFarmId=$plan
```

The app keeps its name, URL, settings and the static web app link (its resource ID does not change). Expect
**one restart**.

A cross-plan move needs the same resource group, region, OS and App Service webspace. Both plans are in
`d365fsc-integration-nonprod`, `centralus`, Linux. **If Azure refuses the move** (a different webspace), leave the
API where it is and scale the old plan down instead
(`az appservice plan update -g d365fsc-integration-nonprod -n asp-appservice-p0v3-linux-dev --sku <smaller sku>`).
`remove` then keeps the old plan and names the API as the app still on it.

Afterwards:

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment nonprod -Component verify    # plan, outbound IPs
./script/deployment/Deploy-Wilson.ps1 -Environment nonprod -Component health
```

- [ ] The SQL firewall allows the API's outbound IPs (see step 4).
- [ ] `health` passes.
- [ ] Dashboard sign-in works, and reprocessing a message works.

## 12. Rollback

Before step 13, every move can be undone:

| What | How |
|---|---|
| An environment's Automation | `./script/deployment/Deploy-Wilson.ps1 -Environment <env> -Component rollback`: disables the triggers on the new app, waits for them to report disabled plus the lock duration, then enables on the old app those that were enabled on the new one. Refused once the old app is gone. |
| Code on a new app | rerun the deploy stage of an earlier run of `eim`; each run carries its own package |
| A gateway consumer | the consumer goes back to the old base URL (the old gateway still runs with the same keys); set its gateway address in the dashboard to the old URL. `rollback -Environment nonprod` is refused on purpose. |
| The dashboard | it has no old copy; rerun `Deploy_shared` of an earlier run for its code. During the overlap it verifies keys against both gateways. |
| The dashboard API's plan | `az webapp update ... --set serverFarmId=<old plan id>`, as in step 11 |
| The schema | none; migrations only move forward, and this change adds none |

After step 13 there is nothing to roll back to. Recreate from the pipeline instead.

## 13. Decommission

After the observation window: at least 7 days with no requests to the old gateway, and each environment stable
on its new Automation app.

```kusto
// In appins-d365fsc-gateway-wtc-nonprod: expect nothing
requests
| where timestamp > ago(7d)
| summarize count() by bin(timestamp, 1d)
```

From an **operator machine** (`remove` refuses to run in a pipeline), for `dev`, `bat`, `test`, and then `nonprod`:

```powershell
./script/deployment/Deploy-Wilson.ps1 -Environment dev -Component remove -WhatIf
./script/deployment/Deploy-Wilson.ps1 -Environment dev -Component remove
```

- `remove` deletes only the resources listed under `legacy` in that configuration, and only of four types:
  Function/web apps, App Service plans, storage accounts and Application Insights.
- The `-WhatIf` run lists what would be deleted and records a marker
  (`%TEMP%/wtc-eim-remove-<env>-<yyyyMMdd>.marker`). The real run needs that marker from the same day and machine,
  then asks you to type the configuration name. `-Confirm:$false` is refused.
- It refuses while any gated trigger on the old Automation app is enabled (cut over first), and if a `legacy`
  name equals a name in use. The dashboard is never `legacy`, so it can never be deleted.
- For `nonprod` it deletes the old gateway, its Flex Consumption plan (read from the app), its storage and its
  Insights, and `asp-appservice-p0v3-linux-dev` **only if no app is left on it**. Otherwise it keeps the plan and
  names the app still on it (`wtc-dashboard-api-dev` if step 11 fell back).

- [ ] The old plan was deleted, or was kept and named because the dashboard API is still on it.

## 14. Clean up

- [ ] Delete the three old pipeline definitions and their variables, and the old DevOps environments
      ([devops-setup.md](devops-setup.md), section 8).
- [ ] Remove the old SQL firewall rules for IPs no app uses any more.
- [ ] In a follow-up commit: empty `gateway.legacyVerificationTargets` in `deploy.base.json`, remove the `legacy`
      sections from the configurations, remove the legacy-name exceptions from `RepositoryLayoutTests`, and
      archive the OpenSpec change `align-structure-and-deployment`.

## 15. Names

### Old to new

Per environment (`{env}` is `dev`, `bat` or `test`: deployment configurations, not ENVIRONMENT values; `DEV2` has
no Function App):

| Resource | Old | New |
|---|---|---|
| Automation Function App | `azf-eim-customer-wtc-{env}` | `azf-eim-automation-wtc-{env}` |
| Automation storage | `stwtceimcustomer{env}` | `stwtceimauto{env}` |
| Automation Insights | `appins-eim-customer-wtc-{env}` | `appins-eim-automation-wtc-{env}` |
| Service Bus queues | `{env}-d365fsc-dataevent`, `{env}-d365fsc-businessevents`, `{env}-d365fsc-to-capture`, `{env}-capture-to-d365fsc`, `{env}-capture-to-d365fsc-response` | unchanged |

Shared (`nonprod`):

| Resource | Old | New |
|---|---|---|
| App Service plan (P0v3, Linux) | `asp-appservice-p0v3-linux-dev` | `asp-eim-wtc-nonprod` |
| Log Analytics workspace | none | `log-eim-wtc-nonprod` |
| Gateway Function App | `azf-d365fsc-gateway-wtc-nonprod` (Flex Consumption) | `azf-eim-gateway-wtc-nonprod` (on the plan, Always On) |
| Gateway Flex plan | created with the app | none |
| Gateway storage | `stwtcd365gwnonprod` | `stwtceimgwnonprod` |
| Gateway Insights | `appins-d365fsc-gateway-wtc-nonprod` | `appins-eim-gateway-wtc-nonprod` |

### Kept

| Resource | Name | Pattern name, if renamed later |
|---|---|---|
| Resource group | `d365fsc-integration-nonprod` | `rg-eim-wtc-nonprod` |
| Service Bus namespace | `asb-eim-wtc-nonprod` | `sbns-eim-wtc-nonprod` |
| Key Vault | `kv-d365fsc-wtc-nonprod` | `kv-eim-wtc-nonprod` |
| SQL server | `azql-wtc-sandbox` | `sql-eim-wtc-nonprod` |
| Database | `eimdb` (and `eimdb_dev`, local development only) | `sqldb-eim-wtc-nonprod` |
| Dashboard API | `wtc-dashboard-api-dev` (moves to `asp-eim-wtc-nonprod` in step 11) | `app-eim-dashboard-api-wtc-nonprod` |
| Dashboard static web app | `wtc-dashboard-frontend-dev` | `swa-eim-dashboard-wtc-nonprod` |
| Region | `centralus` | |
