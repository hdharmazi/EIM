## Why

`Script/Deploy-Manual/` contains ten scripts implementing **two complete, parallel deployment architectures** into the same resource group, with no indication which is authoritative:

- **App Service** — `wtc-dashboard-api-dev` web app, `wtc-dashboard-frontend-dev` Static Web App with a linked backend, and Function Apps deployed by zip.
- **Container Apps** — `dashboard-api`, `dashboard-frontend`, and `eim-customer` container apps built through ACR.

They diverge on more than hosting. The App Service scripts carry Entra tenant `446b6761…` and client `5d1e26b3…`; the Container Apps scripts carry `5e8a5341…` and `317063cf…`. `AzureDeploy.AppService.UpdateAzure.ps1` exists solely to push the newer values, which indicates a re-registration that only reached one path. A third variation exists in the API scope, which appears as both `access_as_user` and `user_impersonation`.

Beyond the duplication:

- The same SQL password and Service Bus root key are hardcoded in **seven** places across four scripts, all committed.
- `AzureDeploy.AppService.UpdateAzure copy.ps1` is byte-identical to its sibling.
- Four environment prefixes (`dev`, `dev1`, `bat`, `test`) are hardcoded across separate scripts with no shared naming source, so adding an environment means copying a file.
- Nothing applies database schema, and nothing provisions the SQL server or the Service Bus namespace.
- There is no production path at all.
- Only `AzureDeploy.Function.EIM.Customer.dev1.ps1` is genuinely idempotent; the rest assume a clean or known state.

Deployment is therefore an act of tribal knowledge: correct execution depends on knowing which scripts are live, in what order, with which values edited first.

**Production is deliberately out of scope.** It will use a different resource group and Service Bus namespace, so it is a separate exercise once the non-production path is proven.

## What Changes

- **App Service becomes the single supported architecture.** **BREAKING** — the Container Apps deployment path is removed, along with the container artifacts that exist only to support it: `docker-compose.yml`, the three Dockerfiles, and `nginx.conf.template`. Local development runs through `run.bat` and the Vite dev server, and does not use containers.
- **One entry point, `Deploy-Eim.ps1`**, parameterised by `-Environment` and `-Component`, replacing the ten scripts. Supported environments are `dev1`, `bat`, and `test`.
- **All resource names derive from `-Environment`** through a single naming function, so adding an environment is a parameter rather than a new file.
- **Secrets leave the scripts.** Per-environment values live in a gitignored configuration file, with a committed example. No credential appears in any committed script.
- **Every step is idempotent and existence-checked**, following the pattern already established in `AzureDeploy.Function.EIM.Customer.dev1.ps1`.
- **Schema migration becomes a deployment step**, invoking the runner from `add-schema-migration-runner` before application components deploy.
- **Service Bus queue provisioning is absorbed** as a component, reusing the existing parameterised queue script.
- **The dashboard is treated as shared, not per-environment.** One API and one frontend serve every non-production environment, which is how they are already deployed.
- **Entra configuration is consolidated.** Deployed environments use registration `446b6761` / `5d1e26b3`; the registration in the committed `appsettings.json` is confirmed as the local-development one and is retained and documented rather than changed.
- **Stale scripts are deleted**: the duplicate `copy` file, the Container Apps scripts, and the one-off `AzureDeploy.AppService.API.config.ps1`.

## Capabilities

### New Capabilities

- `deployment-automation`: A single parameterised, idempotent deployment entry point covering Service Bus queues, database schema, API, Functions, and frontend across non-production environments, with no credentials in source.

### Modified Capabilities

_None. No existing capability specifies deployment._

## Impact

**New**
- `Script/Deploy-Eim.ps1` — the single entry point.
- `Script/config/deploy.example.json` — committed template.
- `Script/config/deploy.<environment>.json` — gitignored per-environment values.

**Removed**
- `AzureDeploy` (Container Apps), `AzureDeploy.Update.ps1` (Container Apps update).
- `AzureDeploy.AppService.UpdateAzure copy.ps1` (byte-identical duplicate).
- `AzureDeploy.AppService.API.config.ps1` (one-off with mixed `bat-`/`dev-` queue names).
- `AzureDeploy.AppService.ps1`, `AzureDeploy.API.ps1`, `AzureDeploy.Frontend.ps1`, `AzureDeploy.AppService.UpdateAzure.ps1`, `AzureDeploy.Function.EIM.Customer.dev1.ps1` — superseded once their behaviour is absorbed.
- `docker-compose.yml`, `ARM.Integration.Dashboard.API/Dockerfile`, `ARM.Integration.Dashboard.Frontend/Dockerfile`, `ARM.Integration.EIM.Customer/Dockerfile`, and `nginx.conf.template` — container artifacts supporting only the removed path.

**Retained**
- `Deploy-ServiceBusQueue-AzCli.ps1` — already parameterised and idempotent; invoked as a component.
- `Deploy-ZipWithPublishProfile.ps1` — generic utility.
- `run.bat` and `run-dashboard.bat` — the local development workflow, unaffected.

**Configuration**
- `ARM.Integration.Dashboard.API/appsettings.json` retains Entra tenant `5e8a5341` and client `317063cf`. This is confirmed as the local-development registration and is correct; it is documented rather than changed, so it is not mistaken for drift later.
- `.gitignore` gains `Script/config/deploy.*.json` with the example excluded.

**Known naming debt, deliberately deferred**
- The shared dashboard resources are suffixed `-dev` but serve every non-production environment. `-nonprod` would be accurate. Renaming is deferred to avoid recreating live resources as part of this change.

**Dependency**
- The schema component requires `add-schema-migration-runner`. Until that lands, the schema step is a no-op placeholder.

**Documentation**
- `Script/Docs` gains deployment instructions, the configuration file format, and the procedure for onboarding a new environment.
