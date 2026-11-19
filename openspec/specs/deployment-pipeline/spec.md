# deployment-pipeline Specification

## Purpose

Defines the Azure DevOps pipelines that build the system once and promote the same tested package through the environments in order, with approvals and locks per environment, manually requested infrastructure, settings and migration steps, pre-flight and health checks, layered configuration, and a self-contained deployment package.

## Requirements

### Requirement: One build is promoted through the environments in order
Each run of `pipelines/eim.yml` SHALL produce one package artifact. The artifact SHALL contain:
- the Automation host
- the gateway
- the dashboard API
- one dashboard frontend for each packaged configuration that has no `uses` and whose Entra values are filled in
- the database migrator
- the configurations `bat`, `test` and `prd`

That same artifact SHALL be deployed by the stages `Deploy_bat` and then `Deploy_test`. A stage SHALL start only after the previous stage succeeded, and no stage SHALL rebuild from source. The production pipeline `pipelines/eim-prd.yml` SHALL deploy the artifact of an `eim` run rather than build its own.

The pipelines SHALL have no stage, DevOps environment or packaged configuration for `dev`, `nonprod` or `prdshared`; development happens on a developer's machine.

#### Scenario: Test receives the build bat received
- **WHEN** run `eim-20261101.1` has deployed to `bat` and `Deploy_test` is approved
- **THEN** `test` is deployed from the `eim-20261101.1` package without rebuilding

#### Scenario: A failed stage stops promotion
- **WHEN** the `Deploy_bat` stage of a run fails
- **THEN** the `Deploy_test` stage of that run does not start

#### Scenario: A rejected stage stops promotion
- **WHEN** the `Deploy_bat` approval is rejected
- **THEN** `Deploy_test` does not start

#### Scenario: Stages of the non-production pipeline
- **WHEN** `pipelines/eim.yml` is inspected
- **THEN**:
  - its stages are exactly `Build`, `Deploy_bat` and `Deploy_test`
  - no file under `pipelines/` names `Deploy_dev`, `Deploy_shared`, `Deploy_prdshared`, `eim-wtc-dev`, `eim-wtc-nonprod` as a DevOps environment, or `eim-wtc-prdshared`
  - the `Build` stage packages the configurations `bat`, `test` and `prd`

#### Scenario: Pull requests build and test only
- **WHEN** the pipeline runs for a pull request to `main`
- **THEN** the `Build` stage runs the tests and builds the package, and no deploy stage runs

#### Scenario: Tests gate the package
- **WHEN** any test in `ARM.Integration.Wilson.slnx` fails
- **THEN** no package artifact is published and no deploy stage runs

### Requirement: Approval gates and locks per environment
Each deploy stage SHALL target its own Azure DevOps environment:
- `eim-wtc-bat` for `Deploy_bat`
- `eim-wtc-test` for `Deploy_test`
- `eim-wtc-prd` for `Deploy_prd`

Each of them SHALL require approval before any step that changes Azure. Every environment SHALL hold an exclusive lock, and a newer run waiting at an environment SHALL replace an older one that has not started there. The pipeline YAML SHALL NOT contain manual validation tasks; approvals live on the environments.

#### Scenario: Bat stage waits for approval
- **WHEN** a merge run's `Build` stage succeeds
- **THEN** `Deploy_bat` waits for approval on `eim-wtc-bat` and changes nothing before it is granted

#### Scenario: Test waits for its own approval after bat
- **WHEN** `Deploy_bat` succeeds
- **THEN** `Deploy_test` starts next and waits for approval on `eim-wtc-test` before changing anything

#### Scenario: Concurrent deployments to one environment
- **WHEN** a second run reaches `eim-wtc-bat` while another run is deploying to it
- **THEN** the second run waits until the first has finished

### Requirement: Infrastructure, settings and migrations run only when requested manually
A run triggered by a merge SHALL NOT create infrastructure, change app settings, run migrations or create database users.

A manual run SHALL offer these options, each off by default:
- `deployInfra`
- `applySettings`
- `runMigrations`
- `grantDatabaseAccess`

It SHALL also offer `deployCode`, on by default.

`deployInfra`, `applySettings` and `grantDatabaseAccess` SHALL apply in every stage of the run. `runMigrations` SHALL apply only in stages whose configuration has no `uses` (`Deploy_bat`, `Deploy_prd`). Each SHALL run before application code in that stage.

The deploy template SHALL take an `ownsResources` parameter:
- When it is true, the infrastructure step SHALL run `infra` and `sql`.
- When it is false, the infrastructure step SHALL run `infra` only.
- Passing `runMigrations` to a stage with `ownsResources` false SHALL be a template expansion error.

#### Scenario: Merge deploys code only
- **WHEN** a merge changes a value in `deploy.base.json`
- **THEN** the run it triggers deploys code only, and no app setting, resource, database user or schema changes

#### Scenario: Manual run applies settings
- **WHEN** someone starts the pipeline manually with `applySettings` selected
- **THEN** each stage applies its configuration's settings before deploying its code

#### Scenario: Settings-only run
- **WHEN** a manual run selects `applySettings` and clears `deployCode`
- **THEN** the run applies settings and runs `verify` and `health` without deploying any code

#### Scenario: Infrastructure without settings
- **WHEN** a manual run selects `deployInfra` only
- **THEN** missing resources are created, managed resources are converged, existing unmanaged resources are used as found, and no app setting is changed

#### Scenario: Owner stage creates the SQL server
- **WHEN** a manual `eim-prd` run selects `deployInfra`
- **THEN** `Deploy_prd` runs `infra` and then `sql`

#### Scenario: Using stage runs no sql component
- **WHEN** a manual `eim` run selects `deployInfra`
- **THEN** `Deploy_test` runs `infra` without `sql`

#### Scenario: Database access only on request
- **WHEN** a manual run selects `grantDatabaseAccess` and the `Deploy_test` approval is granted
- **THEN** `Deploy_test` runs `sqlusers` for `azf-eim-automation-wtc-test` between `myip` and `myipremove`, and a run without that option runs no `sqlusers`

### Requirement: Pre-flight check before changing an environment
Before changing anything, every stage SHALL run `preflight` and a `-WhatIf` preview. Preflight SHALL fail without changing anything when:
- a value the requested components need is an unfilled `<...>` placeholder
- a resolved name is invalid
- the `uses` or production isolation checks fail
- the package's queue and timer functions differ from the configured gated functions
- `settings` or `automation` is requested and either:
  - `automation.triggersEnabled` is missing or not a JSON boolean, or
  - `automation.keepDisabled` names a function not in `automation.gatedFunctions`
- `schema` or `sqlusers` is requested and the SQL server exists without an Entra administrator

When a run deploys code without applying settings, preflight SHALL also compare each target app's settings with the configuration:
- It SHALL fail when an app lacks a configured setting, or an Automation app lacks a gated `AzureWebJobs.<fn>.Disabled` setting.
- It SHALL warn, without failing, when a value differs. This includes a gated `AzureWebJobs.<fn>.Disabled` value that differs from the value `automation.triggersEnabled` and `automation.keepDisabled` produce.

Messages SHALL name the setting and app without any value.

#### Scenario: Unfilled placeholder
- **WHEN** `deploy.base.json` still contains `<deployed tenant id>` and the run includes `frontend`
- **THEN** `Deploy_bat` fails at preflight, naming `entra.tenantId`

#### Scenario: Code-only run against an app missing a setting
- **WHEN** a merge run would deploy to `bat` and `azf-eim-automation-wtc-bat` has no `RetrySweepSchedule`
- **THEN** `Deploy_bat` fails before deploying code, naming the setting and app and stating that a manual run with `applySettings` is needed

#### Scenario: Changed value only warns
- **WHEN** a code-only run finds `RetrySweepBatchSize` on the app differs from the configuration
- **THEN** the run warns, naming the setting and app without either value, and continues

#### Scenario: Trigger state differs from the configuration
- **WHEN** a code-only run to `bat` finds `AzureWebJobs.QueueCaptureCustomer.Disabled=true` on `azf-eim-automation-wtc-bat` while the configuration produces `false`
- **THEN** the run warns, naming `AzureWebJobs.QueueCaptureCustomer.Disabled` and the app without either value, changes no trigger setting, and continues

#### Scenario: Gated function list out of date
- **WHEN** the package's manifest lists a queue or timer function not in `automation.gatedFunctions`
- **THEN** preflight fails naming the function

#### Scenario: Invalid trigger configuration
- **WHEN** the run requests `settings` for `test` and `automation.keepDisabled` names `RetrySweepHandlr`
- **THEN** `Deploy_test` fails at preflight naming `RetrySweepHandlr`, and nothing is changed

#### Scenario: Server without an Entra administrator
- **WHEN** a manual run selects `runMigrations` and `azql-wtc-sandbox` has no Entra administrator
- **THEN** `Deploy_bat` fails at preflight naming `azql-wtc-sandbox` and the manual step that sets the administrator, and nothing is changed

### Requirement: Post-deploy health check
After deploying, every stage SHALL run `verify`, which reports, and `health`. `health` SHALL fail the stage, naming the app and check, when any of these fails:
- each Function App host in scope reaches `Running` within five minutes
- the dashboard API answers with a status below 500, for an owner configuration
- the static web app serves its root, for an owner configuration
- every Key Vault reference setting the configuration writes (`ServiceBus`, `Simulation__ServiceBusConnectionString`) is resolved

#### Scenario: Automation host fails to start
- **WHEN** `azf-eim-automation-wtc-bat` does not reach `Running` after deployment
- **THEN** `Deploy_bat` fails naming the app, and `Deploy_test` does not start

#### Scenario: Dashboard API failing
- **WHEN** the dashboard API answers 503 after deployment
- **THEN** `Deploy_bat` fails naming `wtc-dashboard-api-dev`

#### Scenario: Key Vault reference not resolved
- **WHEN** `Simulation__ServiceBusConnectionString` on `wtc-dashboard-api-dev` reports a Key Vault reference status other than resolved
- **THEN** `Deploy_bat` fails naming the app and the setting, without any secret value

### Requirement: Environment configuration is layered on a shared base
The effective configuration SHALL be built by merging, in order:
1. `deploy.base.json`
2. `deploy.<environment>.json`
3. an optional overrides file
4. the JSON in the `EIM_DEPLOY_CONFIG_OVERRIDES` variable, when set

Objects SHALL merge key by key, and lists and plain values SHALL be replaced. A value that is the same for every configuration SHALL be defined only in the base.

#### Scenario: Shared value changed once
- **WHEN** `automation.retryMaxAttempts` is changed in `deploy.base.json`
- **THEN** `bat` and `test` both receive it on their next settings run

#### Scenario: Environment override
- **WHEN** `deploy.test.json` sets `automation.retrySweepBatchSize`
- **THEN** `test` uses its own value and `bat` keeps the base value

#### Scenario: Pipeline override
- **WHEN** the variable group sets `EIM_DEPLOY_CONFIG_OVERRIDES` to `{"subscriptionId":"..."}`
- **THEN** that value replaces the committed placeholder for the run

### Requirement: Deployment needs no secret variables
Deployment SHALL need no secret pipeline variable and no secret environment variable:
- The EIMDB connection strings SHALL be derived from the configured SQL server and database with Entra authentication, as defined in `sql-identity-access`.
- The Service Bus connection SHALL be held in the Key Vault, as defined in `deployment-automation`.

`EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` SHALL NOT be read by any script or mapped by any pipeline. The script SHALL NOT prompt for a secret.

Each deployment step SHALL map only `EIM_DEPLOY_CONFIG_OVERRIDES` from its pipeline's variable group. That is:
- `eim-wtc-nonprod` for `eim.yml`
- `eim-wtc-prd` for `eim-prd.yml`

Each variable group SHALL be authorised only for its own pipeline. No committed file SHALL contain a connection string, key or password.

#### Scenario: No secret variable mapped
- **WHEN** `pipelines/eim.yml`, `pipelines/eim-prd.yml` and `pipelines/templates/deploy-environment.yml` are inspected
- **THEN** no file names `EIM_DEPLOY_EIMDB` or `EIM_DEPLOY_SERVICEBUS`, and every `AzureCLI@2` step maps `EIM_DEPLOY_CONFIG_OVERRIDES` through `env:` and nothing else from the variable group

#### Scenario: Local run asks for nothing
- **WHEN** an operator runs `settings` locally for `bat` after `az login`, with no `EIM_DEPLOY_*` variable set
- **THEN** the script prompts for no value and writes the settings

#### Scenario: Production stages use the production group
- **WHEN** `pipelines/eim-prd.yml` is inspected
- **THEN** its deploy stage uses the variable group `eim-wtc-prd`

### Requirement: Secret values never appear in deployment output
Deployment logs, previews and failure messages SHALL NOT contain secret values, connection strings with keys, access tokens or function keys. Settings and Key Vault secret values SHALL be passed to Azure through temporary files that are deleted afterwards, never on a command line. Access tokens SHALL be held only in memory.

#### Scenario: Preview lists names only
- **WHEN** a preview lists the app settings it would apply
- **THEN** it lists setting names and no values

#### Scenario: Settings file removed after failure
- **WHEN** applying settings fails
- **THEN** the temporary settings file has been deleted

#### Scenario: Secret file removed after failure
- **WHEN** writing `eim-servicebus` to the Key Vault fails
- **THEN** the temporary secret file has been deleted, and the message names the vault and secret without the value

#### Scenario: Database sign-in failure hides the token
- **WHEN** `sqlusers` cannot sign in to the SQL server
- **THEN** the message names the server, the database and the signed-in identity, and contains no access token

### Requirement: Deployment package is self-contained
`New-DeploymentPackage.ps1` SHALL produce a versioned package containing:
- `Deploy-Wilson.ps1` and the configuration files
- the Function App and API zips, with the `.azurefunctions` folder preserved
- one dashboard frontend build, named `dashboard-frontend.<configuration>.zip`, for each packaged configuration that has no `uses` and whose Entra values are filled in
- a self-contained `win-x64` `ARM.Integration.DbMigrator.exe`
- a manifest that records the version, the commit, the Automation queue and timer functions, and the configurations that have a frontend

Running the package SHALL require only PowerShell 7, the Azure CLI and, for the frontend, Node.js.

#### Scenario: Function zip keeps metadata
- **WHEN** the Automation zip is built
- **THEN** it contains the `.azurefunctions` folder and `functions.metadata`

#### Scenario: Manifest lists gated functions
- **WHEN** the package is built
- **THEN** `manifest.json` lists `AwaitingD365SweepHandler`, `QueueCaptureCustomer`, `QueueDataEventsHandler`, `QueueMessageQueueBusinessEventHandler`, `ReleaseSweepHandler` and `RetrySweepHandler`

#### Scenario: One frontend per dashboard owner
- **WHEN** the package is built for `bat`, `test` and `prd`, and `bat` and `prd` have filled-in Entra values
- **THEN** it contains `dashboard-frontend.bat.zip` and `dashboard-frontend.prd.zip`, each built with its own configuration's Entra values, and no `dashboard-frontend.test.zip`

#### Scenario: Production frontend not yet configured
- **WHEN** `prd` still has Entra placeholders when the package is built
- **THEN** the package has no `dashboard-frontend.prd.zip`, the build warns and succeeds, and a later production `frontend` deployment fails preflight on the missing artifact

#### Scenario: Migrator needs no SDK
- **WHEN** the `schema` component runs on an agent without the .NET SDK
- **THEN** the packaged migrator runs

#### Scenario: Build stage checks the scripts and the package
- **WHEN** the `Build` stage has run `New-DeploymentPackage.ps1`
- **THEN** it runs `Test-DeployWilson.ps1`, and then `Test-DeployWilson.ps1 -PackagePath` against the built package, and no package artifact is published when either fails

#### Scenario: Package check catches lost function metadata
- **WHEN** `Test-DeployWilson.ps1 -PackagePath` finds that `wilson-automation.zip` has no `.azurefunctions/` entry or that its `functions.metadata` does not list exactly the six gated functions
- **THEN** it fails naming the zip and the missing entry or function

### Requirement: Pipelines authenticate with a service connection
Deployment SHALL authenticate to Azure only through the pipeline's Azure Resource Manager service connection:
- `sc-eim-wtc-nonprod` for `eim.yml`
- `sc-eim-wtc-prd` for `eim-prd.yml`

The same identity SHALL authenticate to the SQL database for migrations and database users, through the Azure CLI token. To do this, the service connection's principal SHALL be a member of the SQL server's Entra administrator group.

Publish profiles, Kudu basic credentials and SQL logins SHALL NOT be used by the pipelines. The pipelines that used publish profiles SHALL be removed from the repository.

#### Scenario: No publish profile in use
- **WHEN** the repository's pipeline YAML and scripts are inspected
- **THEN** none references a publish profile or `Deploy-ZipWithPublishProfile.ps1`

#### Scenario: Production uses its own connection
- **WHEN** `pipelines/eim-prd.yml` is inspected
- **THEN** its deploy stage passes `serviceConnection: sc-eim-wtc-prd`, and `pipelines/eim.yml` never names `sc-eim-wtc-prd`

#### Scenario: Database step uses the service connection
- **WHEN** the database step of `Deploy_bat` runs `schema`
- **THEN** the migrator signs in to `eimdb` as the `sc-eim-wtc-nonprod` principal through `Active Directory Default` with the Azure CLI credential, and no SQL password is used

### Requirement: The deploy template can take the package from another pipeline
`pipelines/templates/deploy-environment.yml` SHALL accept a `packageSource` parameter. It defaults to `current`, which downloads the `package` artifact of the running pipeline. Given a pipeline-resource alias instead, it SHALL download that resource's `package` artifact, and every `Deploy-Wilson.ps1` invocation in the template SHALL use that download's path.

#### Scenario: Non-production unchanged
- **WHEN** `eim.yml` uses the template without `packageSource`
- **THEN** the job downloads `current` / `package` and runs `$(Pipeline.Workspace)/package/Deploy-Wilson.ps1`

#### Scenario: Production uses the eim artifact
- **WHEN** `eim-prd.yml` passes `packageSource: eim`
- **THEN** the job downloads `eim` / `package` and runs `$(Pipeline.Workspace)/eim/package/Deploy-Wilson.ps1`
