## ADDED Requirements

### Requirement: One build is promoted through the environments in order
Each run of `pipelines/eim.yml` SHALL produce one package artifact, containing the Automation host, the gateway, the dashboard API, one shared dashboard frontend and the database migrator. That same artifact SHALL be deployed by the stages `Deploy_shared`, `Deploy_bat` and `Deploy_test`, in that order. A stage SHALL start only after the previous stage succeeded, and no stage SHALL rebuild from source. The pipeline SHALL have no stage, DevOps environment or packaged configuration for `dev`; development happens on a developer's machine.

#### Scenario: Test receives the build bat received
- **WHEN** run `eim-20261101.1` has deployed to `nonprod` and `bat` and `test` is approved
- **THEN** `test` is deployed from the `eim-20261101.1` package without rebuilding

#### Scenario: A failed stage stops promotion
- **WHEN** the `Deploy_shared` stage of a run fails
- **THEN** the `Deploy_bat` and `Deploy_test` stages of that run do not start

#### Scenario: A rejected stage stops promotion
- **WHEN** the `Deploy_bat` approval is rejected
- **THEN** `Deploy_test` does not start

#### Scenario: No dev stage
- **WHEN** `pipelines/eim.yml` is inspected
- **THEN** its stages are exactly `Build`, `Deploy_shared`, `Deploy_bat` and `Deploy_test`, no file under `pipelines/` names `Deploy_dev` or `eim-wtc-dev`, and the `Build` stage packages the configurations `nonprod`, `bat` and `test`

#### Scenario: Pull requests build and test only
- **WHEN** the pipeline runs for a pull request to `main`
- **THEN** the `Build` stage runs the tests and builds the package, and no deploy stage runs

#### Scenario: Tests gate the package
- **WHEN** any test in `ARM.Integration.Wilson.slnx` fails
- **THEN** no package artifact is published and no deploy stage runs

### Requirement: Approval gates and locks per environment
Each deploy stage SHALL target its own Azure DevOps environment: `eim-wtc-nonprod`, `eim-wtc-bat` and `eim-wtc-test`. Each of them SHALL require approval before any step that changes Azure. Every environment SHALL hold an exclusive lock, and a newer run waiting at an environment SHALL replace an older one that has not started there. The pipeline YAML SHALL NOT contain manual validation tasks; approvals live on the environments.

#### Scenario: Shared stage waits for approval
- **WHEN** a merge run's `Build` stage succeeds
- **THEN** `Deploy_shared` waits for approval and changes nothing before it is granted

#### Scenario: Bat waits for its own approval after shared
- **WHEN** `Deploy_shared` succeeds
- **THEN** `Deploy_bat` starts next and waits for approval on `eim-wtc-bat` before changing anything

#### Scenario: Concurrent deployments to one environment
- **WHEN** a second run reaches `eim-wtc-bat` while another run is deploying to it
- **THEN** the second run waits until the first has finished

### Requirement: Infrastructure, settings and migrations run only when requested manually
A run triggered by a merge SHALL NOT create infrastructure, change app settings or run migrations. A manual run SHALL offer `deployInfra`, `applySettings` and `runMigrations` as separate options, each off by default, and `deployCode`, on by default. `deployInfra` and `applySettings` SHALL apply in every stage of the run. `runMigrations` SHALL apply only in `Deploy_shared`. Each SHALL run before application code in that stage.

#### Scenario: Merge deploys code only
- **WHEN** a merge changes a value in `deploy.base.json`
- **THEN** the run it triggers deploys code only, and no app setting, resource or schema changes

#### Scenario: Manual run applies settings
- **WHEN** someone starts the pipeline manually with `applySettings` selected
- **THEN** each stage applies its configuration's settings before deploying its code

#### Scenario: Settings-only run
- **WHEN** a manual run selects `applySettings` and clears `deployCode`
- **THEN** the run applies settings and runs `verify` and `health` without deploying any code

#### Scenario: Infrastructure without settings
- **WHEN** a manual run selects `deployInfra` only
- **THEN** missing resources are created and existing resources are converged, and no app setting is changed

### Requirement: Pre-flight check before changing an environment
Before changing anything, every stage SHALL run `preflight` and a `-WhatIf` preview. Preflight SHALL fail without changing anything when:
- a value the requested components need is an unfilled `<...>` placeholder
- a required secret variable is missing
- a derived name is invalid or equals a legacy name
- the package's queue and timer functions differ from the configured gated functions

When a run deploys code without applying settings, preflight SHALL also compare each target app's setting names with the configuration:
- It SHALL fail when an app lacks a configured setting, or an Automation app lacks a gated `AzureWebJobs.<fn>.Disabled` setting.
- It SHALL warn, without failing, when a value differs.

Messages SHALL name the setting and app without any value.

#### Scenario: Unfilled placeholder
- **WHEN** `deploy.base.json` still contains `<deployed tenant id>` and the run includes `frontend`
- **THEN** `Deploy_shared` fails at preflight, naming `entra.tenantId`

#### Scenario: Code-only run against an app missing a setting
- **WHEN** a merge run would deploy to `bat` and `azf-eim-automation-wtc-bat` has no `RetrySweepSchedule`
- **THEN** `Deploy_bat` fails before deploying code, naming the setting and app and stating that a manual run with `applySettings` is needed

#### Scenario: Changed value only warns
- **WHEN** a code-only run finds `RetrySweepBatchSize` on the app differs from the configuration
- **THEN** the run warns, naming the setting and app without either value, and continues

#### Scenario: Gated function list out of date
- **WHEN** the package's manifest lists a queue or timer function not in `automation.gatedFunctions`
- **THEN** preflight fails naming the function

### Requirement: Post-deploy health check
After deploying, every stage SHALL run `verify`, which reports, and `health`, which fails the stage naming the app and check when any of these fails:
- each Function App host in scope reaches `Running` within five minutes
- the dashboard API answers with a status below 500
- the static web app serves its root

#### Scenario: Automation host fails to start
- **WHEN** `azf-eim-automation-wtc-bat` does not reach `Running` after deployment
- **THEN** `Deploy_bat` fails naming the app, and `Deploy_test` does not start

#### Scenario: Dashboard API failing
- **WHEN** the dashboard API answers 503 after deployment
- **THEN** `Deploy_shared` fails naming `wtc-dashboard-api-dev`

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

### Requirement: Secrets come only from secret variables
The EIMDB connection string SHALL be supplied only through `EIM_DEPLOY_EIMDB`, and the Service Bus connection string only through `EIM_DEPLOY_SERVICEBUS`. In the pipeline, both SHALL come from secret variables in the `eim-wtc-nonprod` variable group, mapped explicitly into each deployment step's environment. No committed file SHALL contain either value.

#### Scenario: Secret missing in the pipeline
- **WHEN** a pipeline stage runs `settings` and `EIM_DEPLOY_EIMDB` is empty
- **THEN** preflight fails naming `EIM_DEPLOY_EIMDB` and nothing is changed

#### Scenario: Interactive run prompts
- **WHEN** an operator runs `settings` locally without `EIM_DEPLOY_SERVICEBUS` set
- **THEN** the script prompts for it without echoing the input

### Requirement: Secret values never appear in deployment output
Deployment logs, previews and failure messages SHALL NOT contain secret values, connection strings or function keys. Settings SHALL be passed to Azure through temporary files that are deleted afterwards, never on a command line.

#### Scenario: Preview lists names only
- **WHEN** a preview lists the app settings it would apply
- **THEN** it lists setting names and no values

#### Scenario: Settings file removed after failure
- **WHEN** applying settings fails
- **THEN** the temporary settings file has been deleted

### Requirement: Deployment package is self-contained
`New-DeploymentPackage.ps1` SHALL produce a versioned package containing `Deploy-Wilson.ps1`, the configuration files, the Function App and API zips with the `.azurefunctions` folder preserved, one dashboard frontend build, a self-contained `win-x64` `ARM.Integration.DbMigrator.exe`, and a manifest that records the version, commit and Automation queue and timer functions. Running the package SHALL require only PowerShell 7, the Azure CLI and, for the frontend, Node.js.

#### Scenario: Function zip keeps metadata
- **WHEN** the Automation zip is built
- **THEN** it contains the `.azurefunctions` folder and `functions.metadata`

#### Scenario: Manifest lists gated functions
- **WHEN** the package is built
- **THEN** `manifest.json` lists `AwaitingD365SweepHandler`, `QueueCaptureCustomer`, `QueueDataEventsHandler`, `QueueMessageQueueBusinessEventHandler`, `ReleaseSweepHandler` and `RetrySweepHandler`

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
Deployment SHALL authenticate to Azure only through the Azure Resource Manager service connection `sc-eim-wtc-nonprod`. Publish profiles and Kudu basic credentials SHALL NOT be used, and the pipelines that used them SHALL be removed from the repository.

#### Scenario: No publish profile in use
- **WHEN** the repository's pipeline YAML and scripts are inspected
- **THEN** none references a publish profile or `Deploy-ZipWithPublishProfile.ps1`
