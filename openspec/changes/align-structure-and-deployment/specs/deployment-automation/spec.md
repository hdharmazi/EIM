## MODIFIED Requirements

### Requirement: A single entry point deploys the system
The system SHALL provide one deployment script, `script/deployment/Deploy-Wilson.ps1`, that deploys every component to any supported non-production configuration (`nonprod`, `dev`, `bat`, `test`). Deploying SHALL NOT require selecting between alternative scripts or knowing which of several scripts is current. `Deploy-Eim.ps1` and `Deploy-ZipWithPublishProfile.ps1` SHALL NOT remain in the repository. The script SHALL deploy application code only from a package produced by `New-DeploymentPackage.ps1` and SHALL NOT build from source.

#### Scenario: Full deployment to an environment
- **WHEN** an operator runs the deployment script for a configuration requesting `all`
- **THEN** every component owned by that configuration's scope is deployed in the defined order, and no on-demand component (`cutover`, `rollback`, `remove`) runs

#### Scenario: Single component deployment
- **WHEN** an operator requests a single component
- **THEN** only that component is deployed and the others are left untouched

#### Scenario: Unsupported environment is rejected
- **WHEN** an operator requests an environment that is not configured
- **THEN** the script stops with a message naming the unconfigured environment, and no action is taken

#### Scenario: Deployment configurations are independent of database ENVIRONMENT values
- **WHEN** the shared database holds ENVIRONMENT values that have no deployment configuration, such as `DEV2`
- **THEN** the script neither requires nor creates a configuration, Function App or queue for them, and no deployment step reads or filters the database by ENVIRONMENT value

#### Scenario: Code is taken from the package
- **WHEN** the `automation` component runs and the package has no `wilson-automation.zip`
- **THEN** the script stops naming the missing artifact and does not run `dotnet publish`

### Requirement: Resource names derive from the environment
All Azure resource names SHALL be derived through a single definition from the pattern `{type}-{workload}-{component}-{client}-{suffix}`, with workload `eim`, client `wtc`, suffix equal to the environment name for per-environment resources and `nonprod` for shared resources, and the component omitted for workload-wide resources. Storage account names SHALL use `st{client}{workload}{shortcomponent}{suffix}` and SHALL match `^[a-z0-9]{3,24}$`. Function App names SHALL NOT exceed 32 characters. Every derived name SHALL be overridable through the configuration's `names` section. Adding an environment SHALL NOT require duplicating or editing deployment logic.

#### Scenario: Names derived consistently
- **WHEN** the script targets environment `bat`
- **THEN** it uses Function App `azf-eim-automation-wtc-bat`, storage `stwtceimautobat`, Insights `appins-eim-automation-wtc-bat` and queues prefixed `bat-`

#### Scenario: Shared names derived consistently
- **WHEN** the script targets configuration `nonprod`
- **THEN** it uses plan `asp-eim-wtc-nonprod`, gateway `azf-eim-gateway-wtc-nonprod`, gateway storage `stwtceimgwnonprod` and Log Analytics workspace `log-eim-wtc-nonprod`, and it uses dashboard API `wtc-dashboard-api-dev` and static web app `wtc-dashboard-frontend-dev` from the `names` overrides in `deploy.nonprod.json`

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

### Requirement: Components deploy in a defined order
When multiple components are deployed together, the system SHALL apply them in this order regardless of the order requested: `preflight`, `infra`, `settings`, `myip`, `schema`, `myipremove`, `gateway`, `backend`, `frontend`, `automation`, `verify`, `health`. Message queues SHALL be provisioned by `infra`.

#### Scenario: Queues precede the Functions host
- **WHEN** all components are deployed to a new environment
- **THEN** the message queues exist before the Automation Function App is deployed

#### Scenario: Schema precedes application code
- **WHEN** schema and code components are deployed together
- **THEN** database migrations are applied before any application component is deployed

#### Scenario: API precedes the frontend
- **WHEN** all shared components are deployed
- **THEN** the dashboard API exists before the frontend is deployed and linked to it

#### Scenario: Requested order is ignored
- **WHEN** an operator requests `health, automation, settings`
- **THEN** the script runs `preflight`, `settings`, `automation`, then `health`

### Requirement: Shared components are not deployed per environment
The system SHALL distinguish the `shared` scope, owned by the `nonprod` configuration, from the `environment` scope, owned by `dev`, `bat` and `test`. The dashboard API, the dashboard frontend, the D365 F&SC gateway, the App Service plan, the Log Analytics workspace and database schema migrations SHALL be shared. Queues, Automation storage, Automation Insights and the Automation Function App SHALL be per environment. Requesting a component outside the configuration's scope SHALL be rejected, and `all` SHALL expand only to the components the scope owns.

#### Scenario: Per-environment component
- **WHEN** the Automation host, queues or storage is deployed for `dev`
- **THEN** it is deployed to resources specific to `dev`

#### Scenario: Shared component requested for the wrong environment
- **WHEN** the `backend` or `frontend` component is requested against `bat`
- **THEN** the script rejects the request, naming `nonprod` as the owning configuration, rather than creating a second deployment

#### Scenario: Gateway requested for the wrong environment
- **WHEN** the `gateway` component is requested against `test`
- **THEN** the script rejects the request rather than creating a second gateway application

#### Scenario: Schema requested for an environment
- **WHEN** the `schema` component is requested against `dev`
- **THEN** the script rejects the request, because the database is shared and migrated from `nonprod`

#### Scenario: Automation requested for the shared configuration
- **WHEN** the `automation` component is requested against `nonprod`
- **THEN** the script rejects the request

#### Scenario: Adding an environment requires no gateway
- **WHEN** a new environment is added to the configuration
- **THEN** it requires its own queues, storage and Automation host, and reuses the existing shared gateway, dashboard, plan and database

### Requirement: Gateway deployment component
The deployment script SHALL support a `gateway` component that deploys the D365 F&SC pass-through gateway Function App, and SHALL include it when `all` is requested for the `nonprod` configuration. The gateway SHALL be provisioned once for all non-production environments by the `infra` component of the `nonprod` configuration, under the name `azf-eim-gateway-wtc-nonprod` unless overridden.

#### Scenario: Gateway deployed on its own
- **WHEN** the deployment script is run for `nonprod` with the `gateway` component
- **THEN** the gateway Function App is stopped, the package's `gateway.zip` is deployed, the app is started and its triggers are synchronised

#### Scenario: Gateway included in a full deployment
- **WHEN** the deployment script is run with `all` against `nonprod`
- **THEN** the gateway is deployed together with the other shared components

#### Scenario: Gateway skipped in a full deployment elsewhere
- **WHEN** the deployment script is run with `all` against `dev`
- **THEN** the gateway is not deployed and the remaining components succeed

#### Scenario: Existing components unaffected
- **WHEN** another component is deployed on its own
- **THEN** the gateway Function App is neither created nor modified

### Requirement: Gateway hosting profile
The gateway Function App SHALL run on the shared Linux App Service plan `asp-eim-wtc-nonprod` with Always On enabled, the `dotnet-isolated` runtime version 10 and Functions version 4. It SHALL NOT be provisioned on Flex Consumption.

#### Scenario: Gateway created on the shared plan
- **WHEN** `infra` runs for `nonprod` and the gateway app does not exist
- **THEN** it is created on `asp-eim-wtc-nonprod` with runtime `dotnet-isolated` 10 on Linux

#### Scenario: Always On converged
- **WHEN** `infra` runs for `nonprod` and the gateway app exists with Always On disabled
- **THEN** Always On is enabled and no other app is recreated

#### Scenario: Flex path not used
- **WHEN** the gateway is provisioned
- **THEN** no Flex Consumption creation or scale command is issued

### Requirement: Gateway application settings
Deployment SHALL apply the gateway's required application settings when `settings` runs for `nonprod`: `FUNCTIONS_WORKER_RUNTIME`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, `EIMDB`, `Gateway__BaseUrl` (derived from the gateway's name unless configured), `Gateway__MaxRequestBytes`, `Gateway__IdempotencyRetention` and `Gateway__IdempotencyPurgeSchedule`. The dashboard API's `Gateway__BaseUrl` and `Gateway__AppName` SHALL name the same gateway.

#### Scenario: Settings applied on deployment
- **WHEN** `settings` runs for `nonprod`
- **THEN** all required gateway settings are present on `azf-eim-gateway-wtc-nonprod`, including `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`

#### Scenario: Missing required setting fails fast
- **WHEN** a required gateway setting resolves to empty from the merged configuration
- **THEN** the deployment fails with a message naming the missing setting rather than deploying an unusable app

#### Scenario: Dashboard points at the same gateway
- **WHEN** `settings` runs for `nonprod`
- **THEN** the dashboard API's `Gateway__BaseUrl` equals the gateway's `Gateway__BaseUrl` and `Gateway__AppName` is `azf-eim-gateway-wtc-nonprod`

## ADDED Requirements

### Requirement: Function Apps are deployed stopped and resynchronised
Every Function App deployment SHALL stop the app, deploy the package zip, start the app even when the deploy step failed, and synchronise its triggers, retrying the synchronisation before failing.

#### Scenario: App restarted after a failed zip deploy
- **WHEN** the zip deploy to an Automation app fails
- **THEN** the app is started again and the component fails naming the app

#### Scenario: Trigger sync retried
- **WHEN** the first trigger synchronisation call fails and a later one within six attempts succeeds
- **THEN** the component succeeds

### Requirement: Function App hosting baseline
Every Function App created by the script SHALL run on the shared App Service plan with Always On, HTTPS only, minimum TLS 1.2, FTPS disabled, workspace-based Application Insights, and tags identifying workload, client, scope and environment.

#### Scenario: Automation app hosting
- **WHEN** `infra` creates `azf-eim-automation-wtc-dev`
- **THEN** it is on `asp-eim-wtc-nonprod` with Always On, HTTPS only and Insights `appins-eim-automation-wtc-dev` linked to `log-eim-wtc-nonprod`

#### Scenario: Tags applied
- **WHEN** `infra` creates any resource
- **THEN** the resource carries the tags `eim-workload=eim`, `eim-client=wtc`, `eim-scope` and `eim-environment`

### Requirement: Kept dashboard resources are deployed in place
The `infra` component SHALL create the dashboard API and the dashboard static web app only when they do not exist, and SHALL NOT recreate, rename, move or change the plan of an existing dashboard API or static web app. The `settings`, `backend` and `frontend` components SHALL update the existing resources named by the configuration.

#### Scenario: Existing dashboard left alone by infra
- **WHEN** `infra` runs for `nonprod` and `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` exist
- **THEN** no create, plan-change or delete command is issued for either resource

#### Scenario: Dashboard code deployed in place
- **WHEN** `backend` and `frontend` run for `nonprod`
- **THEN** the API package is deployed to `wtc-dashboard-api-dev`, the SPA build is deployed to `wtc-dashboard-frontend-dev`, and the static web app keeps its existing hostname

### Requirement: Retired application settings are removed
The base configuration SHALL list retired application settings in `automation.retiredSettings`, initially `PollSchedule`. The script SHALL never write a retired setting. The `settings` component SHALL delete each retired setting that is present on a target Automation app. The drift check SHALL report a retired setting that is present as a warning and SHALL NOT fail because of it.

#### Scenario: Retired setting not written
- **WHEN** `settings` runs for `dev`
- **THEN** the settings written to `azf-eim-automation-wtc-dev` do not include `PollSchedule`

#### Scenario: Retired setting removed from a target app
- **WHEN** `settings` runs for `bat` and `azf-eim-automation-wtc-bat` has a `PollSchedule` setting
- **THEN** `PollSchedule` is deleted from that app and no other setting is deleted

#### Scenario: Retired setting reported as drift warning
- **WHEN** a code-only run's drift check finds `PollSchedule` on the target app
- **THEN** the run logs a warning naming `PollSchedule` and continues
