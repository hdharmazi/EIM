## ADDED Requirements

### Requirement: Production is a separate stamp
The system SHALL deploy production from two configurations: `prdshared` (scope `shared`) and `prd` (scope `environment`). Both SHALL have `sharedSuffix` `prd` and `"production": true`, and neither SHALL have a `legacy` section. Production SHALL use the existing resource group `d365fsc-integration-prod`, named by a `names.resourceGroup` override; the script SHALL NOT create it. Every other production resource name SHALL be the derived pattern name, with no `names` override. Production SHALL share no resource group, Service Bus namespace, SQL server, database, App Service plan, Log Analytics workspace, gateway, dashboard, Function App, storage account, Application Insights or queue with any non-production configuration. The production App Service plan SHALL use SKU `P0v3`.

#### Scenario: Production shared names
- **WHEN** the resource names are derived for `prdshared`
- **THEN** they are:
  - resource group `d365fsc-integration-prod` (the override)
  - Service Bus namespace `sbns-eim-wtc-prd`
  - SQL server `sql-eim-wtc-prd`
  - database `sqldb-eim-wtc-prd`
  - plan `asp-eim-wtc-prd`
  - workspace `log-eim-wtc-prd`
  - gateway `azf-eim-gateway-wtc-prd`, with storage `stwtceimgwprd` and Insights `appins-eim-gateway-wtc-prd`
  - dashboard API `app-eim-dashboard-api-wtc-prd`
  - static web app `swa-eim-dashboard-wtc-prd`

#### Scenario: Production environment names
- **WHEN** the resource names are derived for `prd`
- **THEN** the Automation app is `azf-eim-automation-wtc-prd`, its storage `stwtceimautoprd`, its Insights `appins-eim-automation-wtc-prd`, the queues are prefixed `prd-`, and the shared names equal those of `prdshared`

#### Scenario: No name shared across stamps
- **WHEN** the derived and overridden names of every configuration with `production` true are compared with those of every configuration with `production` false
- **THEN** no name appears in both sets

#### Scenario: Production plan SKU
- **WHEN** the merged `prdshared` configuration is read
- **THEN** `appServicePlan.sku` is `P0v3`

### Requirement: Production configuration does not inherit non-production values
`deploy.prdshared.json` and `deploy.prd.json` SHALL each explicitly reset every value in `deploy.base.json` that identifies non-production. That covers:
- `sharedSuffix`
- `names.resourceGroup` (set to `d365fsc-integration-prod`), and `names.serviceBusNamespace`, `names.keyVault`, `names.sqlServer` and `names.sqlDatabase` (set to `""`, so the pattern name is derived)
- `entra.tenantId`, `entra.clientId` and `entra.audience`
- `gateway.legacyVerificationTargets`
- `automation.excludedUsers`
- `dashboard.simulationEnabled` and `dashboard.simulationQueuePrefix`

A production value that is not yet known SHALL be a `<...>` placeholder, so preflight names it until it is filled in. A test SHALL fail when a merged production configuration carries any of these non-production values. The only exception is `entra.tenantId`, which MAY equal the base value.

`subscriptionId` is not reset: `d365fsc-integration-prod` is in the same subscription as `d365fsc-integration-nonprod`, so production inherits it from `deploy.base.json`. Stamp isolation is carried by the resource group, the names, the service connection and the secrets, not by the subscription.

#### Scenario: Existing production resource group
- **WHEN** `prdshared` or `prd` is merged
- **THEN** the resource group is `d365fsc-integration-prod`, and no `infra` step creates a resource group

#### Scenario: Base names not inherited
- **WHEN** `deploy.base.json` sets `names.sqlServer` to `azql-wtc-sandbox` and `prdshared` is merged
- **THEN** the merged `names.sqlServer` is blank and the derived SQL server is `sql-eim-wtc-prd`

#### Scenario: Verification targets not inherited
- **WHEN** `prdshared` is merged
- **THEN** `gateway.legacyVerificationTargets` is empty and the production dashboard API gets no `Gateway__Applications__*` setting

#### Scenario: Non-production client id leaks
- **WHEN** `deploy.prdshared.json` omits `entra.clientId` and `deploy.base.json` holds a filled-in non-production client id
- **THEN** `DeploymentConfigTests` fails naming `prdshared` and `entra.clientId`

#### Scenario: Unfilled production value
- **WHEN** `deploy.prd.json` still holds `<production semicolon separated user guids>` and `settings` runs for `prd`
- **THEN** preflight fails naming `automation.excludedUsers`, and nothing is changed

### Requirement: Production resources are never removed by the script
`Deploy-Wilson.ps1 -Component remove` SHALL refuse every configuration whose merged `production` value is `true`. It SHALL refuse before any Azure call, with or without `-WhatIf`, and SHALL delete nothing.

#### Scenario: Remove refused on production
- **WHEN** an operator runs `Deploy-Wilson.ps1 -Environment prd -Component remove -WhatIf`
- **THEN** the script refuses, stating that `remove` never runs against a production configuration, and lists nothing for deletion

#### Scenario: Remove still works on non-production
- **WHEN** `remove -WhatIf` runs for `nonprod`
- **THEN** the legacy resources are listed as before

### Requirement: Production is deployed by its own pipeline from a tested package
`pipelines/eim-prd.yml` SHALL have no CI or pull-request trigger. It SHALL deploy only the `package` artifact of an `eim` pipeline run, referenced as a pipeline resource, and SHALL NOT rebuild from source. Its stages SHALL be exactly `Deploy_prdshared` then `Deploy_prd`, and:
- `Deploy_prdshared` deploys `gateway, backend, frontend`, and runs migrations when `runMigrations` is selected.
- `Deploy_prd` deploys `automation` and never runs migrations.

Both stages SHALL use:
- the service connection `sc-eim-wtc-prd`
- the variable group `eim-wtc-prd`
- the DevOps environments `eim-wtc-prdshared` and `eim-wtc-prd` respectively, each with approvals and an exclusive lock

It SHALL offer the same `deployInfra`, `applySettings`, `runMigrations` and `deployCode` parameters as `eim.yml`, with the same defaults. It SHALL use `lockBehavior: runLatest`.

#### Scenario: No automatic production deployment
- **WHEN** a change is merged to `main` or a pull request is opened
- **THEN** `eim-prd` does not run

#### Scenario: Promoted package
- **WHEN** `eim-prd` runs with `eim` run `eim-20261101.1` selected as its resource
- **THEN** both stages deploy the `package` artifact of `eim-20261101.1`, and no stage builds or tests source

#### Scenario: Production stage order
- **WHEN** `Deploy_prdshared` is rejected or fails
- **THEN** `Deploy_prd` does not start

#### Scenario: Production uses production credentials
- **WHEN** `pipelines/eim-prd.yml` is inspected
- **THEN** every deploy stage passes `serviceConnection: sc-eim-wtc-prd` and `variableGroup: eim-wtc-prd`, and no production stage names `sc-eim-wtc-nonprod` or `eim-wtc-nonprod`

#### Scenario: Non-production pipeline untouched by production
- **WHEN** `pipelines/eim.yml` is inspected
- **THEN** it has no `Deploy_prdshared` or `Deploy_prd` stage and names no production service connection, variable group or DevOps environment

### Requirement: Production go-live enables triggers through cutover
A new production Automation app SHALL start with every gated trigger disabled. The triggers SHALL be enabled only by `Deploy-Wilson.ps1 -Environment prd -Component cutover`, run from an operator machine. With no legacy app configured, that command enables every gated function except those in `automation.keepDisabled`. `rollback` for `prd` SHALL be refused, naming the missing legacy app.

#### Scenario: Triggers off after the first deployment
- **WHEN** the first `eim-prd` run with `applySettings` and `deployCode` completes
- **THEN** every `AzureWebJobs.<function>.Disabled` setting on `azf-eim-automation-wtc-prd` is `true`

#### Scenario: Go-live
- **WHEN** an operator runs `cutover` for `prd` with an empty `automation.keepDisabled`
- **THEN** all six gated functions are enabled on `azf-eim-automation-wtc-prd`

#### Scenario: No rollback target
- **WHEN** an operator runs `rollback` for `prd`
- **THEN** the script refuses, stating that `prd` has no legacy Automation app, and changes no trigger state
