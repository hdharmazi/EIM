# Azure DevOps setup (one time)

The checklist for running [`pipelines/eim.yml`](../../pipelines/eim.yml) and, for production,
[`pipelines/eim-prd.yml`](../../pipelines/eim-prd.yml) (section 9). Do it once, before the first run.
How the pipelines and the script work is in [deployment.md](deployment.md).

Nothing in this list puts a secret anywhere. The pipelines need no secret variable: the apps reach SQL with their
managed identity, the deployer signs in to SQL with the service connection's identity, and the Service Bus
connection is kept in Key Vault by the script.

## 1. Environments

**Pipelines > Environments > New environment**, type **None**. Then, on each one, **Approvals and checks**:

| Environment | Used by | Approvals | Exclusive lock |
|---|---|---|---|
| `eim-wtc-bat` | `Deploy_bat` | yes | yes |
| `eim-wtc-test` | `Deploy_test` | yes | yes |

There is no development environment or stage: development runs locally. The environment `eim-wtc-nonprod`, and the
production environment for shared resources, belonged to an earlier pipeline layout and are no longer used; delete
them if they exist, as well as any environment created for a development stage.

- **Approvals:** add the responsible reviewers. Approvals live in Azure DevOps, not in the YAML, so there is no
  `ManualValidation` step.
- **Exclusive lock:** stops two runs deploying to the same environment at once. With
  `lockBehavior: runLatest` in the YAML, a newer run waiting at an environment replaces an older one that was
  never approved.
- `eim-wtc-bat` gates the gateway (used by external consumers), the dashboard used by every non-production
  environment and, on manual runs, migrations and database users on the one `eimdb` every ENVIRONMENT value reads.

## 2. Service connection

**Project settings > Service connections > New service connection > Azure Resource Manager**:

| Field | Value |
|---|---|
| Identity type | App registration (automatic), **Workload identity federation** |
| Scope level | Subscription, then resource group `d365fsc-integration-nonprod` |
| Service connection name | `sc-eim-wtc-nonprod` |
| Grant access permission to all pipelines | off; authorize the pipeline on its first run instead |

The identity gets **Contributor** on `d365fsc-integration-nonprod`. That covers the plan, apps, storage, Insights,
queues, app settings, managed identities, Key Vault access policies, the static web app deployment token and the
SQL firewall rules. No role assignment is needed: the script assigns no RBAC role.

Three more grants, which Contributor does not give:

- **SQL admin group.** Add the service connection's principal to the Entra group that is the Entra admin of the SQL
  server (`azql-wtc-sandbox`; section 3). `schema` and `sqlusers` sign in as that principal.
- **Key Vault secrets.** On the existing vault `kv-d365fsc-wtc-nonprod` (access-policy mode), give the principal
  secret `get`, `list` and `set` (**Access policies > Create**). `settings` writes the secret `eim-servicebus`.
  A vault the script creates gives its creator these permissions itself.
- **An isolated environment** (a configuration without `uses` whose resource group does not exist) needs the
  principal to be able to create resource groups in the subscription, or a `names.resourceGroup` override to an
  existing group it has Contributor on. `bat` and `prd` use existing groups.

If the automatic option is not available to you, create the connection with **App registration or managed
identity (manual)**, federated, and ask a subscription owner to assign Contributor on the resource group.

There is no publish profile, SCM basic authentication or static web app token to store. The script fetches what
it needs through the service connection at run time.

## 3. SQL admin group

The apps and the deployer reach SQL through Entra ID, so the SQL server needs an Entra admin, and the admin should
be a group:

1. Create an Entra security group for the non-production SQL admins. Add the operators who run migrations or
   database users by hand, and the `sc-eim-wtc-nonprod` principal.
2. Set it as the Entra admin of `azql-wtc-sandbox`. The script does not: it never changes a server it did not
   create. Preview the command first:

   ```bash
   az sql server ad-admin create -g d365fsc-integration-nonprod -s azql-wtc-sandbox \
     --display-name <group name> --object-id <group object id>
   ```

   SQL authentication stays enabled, so nothing that still uses a SQL login breaks.
3. Check it with `Deploy-Wilson.ps1 -Environment bat -Component sql -WhatIf`, which reports the Entra admin.

Preflight fails `schema` and `sqlusers` while the server has no Entra admin.

## 4. Variable group

**Pipelines > Library > + Variable group**, named `eim-wtc-nonprod`:

| Variable | Secret | Value |
|---|---|---|
| `EIM_DEPLOY_CONFIG_OVERRIDES` | no | optional JSON merged over the configuration, e.g. `{"subscriptionId":"<subscription id>"}` |

- The group holds no secret. If it still has the two connection-string variables of the earlier layout, delete
  them; nothing reads them.
- `EIM_DEPLOY_CONFIG_OVERRIDES` may be left out. An undefined variable reaches the script as the literal text
  `$(EIM_DEPLOY_CONFIG_OVERRIDES)`, which the script ignores.
- One group serves every non-production stage. Production has its own group, `eim-wtc-prd` (section 9).
- **Pipeline permissions:** restrict the group to the `eim` pipeline.
- `EIM_DEPLOY_CONFIG_OVERRIDES` is read by the deploy stages only. The SPA is built in the `Build` stage from the
  committed configuration, so the Entra values must be committed in `deploy.base.json`; they are not secret.

## 5. Create the pipeline

1. **Pipelines > New pipeline > Azure Repos Git >** `ARM.Integration.Wilson`.
2. **Existing Azure Pipelines YAML file**, path `/pipelines/eim.yml`.
3. **Save** (do not run yet). Rename it to `eim`.
4. **Run pipeline** manually the first time, with `deployCode` cleared and nothing else ticked. That run is
   preflight only: every stage runs preflight and skips verify and health, so it passes before the new apps exist.
   On the first run Azure DevOps asks you to **Permit** the pipeline to use the service connection, the variable
   group and each environment. Permit each one.

## 6. Branch policy

On `main`, the existing build validation runs on pull requests. The `eim` pipeline's `pr` trigger runs the
`Build` stage only (tests, the script self-test and the package checks) and never a deploy stage. Add it as a
build validation policy on `main` if you want those checks to block merges.

## 7. The auto-AI pull request pipeline

The YAML lives in `pipelines/auto-ai-pull-request.yml`. Edit the existing pipeline definition:
**Edit > ... > Settings > YAML file path** = `/pipelines/auto-ai-pull-request.yml`. Its setup is otherwise
unchanged; see [../auto-ai-pull-request-setup.md](../auto-ai-pull-request-setup.md).

## 8. Build service permissions and the old pipeline definitions

The pipeline only reads the repository and publishes an artifact; it does not push tags or commits. The project's
build service identity needs:

- **Read** on the `ARM.Integration.Wilson` repository (the default)
- **Use** permission on `sc-eim-wtc-nonprod`, the `eim-wtc-nonprod` variable group and the two environments
  (granted by **Permit** on the first run, or under each resource's **Security** / **Pipeline permissions**)

Three older definitions were created from the deleted YAML files `deploy-dashboard-nonprod.yml`,
`deploy-eim-customer-nonprod.yml` and `deploy-d365fsc-gateway-nonprod.yml`. Disable them after the first successful
run of `eim` (**... > Settings > Disabled**). Delete them, with their pipeline variables (publish profiles and the
static web app token) and the old environments `eim-customer-dev`, `eim-customer-bat`, `eim-customer-test`,
`d365fsc-gateway-nonprod`, `dashboard-nonprod-api` and `dashboard-nonprod-frontend`, once the old apps are deleted.

## 9. Production (`eim-prd`)

The checklist for [`pipelines/eim-prd.yml`](../../pipelines/eim-prd.yml). Do it after `eim` runs. Every production
resource is separate from its non-production counterpart; never point a production resource at a non-production
one or the reverse. The steps that follow it are in [production-go-live.md](production-go-live.md).

**Environment.** **Pipelines > Environments > New environment**, type **None**:

| Environment | Used by | Approvals | Exclusive lock |
|---|---|---|---|
| `eim-wtc-prd` | `Deploy_prd` | yes, production approvers | yes |

The `eim-wtc-prd` approval also gates production migrations and database users.

**Service connection** `sc-eim-wtc-prd`, created as in section 2 with these differences:

| Field | Value |
|---|---|
| Scope level | Subscription, then resource group `d365fsc-integration-prod` |
| Service connection name | `sc-eim-wtc-prd` |
| Grant access permission to all pipelines | off; authorise the `eim-prd` pipeline only |

The identity gets **Contributor** on `d365fsc-integration-prod` only, and no role on `d365fsc-integration-nonprod`.
It creates the production Key Vault, so it gets the vault's secret permissions as its creator.

**SQL admin group.** Create a separate Entra group for the production SQL admins, holding the production operators
and the `sc-eim-wtc-prd` principal. Put its display name and object id in `sql.entraAdmin` and
`sql.entraAdminObjectId` of `deploy.prd.json`; `sql` creates `sql-eim-wtc-prd` with it as the Entra admin, and
refuses while either value is a placeholder.

**Variable group** `eim-wtc-prd`:

| Variable | Secret | Value |
|---|---|---|
| `EIM_DEPLOY_CONFIG_OVERRIDES` | no | optional JSON merged over the configuration |

- **Pipeline permissions:** restrict the group to the `eim-prd` pipeline. The `eim` pipeline must not be able to
  read it, and `eim-prd` never uses `eim-wtc-nonprod`.

**Pipeline.**

1. **Pipelines > New pipeline > Azure Repos Git >** `ARM.Integration.Wilson`, **Existing Azure Pipelines YAML
   file**, path `/pipelines/eim-prd.yml`.
2. **Save** (do not run yet). Rename it to `eim-prd`. Its pipeline resource has `source: eim` and `branch: main`, so
   the `eim` pipeline must already carry that name, and the default package is the latest `eim` run from `main`.
3. **Run pipeline** manually with `deployCode` cleared and nothing else ticked. Under **Resources**, pick an `eim`
   run from `main` whose `Deploy_test` stage succeeded. **Permit** the service connection, the variable group and
   the environment. The run is preflight only.

The build service identity needs **Use** on `sc-eim-wtc-prd`, the `eim-wtc-prd` variable group and the production
environment, granted to `eim-prd` only.
