# Non-production Dashboard and Azure Function CI/CD Plan

**Status:** Validated

## Objective

Add two Azure DevOps pipelines:

1. Deploy the shared non-production Dashboard API and frontend.
2. Select an environment and deploy `ARM.Integration.EIM.Customer` to its dedicated Function App.

## Current understanding

- Application: `ARM.Integration.EIM.Customer`
- Runtime: .NET 10 isolated worker, Azure Functions v4, Linux
- Selectable targets: DEV1 (`azf-eim-customer-wtc-dev1`), TEST (`azf-eim-customer-wtc-test`), and BAT (`azf-eim-customer-wtc-bat`)
- Existing deployment style: Release publish followed by ZIP deployment
- Trigger: manual only
- Deployment scope: code only; Function App settings remain managed in Azure
- Authentication: one environment-specific Function publish profile stored as an Azure DevOps secret
- Region used by repository scripts: `centralus`
- Shared dashboard API: `wtc-dashboard-api-dev` on Linux App Service
- Shared dashboard frontend: `wtc-dashboard-frontend-dev` on Azure Static Web Apps
- Dashboard environment routing is selected through application configuration in the UI/API, not separate dashboard deployments.

## Decisions

- Both pipelines are manual-only.
- Dashboard supports component selection: API, frontend, or both.
- Function deployment supports `dev1`, `test`, and `bat`.
- Pipelines deploy code only and do not overwrite Azure runtime settings.
- Publish profiles/tokens are temporary deployment credentials until workload identity is available.

## Function pipeline

1. Start manually and require an `environment` parameter: `dev1`, `test`, or `bat`.
2. Restore, build, and test the .NET solution.
3. Publish `ARM.Integration.EIM.Customer` in Release mode.
4. Publish an immutable ZIP artifact.
5. Map the selected environment to its Function App and secret publish-profile variable.
6. Deploy the ZIP through the Function App's Kudu ZIP-deploy endpoint using the selected publish profile.
7. Use distinct Azure DevOps Environments (`eim-customer-dev1`, `eim-customer-test`, `eim-customer-bat`) so approvals can be configured per target.
8. Verify deployment completion and report the selected app and build.

## Dashboard pipeline

1. Start manually and select `api`, `frontend`, or `both`.
2. Build the API with .NET 10 and build the frontend with Node.js 22.
3. Supply the Entra/API configuration to Vite at build time through pipeline variables.
4. Publish immutable API ZIP and frontend `dist` artifacts.
5. Deploy the API first when `both` is selected.
6. Deploy the frontend to the shared Static Web App after the API.
7. Do not alter API runtime settings or logical environment configuration.

## Security

- Store `DEV1_FUNCTION_PUBLISH_PROFILE`, `TEST_FUNCTION_PUBLISH_PROFILE`, and `BAT_FUNCTION_PUBLISH_PROFILE` as secret pipeline variables or in a restricted variable group.
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
	- [x] Both Azure DevOps YAML files have no editor diagnostics.
	- [x] Target names and required secret-variable names match the setup guide.
	- [x] No runtime application settings are changed by either pipeline.

## Rollback

Retain pipeline artifacts so a previously successful ZIP can be redeployed. Do not mutate runtime app settings during code-only deployment. Rollback by manually running the pipeline against a prior artifact/commit.

## Validation Proof

- `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj --configuration Release`
	- Passed: 12 tests, 0 failures.
- `dotnet publish ARM.Integration.EIM.Customer/ARM.Integration.EIM.Customer.csproj --configuration Release --output artifacts/pipeline-validation/function`
	- Passed; `host.json` exists at publish root.
- `dotnet publish ARM.Integration.Dashboard.API/ARM.Integration.Dashboard.API.csproj --configuration Release --output artifacts/pipeline-validation/api`
	- Passed; `ARM.Integration.Dashboard.API.dll` exists at publish root.
- `npm --prefix ARM.Integration.Dashboard.Frontend ci` and `npm --prefix ARM.Integration.Dashboard.Frontend run build`
	- Passed after stopping the locally running Vite process that held `esbuild.exe`.
	- npm reported 4 dependency vulnerabilities (2 moderate, 2 high); follow-up dependency remediation is recommended.
	- Vite reported an existing bundle-size warning over 500 kB.
- PowerShell parser validation for `Script/Deploy-ZipWithPublishProfile.ps1`
	- Passed.
- Editor diagnostics for both deployment YAML files
	- No errors.
- `git diff --check`
	- Passed.
- Static IaC role verification
	- Not applicable: these are code-only pipelines using publish profiles/SWA token and do not provision or modify RBAC.
