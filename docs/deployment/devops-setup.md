# Azure DevOps setup (one time)

The checklist for running [`pipelines/eim.yml`](../../pipelines/eim.yml). Do it once, before the first run.
How the pipeline and the script work is in [deployment.md](deployment.md).

Nothing in this list puts a secret in the repository. The two connection strings live only in the variable
group, marked secret.

## 1. Environments

**Pipelines > Environments > New environment**, type **None**. Then, on each one, **Approvals and checks**:

| Environment | Used by | Approvals | Exclusive lock |
|---|---|---|---|
| `eim-wtc-nonprod` | `Deploy_shared` | yes | yes |
| `eim-wtc-bat` | `Deploy_bat` | yes | yes |
| `eim-wtc-test` | `Deploy_test` | yes | yes |

There is no development environment or stage: development runs locally. If an environment for a development
stage was created ahead of the pipeline, delete it.

- **Approvals:** add the responsible reviewers. Approvals live in Azure DevOps, not in the YAML, so there is no
  `ManualValidation` step.
- **Exclusive lock:** stops two runs deploying to the same environment at once. With
  `lockBehavior: runLatest` in the YAML, a newer run waiting at an environment replaces an older one that was
  never approved.
- `eim-wtc-nonprod` needs an approval because the shared stage changes the gateway (used by external consumers),
  the dashboard used by every environment and, on manual runs, the one `eimdb` every ENVIRONMENT value reads. It
  sits in front of bat, so every merge waits for two approvals, shared then bat, before any Automation code is
  deployed. That was accepted: nothing deployed is less gated than bat.

## 2. Service connection

**Project settings > Service connections > New service connection > Azure Resource Manager**:

| Field | Value |
|---|---|
| Identity type | App registration (automatic), **Workload identity federation** |
| Scope level | Subscription, then resource group `d365fsc-integration-nonprod` |
| Service connection name | `sc-eim-wtc-nonprod` |
| Grant access permission to all pipelines | off; authorize the pipeline on its first run instead |

The identity gets **Contributor** on `d365fsc-integration-nonprod`. That is enough for everything the pipeline
does: plan, apps, storage, Insights, queues, app settings, the static web app deployment token, and the SQL
firewall rules on `azql-wtc-sandbox`, which is in the same resource group. No other role assignment is needed.

If the automatic option is not available to you, create the connection with **App registration or managed
identity (manual)**, federated, and ask a subscription owner to assign Contributor on the resource group.

There is no publish profile, SCM basic authentication or static web app token to store. The script fetches what
it needs through the service connection at run time.

## 3. Variable group

**Pipelines > Library > + Variable group**, named `eim-wtc-nonprod`:

| Variable | Secret | Value |
|---|---|---|
| `EIM_DEPLOY_EIMDB` | **yes** | the `eimdb` SQL connection string (`Initial Catalog=eimdb`, never `eimdb_dev`) |
| `EIM_DEPLOY_SERVICEBUS` | **yes** | the `asb-eim-wtc-nonprod` connection string |
| `EIM_DEPLOY_CONFIG_OVERRIDES` | no | optional JSON merged over the configuration, e.g. `{"subscriptionId":"<subscription id>"}` |

- Tick the padlock on both connection strings. The YAML maps them into each step through `env:`; secret
  variables are not visible to scripts otherwise.
- `EIM_DEPLOY_CONFIG_OVERRIDES` may be left out. An undefined variable reaches the script as the literal text
  `$(EIM_DEPLOY_CONFIG_OVERRIDES)`, which the script ignores. An undefined connection string arrives the same way
  and counts as missing, so preflight names it.
- One group serves every stage, because every configuration uses the same database and namespace.
- **Pipeline permissions:** restrict the group to the `eim` pipeline.
- `EIM_DEPLOY_CONFIG_OVERRIDES` is read by the deploy stages only. The SPA is built in the `Build` stage from the
  committed configuration, so the Entra values must be committed in `deploy.base.json`; they are not secret.

## 4. Create the pipeline

1. **Pipelines > New pipeline > Azure Repos Git >** `ARM.Integration.Wilson`.
2. **Existing Azure Pipelines YAML file**, path `/pipelines/eim.yml`.
3. **Save** (do not run yet). Rename it to `eim`.
4. **Run pipeline** manually the first time, with `deployCode` cleared and nothing else ticked. That run is
   preflight only: every stage runs preflight and skips verify and health, so it passes before the new apps exist.
   On the first run Azure DevOps asks you to **Permit** the pipeline to use the service connection, the variable
   group and each environment. Permit each one.

## 5. Branch policy

On `main`, the existing build validation runs on pull requests. The `eim` pipeline's `pr` trigger runs the
`Build` stage only (tests, the script self-test and the package checks) and never a deploy stage. Add it as a
build validation policy on `main` if you want those checks to block merges.

## 6. The auto-AI pull request pipeline

The YAML moved from the old `.azure-pipelines` folder to `pipelines/auto-ai-pull-request.yml`. Edit the existing
pipeline definition: **Edit > ... > Settings > YAML file path** = `/pipelines/auto-ai-pull-request.yml`. Its
setup is otherwise unchanged; see [../auto-ai-pull-request-setup.md](../auto-ai-pull-request-setup.md).

## 7. Build service permissions

The pipeline only reads the repository and publishes an artifact; it does not push tags or commits. The project's
build service identity needs:

- **Read** on the `ARM.Integration.Wilson` repository (the default)
- **Use** permission on `sc-eim-wtc-nonprod`, the `eim-wtc-nonprod` variable group and the three environments
  (granted by **Permit** on the first run, or under each resource's **Security** / **Pipeline permissions**)

## 8. The old pipeline definitions

Three definitions were created from the deleted YAML files `deploy-dashboard-nonprod.yml`,
`deploy-eim-customer-nonprod.yml` and `deploy-d365fsc-gateway-nonprod.yml`.

1. Before the #5246 deployment change is merged, tag the last commit on `main` before it as `pre-5246-deploy`.
   The old definitions can still run from that tag during the overlap if needed.
2. After the first successful run of `eim`, **disable** the three definitions (**... > Settings > Disabled**).
3. After decommission ([resource-rename-cutover.md](resource-rename-cutover.md), step 14), delete them, together
   with their pipeline variables (publish profiles and the static web app token), and the old environments
   `eim-customer-dev`, `eim-customer-bat`, `eim-customer-test`, `d365fsc-gateway-nonprod`,
   `dashboard-nonprod-api` and `dashboard-nonprod-frontend`.
