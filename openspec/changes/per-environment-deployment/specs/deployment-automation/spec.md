## RENAMED Requirements

- FROM: `### Requirement: Kept dashboard resources are deployed in place`
- TO: `### Requirement: Missing resources are created and existing resources are used as found`

## MODIFIED Requirements

### Requirement: A single entry point deploys the system
The system SHALL provide one deployment script, `script/deployment/Deploy-Wilson.ps1`, that deploys every component to any configured deployment configuration. A deployment configuration is a file `script/deployment/config/deploy.<environment>.json` other than `deploy.base.json`. The shipped configurations SHALL be `bat`, `test` and `prd`.

There SHALL be no `dev` configuration; development runs on a developer's machine. Deploying SHALL NOT require selecting between alternative scripts or knowing which of several scripts is current. `Deploy-Eim.ps1` and `Deploy-ZipWithPublishProfile.ps1` SHALL NOT remain in the repository.

The script SHALL accept only the components `preflight`, `infra`, `sql`, `settings`, `myip`, `schema`, `sqlusers`, `myipremove`, `gateway`, `backend`, `frontend`, `automation`, `verify` and `health`, plus `all`. It SHALL have no on-demand component.

The script SHALL deploy application code only from a package produced by `New-DeploymentPackage.ps1` and SHALL NOT build from source.

#### Scenario: Full deployment to an owner configuration
- **WHEN** an operator runs the deployment script for `bat` requesting `all`
- **THEN** every component except `myip` and `myipremove` runs in the defined order

#### Scenario: Full deployment to a using configuration
- **WHEN** an operator runs the deployment script for `test` requesting `all`
- **THEN** only `preflight`, `infra`, `settings`, `sqlusers`, `automation`, `verify` and `health` run, in the defined order

#### Scenario: Single component deployment
- **WHEN** an operator requests a single component
- **THEN** only that component is deployed and the others are left untouched

#### Scenario: Removed components rejected
- **WHEN** an operator runs the deployment script with `-Component cutover`, `-Component rollback` or `-Component remove`
- **THEN** PowerShell rejects the value as outside the component set before the script body runs, and no action is taken

#### Scenario: Unsupported environment is rejected
- **WHEN** an operator requests an environment that is not configured
- **THEN** the script stops with a message naming the unconfigured environment, and no action is taken

#### Scenario: Dev is not a configuration
- **WHEN** an operator runs the deployment script with `-Environment dev`
- **THEN** the script stops naming `dev` as unknown and listing the configurations `bat`, `prd` and `test`, and no action is taken

#### Scenario: Removed configuration keys rejected
- **WHEN** a merged configuration contains a `scope` or `sharedSuffix` key, for example through `EIM_DEPLOY_CONFIG_OVERRIDES`
- **THEN** the script stops naming the key and the configuration, and no action is taken

#### Scenario: Deployment configurations are independent of database ENVIRONMENT values
- **WHEN** the shared database holds ENVIRONMENT values that have no deployment configuration, such as `DEV2`
- **THEN** the script neither requires nor creates a configuration, Function App or queue for them, and no deployment step reads or filters the database by ENVIRONMENT value

#### Scenario: Code is taken from the package
- **WHEN** the `automation` component runs and the package has no `wilson-automation.zip`
- **THEN** the script stops naming the missing artifact and does not run `dotnet publish`

### Requirement: Resource names derive from the environment
All Azure resource names SHALL be derived through a single definition from the configuration's own `environment`, `client` and `workload`. There SHALL be no shared suffix. The patterns are:
- `{type}-{workload}-{component}-{client}-{environment}`, with the component omitted for workload-wide resources
- the resource group: `rg-{workload}-{client}-{environment}`
- storage accounts: `st{client}{workload}{shortcomponent}{environment}`

The derived names SHALL be:
- Service Bus namespace `sbns-…`, Key Vault `kv-…`, SQL server `sql-…`, database `sqldb-…`
- App Service plan `asp-…`, Log Analytics workspace `log-…`
- gateway `azf-{w}-gateway-…`, with storage `st{c}{w}gw{e}` and Insights `appins-{w}-gateway-…`
- dashboard API `app-{w}-dashboard-api-…` and static web app `swa-{w}-dashboard-…`
- Automation app `azf-{w}-automation-…`, with storage `st{c}{w}auto{e}` and Insights `appins-{w}-automation-…`
- queues prefixed `{environment}-`

Every name SHALL be overridable through the configuration's `names` section, and a blank override SHALL fall back to the derived name. A configuration with `uses` SHALL take the inherited names from its target, as defined in "A configuration can use another environment's resources".

Preflight SHALL fail, naming the resource and making no change, when a resolved name is invalid:
- storage `^[a-z0-9]{3,24}$`
- Function App longer than 32 characters
- Key Vault `^[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$`
- SQL server `^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$`

Adding an environment SHALL NOT require duplicating or editing deployment logic.

#### Scenario: Bat names
- **WHEN** the names are resolved for `bat`
- **THEN** they are:
  - from the `names` overrides in `deploy.bat.json`: resource group `d365fsc-integration-nonprod`, namespace `asb-eim-wtc-nonprod`, Key Vault `kv-d365fsc-wtc-nonprod`, SQL server `azql-wtc-sandbox`, database `eimdb`, dashboard API `wtc-dashboard-api-dev`, static web app `wtc-dashboard-frontend-dev`
  - derived: plan `asp-eim-wtc-bat`, workspace `log-eim-wtc-bat`, gateway `azf-eim-gateway-wtc-bat` with storage `stwtceimgwbat` and Insights `appins-eim-gateway-wtc-bat`, Automation app `azf-eim-automation-wtc-bat` with storage `stwtceimautobat` and Insights `appins-eim-automation-wtc-bat`, queues prefixed `bat-`

#### Scenario: Names of a using configuration
- **WHEN** the names are resolved for `test`, which has `"uses": "bat"`
- **THEN** every inherited name equals the `bat` name, and the Automation app is `azf-eim-automation-wtc-test` with storage `stwtceimautotest` and Insights `appins-eim-automation-wtc-test`, and the queues are prefixed `test-`

#### Scenario: Isolated environment derives every name
- **WHEN** a configuration `deploy.prdmck.json` contains only `{ "environment": "prdmck" }`
- **THEN** its resource group is `rg-eim-wtc-prdmck`, its namespace `sbns-eim-wtc-prdmck`, its Key Vault `kv-eim-wtc-prdmck`, its SQL server `sql-eim-wtc-prdmck`, its Automation app `azf-eim-automation-wtc-prdmck`, and no name is taken from another configuration

#### Scenario: Blank override falls back to the pattern
- **WHEN** the merged `prd` configuration has `names.sqlServer` set to an empty string
- **THEN** the SQL server name is `sql-eim-wtc-prd`

#### Scenario: Override wins over the derived name
- **WHEN** `names.dashboardApi` is `wtc-dashboard-api-dev` in the merged `bat` configuration
- **THEN** every component that addresses the dashboard API (`infra`, `settings`, `sqlusers`, `backend`, `frontend`, `health`) uses `wtc-dashboard-api-dev` and never `app-eim-dashboard-api-wtc-bat`

#### Scenario: Invalid derived storage name
- **WHEN** a configured environment name would produce a storage account name longer than 24 characters
- **THEN** preflight fails naming the storage account and makes no change

#### Scenario: Invalid Key Vault name
- **WHEN** a configured environment name would produce a Key Vault name longer than 24 characters
- **THEN** preflight fails naming the Key Vault and makes no change

#### Scenario: Adding an environment
- **WHEN** a new environment is introduced
- **THEN** it is added by providing configuration, without creating a new script

### Requirement: Components deploy in a defined order
When multiple components are deployed together, the system SHALL apply them in this order regardless of the order requested:

```
preflight, infra, sql, settings, myip, schema, sqlusers, myipremove,
gateway, backend, frontend, automation, verify, health
```

Message queues SHALL be provisioned by `infra`.

#### Scenario: Queues precede the Functions host
- **WHEN** all components are deployed to a new environment
- **THEN** the message queues exist before the Automation Function App is deployed

#### Scenario: Schema precedes application code
- **WHEN** schema and code components are deployed together
- **THEN** database migrations are applied before any application component is deployed

#### Scenario: Database users precede application code
- **WHEN** `sqlusers` and `automation` are requested together
- **THEN** the database users exist before the Automation code is deployed

#### Scenario: API precedes the frontend
- **WHEN** all components are deployed to an owner configuration
- **THEN** the dashboard API exists before the frontend is deployed and linked to it

#### Scenario: Requested order is ignored
- **WHEN** an operator requests `health, automation, settings`
- **THEN** the script runs `preflight`, `settings`, `automation`, then `health`

### Requirement: Gateway deployment component
The deployment script SHALL support a `gateway` component that deploys the D365 F&SC pass-through gateway Function App. `all` SHALL include it for an owner configuration, meaning a configuration without `uses`. The gateway SHALL be provisioned by that configuration's `infra`, under its resolved `gatewayApp` name. A configuration with `uses` SHALL use its target's gateway and SHALL NOT deploy one.

#### Scenario: Gateway deployed on its own
- **WHEN** the deployment script is run for `bat` with the `gateway` component
- **THEN** `azf-eim-gateway-wtc-bat` is stopped, the package's `gateway.zip` is deployed, the app is started and its triggers are synchronised

#### Scenario: Gateway included in a full deployment
- **WHEN** the deployment script is run with `all` against `prd`
- **THEN** `azf-eim-gateway-wtc-prd` is deployed together with the dashboard and Automation components

#### Scenario: Gateway refused for a using configuration
- **WHEN** the `gateway` component is requested against `test`
- **THEN** the script refuses before any Azure call, naming `bat` as the configuration that owns the gateway

#### Scenario: Existing components unaffected
- **WHEN** another component is deployed on its own
- **THEN** the gateway Function App is neither created nor modified

### Requirement: Gateway hosting profile
The gateway Function App SHALL run on its owner configuration's Linux App Service plan with Always On enabled, the `dotnet-isolated` runtime version 10 and Functions version 4. It SHALL NOT be provisioned on Flex Consumption.

#### Scenario: Gateway created on the owner's plan
- **WHEN** `infra` runs for `bat` and the gateway app does not exist
- **THEN** `azf-eim-gateway-wtc-bat` is created on `asp-eim-wtc-bat` with runtime `dotnet-isolated` 10 on Linux

#### Scenario: Always On converged on a managed gateway
- **WHEN** `infra` runs for `bat` and `azf-eim-gateway-wtc-bat` exists, carries the `eim-environment` tag and has Always On disabled
- **THEN** Always On is enabled and no other app is recreated

#### Scenario: Flex path not used
- **WHEN** the gateway is provisioned
- **THEN** no Flex Consumption creation or scale command is issued

### Requirement: Gateway application settings
When `settings` runs for an owner configuration, deployment SHALL apply the gateway's required application settings:
- `FUNCTIONS_WORKER_RUNTIME`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`
- `EIMDB`, as the managed-identity connection string defined in `sql-identity-access`
- `Gateway__BaseUrl`, derived from the gateway's name unless configured
- `Gateway__MaxRequestBytes`
- `Gateway__IdempotencyRetention`
- `Gateway__IdempotencyPurgeSchedule`

The dashboard API's `Gateway__BaseUrl` and `Gateway__AppName` SHALL name the same gateway.

#### Scenario: Settings applied on deployment
- **WHEN** `settings` runs for `bat`
- **THEN** all required gateway settings are present on `azf-eim-gateway-wtc-bat`, including `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated` and an `EIMDB` value with `Authentication=Active Directory Managed Identity` and no password

#### Scenario: Missing required setting fails fast
- **WHEN** a required gateway setting resolves to empty from the merged configuration
- **THEN** the deployment fails with a message naming the missing setting rather than deploying an unusable app

#### Scenario: Dashboard points at the same gateway
- **WHEN** `settings` runs for `bat`
- **THEN** the dashboard API's `Gateway__BaseUrl` equals the gateway's `Gateway__BaseUrl`, and `Gateway__AppName` is `azf-eim-gateway-wtc-bat`

### Requirement: Function App hosting baseline
Every Function App created by the script SHALL have:
- its owner configuration's App Service plan
- Always On, HTTPS only, minimum TLS 1.2 and FTPS disabled
- workspace-based Application Insights linked to the owner's Log Analytics workspace
- a system-assigned managed identity

Every resource the script creates SHALL carry the tags `eim-workload`, `eim-client` and `eim-environment`, where `eim-environment` is the configuration that created it. There SHALL be no `eim-scope` tag.

`infra` SHALL converge the baseline only on Function Apps that carry the `eim-environment` tag. It SHALL assign the system identity on every app the configuration deploys, tagged or not.

#### Scenario: Automation app hosting
- **WHEN** `infra` creates `azf-eim-automation-wtc-test`
- **THEN** it is on `asp-eim-wtc-bat` with Always On, HTTPS only, a system-assigned identity and Insights `appins-eim-automation-wtc-test` linked to `log-eim-wtc-bat`

#### Scenario: Tags applied
- **WHEN** `infra` creates any resource for `test`
- **THEN** the resource carries `eim-workload=eim`, `eim-client=wtc` and `eim-environment=test`, and no `eim-scope` tag

### Requirement: Missing resources are created and existing resources are used as found
`infra` and `sql` SHALL create each resource the configuration deploys only when it does not exist.

For an owner configuration, that covers:
- the resource group
- the Service Bus namespace (`serviceBus.sku`)
- the Key Vault, with access-policy authorisation, recovering it when it is soft-deleted
- the App Service plan (`appServicePlan.sku`)
- the Log Analytics workspace (`logAnalytics.retentionDays`)
- the gateway with its storage and Insights
- the dashboard API and the static web app
- the SQL server and database, as defined in `sql-identity-access`

For every configuration, that covers its queues and its Automation app with its storage and Insights.

An existing resource SHALL be used as found. The script SHALL NOT recreate, rename, move, retag or change the SKU, tier, plan, firewall, authentication or site configuration of an existing resource that does not carry the `eim-environment` tag.

The only changes the script SHALL make to such a resource are:
- assigning a system-assigned managed identity to an app the configuration deploys
- adding a Key Vault access policy with secret `get` for that identity
- deploying code and writing the application settings the configuration defines

`settings`, `backend` and `frontend` SHALL update the existing dashboard resources named by the configuration in place.

#### Scenario: Existing dashboard left alone by infra
- **WHEN** `infra` runs for `bat` and `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev` exist without an `eim-environment` tag
- **THEN** no create, plan-change, tag, site-configuration or delete command is issued for either resource, and the only change to `wtc-dashboard-api-dev` is a system-assigned identity when it has none

#### Scenario: Dashboard code deployed in place
- **WHEN** `backend` and `frontend` run for `bat`
- **THEN** the API package is deployed to `wtc-dashboard-api-dev`, the SPA build is deployed to `wtc-dashboard-frontend-dev`, and the static web app keeps its existing hostname

#### Scenario: Existing namespace keeps its SKU
- **WHEN** `infra` runs for `bat`, `asb-eim-wtc-nonprod` exists with a SKU different from `serviceBus.sku`
- **THEN** no update command is issued for the namespace, and the `bat-*` queues are created only where missing

#### Scenario: Isolated environment created from nothing
- **WHEN** `infra` and then `sql` run for `prdmck` and none of its resources exist
- **THEN** `rg-eim-wtc-prdmck`, `sbns-eim-wtc-prdmck` with the `prdmck-*` queues, `kv-eim-wtc-prdmck`, `asp-eim-wtc-prdmck`, `log-eim-wtc-prdmck`, the gateway, the dashboard API and static web app, the Automation app, `sql-eim-wtc-prdmck` and `sqldb-eim-wtc-prdmck` are created, each tagged `eim-environment=prdmck`

#### Scenario: Second run creates nothing
- **WHEN** `infra` runs twice in succession for `prd`
- **THEN** the second run issues no create command

#### Scenario: Key Vault access for app identities
- **WHEN** `infra` runs for `test`
- **THEN** `azf-eim-automation-wtc-test` has a system-assigned identity, and `kv-d365fsc-wtc-nonprod` has an access policy granting that identity secret `get`

### Requirement: Preflight checks the foundation resources
For a configuration with `uses`, preflight SHALL fail, changing nothing, when any resource it takes from its target does not exist or cannot be read. That covers:
- the resource group
- the Service Bus namespace
- the Key Vault
- the App Service plan
- the Log Analytics workspace
- the SQL server and the database

Each missing resource SHALL be named, and the message SHALL state that `infra` and `sql` must run for the target configuration first.

For every configuration, preflight SHALL fail, naming the vault, when the Key Vault exists and uses RBAC authorisation instead of access policies.

For an owner configuration, missing foundation resources SHALL NOT fail preflight; `infra` and `sql` create them.

#### Scenario: Owner not yet deployed
- **WHEN** preflight runs for `test` with `infra` requested and `asp-eim-wtc-bat` does not exist
- **THEN** preflight fails naming `asp-eim-wtc-bat` and stating that `infra` and `sql` must run for `bat` first, and no resource is created

#### Scenario: Owner foundation missing is not a failure
- **WHEN** preflight runs for `prd` with `infra` requested and `sbns-eim-wtc-prd` does not exist
- **THEN** preflight reports that `infra` will create it and does not fail

#### Scenario: RBAC-mode vault
- **WHEN** preflight runs for `bat` and `kv-d365fsc-wtc-nonprod` has RBAC authorisation enabled
- **THEN** preflight fails naming the vault and stating that access-policy authorisation is required, and nothing is changed

### Requirement: Dashboard simulation setting
The configuration key `dashboard.simulationEnabled` SHALL control simulation on the dashboard API. It is `true` in `deploy.base.json` and `false` in `deploy.prd.json`.

When `settings` runs for an owner configuration:
- It SHALL always write `Simulation__Enabled` as `true` or `false`.
- When simulation is enabled, it SHALL also write `Simulation__ServiceBusConnectionString` as the Key Vault reference defined in "The Service Bus connection is held in the Key Vault", and the four `Simulation__*` queue settings derived from `dashboard.simulationQueuePrefix`.
- It SHALL fail before writing anything when that prefix is blank or a placeholder, or is neither the owner's queue prefix nor the queue prefix of a configuration that uses the owner.
- When simulation is disabled, it SHALL write no other `Simulation__*` setting, and SHALL delete any other `Simulation__*` setting present on the dashboard API.

The preflight drift check SHALL treat `Simulation__Enabled` as a configured setting.

#### Scenario: Non-production keeps simulation
- **WHEN** `settings` runs for `bat`
- **THEN** `wtc-dashboard-api-dev` gets:
  - `Simulation__Enabled=true`
  - `Simulation__ServiceBusConnectionString` as a `@Microsoft.KeyVault(...)` reference to `eim-servicebus` in `kv-d365fsc-wtc-nonprod`
  - the four queue settings prefixed `bat-`

#### Scenario: Production has no simulation settings
- **WHEN** `settings` runs for `prd`
- **THEN** `app-eim-dashboard-api-wtc-prd` gets `Simulation__Enabled=false` and no other `Simulation__*` setting

#### Scenario: Stale simulation settings removed
- **WHEN** `settings` runs for `prd` and the dashboard API has `Simulation__ServiceBusConnectionString`
- **THEN** that setting is deleted, and no setting outside `Simulation__*` and `Gateway__Applications__*` is deleted

#### Scenario: Enabled without a queue prefix
- **WHEN** `dashboard.simulationEnabled` is `true` and `dashboard.simulationQueuePrefix` is empty
- **THEN** `settings` fails naming `dashboard.simulationQueuePrefix`, and no setting is written

#### Scenario: Prefix of an unrelated environment
- **WHEN** `settings` runs for `bat` with `dashboard.simulationQueuePrefix` `prd`
- **THEN** `settings` fails naming `dashboard.simulationQueuePrefix` and the allowed prefixes `bat` and `test`, and no setting is written

#### Scenario: Code-only run before the setting exists
- **WHEN** a code-only run deploys `backend` to `bat` and `wtc-dashboard-api-dev` has no `Simulation__Enabled`
- **THEN** preflight fails naming `Simulation__Enabled` and the app, and states that a manual run with `applySettings` is needed

## ADDED Requirements

### Requirement: A configuration can use another environment's resources
A configuration that sets `"uses": "<environment>"` SHALL take its target's resolved names for these resources:
- `resourceGroup`, `serviceBusNamespace`, `keyVault`
- `sqlServer`, `sqlDatabase`
- `appServicePlan`, `logAnalytics`
- `gatewayApp`, `gatewayStorage`, `gatewayInsights`
- `dashboardApi`, `staticWebApp`

Only resource names SHALL be inherited. Settings such as triggers, simulation, Entra values and schedules SHALL come from the configuration's own merge of `deploy.base.json` and its own file.

A configuration with `uses` SHALL deploy only:
- its own queues
- its own Automation app with its storage and Insights
- that app's settings, identity, Key Vault access and database user

It MAY request only `preflight`, `infra`, `settings`, `myip`, `sqlusers`, `myipremove`, `automation`, `verify` and `health`. Requesting `sql`, `schema`, `gateway`, `backend` or `frontend` SHALL be refused before any Azure call, naming the target configuration.

Preflight SHALL fail before any Azure call, naming the configuration and the cause, when:
- `uses` names a configuration that does not exist
- `uses` names the configuration itself, or a configuration that itself has `uses`
- a configuration with `production` true uses a configuration with `production` false
- the configuration sets a `names` key it inherits

`deploy.test.json` SHALL set `"uses": "bat"`. `deploy.bat.json` and `deploy.prd.json` SHALL NOT set `uses`.

#### Scenario: Test uses bat
- **WHEN** `infra` runs for `test`
- **THEN** it creates only the missing `test-*` queues, `stwtceimautotest`, `appins-eim-automation-wtc-test` and `azf-eim-automation-wtc-test` on `asp-eim-wtc-bat`, and creates no plan, workspace, gateway, dashboard, Key Vault, namespace, SQL server or database

#### Scenario: Owner-only component refused
- **WHEN** `schema` or `frontend` is requested against `test`
- **THEN** the script refuses before any Azure call, naming `bat` as the owning configuration

#### Scenario: Unknown target
- **WHEN** a configuration sets `"uses": "uat"` and no `deploy.uat.json` exists
- **THEN** preflight fails naming `uat` as unknown, and nothing is changed

#### Scenario: Chain refused
- **WHEN** `deploy.uat.json` sets `"uses": "test"` and `test` uses `bat`
- **THEN** preflight fails for `uat`, naming `test` and stating that a used configuration cannot itself use another

#### Scenario: Self reference refused
- **WHEN** `deploy.test.json` sets `"uses": "test"`
- **THEN** preflight fails naming `test`, and nothing is changed

#### Scenario: Production using non-production refused
- **WHEN** `deploy.prd.json` sets `"uses": "bat"`
- **THEN** preflight fails naming `prd` and `bat`, and nothing is changed

#### Scenario: Inherited name overridden
- **WHEN** `deploy.test.json` uses `bat` and sets `names.keyVault`
- **THEN** preflight fails naming `names.keyVault` and `bat`, and nothing is changed

#### Scenario: Settings are not inherited
- **WHEN** `deploy.bat.json` sets `automation.keepDisabled` to `["RetrySweepHandler"]` and `deploy.test.json` does not set it
- **THEN** the merged `test` configuration has the base value of `automation.keepDisabled`

### Requirement: The deployment script deletes no Azure resource
`Deploy-Wilson.ps1` SHALL NOT delete any Azure resource, database user, queue or database row in any configuration. The only deletions it SHALL perform are:
- app settings it owns on the apps it deploys:
  - the names in `automation.retiredSettings`
  - `Simulation__*` settings that the dashboard simulation setting no longer writes
  - `Gateway__Applications__*` settings
- the SQL firewall rule that `myip` added, deleted by `myipremove`
- `app-outbound-*` firewall rules on a SQL server that carries the `eim-environment` tag, for addresses no app of the configuration has any more

Deployment configurations SHALL describe only the resources the deployment uses. No configuration SHALL have a `legacy` section, a `gateway.legacyVerificationTargets` key or a `retiredAutomationApps` key. The script SHALL NOT read, change or report any app that its configuration does not name.

#### Scenario: No resource delete command
- **WHEN** `Deploy-Wilson.ps1` is inspected
- **THEN** it contains no `az resource delete` call, and no `delete` call for a Function App, web app, App Service plan, storage account, Application Insights component, static web app, Key Vault, SQL server, database or Service Bus namespace or queue
- **AND** its only `delete` calls are `config appsettings delete` and `sql server firewall-rule delete`, and its SQL text contains no `DROP`

#### Scenario: Configurations describe only current resources
- **WHEN** `deploy.base.json` and every `deploy.<name>.json` are merged
- **THEN** none has a `legacy` key, a `gateway.legacyVerificationTargets` key or a `retiredAutomationApps` key

#### Scenario: Verify reports only configured apps
- **WHEN** `verify` runs for `bat`
- **THEN** it reports `azf-eim-gateway-wtc-bat`, `wtc-dashboard-api-dev`, `wtc-dashboard-frontend-dev` and `azf-eim-automation-wtc-bat` and no other app, and when it runs for `test` it reports only `azf-eim-automation-wtc-test`

#### Scenario: Unmanaged server firewall untouched
- **WHEN** `sql` runs for `bat` and `azql-wtc-sandbox` has no `eim-environment` tag
- **THEN** no firewall rule on `azql-wtc-sandbox` is created or deleted

### Requirement: The Service Bus connection is held in the Key Vault
When `settings` runs for an owner configuration, it SHALL read the primary connection string of the namespace's `RootManageSharedAccessKey` rule. It SHALL write that string to the owner's Key Vault as the secret `eim-servicebus`, but only when the secret is missing or its value differs. The value SHALL pass through a temporary file that is deleted afterwards, and SHALL never appear in output.

The application settings that need the connection SHALL hold a versionless Key Vault reference, not the value:
- `ServiceBus` on every Automation app
- `Simulation__ServiceBusConnectionString` on the dashboard API, when simulation is enabled

The reference SHALL have the form `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/eim-servicebus/)`.

When `settings` runs for a configuration with `uses`, it SHALL fail before writing anything if `eim-servicebus` does not exist in the target's Key Vault, stating that `settings` must run for the target first.

`verify` SHALL report the Key Vault reference status of these settings. `health` SHALL fail, naming the app and setting, when a reference is not resolved.

#### Scenario: Secret written once
- **WHEN** `settings` runs twice for `bat` and the namespace keys did not change
- **THEN** `eim-servicebus` in `kv-d365fsc-wtc-nonprod` gets one new version on the first run at most, and none on the second

#### Scenario: Apps reference the secret
- **WHEN** `settings` runs for `test` after `settings` ran for `bat`
- **THEN** `azf-eim-automation-wtc-test` has `ServiceBus=@Microsoft.KeyVault(SecretUri=https://kv-d365fsc-wtc-nonprod.vault.azure.net/secrets/eim-servicebus/)`, and no setting holds a Service Bus connection string value

#### Scenario: Target secret missing
- **WHEN** `settings` runs for `test` and `kv-d365fsc-wtc-nonprod` has no `eim-servicebus` secret
- **THEN** `settings` fails stating that `settings` must run for `bat` first, and no setting is written

#### Scenario: Unresolved reference fails health
- **WHEN** `health` runs for `bat` and the `ServiceBus` reference on `azf-eim-automation-wtc-bat` is not resolved
- **THEN** the stage fails naming `azf-eim-automation-wtc-bat` and `ServiceBus`, without any secret value

#### Scenario: Preview names the secret only
- **WHEN** `settings` runs for `bat` with `-WhatIf`
- **THEN** the preview names the secret `eim-servicebus` and the vault, and prints no connection string

## REMOVED Requirements

### Requirement: Shared components are not deployed per environment
**Reason**: The user dropped the shared scope and the stamp concept. A configuration either owns its resources or takes them from another configuration with `uses`.
**Migration**: "A configuration can use another environment's resources" replaces this requirement. `deploy.nonprod.json` is folded into `deploy.bat.json`, and `test` uses `bat`. Components that the shared configuration owned (`gateway`, `backend`, `frontend`, `schema`) now run for `bat`.

### Requirement: Each stamp has exactly one shared configuration
**Reason**: There are no stamps or shared configurations. `deploy.nonprod.json` and `deploy.prdshared.json` are deleted.
**Migration**: The `uses` checks in "A configuration can use another environment's resources" and the isolation check in `production-stamp` replace it. Production is the single `prd` configuration.

### Requirement: Secrets point at the configured resources
**Reason**: There are no deployment secrets left to check. `EIM_DEPLOY_EIMDB` is replaced by Entra authentication, and `EIM_DEPLOY_SERVICEBUS` by the Key Vault secret that the script fills from the configured namespace. Every value is therefore derived from the configured names.
**Migration**: Remove `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` from the variable groups after the first successful run. See `sql-identity-access` and "The Service Bus connection is held in the Key Vault".
