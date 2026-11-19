## MODIFIED Requirements

### Requirement: A single entry point deploys the system
The system SHALL provide one deployment script, `script/deployment/Deploy-Wilson.ps1`, that deploys every component to any configured deployment configuration: the non-production configurations `nonprod`, `bat` and `test`, and the production configurations `prdshared` and `prd`. There SHALL be no `dev` configuration; development runs on a developer's machine. Deploying SHALL NOT require selecting between alternative scripts or knowing which of several scripts is current. `Deploy-Eim.ps1` and `Deploy-ZipWithPublishProfile.ps1` SHALL NOT remain in the repository. The script SHALL deploy application code only from a package produced by `New-DeploymentPackage.ps1` and SHALL NOT build from source.

#### Scenario: Full deployment to an environment
- **WHEN** an operator runs the deployment script for a configuration requesting `all`
- **THEN** every component owned by that configuration's scope is deployed in the defined order, and no on-demand component (`cutover`, `rollback`, `remove`) runs

#### Scenario: Single component deployment
- **WHEN** an operator requests a single component
- **THEN** only that component is deployed and the others are left untouched

#### Scenario: Unsupported environment is rejected
- **WHEN** an operator requests an environment that is not configured
- **THEN** the script stops with a message naming the unconfigured environment, and no action is taken

#### Scenario: Dev is not a configuration
- **WHEN** an operator runs the deployment script with `-Environment dev`
- **THEN** the script stops naming `dev` as unknown and listing the configurations `bat`, `nonprod`, `prd`, `prdshared` and `test`, and no action is taken

#### Scenario: Deployment configurations are independent of database ENVIRONMENT values
- **WHEN** the shared database holds ENVIRONMENT values that have no deployment configuration, such as `DEV2`
- **THEN** the script neither requires nor creates a configuration, Function App or queue for them, and no deployment step reads or filters the database by ENVIRONMENT value

#### Scenario: Code is taken from the package
- **WHEN** the `automation` component runs and the package has no `wilson-automation.zip`
- **THEN** the script stops naming the missing artifact and does not run `dotnet publish`

### Requirement: Resource names derive from the environment
All Azure resource names SHALL be derived through a single definition from the pattern `{type}-{workload}-{component}-{client}-{suffix}`, with workload `eim`, client `wtc`, and the component omitted for workload-wide resources. The suffix SHALL be the environment name for per-environment resources and the configuration's `sharedSuffix` for shared resources: `nonprod` for the non-production stamp and `prd` for the production stamp. Storage account names SHALL use `st{client}{workload}{shortcomponent}{suffix}` and SHALL match `^[a-z0-9]{3,24}$`. Function App names SHALL NOT exceed 32 characters. Every derived name SHALL be overridable through the configuration's `names` section. A blank override SHALL fall back to the derived name. Adding an environment SHALL NOT require duplicating or editing deployment logic.

#### Scenario: Names derived consistently
- **WHEN** the script targets environment `bat`
- **THEN** it uses Function App `azf-eim-automation-wtc-bat`, storage `stwtceimautobat`, Insights `appins-eim-automation-wtc-bat` and queues prefixed `bat-`

#### Scenario: Shared names derived consistently
- **WHEN** the script targets configuration `nonprod`
- **THEN** it uses:
  - plan `asp-eim-wtc-nonprod`
  - gateway `azf-eim-gateway-wtc-nonprod`
  - gateway storage `stwtceimgwnonprod`
  - Log Analytics workspace `log-eim-wtc-nonprod`
  - dashboard API `wtc-dashboard-api-dev` and static web app `wtc-dashboard-frontend-dev`, from the `names` overrides in `deploy.nonprod.json`

#### Scenario: Shared names follow the shared suffix
- **WHEN** the script targets configuration `prdshared`, whose `sharedSuffix` is `prd`
- **THEN** it uses plan `asp-eim-wtc-prd`, gateway `azf-eim-gateway-wtc-prd`, gateway storage `stwtceimgwprd` and Log Analytics workspace `log-eim-wtc-prd`

#### Scenario: Blank override falls back to the pattern
- **WHEN** the merged configuration has `names.sqlServer` set to an empty string and `sharedSuffix` `prd`
- **THEN** the SQL server name is `sql-eim-wtc-prd`

#### Scenario: Non-conforming resource name
- **WHEN** an existing resource does not follow the naming pattern, such as SQL server `azql-wtc-sandbox`, resource group `d365fsc-integration-nonprod`, dashboard API `wtc-dashboard-api-dev` or static web app `wtc-dashboard-frontend-dev`
- **THEN** its name is supplied as an explicit `names` override rather than requiring a change to deployment logic

#### Scenario: Override wins over the derived name
- **WHEN** `names.dashboardApi` is `wtc-dashboard-api-dev` in the merged configuration
- **THEN** every component that addresses the dashboard API (`infra`, `settings`, `backend`, `frontend`, `health`) uses `wtc-dashboard-api-dev` and never `app-eim-dashboard-api-wtc-nonprod`

#### Scenario: Invalid derived storage name
- **WHEN** a configured environment name would produce a storage account name longer than 24 characters
- **THEN** preflight fails naming the storage account and makes no change

#### Scenario: Adding an environment
- **WHEN** a new environment is introduced
- **THEN** it is added by providing configuration, without creating a new script

### Requirement: Shared components are not deployed per environment
The system SHALL distinguish the `shared` scope from the `environment` scope:
- Each stamp's `shared` scope is owned by the stamp's shared configuration: `nonprod` for non-production, `prdshared` for production.
- The `environment` scope is owned by the stamp's environment configurations: `bat` and `test` for non-production, `prd` for production.

A stamp is the set of configurations with the same `sharedSuffix`. The dashboard API, the dashboard frontend, the D365 F&SC gateway, the App Service plan, the Log Analytics workspace and database schema migrations SHALL be shared within a stamp. Queues, Automation storage, Automation Insights and the Automation Function App SHALL be per environment. Requesting a component outside the configuration's scope SHALL be rejected, naming the stamp's owning configuration, and `all` SHALL expand only to the components the scope owns.

#### Scenario: Per-environment component
- **WHEN** the Automation host, queues or storage is deployed for `bat`
- **THEN** it is deployed to resources specific to `bat`

#### Scenario: Shared component requested for the wrong environment
- **WHEN** the `backend` or `frontend` component is requested against `bat`
- **THEN** the script rejects the request, naming `nonprod` as the owning configuration, rather than creating a second deployment

#### Scenario: Shared component requested for the production environment
- **WHEN** the `gateway` component is requested against `prd`
- **THEN** the script rejects the request, naming `prdshared` as the owning configuration

#### Scenario: Gateway requested for the wrong environment
- **WHEN** the `gateway` component is requested against `test`
- **THEN** the script rejects the request rather than creating a second gateway application

#### Scenario: Schema requested for an environment
- **WHEN** the `schema` component is requested against `bat`
- **THEN** the script rejects the request, because the database is shared and migrated from `nonprod`

#### Scenario: Automation requested for the shared configuration
- **WHEN** the `automation` component is requested against `prdshared`
- **THEN** the script rejects the request, naming `prd` as the environment configuration of that stamp

#### Scenario: Shared infrastructure missing for the production environment
- **WHEN** `infra` runs for `prd` and `asp-eim-wtc-prd` does not exist
- **THEN** the script stops, stating that `infra` must run for the shared configuration `prdshared` first

#### Scenario: Adding an environment requires no gateway
- **WHEN** a new environment is added to a stamp
- **THEN** it requires its own queues, storage and Automation host, and reuses that stamp's existing shared gateway, dashboard, plan and database

## ADDED Requirements

### Requirement: Each stamp has exactly one shared configuration
For every configuration, there SHALL be exactly one configuration with scope `shared` and the same `sharedSuffix`. Preflight SHALL fail when there is none, or more than one, naming the suffix and the configurations found.

#### Scenario: Shared configuration resolved for production
- **WHEN** the shared configuration is resolved for `prd`
- **THEN** it is `prdshared`

#### Scenario: Two shared configurations for one suffix
- **WHEN** two configurations with scope `shared` both have `sharedSuffix` `prd`
- **THEN** preflight fails naming both configurations, and nothing is changed

### Requirement: Preflight checks the foundation resources
Preflight SHALL fail, changing nothing, when any of these does not exist or cannot be read:
- the resource group `names.resourceGroup`
- the Service Bus namespace `names.serviceBusNamespace`
- the SQL server `names.sqlServer`
- the database `names.sqlDatabase`

Each missing resource SHALL be named. The script SHALL NOT create any of these four resources.

#### Scenario: Misspelled namespace
- **WHEN** preflight runs for `prdshared` and `sbns-eim-wtc-prd` does not exist in `d365fsc-integration-prod`
- **THEN** preflight fails naming `sbns-eim-wtc-prd`, and no resource is created

#### Scenario: Database missing
- **WHEN** preflight runs for `prdshared` and `sqldb-eim-wtc-prd` does not exist on `sql-eim-wtc-prd`
- **THEN** preflight fails naming the database and the server

### Requirement: Secrets point at the configured resources
Whenever a run needs the deployment secrets, preflight SHALL check where they point:
- The SQL server in `EIM_DEPLOY_EIMDB` SHALL equal `names.sqlServer`.
- Its `Initial Catalog` (or `Database`) SHALL equal `names.sqlDatabase`.
- The namespace in the `EIM_DEPLOY_SERVICEBUS` endpoint SHALL equal `names.serviceBusNamespace`.

On a mismatch, preflight SHALL fail, changing nothing. The message SHALL name the variable, the parsed resource name and the expected name. It SHALL NOT include the connection string, a key or a password.

#### Scenario: Non-production database secret in the production group
- **WHEN** `settings` runs for `prdshared` and `EIM_DEPLOY_EIMDB` names server `azql-wtc-sandbox`
- **THEN** preflight fails naming `EIM_DEPLOY_EIMDB`, `azql-wtc-sandbox` and `sql-eim-wtc-prd`, and no setting is written

#### Scenario: Wrong catalog on the right server
- **WHEN** `settings` runs for `nonprod` and `EIM_DEPLOY_EIMDB` names server `azql-wtc-sandbox` with `Initial Catalog=eimdb_dev`
- **THEN** preflight fails naming `EIM_DEPLOY_EIMDB`, `eimdb_dev` and `eimdb`

#### Scenario: Non-production Service Bus secret in the production group
- **WHEN** `settings` runs for `prd` and `EIM_DEPLOY_SERVICEBUS` has endpoint `sb://asb-eim-wtc-nonprod.servicebus.windows.net/`
- **THEN** preflight fails naming `EIM_DEPLOY_SERVICEBUS`, `asb-eim-wtc-nonprod` and `sbns-eim-wtc-prd`

#### Scenario: Matching secrets pass
- **WHEN** both secrets point at the configured server, database and namespace
- **THEN** this check adds no problem

### Requirement: Dashboard simulation setting
The configuration key `dashboard.simulationEnabled` SHALL control simulation on the dashboard API. It is `true` in `deploy.base.json` and `false` in every production configuration.
- `settings` for a shared configuration SHALL always write `Simulation__Enabled` as `true` or `false`.
- When enabled, `settings` SHALL also write `Simulation__ServiceBusConnectionString` and the four `Simulation__*` queue settings derived from `dashboard.simulationQueuePrefix`, and SHALL fail before writing anything when that prefix is blank or a placeholder.
- When disabled, `settings` SHALL write no other `Simulation__*` setting, and SHALL delete any other `Simulation__*` setting present on the dashboard API.
- The preflight drift check SHALL treat `Simulation__Enabled` as a configured setting.

#### Scenario: Non-production keeps simulation
- **WHEN** `settings` runs for `nonprod`
- **THEN** `wtc-dashboard-api-dev` gets `Simulation__Enabled=true`, `Simulation__ServiceBusConnectionString` and the four queue settings prefixed `bat-`

#### Scenario: Production has no simulation settings
- **WHEN** `settings` runs for `prdshared`
- **THEN** `app-eim-dashboard-api-wtc-prd` gets `Simulation__Enabled=false` and no other `Simulation__*` setting

#### Scenario: Stale simulation settings removed
- **WHEN** `settings` runs for `prdshared` and the dashboard API has `Simulation__ServiceBusConnectionString`
- **THEN** that setting is deleted, and no setting outside `Simulation__*` and `Gateway__Applications__*` is deleted

#### Scenario: Enabled without a queue prefix
- **WHEN** `dashboard.simulationEnabled` is `true` and `dashboard.simulationQueuePrefix` is empty
- **THEN** `settings` fails naming `dashboard.simulationQueuePrefix`, and no setting is written

#### Scenario: Code-only run before the setting exists
- **WHEN** a code-only run deploys `backend` to `nonprod` and `wtc-dashboard-api-dev` has no `Simulation__Enabled`
- **THEN** preflight fails naming `Simulation__Enabled` and the app, and states that a manual run with `applySettings` is needed
