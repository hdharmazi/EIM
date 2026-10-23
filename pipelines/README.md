# Azure DevOps pipelines

| Pipeline | Trigger | Purpose |
|---|---|---|
| `eim.yml` | `main` (code, SQL, deployment script and pipeline paths); pull requests run the Build stage only | Build and test once, package, then deploy the shared nonprod resources and promote the Automation host through `bat` and `test` |
| `auto-ai-pull-request.yml` | `feature/*` push | Create a draft pull request with Microsoft Foundry-generated metadata |

## `eim.yml`

Stages: `Build` → `Deploy_shared` (DevOps environment `eim-wtc-nonprod`) → `Deploy_bat` (`eim-wtc-bat`) → `Deploy_test` (`eim-wtc-test`). Each deploy stage runs only when the previous stage succeeded, and never for a pull request. There is no development stage: development runs locally (see "Local development" in the repository [README](../README.md)).

| Template | Purpose |
|---|---|
| `templates/build-package.yml` | `dotnet test`, frontend `npm ci` and lint, the deployment script self-test, `New-DeploymentPackage.ps1`, the package content checks, then publish of artifact `package` |
| `templates/deploy-environment.yml` | Runs `Deploy-Wilson.ps1` from the package: preflight and `-WhatIf` preview, then infrastructure, settings and migrations when ticked, code, and finally `verify` and `health` |

A merge deploys code only. Infrastructure (`deployInfra`), app settings (`applySettings`) and migrations (`runMigrations`, shared stage only) run only when ticked on a manual run.

One-time setup (environments, the `sc-eim-wtc-nonprod` service connection and the `eim-wtc-nonprod` variable group): [DevOps setup](../docs/deployment/devops-setup.md). How the deployment works: [Deployment](../docs/deployment/deployment.md).
