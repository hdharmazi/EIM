# Azure DevOps pipelines

| Pipeline | Trigger | Purpose |
|---|---|---|
| `eim.yml` | `main` (code, SQL, deployment script and pipeline paths); pull requests run the Build stage only | Build and test once, package, then deploy `bat` (which owns the non-production resources) and `test` (which uses bat's) |
| `eim-prd.yml` | Manual only | Promote the package of an `eim` run to the isolated production configuration `prd` |
| `auto-ai-pull-request.yml` | `feature/*` push | Create a draft pull request with Microsoft Foundry-generated metadata |

## `eim.yml`

Stages: `Build` → `Deploy_bat` (DevOps environment `eim-wtc-bat`) → `Deploy_test` (`eim-wtc-test`). Each deploy stage runs only when the previous stage succeeded, and never for a pull request. There is no development stage: development runs locally (see "Local development" in the repository [README](../README.md)).

- `Deploy_bat` (`ownsResources: true`) deploys the gateway, the dashboard API and SPA, and bat's Automation app. With `deployInfra` it also runs `sql` for the SQL server and `eimdb`; with `runMigrations` it applies the migrations.
- `Deploy_test` (`ownsResources: false`) deploys only test's Automation app, on bat's plan, Service Bus namespace, Key Vault and database. It never runs migrations: the template refuses `runMigrations` for a stage that does not own its resources.

| Template | Purpose |
|---|---|
| `templates/build-package.yml` | `dotnet test`, frontend `npm ci` and lint, the deployment script self-test, `New-DeploymentPackage.ps1`, the package content checks, then publish of artifact `package` |
| `templates/deploy-environment.yml` | Runs `Deploy-Wilson.ps1` from the package: preflight and `-WhatIf` preview, then infrastructure (`infra`, plus `sql` for an owner), settings and the database step when ticked, code, and finally `verify` and `health`. `packageSource` picks the package: `current` (this run's artifact, the default) or a pipeline resource alias (`eim` in `eim-prd.yml`) |

The database step runs when `runMigrations` or `grantDatabaseAccess` is ticked. It opens the SQL firewall for the agent (`myip`), caches the SQL token while the federated sign-in is fresh, runs `schema` and/or `sqlusers`, and closes the firewall rule again (`myipremove`) in a `finally`.

The Build stage packages `bat,test,prd`. `eim.yml` deploys only `bat` and `test`; `prd` travels in the same package so that `eim-prd.yml` can promote it.

A merge deploys code only. Infrastructure (`deployInfra`), app settings (`applySettings`), migrations (`runMigrations`, owner stages only) and database users (`grantDatabaseAccess`) run only when ticked on a manual run.

There are no deployment secrets. The apps reach SQL with their managed identities and Service Bus through the Key Vault secret `eim-servicebus`. Every `AzureCLI@2` step maps only the optional `EIM_DEPLOY_CONFIG_OVERRIDES`. The service connection's principal must be a member of the SQL server's Entra admin group for the database step.

One-time setup (environments, the `sc-eim-wtc-nonprod` service connection and the `eim-wtc-nonprod` variable group): [DevOps setup](../docs/deployment/devops-setup.md). How the deployment works: [Deployment](../docs/deployment/deployment.md).

## `eim-prd.yml`

One stage: `Deploy_prd` (DevOps environment `eim-wtc-prd`, `ownsResources: true`), deploying the gateway, the dashboard API and SPA, and the Automation app. There is no Build stage and no `dotnet` or `npm` step.

- **Package promotion.** The pipeline resource `eim` (source: the `eim` pipeline, `branch: main`, `trigger: none`) supplies the package. `branch: main` makes the latest `eim` run from `main` the default at queue time; pull request and feature runs publish a package too, and are never the default. On a manual run, check the `eim` run under **Resources**: a run from `main` whose `Deploy_test` stage succeeded, so production gets exactly the binaries bat and test ran. Another run can still be picked there by hand, so the approver checks it. Preflight prints the package version from its manifest, so the approver sees it.
- **Trigger.** Manual only (`trigger: none`, `pr: none`). The same five parameters and defaults as `eim.yml`: `deployInfra`, `applySettings`, `runMigrations` and `grantDatabaseAccess` (false), and `deployCode` (true).
- **Production DevOps resources.** The stage passes them explicitly; the template defaults stay non-production.
  - service connection `sc-eim-wtc-prd`: workload identity, Contributor on `d365fsc-integration-prod` only, authorised for `eim-prd` only, and a member of the production SQL server's Entra admin group
  - variable group `eim-wtc-prd`: only the optional `EIM_DEPLOY_CONFIG_OVERRIDES`, restricted to `eim-prd`
  - environment `eim-wtc-prd`: production approvers and an exclusive lock

Setup and go-live: [DevOps setup](../docs/deployment/devops-setup.md) and [Production go-live](../docs/deployment/production-go-live.md).
