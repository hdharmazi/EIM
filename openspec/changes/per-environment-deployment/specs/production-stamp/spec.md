## RENAMED Requirements

- FROM: `### Requirement: Production is a separate stamp`
- TO: `### Requirement: Production is an isolated environment`

- FROM: `### Requirement: Production go-live enables triggers through cutover`
- TO: `### Requirement: Production go-live enables triggers through configuration`

## MODIFIED Requirements

### Requirement: Production is an isolated environment
The system SHALL deploy production from the single configuration `prd`, with `"production": true` and no `uses`.

Production SHALL use the existing resource group `d365fsc-integration-prod`, named by a `names.resourceGroup` override. Every other production resource name SHALL be the derived pattern name, with no `names` override. `infra` and `sql` for `prd` SHALL create each production resource that is missing, as defined in `deployment-automation` and `sql-identity-access`.

Preflight for any configuration SHALL fail, naming the configurations and the name, changing nothing, when:
- a configuration with `production` true has `uses` naming a configuration with `production` false, or
- any name resolved for a configuration with `production` true equals, ignoring case, any name resolved for a configuration with `production` false. Queue prefixes are not compared.

This check SHALL run over every configuration file in the configuration directory, so a non-production configuration that uses `prd` also fails.

The production App Service plan SHALL use SKU `P0v3`.

#### Scenario: Production names
- **WHEN** the resource names are resolved for `prd`
- **THEN** they are:
  - resource group `d365fsc-integration-prod` (the override)
  - Service Bus namespace `sbns-eim-wtc-prd`
  - Key Vault `kv-eim-wtc-prd`
  - SQL server `sql-eim-wtc-prd`
  - database `sqldb-eim-wtc-prd`
  - plan `asp-eim-wtc-prd`
  - workspace `log-eim-wtc-prd`
  - gateway `azf-eim-gateway-wtc-prd`, with storage `stwtceimgwprd` and Insights `appins-eim-gateway-wtc-prd`
  - dashboard API `app-eim-dashboard-api-wtc-prd`
  - static web app `swa-eim-dashboard-wtc-prd`
  - Automation app `azf-eim-automation-wtc-prd`, with storage `stwtceimautoprd` and Insights `appins-eim-automation-wtc-prd`
  - queues prefixed `prd-`

#### Scenario: No name shared with non-production
- **WHEN** the resolved names of every configuration with `production` true are compared with those of every configuration with `production` false
- **THEN** no name appears in both sets

#### Scenario: Production overrides a non-production name
- **WHEN** `deploy.prd.json` sets `names.sqlServer` to `azql-wtc-sandbox`
- **THEN** preflight fails for every configuration, naming `prd`, `bat` and `azql-wtc-sandbox`, and nothing is changed

#### Scenario: Non-production uses production
- **WHEN** a configuration `deploy.uat.json` without `production` sets `"uses": "prd"`
- **THEN** preflight fails naming `uat`, `prd` and the first shared name, and nothing is changed

#### Scenario: Production plan SKU
- **WHEN** the merged `prd` configuration is read
- **THEN** `appServicePlan.sku` is `P0v3`

### Requirement: Production configuration does not inherit non-production values
`deploy.base.json` SHALL hold no `names` section and no `sharedSuffix`.

`deploy.prd.json` SHALL explicitly reset every value in `deploy.base.json` that identifies non-production. That covers:
- `entra.tenantId`, `entra.clientId` and `entra.audience`
- `automation.excludedUsers`
- `dashboard.simulationEnabled` and `dashboard.simulationQueuePrefix`

A production value that is not yet known SHALL be a `<...>` placeholder, so preflight names it until it is filled in. This includes `sql.entraAdmin` and `sql.entraAdminObjectId`. A test SHALL fail when the merged `prd` configuration carries any of the non-production values above. The only exception is `entra.tenantId`, which MAY equal the base value.

`automation.triggersEnabled` is not in this list: the base value is `false`, which does not identify non-production.

`subscriptionId` is not reset. `d365fsc-integration-prod` is in the same subscription as `d365fsc-integration-nonprod`, so production inherits it from `deploy.base.json`. Isolation is carried by the resource group, the names, the service connection and the SQL administrator group, not by the subscription.

#### Scenario: Existing production resource group
- **WHEN** `prd` is merged and `infra` runs for it
- **THEN** the resource group is `d365fsc-integration-prod`, and no resource group is created because it exists

#### Scenario: Base holds no names
- **WHEN** `deploy.base.json` is read
- **THEN** it has no `names` key and no `sharedSuffix` key

#### Scenario: No legacy verification target key
- **WHEN** `prd` is merged
- **THEN** it has no `gateway.legacyVerificationTargets` key, and the production dashboard API gets no `Gateway__Applications__*` setting

#### Scenario: Non-production client id leaks
- **WHEN** `deploy.prd.json` omits `entra.clientId` and `deploy.base.json` holds a filled-in non-production client id
- **THEN** `DeploymentConfigTests` fails naming `prd` and `entra.clientId`

#### Scenario: Unfilled production value
- **WHEN** `deploy.prd.json` still holds `<production semicolon separated user guids>` and `settings` runs for `prd`
- **THEN** preflight fails naming `automation.excludedUsers`, and nothing is changed

#### Scenario: Unfilled production SQL administrator
- **WHEN** `deploy.prd.json` still holds the `sql.entraAdminObjectId` placeholder, `sql-eim-wtc-prd` does not exist and `sql` runs for `prd`
- **THEN** preflight fails naming `sql.entraAdminObjectId`, and no SQL server is created

### Requirement: Production is deployed by its own pipeline from a tested package
`pipelines/eim-prd.yml` SHALL have no CI or pull-request trigger. It SHALL deploy only the `package` artifact of an `eim` pipeline run, referenced as a pipeline resource, and SHALL NOT rebuild from source.

Its only stage SHALL be `Deploy_prd`. That stage:
- deploys `gateway, backend, frontend, automation`
- passes `ownsResources: true`
- runs migrations when `runMigrations` is selected
- runs `sqlusers` when `grantDatabaseAccess` is selected
- uses the service connection `sc-eim-wtc-prd`, the variable group `eim-wtc-prd` and the DevOps environment `eim-wtc-prd`, which has approvals and an exclusive lock

The pipeline SHALL offer the same `deployInfra`, `applySettings`, `runMigrations`, `grantDatabaseAccess` and `deployCode` parameters as `eim.yml`, with the same defaults. It SHALL use `lockBehavior: runLatest`.

#### Scenario: No automatic production deployment
- **WHEN** a change is merged to `main` or a pull request is opened
- **THEN** `eim-prd` does not run

#### Scenario: Promoted package
- **WHEN** `eim-prd` runs with `eim` run `eim-20261101.1` selected as its resource
- **THEN** `Deploy_prd` deploys the `package` artifact of `eim-20261101.1`, and no stage builds or tests source

#### Scenario: Single production stage
- **WHEN** `pipelines/eim-prd.yml` is inspected
- **THEN** its only stage is `Deploy_prd`, and no file under `pipelines/` names `Deploy_prdshared` or `eim-wtc-prdshared`

#### Scenario: Production uses production credentials
- **WHEN** `pipelines/eim-prd.yml` is inspected
- **THEN** `Deploy_prd` passes `serviceConnection: sc-eim-wtc-prd` and `variableGroup: eim-wtc-prd`, and names neither `sc-eim-wtc-nonprod` nor `eim-wtc-nonprod`

#### Scenario: Non-production pipeline untouched by production
- **WHEN** `pipelines/eim.yml` is inspected
- **THEN** it has no `Deploy_prd` stage and names no production service connection, variable group or DevOps environment

### Requirement: Production go-live enables triggers through configuration
A new production Automation app SHALL start with every gated trigger disabled, because `deploy.prd.json` sets `automation.triggersEnabled` to `false`. `deploy.prd.json` SHALL set `automation.triggersEnabled` explicitly as a JSON boolean, both before and after go-live.

The triggers SHALL be enabled only by:
1. a reviewed change that sets `automation.triggersEnabled` to `true` in `deploy.prd.json`, and then
2. an `eim-prd` run with `applySettings` selected that deploys the package of an `eim` run containing that change.

That run SHALL enable every gated function except those in `automation.keepDisabled`.

Setting the value back to `false` and running `settings` again SHALL disable every gated trigger. No component other than `settings` SHALL change a production trigger state.

#### Scenario: Triggers off after the first deployment
- **WHEN** the first `eim-prd` run with `applySettings` and `deployCode` completes, with `automation.triggersEnabled` `false` in `deploy.prd.json`
- **THEN** every `AzureWebJobs.<function>.Disabled` setting on `azf-eim-automation-wtc-prd` is `true`

#### Scenario: Production switch set explicitly
- **WHEN** `deploy.prd.json` omits `automation.triggersEnabled`
- **THEN** `DeploymentConfigTests` fails naming `prd` and `automation.triggersEnabled`

#### Scenario: Go-live
- **WHEN** `deploy.prd.json` sets `automation.triggersEnabled` to `true` with an empty `automation.keepDisabled`, and an `eim-prd` run with `applySettings` deploys a package that contains it
- **THEN** all six gated functions on `azf-eim-automation-wtc-prd` have `AzureWebJobs.<function>.Disabled=false`

#### Scenario: Code-only run does not go live
- **WHEN** `deploy.prd.json` sets `automation.triggersEnabled` to `true` and an `eim-prd` run deploys code without `applySettings`
- **THEN** no trigger setting changes, and preflight warns that the six `AzureWebJobs.<function>.Disabled` values differ from the configuration, naming them without values

#### Scenario: Production turned off again
- **WHEN** `automation.triggersEnabled` is set back to `false` in `deploy.prd.json` and an `eim-prd` run with `applySettings` deploys it
- **THEN** all six gated functions on `azf-eim-automation-wtc-prd` have `AzureWebJobs.<function>.Disabled=true`

## REMOVED Requirements

### Requirement: Production resources are never removed by the script
**Reason**: The `remove` component no longer exists, so there is nothing to refuse for production.
**Migration**: The requirement "The deployment script deletes no Azure resource" in `deployment-automation` replaces this one for every configuration, production included. Retiring a production resource is a manual, reviewed operation.
