## MODIFIED Requirements

### Requirement: One build is promoted through the environments in order
Each run of `pipelines/eim.yml` SHALL produce one package artifact. The artifact SHALL contain:
- the Automation host
- the gateway
- the dashboard API
- one dashboard frontend for each shared configuration whose Entra values are filled in
- the database migrator
- the configurations `nonprod`, `bat`, `test`, `prdshared` and `prd`

That same artifact SHALL be deployed by the stages `Deploy_shared`, `Deploy_bat` and `Deploy_test`, in that order. A stage SHALL start only after the previous stage succeeded, and no stage SHALL rebuild from source. The production pipeline `pipelines/eim-prd.yml` SHALL deploy the artifact of an `eim` run rather than build its own. The pipelines SHALL have no stage, DevOps environment or packaged configuration for `dev`; development happens on a developer's machine.

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
- **THEN** its stages are exactly `Build`, `Deploy_shared`, `Deploy_bat` and `Deploy_test`, no file under `pipelines/` names `Deploy_dev` or `eim-wtc-dev`, and the `Build` stage packages the configurations `nonprod`, `bat`, `test`, `prdshared` and `prd`

#### Scenario: Pull requests build and test only
- **WHEN** the pipeline runs for a pull request to `main`
- **THEN** the `Build` stage runs the tests and builds the package, and no deploy stage runs

#### Scenario: Tests gate the package
- **WHEN** any test in `ARM.Integration.Wilson.slnx` fails
- **THEN** no package artifact is published and no deploy stage runs

### Requirement: Secrets come only from secret variables
The EIMDB connection string SHALL be supplied only through `EIM_DEPLOY_EIMDB`, and the Service Bus connection string only through `EIM_DEPLOY_SERVICEBUS`. In the pipelines, both SHALL come from secret variables in the stamp's variable group, mapped explicitly into each deployment step's environment:
- `eim-wtc-nonprod` for `eim.yml`
- `eim-wtc-prd` for `eim-prd.yml`

Each variable group SHALL be authorised only for its own pipeline. No committed file SHALL contain either value.

#### Scenario: Secret missing in the pipeline
- **WHEN** a pipeline stage runs `settings` and `EIM_DEPLOY_EIMDB` is empty
- **THEN** preflight fails naming `EIM_DEPLOY_EIMDB` and nothing is changed

#### Scenario: Interactive run prompts
- **WHEN** an operator runs `settings` locally without `EIM_DEPLOY_SERVICEBUS` set
- **THEN** the script prompts for it without echoing the input

#### Scenario: Production stages use the production group
- **WHEN** `pipelines/eim-prd.yml` is inspected
- **THEN** each deploy stage uses the variable group `eim-wtc-prd`, and every `AzureCLI@2` step maps `EIM_DEPLOY_EIMDB`, `EIM_DEPLOY_SERVICEBUS` and `EIM_DEPLOY_CONFIG_OVERRIDES` through `env:`

### Requirement: Deployment package is self-contained
`New-DeploymentPackage.ps1` SHALL produce a versioned package containing:
- `Deploy-Wilson.ps1` and the configuration files
- the Function App and API zips, with the `.azurefunctions` folder preserved
- one dashboard frontend build for each packaged shared configuration whose Entra values are filled in, named `dashboard-frontend.<configuration>.zip`
- a self-contained `win-x64` `ARM.Integration.DbMigrator.exe`
- a manifest that records the version, the commit, the Automation queue and timer functions, and the configurations that have a frontend

Running the package SHALL require only PowerShell 7, the Azure CLI and, for the frontend, Node.js.

#### Scenario: Function zip keeps metadata
- **WHEN** the Automation zip is built
- **THEN** it contains the `.azurefunctions` folder and `functions.metadata`

#### Scenario: Manifest lists gated functions
- **WHEN** the package is built
- **THEN** `manifest.json` lists `AwaitingD365SweepHandler`, `QueueCaptureCustomer`, `QueueDataEventsHandler`, `QueueMessageQueueBusinessEventHandler`, `ReleaseSweepHandler` and `RetrySweepHandler`

#### Scenario: One frontend per shared configuration
- **WHEN** the package is built for `nonprod` and `prdshared` and both have filled-in Entra values
- **THEN** it contains `dashboard-frontend.nonprod.zip` and `dashboard-frontend.prdshared.zip`, each built with its own configuration's Entra values

#### Scenario: Production frontend not yet configured
- **WHEN** `prdshared` still has Entra placeholders when the package is built
- **THEN** the package has no `dashboard-frontend.prdshared.zip`, the build warns and succeeds, and a later production `frontend` deployment fails preflight on the missing artifact

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
Deployment SHALL authenticate to Azure only through the stamp's Azure Resource Manager service connection:
- `sc-eim-wtc-nonprod` for `eim.yml`, scoped to the non-production resource group
- `sc-eim-wtc-prd` for `eim-prd.yml`, scoped to `d365fsc-integration-prod`

Publish profiles and Kudu basic credentials SHALL NOT be used, and the pipelines that used them SHALL be removed from the repository.

#### Scenario: No publish profile in use
- **WHEN** the repository's pipeline YAML and scripts are inspected
- **THEN** none references a publish profile or `Deploy-ZipWithPublishProfile.ps1`

#### Scenario: Production uses its own connection
- **WHEN** `pipelines/eim-prd.yml` is inspected
- **THEN** every deploy stage passes `serviceConnection: sc-eim-wtc-prd`, and `pipelines/eim.yml` never names `sc-eim-wtc-prd`

## ADDED Requirements

### Requirement: The deploy template can take the package from another pipeline
`pipelines/templates/deploy-environment.yml` SHALL accept a `packageSource` parameter. It defaults to `current`, which downloads the `package` artifact of the running pipeline. Given a pipeline-resource alias instead, it SHALL download that resource's `package` artifact, and every `Deploy-Wilson.ps1` invocation in the template SHALL use that download's path.

#### Scenario: Non-production unchanged
- **WHEN** `eim.yml` uses the template without `packageSource`
- **THEN** the job downloads `current` / `package` and runs `$(Pipeline.Workspace)/package/Deploy-Wilson.ps1`

#### Scenario: Production uses the eim artifact
- **WHEN** `eim-prd.yml` passes `packageSource: eim`
- **THEN** the job downloads `eim` / `package` and runs `$(Pipeline.Workspace)/eim/package/Deploy-Wilson.ps1`
