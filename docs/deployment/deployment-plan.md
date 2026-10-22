# Non-production Dashboard and Azure Function CI/CD Plan

**Status:** Validated

## Objective

Add three Azure DevOps pipelines:

1. Deploy the shared non-production Dashboard API and frontend.
2. Build `Wilson.Integration.Automation` once and promote the package through `dev`, `bat`, and `test`.
3. Test and deploy the shared non-production D365 F&SC Gateway.

## Current understanding

- Application: `Wilson.Integration.Automation`
- Runtime: .NET 10 isolated worker, Azure Functions v4, Linux
- Promotion targets: `dev` (`azf-eim-customer-wtc-dev`), `bat` (`azf-eim-customer-wtc-bat`), and `test` (`azf-eim-customer-wtc-test`)
- Existing deployment style: Release publish followed by ZIP deployment
- Trigger: manual only
- Deployment scope: code only; Function App settings remain managed in Azure
- Authentication: one environment-specific Function publish profile stored as an Azure DevOps secret
- Region used by repository scripts: `centralus`
- Shared dashboard API: `wtc-dashboard-api-dev` on Linux App Service
- Shared dashboard frontend: `wtc-dashboard-frontend-dev` on Azure Static Web Apps
- Shared D365 F&SC Gateway: `azf-d365fsc-gateway-wtc-nonprod` on Azure Functions
- Dashboard environment routing is selected through application configuration in the UI/API, not separate dashboard deployments.

## Decisions

- All three pipelines are manual-only.
- Dashboard supports component selection: API, frontend, or both.
- Wilson.Integration.Automation promotes one package in order through `dev`, `bat`, and `test`.
- Manual validation gates after `dev` and `bat` reject after 24 hours if no decision is made.
- Gateway always deploys to the shared nonprod Function App after successful tests.
- Pipelines deploy code only and do not overwrite Azure runtime settings.
- Publish profiles/tokens are temporary deployment credentials until workload identity is available.

## Function pipeline

1. Start manually with no environment parameter.
2. Restore, build, and test the .NET solution.
3. Publish `Wilson.Integration.Automation` in Release mode.
4. Publish an immutable ZIP artifact.
5. Deploy the ZIP to `dev`, then pause for manual validation.
6. After approval, deploy the same ZIP to `bat`, then pause for manual validation.
7. After approval, deploy the same ZIP to `test` without rebuilding.
8. Use distinct Azure DevOps Environments (`eim-customer-dev`, `eim-customer-bat`, `eim-customer-test`) for deployment history and optional checks.
9. Deploy each ZIP through the Function App's Kudu ZIP-deploy endpoint using its publish profile.

## Gateway pipeline

1. Start manually with no component or environment parameter.
2. Restore the Gateway project and run `ARM.Integration.D365FSC.Gateway.Tests`.
3. Publish `ARM.Integration.D365FSC.Gateway` in Release mode.
4. Publish an immutable ZIP artifact with `host.json` at the ZIP root.
5. Deploy the artifact to `azf-d365fsc-gateway-wtc-nonprod` using Azure DevOps Environment `d365fsc-gateway-nonprod`.
6. Change code only; retain Gateway settings, keys, database configuration, and infrastructure in Azure.

## Dashboard pipeline

1. Start manually and select `api`, `frontend`, or `both`.
2. Build the API with .NET 10 and build the frontend with Node.js 22.
3. Supply the Entra/API configuration to Vite at build time through pipeline variables.
4. Publish immutable API ZIP and frontend `dist` artifacts.
5. Deploy the API first when `both` is selected.
6. Deploy the frontend to the shared Static Web App after the API.
7. Do not alter API runtime settings or logical environment configuration.

## Security

- Store `DEV_FUNCTION_PUBLISH_PROFILE`, `BAT_FUNCTION_PUBLISH_PROFILE`, and `TEST_FUNCTION_PUBLISH_PROFILE` as secret pipeline variables or in a restricted variable group.
- Configure `NONPROD_AZURE_SERVICE_CONNECTION` with the name of an authorized Azure Resource Manager service connection for Gateway deployment.
- Store the validation identities in non-secret `MANUAL_VALIDATION_NOTIFY_USERS`.
- Store `NONPROD_API_PUBLISH_PROFILE` and `NONPROD_SWA_DEPLOYMENT_TOKEN` as secret pipeline variables.
- Do not store publish profiles, connection strings, or application secrets in source control or artifacts.
- Keep runtime app settings in Azure configuration/Key Vault rather than the code deployment package.
- Rotate a publish profile immediately if it is exposed, and migrate to workload identity when permissions become available.

## Validation

- [x] All validation checks pass
	- [x] PowerShell deployment helper parses without syntax errors.
	- [x] Function project restores and publishes with .NET 10.
	- [x] EIM tests pass.
	- [x] Dashboard API restores and publishes with .NET 10.
	- [x] Dashboard frontend installs dependencies and builds with Node.js 22.
	- [x] Function package contains `host.json` at ZIP root.
	- [x] Dashboard API ZIP contains the application DLL at ZIP root.
	- [x] All three Azure DevOps deployment YAML files have no editor diagnostics.
	- [x] Target names and required secret-variable names match the setup guide.
	- [x] One Wilson.Integration.Automation artifact is referenced by all three deployment stages.
	- [x] Gateway tests run before the Gateway artifact is published and deployed.
	- [x] No runtime application settings are changed by any pipeline.

## Rollback

Retain pipeline artifacts so a previously successful ZIP can be redeployed. Do not mutate runtime app settings during code-only deployment. Rollback by redeploying the retained package or manually running the pipeline from a prior commit.

## Validation Proof

- EIM and Gateway test projects through the VS Code test adapter
	- Passed: 61 tests, 0 failures.
- Release publish and ZIP validation for `Wilson.Integration.Automation` and `ARM.Integration.D365FSC.Gateway`
	- Passed; both archives contain `host.json` at ZIP root.
- `dotnet build ARM.Integration.Wilson.slnx --configuration Release`
	- Passed with existing nullable/package warnings.
	- Gateway tests report five known high-severity advisories for `System.Security.Cryptography.Xml` 10.0.7.
- `dotnet publish ARM.Integration.Dashboard.API/ARM.Integration.Dashboard.API.csproj --configuration Release --output artifacts/pipeline-validation/api`
	- Passed; `ARM.Integration.Dashboard.API.dll` exists at publish root.
- `npm --prefix ARM.Integration.Dashboard.Frontend ci` and `npm --prefix ARM.Integration.Dashboard.Frontend run build`
	- Passed after stopping the locally running Vite process that held `esbuild.exe`.
	- npm reported 4 dependency vulnerabilities (2 moderate, 2 high); follow-up dependency remediation is recommended.
	- Vite reported an existing bundle-size warning over 500 kB.
- PowerShell parser validation for `script/deployment/Deploy-Eim.ps1` and `script/deployment/Deploy-ZipWithPublishProfile.ps1`
	- Passed; deployment config ignore rules also passed.
- Editor diagnostics for all three deployment YAML files
	- No errors.
- Relative Markdown link validation
	- Passed for the root README, indexes, pipeline setup, and deployment guides.
- `git diff --check`
	- Passed.
- Static IaC role verification
	- Not applicable: these are code-only pipelines using publish profiles/SWA token and do not provision or modify RBAC.
