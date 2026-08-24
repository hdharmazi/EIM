# Azure deployment pipelines setup

Two manual Azure DevOps pipelines deploy code without changing Azure runtime settings:

- `.azure-pipelines/workflows/deploy-dashboard-nonprod.yml`
- `.azure-pipelines/workflows/deploy-eim-customer-function.yml`

## Architecture

The Dashboard API and frontend are one shared non-production application. Logical environments are selected through UI/API configuration. The EIM.Customer Function has one Function App per environment so each trigger consumes only its own queues.

## Create the pipelines

For each YAML file:

1. Open **Pipelines** → **New pipeline**.
2. Choose **Azure Repos Git** → `ARM.Integration.Wilson`.
3. Choose **Existing Azure Pipelines YAML file**.
4. Select the YAML path.
5. Save without running until its secrets are configured.

## Function pipeline secrets

Download each Function App's publish profile from Azure Portal and add these Azure DevOps pipeline variables with **Keep this value secret** enabled:

| Variable | Function App |
|---|---|
| `DEV1_FUNCTION_PUBLISH_PROFILE` | `azf-eim-customer-wtc-dev1` |
| `TEST_FUNCTION_PUBLISH_PROFILE` | `azf-eim-customer-wtc-test` |
| `BAT_FUNCTION_PUBLISH_PROFILE` | `azf-eim-customer-wtc-bat` |

The pipeline asks for `dev1`, `test`, or `bat` when manually run. It builds one code artifact and deploys it without changing Function App settings.

Publish-profile ZIP deployment requires **SCM Basic Auth Publishing Credentials** on each target Function App. In Azure Portal, open the Function App → **Configuration** → **General settings**, enable **SCM Basic Auth Publishing Credentials**, save, then download a fresh publish profile. If the setting was previously disabled, an older profile may not authenticate; replace the corresponding secret pipeline variable with the newly downloaded XML.

## Dashboard pipeline variables

Add these variables to the Dashboard pipeline:

| Variable | Secret | Description |
|---|---:|---|
| `NONPROD_API_PUBLISH_PROFILE` | Yes | Publish-profile XML for `wtc-dashboard-api-dev` |
| `NONPROD_SWA_DEPLOYMENT_TOKEN` | Yes | Deployment token for `wtc-dashboard-frontend-dev` |
| `VITE_ENTRA_TENANT_ID` | No | Entra tenant used by the SPA |
| `VITE_ENTRA_CLIENT_ID` | No | SPA/client application ID |
| `VITE_API_SCOPE` | No | API delegated scope requested by the SPA |

`VITE_API_BASE_URL` stays empty because the Static Web App uses the linked App Service backend and same-origin `/api` routing.

The pipeline asks whether to deploy `api`, `frontend`, or `both`. With `both`, the API deploys before the frontend.

## Azure DevOps Environments and approvals

The YAML references these Azure DevOps Environments:

- `eim-customer-dev1`
- `eim-customer-test`
- `eim-customer-bat`
- `dashboard-nonprod-api`
- `dashboard-nonprod-frontend`

Azure DevOps can create them on first use if the pipeline author has permission. To require approval for BAT:

1. Open **Pipelines** → **Environments** → `eim-customer-bat`.
2. Open **Approvals and checks**.
3. Add **Approvals** and select the responsible reviewer(s).

Approvals are configured in Azure DevOps, not stored in YAML.

## Running deployments

### Function

1. Open the Function deployment pipeline.
2. Select **Run pipeline**.
3. Choose `dev1`, `test`, or `bat`.
4. Run and approve the target environment if required.

### Dashboard

1. Open the Dashboard deployment pipeline.
2. Select **Run pipeline**.
3. Choose `api`, `frontend`, or `both`.
4. Run and review the deployment stages.

## Security and rotation

Publish profiles and SWA tokens are long-lived credentials:

- Keep them only in secret pipeline variables or a restricted variable group.
- Restrict who can edit and run deployment pipelines.
- Never echo, download as artifacts, or commit the values.
- Rotate immediately if exposed.
- Prefer workload-identity service connections when organization permissions become available.

Runtime settings such as SQL, Service Bus, queue names, schedules, and API keys remain configured in Azure and are not changed by these pipelines.

## Rollback

Pipeline artifacts are retained according to Azure DevOps retention policy. To roll back, run the pipeline from the previous known-good commit or redeploy its retained artifact. The deployment does not modify runtime settings.

## Troubleshooting

- **Kudu ZIP deploy returns HTTP 401:** SCM Basic Auth Publishing Credentials are disabled or the stored publish profile is stale. Enable SCM basic authentication on the exact target app, save, download a new publish profile, replace the environment-specific secret variable, and rerun.
- **Publish-profile download is disabled:** enable SCM Basic Auth Publishing Credentials first or ask an Azure resource administrator to do so.
