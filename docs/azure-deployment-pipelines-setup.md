# Azure deployment pipelines setup

Three manual Azure DevOps pipelines deploy code without changing Azure runtime settings:

- `.azure-pipelines/workflows/deployment/deploy-dashboard-nonprod.yml`
- `.azure-pipelines/workflows/deployment/deploy-eim-customer-nonprod.yml`
- `.azure-pipelines/workflows/deployment/deploy-d365fsc-gateway-nonprod.yml`

## Architecture

The Dashboard API and frontend are one shared non-production application, and the D365 F&SC gateway is
another. Logical environments are selected through UI/API configuration for the dashboard, and from the
request path for the gateway, so neither needs a copy per environment.

The EIM.Customer Function has one Function App per environment because its Service Bus triggers bind to
environment-specific queues at startup — that is the one component that genuinely cannot be shared.

Databases: `eimdb` is the shared non-production database used by every deployed environment. `eimdb_dev`
sits on the same SQL server but is for local development only — it exists so local work does not disturb
anyone testing against the sandbox. Nothing deployed points at it. Because both live on the same server,
it is easy to connect to the wrong one; check the catalog, not just the server.

## Create the pipelines

For each YAML file:

1. Open **Pipelines** → **New pipeline**.
2. Choose **Azure Repos Git** → `ARM.Integration.Wilson`.
3. Choose **Existing Azure Pipelines YAML file**.
4. Select the YAML path.
5. Save without running until its secrets are configured.

## EIM.Customer pipeline variables

Download each Function App's publish profile from Azure Portal and add these Azure DevOps pipeline variables with **Keep this value secret** enabled:

| Variable | Function App |
|---|---|
| `DEV_FUNCTION_PUBLISH_PROFILE` | `azf-eim-customer-wtc-dev` |
| `BAT_FUNCTION_PUBLISH_PROFILE` | `azf-eim-customer-wtc-bat` |
| `TEST_FUNCTION_PUBLISH_PROFILE` | `azf-eim-customer-wtc-test` |

Also add `MANUAL_VALIDATION_NOTIFY_USERS` as a non-secret variable containing the Azure DevOps user or group to notify at the promotion gates.

The pipeline builds one immutable package, deploys it to `dev`, pauses for manual validation, deploys the same package to `bat`, pauses again, and finally deploys it to `test`. Each validation can wait for up to 24 hours and rejects the promotion on timeout.

Publish-profile ZIP deployment requires **SCM Basic Auth Publishing Credentials** on each target Function App. In Azure Portal, open the Function App → **Configuration** → **General settings**, enable **SCM Basic Auth Publishing Credentials**, save, then download a fresh publish profile. If the setting was previously disabled, an older profile may not authenticate; replace the corresponding secret pipeline variable with the newly downloaded XML.

## Gateway pipeline variables

Add this non-secret variable to the Gateway pipeline:

| Variable | Description |
|---|---|
| `NONPROD_AZURE_SERVICE_CONNECTION` | Name of an Azure Resource Manager service connection authorized for `d365fsc-integration-nonprod` |

The Gateway is shared across `dev`, `bat`, and `test`. A manual run tests and packages the Gateway,
then deploys it to the shared nonprod Function App through Azure CLI. The Gateway runs on Flex
Consumption, so its pipeline does not use a publish profile or the Kudu `/api/zipdeploy` endpoint.

Create the service connection under **Project settings** → **Service connections** → **New service
connection** → **Azure Resource Manager**. Grant it permission to deploy the Gateway Function App,
then authorize it for this pipeline. The service connection identity needs a deployment-capable role
such as **Contributor** on the resource group, or a narrower equivalent on the Function App.

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

- `eim-customer-dev`
- `eim-customer-bat`
- `eim-customer-test`
- `d365fsc-gateway-nonprod`
- `dashboard-nonprod-api`
- `dashboard-nonprod-frontend`

Azure DevOps can create them on first use if the pipeline author has permission. The EIM.Customer pipeline already contains manual validation tasks after `dev` and `bat`; additional environment approvals can be configured in Azure DevOps when required.

1. Open **Pipelines** → **Environments** → the target environment.
2. Open **Approvals and checks**.
3. Add **Approvals** and select the responsible reviewer(s).

Azure DevOps Environment approvals are configured in Azure DevOps, not stored in YAML. The EIM.Customer `ManualValidation` promotion gates are stored in YAML.

## Running deployments

### Function

1. Open the Function deployment pipeline.
2. Select **Run pipeline** and run it without an environment parameter.
3. Validate `dev` when the first manual gate is reached, then approve or reject promotion to `bat`.
4. Validate `bat` at the second gate, then approve or reject promotion to `test`.

Every environment receives the package created by the single Build stage; approval does not rebuild it.

### Gateway

1. Open the Gateway deployment pipeline.
2. Select **Run pipeline**.
3. Review the Gateway test results and shared nonprod deployment stage.

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
- **Gateway Kudu ZIP deploy returns HTTP 404:** the Gateway is a Flex Consumption Function App and must use the Gateway pipeline's Azure CLI deployment step. Pull the current YAML, configure `NONPROD_AZURE_SERVICE_CONNECTION`, and do not use `NONPROD_GATEWAY_PUBLISH_PROFILE` for this pipeline.
- **Gateway AzureCLI task cannot find or use the service connection:** confirm `NONPROD_AZURE_SERVICE_CONNECTION` exactly matches the Azure DevOps service connection name and authorize the connection for the pipeline. Confirm its identity has deployment permission on `d365fsc-integration-nonprod`.
- **Publish-profile download is disabled:** enable SCM Basic Auth Publishing Credentials first or ask an Azure resource administrator to do so.
- **Publish-profile variable is not valid XML:** open the exact target Function App, download its publish profile, open the downloaded file in a text editor, and paste the complete XML contents into the matching secret pipeline variable. Do not paste the filename, a Secure Files path, JSON, surrounding quotes, or the Azure DevOps `$(VARIABLE_NAME)` expression. If the variable comes from a variable group, link and authorize that group for the pipeline.
- **Manual validation does not notify anyone:** verify `MANUAL_VALIDATION_NOTIFY_USERS` contains valid Azure DevOps identities and that they can access the pipeline run.
