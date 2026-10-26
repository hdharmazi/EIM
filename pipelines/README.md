# Azure DevOps pipelines

| Pipeline | Trigger | Purpose |
|---|---|---|
| `eim.yml` | `main` (code, SQL, deployment script and pipeline paths); pull requests run the Build stage only | Build and test once, package, then deploy the shared nonprod resources and promote the Automation host through `bat` and `test` |
| `eim-prd.yml` | Manual only | Promote the package of an `eim` run to the production stamp: `prdshared`, then `prd` |
| `auto-ai-pull-request.yml` | `feature/*` push | Create a draft pull request with Microsoft Foundry-generated metadata |

## `eim.yml`

Stages: `Build` → `Deploy_shared` (DevOps environment `eim-wtc-nonprod`) → `Deploy_bat` (`eim-wtc-bat`) → `Deploy_test` (`eim-wtc-test`). Each deploy stage runs only when the previous stage succeeded, and never for a pull request. There is no development stage: development runs locally (see "Local development" in the repository [README](../README.md)).

| Template | Purpose |
|---|---|
| `templates/build-package.yml` | `dotnet test`, frontend `npm ci` and lint, the deployment script self-test, `New-DeploymentPackage.ps1`, the package content checks, then publish of artifact `package` |
| `templates/deploy-environment.yml` | Runs `Deploy-Wilson.ps1` from the package: preflight and `-WhatIf` preview, then infrastructure, settings and migrations when ticked, code, and finally `verify` and `health`. `packageSource` picks the package: `current` (this run's artifact, the default) or a pipeline resource alias (`eim` in `eim-prd.yml`) |

The Build stage packages every configuration: `nonprod,bat,test,prdshared,prd`. `eim.yml` deploys only the non-production ones; the production configurations travel in the same package so that `eim-prd.yml` can promote it.

A merge deploys code only. Infrastructure (`deployInfra`), app settings (`applySettings`) and migrations (`runMigrations`, shared stage only) run only when ticked on a manual run.

One-time setup (environments, the `sc-eim-wtc-nonprod` service connection and the `eim-wtc-nonprod` variable group): [DevOps setup](../docs/deployment/devops-setup.md). How the deployment works: [Deployment](../docs/deployment/deployment.md).

## `eim-prd.yml`

Stages: `Deploy_prdshared` (DevOps environment `eim-wtc-prdshared`) → `Deploy_prd` (`eim-wtc-prd`). `Deploy_prd` runs only when `Deploy_prdshared` succeeded. There is no Build stage and no `dotnet` or `npm` step.

- **Package promotion.** The pipeline resource `eim` (source: the `eim` pipeline, `branch: main`, `trigger: none`) supplies the package. `branch: main` makes the latest `eim` run from `main` the default at queue time; pull request and feature runs publish a package too, and are never the default. On a manual run, check the `eim` run under **Resources**: a run from `main` whose `Deploy_test` stage succeeded, so production gets exactly the binaries bat and test ran. Another run can still be picked there by hand, so the approver checks it. Preflight prints the package version from its manifest, so the approver sees it.
- **Trigger.** Manual only (`trigger: none`, `pr: none`). The same four parameters as `eim.yml`: `deployInfra`, `applySettings`, `runMigrations` (false) and `deployCode` (true). Migrations run only in `Deploy_prdshared`.
- **Production DevOps resources.** Both stages pass them explicitly; the template defaults stay non-production.
  - service connection `sc-eim-wtc-prd`: workload identity, Contributor on `d365fsc-integration-prod` only, authorised for `eim-prd` only
  - variable group `eim-wtc-prd`: `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` (both secret, pointing at `sql-eim-wtc-prd`/`sqldb-eim-wtc-prd` and `sbns-eim-wtc-prd`) and optional `EIM_DEPLOY_CONFIG_OVERRIDES`, restricted to `eim-prd`
  - environments `eim-wtc-prdshared` and `eim-wtc-prd`: production approvers and an exclusive lock

Setup and go-live: [DevOps setup](../docs/deployment/devops-setup.md) and [Production go-live](../docs/deployment/production-go-live.md).
