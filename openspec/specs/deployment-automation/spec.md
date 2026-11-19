# deployment-automation Specification

## Purpose
TBD - created by archiving change consolidate-deployment-scripts. Update Purpose after archive.
## Requirements

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

Every name SHALL be overridable through the configuration's `names` section, and a blank override SHALL fall back to the derived name. A configuration with `uses` SHALL take its names from its target, as defined in "A configuration can use another environment's resources":
- it SHALL take the locked names from its target;
- it SHALL take an overridable name from its target unless it sets that name. A blank or missing override SHALL take the target's name, never the derived pattern.

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
- **WHEN** the names are resolved for the shipped `test`, which has `"uses": "bat"` and no `names`
- **THEN**:
  - every locked and overridable name equals the `bat` name, including Key Vault `kv-d365fsc-wtc-nonprod`, plan `asp-eim-wtc-bat` and workspace `log-eim-wtc-bat`
  - the Automation app is `azf-eim-automation-wtc-test`, with storage `stwtceimautotest` and Insights `appins-eim-automation-wtc-test`
  - the queues are prefixed `test-`

#### Scenario: Using configuration with its own Key Vault
- **WHEN** the names are resolved for `test` with `names.keyVault` set to `kv-eim-wtc-test`
- **THEN** the Key Vault is `kv-eim-wtc-test`, and every other inherited name equals the `bat` name, including the plan `asp-eim-wtc-bat` and the workspace `log-eim-wtc-bat`

#### Scenario: Blank overridable name inherits
- **WHEN** the names are resolved for `test` with `names.logAnalytics` set to an empty string
- **THEN** the workspace is `log-eim-wtc-bat`, not `log-eim-wtc-test`

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

#### Scenario: Invalid overridden Key Vault name
- **WHEN** `test` sets `names.keyVault` to `kv_eim_wtc_test`
- **THEN** preflight fails naming the Key Vault `kv_eim_wtc_test` and makes no change

#### Scenario: Adding an environment
- **WHEN** a new environment is introduced
- **THEN** it is added by providing configuration, without creating a new script

#### Scenario: Names derived consistently
- **WHEN** the script targets environment `bat`
- **THEN** it uses Function App `azf-eim-automation-wtc-bat`, storage `stwtceimautobat`, Insights `appins-eim-automation-wtc-bat` and queues prefixed `bat-`

#### Scenario: Non-conforming resource name
- **WHEN** an existing resource does not follow the naming pattern, such as SQL server `azql-wtc-sandbox`, resource group `d365fsc-integration-nonprod`, dashboard API `wtc-dashboard-api-dev` or static web app `wtc-dashboard-frontend-dev`
- **THEN** its name is supplied as an explicit `names` override rather than requiring a change to deployment logic

### Requirement: No credentials appear in committed files
The deployment script and any committed configuration SHALL NOT contain connection strings, keys, passwords, or other credentials. Credentials SHALL be supplied from a source excluded from version control.

#### Scenario: Committed files are free of credentials
- **WHEN** the deployment script and its committed example configuration are inspected
- **THEN** no credential value is present

#### Scenario: Missing configuration is reported clearly
- **WHEN** the configuration for the requested environment is absent
- **THEN** the script stops and identifies what is missing, without falling back to an embedded or default value

#### Scenario: Values may be supplied without a file
- **WHEN** the script runs in an automated context where a configuration file is not available
- **THEN** the required values can be supplied by environment variables instead

### Requirement: Deployment is idempotent
Running the deployment script repeatedly against an environment SHALL be safe. Existing resources SHALL NOT be recreated, and unchanged configuration SHALL NOT cause disruption.

#### Scenario: Second run creates nothing
- **WHEN** the script is run twice in succession against the same environment
- **THEN** the second run creates no resource that the first created

#### Scenario: Existing resources are preserved
- **WHEN** the script runs against an environment whose resources already exist
- **THEN** those resources are updated in place rather than recreated

### Requirement: Intended actions can be previewed
The system SHALL provide a mode that reports the actions the deployment would take without performing them.

#### Scenario: Preview performs no change
- **WHEN** the script is run in preview mode
- **THEN** it reports the resources it would create or update, and makes no change

#### Scenario: Preview reports derived names
- **WHEN** the script is run in preview mode
- **THEN** the resource names it would target are reported, so they can be checked before any action

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

### Requirement: A failed step halts the deployment
The system SHALL stop on the first failing step, report which step failed, and exit with a non-zero status. It SHALL NOT continue to subsequent components after a failure.

#### Scenario: Failure stops subsequent components
- **WHEN** a component fails to deploy during a multi-component run
- **THEN** no subsequent component is attempted

#### Scenario: Failure is reported for automation
- **WHEN** a deployment step fails
- **THEN** the script exits with a non-zero status and names the failed step

### Requirement: Environment-independent locations are configurable
The resource group and Service Bus namespace SHALL be supplied by configuration rather than assumed constant, so that an environment residing in a different location can be added without altering deployment logic.

#### Scenario: Location comes from configuration
- **WHEN** the script targets an environment
- **THEN** the resource group and Service Bus namespace it uses come from that environment's configuration

#### Scenario: An environment in a different location can be added
- **WHEN** an environment residing in a different resource group and Service Bus namespace is configured
- **THEN** it is deployable without changing the naming or deployment logic

### Requirement: Entra configuration is applied consistently
The system SHALL apply one Entra tenant, client, and API scope per environment to both the frontend build and the API configuration, so the two cannot diverge.

#### Scenario: Frontend and API agree
- **WHEN** an environment's Entra configuration changes and both components are deployed
- **THEN** the frontend build and the API configuration reflect the same tenant, client, and scope

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

### Requirement: One path provisions Service Bus queues
Queue provisioning SHALL have a single path in `Deploy-Wilson.ps1`. That path SHALL read the queues from the `serviceBusQueues` list of the merged configuration (`deploy.base.json`, then `deploy.<name>.json`, then overrides) and derive every queue name as `<queuePrefix>-<name>`. Templates that enumerate queue names for specific environments SHALL NOT be maintained alongside it, and the script SHALL NOT hard-code the queue list.

An environment file that sets `serviceBusQueues` SHALL replace the whole list, as for every other list in the configuration.

#### Scenario: Queue names derived from the environment
- **WHEN** queues are provisioned for an environment
- **THEN** every queue name is that environment's queue prefix, a hyphen, and the entry's `name`, rather than a name written per environment

#### Scenario: Shipped queues resolve to today's names
- **WHEN** the queues are resolved for `bat`, `test` and `prd` from the committed configuration
- **THEN** they are, in order, `<prefix>-d365fsc-dataevent`, `<prefix>-d365fsc-businessevents`, `<prefix>-d365fsc-to-capture`, `<prefix>-capture-to-d365fsc` and `<prefix>-capture-to-d365fsc-response`, with prefixes `bat`, `test` and `prd`

#### Scenario: Adding an environment needs no template edit
- **WHEN** a new environment is added
- **THEN** its queues are created by the deployment script from `serviceBusQueues` without editing an infrastructure template or the script

#### Scenario: Adding a queue needs no script edit
- **WHEN** an override sets `serviceBusQueues` to the five base entries plus `{ "name": "extra-queue", "setting": "ExtraQueue" }` for `prdmck`, and nothing exists
- **THEN** the infra plan includes `create queue prdmck-extra-queue`, and the Automation settings include `ExtraQueue = prdmck-extra-queue` alongside the five existing settings with unchanged values

#### Scenario: An environment list replaces the base list
- **WHEN** an override sets `serviceBusQueues` to a list with only the `d365fsc-dataevent` entry
- **THEN** the resolved queues are that single entry, and preflight fails (see "Service Bus queue configuration is validated before deployment")

### Requirement: Function Apps are deployed stopped and resynchronised
Every Function App deployment SHALL stop the app, deploy the package zip, start the app even when the deploy step failed, and synchronise its triggers, retrying the synchronisation before failing.

#### Scenario: App restarted after a failed zip deploy
- **WHEN** the zip deploy to an Automation app fails
- **THEN** the app is started again and the component fails naming the app

#### Scenario: Trigger sync retried
- **WHEN** the first trigger synchronisation call fails and a later one within six attempts succeeds
- **THEN** the component succeeds

### Requirement: Function App hosting baseline
Every Function App created by the script SHALL have:
- its owner configuration's App Service plan, also for a configuration with `uses`
- Always On, HTTPS only, minimum TLS 1.2 and FTPS disabled
- workspace-based Application Insights linked to the Log Analytics workspace resolved for its configuration: the owner's workspace, unless a using configuration owns its workspace
- a system-assigned managed identity

Every resource the script creates SHALL carry the tags `eim-workload`, `eim-client` and `eim-environment`, where `eim-environment` is the configuration that created it. There SHALL be no `eim-scope` tag.

`infra` SHALL converge the baseline only on Function Apps that carry the `eim-environment` tag. It SHALL assign the system identity on every app the configuration deploys, tagged or not.

`infra` SHALL NOT relink existing Insights to another workspace. When a using configuration owns its workspace and its existing Automation Insights is linked to a different one, `infra` SHALL warn, naming the Insights resource, the current workspace and the resolved workspace. It SHALL still succeed.

#### Scenario: Automation app hosting
- **WHEN** `infra` creates `azf-eim-automation-wtc-test` for the shipped `test`
- **THEN** it is on `asp-eim-wtc-bat` with Always On, HTTPS only, a system-assigned identity and Insights `appins-eim-automation-wtc-test` linked to `log-eim-wtc-bat`

#### Scenario: Automation Insights in an owned workspace
- **WHEN** `infra` creates `appins-eim-automation-wtc-test` for `test` with `names.logAnalytics` set to `log-eim-wtc-test`
- **THEN** the Insights resource is linked to `log-eim-wtc-test`, `azf-eim-automation-wtc-test` is on `asp-eim-wtc-bat`, and `appins-eim-gateway-wtc-bat` stays linked to `log-eim-wtc-bat`

#### Scenario: Existing Insights on the target's workspace
- **WHEN** `infra` runs for `test` with `names.logAnalytics` set to `log-eim-wtc-test`, and `appins-eim-automation-wtc-test` already exists linked to `log-eim-wtc-bat`
- **THEN** `infra` warns naming `appins-eim-automation-wtc-test`, `log-eim-wtc-bat` and `log-eim-wtc-test`, does not relink it, and succeeds

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

For a configuration with `uses`, that covers each of the following it owns, in the target's resource group, created as for an owner:
- the Key Vault
- the Log Analytics workspace

A configuration with `uses` SHALL never create an App Service plan.

For every configuration, that covers its queues and its Automation app with its storage and Insights.

An existing resource SHALL be used as found. The script SHALL NOT recreate, rename, move, retag or change the SKU, tier, plan, firewall, authentication or site configuration of an existing resource that does not carry the `eim-environment` tag.

The only changes the script SHALL make to such a resource are:
- assigning a system-assigned managed identity to an app the configuration deploys
- adding a Key Vault access policy with secret `get` for that identity
- writing the `eim-servicebus` secret to a Key Vault the configuration owns
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
- **WHEN** `infra` runs twice in succession for `prd`, or for `test` with `names.keyVault` set to `kv-eim-wtc-test`
- **THEN** the second run issues no create command

#### Scenario: Owned vault exists already
- **WHEN** `infra` runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`, and `kv-eim-wtc-test` exists without an `eim-environment` tag
- **THEN** no create, tag or update command is issued for the vault, and the only change to it is the access policy for `azf-eim-automation-wtc-test`

#### Scenario: Key Vault access for app identities
- **WHEN** `infra` runs for the shipped `test`
- **THEN** `azf-eim-automation-wtc-test` has a system-assigned identity, and `kv-d365fsc-wtc-nonprod` has an access policy granting that identity secret `get`

#### Scenario: Key Vault access on an owned vault
- **WHEN** `infra` runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`
- **THEN** `kv-eim-wtc-test` has an access policy granting the identity of `azf-eim-automation-wtc-test` secret `get`, and no access policy on `kv-d365fsc-wtc-nonprod` is added, changed or removed

### Requirement: Retired application settings are removed
The base configuration SHALL list retired application settings in `automation.retiredSettings`, initially `PollSchedule`. The script SHALL never write a retired setting. The `settings` component SHALL delete each retired setting that is present on a target Automation app. The drift check SHALL report a retired setting that is present as a warning and SHALL NOT fail because of it.

#### Scenario: Retired setting not written
- **WHEN** `settings` runs for `test`
- **THEN** the settings written to `azf-eim-automation-wtc-test` do not include `PollSchedule`

#### Scenario: Retired setting removed from a target app
- **WHEN** `settings` runs for `bat` and `azf-eim-automation-wtc-bat` has a `PollSchedule` setting
- **THEN** `PollSchedule` is deleted from that app and no other setting is deleted

#### Scenario: Retired setting reported as drift warning
- **WHEN** a code-only run's drift check finds `PollSchedule` on the target app
- **THEN** the run logs a warning naming `PollSchedule` and continues

### Requirement: Preflight checks the foundation resources
For a configuration with `uses`, preflight SHALL fail, changing nothing, when a resource it takes from its target does not exist or cannot be read. That covers:
- the resource group
- the Service Bus namespace
- the Key Vault, unless the configuration owns it
- the App Service plan
- the Log Analytics workspace, unless the configuration owns it
- the SQL server and the database

Each missing resource SHALL be named, and the message SHALL state that `infra` and `sql` must run for the target configuration first.

A missing Key Vault or Log Analytics workspace that the configuration owns SHALL NOT fail preflight. Preflight SHALL report that `infra` will create it.

For every configuration, preflight SHALL fail, naming the vault, when the resolved Key Vault exists and uses RBAC authorisation instead of access policies. That includes a vault the configuration owns.

For an owner configuration, missing foundation resources SHALL NOT fail preflight; `infra` and `sql` create them.

#### Scenario: Owner not yet deployed
- **WHEN** preflight runs for `test` with `infra` requested and `asp-eim-wtc-bat` does not exist
- **THEN** preflight fails naming `asp-eim-wtc-bat` and stating that `infra` and `sql` must run for `bat` first, and no resource is created

#### Scenario: Target plan missing while the vault is owned
- **WHEN** preflight runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`, and `asp-eim-wtc-bat` does not exist
- **THEN** preflight fails naming `asp-eim-wtc-bat` and `bat`, and no resource is created

#### Scenario: Owned vault not yet created
- **WHEN** preflight runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`, `kv-eim-wtc-test` does not exist, and every `bat` resource exists
- **THEN** preflight reports that `infra` will create `kv-eim-wtc-test` and does not fail

#### Scenario: Owner foundation missing is not a failure
- **WHEN** preflight runs for `prd` with `infra` requested and `sbns-eim-wtc-prd` does not exist
- **THEN** preflight reports that `infra` will create it and does not fail

#### Scenario: RBAC-mode vault
- **WHEN** preflight runs for `bat` and `kv-d365fsc-wtc-nonprod` has RBAC authorisation enabled
- **THEN** preflight fails naming the vault and stating that access-policy authorisation is required, and nothing is changed

#### Scenario: RBAC-mode owned vault
- **WHEN** preflight runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`, and `kv-eim-wtc-test` has RBAC authorisation enabled
- **THEN** preflight fails naming `kv-eim-wtc-test` and stating that access-policy authorisation is required, and nothing is changed

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

### Requirement: A configuration can use another environment's resources
A configuration that sets `"uses": "<environment>"` SHALL take its target's resolved names for these **locked** resources:
- `resourceGroup`, `serviceBusNamespace`
- `sqlServer`, `sqlDatabase`
- `appServicePlan`
- `gatewayApp`, `gatewayStorage`, `gatewayInsights`
- `dashboardApi`, `staticWebApp`

For these **overridable** resources, the configuration SHALL take its target's resolved names unless it sets its own:
- `keyVault`, `logAnalytics`

An overridable name is **owned** by the using configuration when it is not blank and differs, ignoring case, from the target's resolved name. An override equal to the target's name SHALL behave exactly as an inherited name.

Only resource names SHALL be inherited. Settings such as triggers, simulation, Entra values, schedules and `logAnalytics.retentionDays` SHALL come from the configuration's own merge of `deploy.base.json` and its own file.

A configuration with `uses` SHALL deploy only:
- its own queues
- its own Automation app with its storage and Insights, on the target's App Service plan
- that app's settings, identity, Key Vault access and database user
- the Key Vault and Log Analytics workspace it owns, in the target's resource group

The target's apps SHALL keep their own names. The target's dashboard API SHALL keep referencing the target's Key Vault, and the target's gateway SHALL keep the target's workspace, whatever a using configuration owns.

It MAY request only `preflight`, `infra`, `settings`, `myip`, `sqlusers`, `myipremove`, `automation`, `verify` and `health`. Requesting `sql`, `schema`, `gateway`, `backend` or `frontend` SHALL be refused before any Azure call, naming the target configuration.

Preflight SHALL fail before any Azure call, naming the configuration and the cause, when:
- `uses` names a configuration that does not exist
- `uses` names the configuration itself, or a configuration that itself has `uses`
- a configuration with `production` true uses a configuration with `production` false
- the configuration sets a locked `names` key to a non-blank value

`deploy.test.json` SHALL set `"uses": "bat"` and SHALL NOT set any locked `names` key. `deploy.bat.json` and `deploy.prd.json` SHALL NOT set `uses`.

#### Scenario: Test uses bat
- **WHEN** `infra` runs for the shipped `test`
- **THEN** it creates only the missing `test-*` queues, `stwtceimautotest`, `appins-eim-automation-wtc-test` and `azf-eim-automation-wtc-test` on `asp-eim-wtc-bat`, and creates no plan, workspace, gateway, dashboard, Key Vault, namespace, SQL server or database

#### Scenario: Test with its own Key Vault
- **WHEN** `infra` runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`, and that vault does not exist
- **THEN**:
  - it creates `kv-eim-wtc-test` in `d365fsc-integration-nonprod` with access-policy authorisation, tagged `eim-environment=test`
  - it creates only the missing `test-*` queues and the Automation storage, Insights and app, on `asp-eim-wtc-bat`
  - it grants the identity of `azf-eim-automation-wtc-test` secret `get` on `kv-eim-wtc-test`
  - it creates no plan, workspace, gateway, dashboard, namespace, SQL server or database

#### Scenario: Test with its own workspace
- **WHEN** `infra` runs for `test` with `names.logAnalytics` set to `log-eim-wtc-test`, and none of the test resources exist
- **THEN** it creates:
  - `log-eim-wtc-test` with the merged `test` `logAnalytics.retentionDays`
  - `appins-eim-automation-wtc-test` linked to `log-eim-wtc-test`
  - `azf-eim-automation-wtc-test` on `asp-eim-wtc-bat`
- **AND** it creates no App Service plan, and the gateway and dashboard API of `bat` are not read, changed or moved

#### Scenario: Override equal to the target's name
- **WHEN** `test` sets `names.keyVault` to `KV-D365FSC-WTC-NONPROD`
- **THEN** preflight passes, the Key Vault resolves to `kv-d365fsc-wtc-nonprod`, and `infra` plans exactly the steps it plans for the shipped `test`

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

#### Scenario: Locked name overridden
- **WHEN** `deploy.test.json` uses `bat` and sets `names.sqlServer`, `names.resourceGroup`, `names.serviceBusNamespace`, `names.dashboardApi` or any other locked key to a non-blank value
- **THEN** preflight fails naming that key and `bat`, and nothing is changed

#### Scenario: App Service plan stays locked
- **WHEN** `deploy.test.json` uses `bat` and sets `names.appServicePlan` to `asp-eim-wtc-test`
- **THEN** preflight fails naming `names.appServicePlan` and `bat`, and nothing is changed

#### Scenario: Overridable name set
- **WHEN** `deploy.test.json` uses `bat` and sets `names.keyVault` or `names.logAnalytics`
- **THEN** preflight reports no `uses` problem for that key

#### Scenario: Owned name shared with production
- **WHEN** `deploy.test.json` uses `bat` and sets `names.keyVault` to `kv-eim-wtc-prd`
- **THEN** preflight fails for every configuration, naming `prd`, `test` and `kv-eim-wtc-prd`, and nothing is changed

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

The script SHALL NOT remove an access policy, and SHALL NOT remove a Key Vault or Log Analytics workspace that a configuration no longer names.

#### Scenario: No resource delete command
- **WHEN** `Deploy-Wilson.ps1` is inspected
- **THEN** it contains no `az resource delete` call, and no `delete` call for a Function App, web app, App Service plan, storage account, Application Insights component, static web app, Key Vault, SQL server, database or Service Bus namespace or queue
- **AND** its only `delete` calls are `config appsettings delete` and `sql server firewall-rule delete`, and its SQL text contains no `DROP`, and it issues no `keyvault delete-policy`

#### Scenario: Configurations describe only current resources
- **WHEN** `deploy.base.json` and every `deploy.<name>.json` are merged
- **THEN** none has a `legacy` key, a `gateway.legacyVerificationTargets` key or a `retiredAutomationApps` key

#### Scenario: Verify reports only configured apps
- **WHEN** `verify` runs for `bat`
- **THEN** it reports `azf-eim-gateway-wtc-bat`, `wtc-dashboard-api-dev`, `wtc-dashboard-frontend-dev` and `azf-eim-automation-wtc-bat` and no other app, and when it runs for `test` it reports only `azf-eim-automation-wtc-test`

#### Scenario: Owned vault dropped
- **WHEN** `test` owned `kv-eim-wtc-test`, its override is removed, and `infra` and `settings` run for `test`
- **THEN** `ServiceBus` on `azf-eim-automation-wtc-test` references `kv-d365fsc-wtc-nonprod` again, and `kv-eim-wtc-test` and its access policies are not removed

#### Scenario: Unmanaged server firewall untouched
- **WHEN** `sql` runs for `bat` and `azql-wtc-sandbox` has no `eim-environment` tag
- **THEN** no firewall rule on `azql-wtc-sandbox` is created or deleted

### Requirement: The Service Bus connection is held in the Key Vault
`settings` SHALL keep the secret `eim-servicebus` in the Key Vault when it runs for an owner configuration, or for a configuration with `uses` that owns its Key Vault. In that case:
- it SHALL read the primary connection string of the `RootManageSharedAccessKey` rule of the resolved namespace;
- it SHALL write that string to the resolved Key Vault as the secret `eim-servicebus`, but only when the secret is missing or its value differs;
- it SHALL do both before it writes any app setting that references the secret.

The value SHALL pass through a temporary file that is deleted afterwards, and SHALL never appear in output.

The application settings that need the connection SHALL hold a versionless Key Vault reference to the vault resolved for the app's configuration, not the value:
- `ServiceBus` on every Automation app
- `Simulation__ServiceBusConnectionString` on the dashboard API, when simulation is enabled

The reference SHALL have the form `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/eim-servicebus/)`.

When `settings` runs for a configuration with `uses` that does not own its Key Vault, it SHALL fail before writing anything if `eim-servicebus` does not exist in the target's Key Vault, stating that `settings` must run for the target first. Preflight SHALL apply the same check only when the configuration does not own its Key Vault.

`verify` SHALL report the Key Vault reference status of these settings, with the vault each reference resolves against. `health` SHALL fail, naming the app and setting, when a reference is not resolved.

#### Scenario: Secret written once
- **WHEN** `settings` runs twice for `bat` and the namespace keys did not change
- **THEN** `eim-servicebus` in `kv-d365fsc-wtc-nonprod` gets one new version on the first run at most, and none on the second

#### Scenario: Apps reference the secret
- **WHEN** `settings` runs for the shipped `test` after `settings` ran for `bat`
- **THEN** `azf-eim-automation-wtc-test` has `ServiceBus=@Microsoft.KeyVault(SecretUri=https://kv-d365fsc-wtc-nonprod.vault.azure.net/secrets/eim-servicebus/)`, and no setting holds a Service Bus connection string value

#### Scenario: Using configuration with its own vault
- **WHEN** `settings` runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`
- **THEN**:
  - `eim-servicebus` in `kv-eim-wtc-test` is written from `asb-eim-wtc-nonprod` when it is missing or differs;
  - `azf-eim-automation-wtc-test` has `ServiceBus=@Microsoft.KeyVault(SecretUri=https://kv-eim-wtc-test.vault.azure.net/secrets/eim-servicebus/)`;
  - no setting of `bat`'s apps is written;
  - `settings` does not require the secret in `kv-d365fsc-wtc-nonprod`.

#### Scenario: Target's dashboard keeps the target's vault
- **WHEN** `settings` runs for `bat` while `test` owns `kv-eim-wtc-test`
- **THEN** `Simulation__ServiceBusConnectionString` on `wtc-dashboard-api-dev` and `ServiceBus` on `azf-eim-automation-wtc-bat` reference `kv-d365fsc-wtc-nonprod`

#### Scenario: Target secret missing
- **WHEN** `settings` runs for the shipped `test` and `kv-d365fsc-wtc-nonprod` has no `eim-servicebus` secret
- **THEN** `settings` fails stating that `settings` must run for `bat` first, and no setting is written

#### Scenario: Deployer cannot write the owned vault
- **WHEN** `settings` runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`, and the deployer has no secret `set` on `kv-eim-wtc-test`
- **THEN** `settings` fails stating that the deployer needs secret get and set on the vault, and no app setting is written

#### Scenario: Unresolved reference fails health
- **WHEN** `health` runs for `bat` and the `ServiceBus` reference on `azf-eim-automation-wtc-bat` is not resolved
- **THEN** the stage fails naming `azf-eim-automation-wtc-bat` and `ServiceBus`, without any secret value

#### Scenario: Verify names the vault
- **WHEN** `verify` runs for `test` with `names.keyVault` set to `kv-eim-wtc-test`, after `settings`
- **THEN** the reference report lists `ServiceBus` with its status and `kv-eim-wtc-test`

#### Scenario: Preview names the secret only
- **WHEN** `settings` runs for `bat` with `-WhatIf`
- **THEN** the preview names the secret `eim-servicebus` and the vault, and prints no connection string

### Requirement: New queues get duplicate detection by default
When a queue in `serviceBusQueues` does not exist, `infra` SHALL create it with:
- maximum size 1024 MB
- default message TTL `P14D`
- lock duration `PT60S`
- maximum delivery count 10
- dead-lettering on message expiration
- no partitioning

It SHALL also enable duplicate detection with a history window of `PT5M`, unless the entry sets `"duplicateDetection": false`, and enable sessions only when the entry sets `"requiresSession": true`. Omitted flags SHALL default to duplicate detection on and sessions off.

The plan label SHALL be `create queue <name>`. It SHALL carry the suffix ` (sessions)` when sessions are enabled and ` (no duplicate detection)` when duplicate detection is off.

#### Scenario: Default queue has duplicate detection and no sessions
- **WHEN** `infra` plans for `prdmck` and no queue exists
- **THEN** each `create queue prdmck-*` step's arguments contain `--max-size 1024 --default-message-time-to-live P14D --lock-duration PT60S --max-delivery-count 10 --enable-dead-lettering-on-message-expiration true --enable-partitioning false` and `--duplicate-detection true --duplicate-detection-history-time-window PT5M`, and do not contain `--enable-session`

#### Scenario: Session queue
- **WHEN** an entry sets `"requiresSession": true` and its queue does not exist
- **THEN** the create step's arguments contain `--enable-session true`, and its label ends with ` (sessions)`

#### Scenario: Duplicate detection turned off
- **WHEN** an entry sets `"duplicateDetection": false` and its queue does not exist
- **THEN** the create step's arguments contain neither `--duplicate-detection` nor `--duplicate-detection-history-time-window`, and its label ends with ` (no duplicate detection)`

#### Scenario: Production queues match non-production
- **WHEN** `infra` runs for `prd` and none of the `prd-*` queues exist
- **THEN** the five `prd-*` queues are created with duplicate detection on (window `PT5M`) and sessions off

### Requirement: Existing queues are left as they are
`infra` SHALL plan no step for a queue in `serviceBusQueues` that already exists, whatever its sessions, duplicate detection, window, TTL, lock duration or other properties. It SHALL NOT update, recreate or delete it. The existence probe SHALL cover exactly the resolved queues of the configuration.

#### Scenario: Existing bat queues untouched
- **WHEN** `infra` plans for `bat` and all five `bat-*` queues exist
- **THEN** the plan contains no step whose arguments name a `bat-*` queue

#### Scenario: Existing queue without duplicate detection stays as it is
- **WHEN** a queue in the list exists without duplicate detection, and its entry leaves `duplicateDetection` at the default
- **THEN** no create, update or delete step is planned for that queue

#### Scenario: Only missing queues are created
- **WHEN** `infra` plans for `test`, and `test-d365fsc-dataevent` exists while the other four `test-*` queues do not
- **THEN** exactly four `create queue test-*` steps are planned, none for `test-d365fsc-dataevent`

### Requirement: Service Bus queue configuration is validated before deployment
Offline preflight SHALL check the merged `serviceBusQueues` and fail, changing nothing and before signing in to Azure, when:
- the list is missing, not a list or empty
- an entry is not an object
- an entry has a key other than `name`, `setting`, `requiresSession` and `duplicateDetection`
- a `name` is blank or does not match `^[a-z0-9]+(-[a-z0-9]+)*$`
- a resolved name `<queuePrefix>-<name>` is longer than 260 characters
- two entries resolve to the same queue name (case-insensitive)
- a `setting` does not match `^[A-Za-z_][A-Za-z0-9_]*$`
- two entries name the same `setting` (case-insensitive)
- `requiresSession` or `duplicateDetection` is present and is not a JSON boolean
- any of `D365FscDataEventQueue`, `D365BusinessEventQueue`, `D365ToCaptureQueue`, `CaptureToD365Queue` and `CaptureToD365QueueResponse` is not the `setting` of an entry

Each message SHALL name the configuration and the offending entry or setting.

#### Scenario: Missing required setting
- **WHEN** an override removes the entry whose `setting` is `CaptureToD365QueueResponse`
- **THEN** preflight fails naming `CaptureToD365QueueResponse`, and no Azure command runs

#### Scenario: Duplicate queue name
- **WHEN** two entries have the names `d365fsc-dataevent` and `D365FSC-DataEvent`
- **THEN** preflight fails naming the duplicate queue

#### Scenario: Duplicate setting
- **WHEN** two entries both have the setting `CaptureToD365Queue`
- **THEN** preflight fails naming the duplicate setting `CaptureToD365Queue`

#### Scenario: Invalid queue name
- **WHEN** an entry has the name `Capture To D365`
- **THEN** preflight fails naming that entry's name

#### Scenario: Misspelt flag
- **WHEN** an entry has the key `duplicateDetecton`
- **THEN** preflight fails naming the unknown key `duplicateDetecton`

#### Scenario: Non-boolean flag
- **WHEN** an entry has `"requiresSession": "true"` (a string)
- **THEN** preflight fails stating that `requiresSession` must be the JSON value true or false

#### Scenario: Empty list
- **WHEN** an override sets `serviceBusQueues` to `[]`
- **THEN** preflight fails stating that `serviceBusQueues` must list the queues

#### Scenario: Committed configurations are valid
- **WHEN** the build runs `DeploymentConfigTests`
- **THEN** every committed configuration, merged with the base, has a `serviceBusQueues` list with unique names and settings whose settings include every entry of `RequiredSettings.Queues`

#### Scenario: Shipped queue list is pinned
- **WHEN** the build runs `DeploymentConfigTests`
- **THEN** the base `serviceBusQueues` is exactly the five name/setting pairs `d365fsc-dataevent`/`D365FscDataEventQueue`, `d365fsc-businessevents`/`D365BusinessEventQueue`, `d365fsc-to-capture`/`D365ToCaptureQueue`, `capture-to-d365fsc`/`CaptureToD365Queue`, `capture-to-d365fsc-response`/`CaptureToD365QueueResponse`, none with `requiresSession: true` or `duplicateDetection: false`

#### Scenario: Every trigger binds to a configured queue setting
- **WHEN** the build runs `DeploymentConfigTests`
- **THEN** every `ServiceBusTrigger` queue expression `%X%` on an Automation function names a `setting` in the base `serviceBusQueues`

### Requirement: Queue app settings come from the queue list
`settings` SHALL write, on the Automation app, one setting per `serviceBusQueues` entry that has a `setting`, whose value is the resolved queue name. It SHALL write no queue setting that the list does not define.

When `dashboard.simulationEnabled` is true, the dashboard API settings SHALL map:
- `Simulation__D365FscDataEventQueue` from `D365FscDataEventQueue`
- `Simulation__CaptureToD365Queue` from `CaptureToD365Queue`
- `Simulation__CaptureToD365ResponseQueue` from `CaptureToD365QueueResponse`
- `Simulation__D365ToCaptureQueue` from `D365ToCaptureQueue`

These SHALL be resolved with the queue list and prefix of the configuration whose queue prefix is `dashboard.simulationQueuePrefix`.

#### Scenario: bat Automation settings unchanged
- **WHEN** the Automation settings are built for `bat` from the committed configuration
- **THEN** they contain `D365FscDataEventQueue = bat-d365fsc-dataevent`, `D365BusinessEventQueue = bat-d365fsc-businessevents`, `D365ToCaptureQueue = bat-d365fsc-to-capture`, `CaptureToD365Queue = bat-capture-to-d365fsc` and `CaptureToD365QueueResponse = bat-capture-to-d365fsc-response`, as before this change

#### Scenario: Queue without a setting
- **WHEN** an entry has no `setting`
- **THEN** its queue is created when missing, and no Automation setting is written for it

#### Scenario: Simulation response queue mapping
- **WHEN** the dashboard API settings are built for `bat` with simulation enabled and prefix `bat`
- **THEN** `Simulation__CaptureToD365ResponseQueue` is `bat-capture-to-d365fsc-response` and `Simulation__D365FscDataEventQueue` is `bat-d365fsc-dataevent`

#### Scenario: Simulation on a using configuration's queues
- **WHEN** `bat` has `dashboard.simulationQueuePrefix` `test`
- **THEN** the four `Simulation__*` queue settings are the `test-*` names resolved from `test`'s merged `serviceBusQueues`

#### Scenario: Simulation prefix outside the owner and its users
- **WHEN** `bat` has `dashboard.simulationQueuePrefix` `prd`
- **THEN** building the dashboard API settings fails stating that `bat` and the configurations that use it publish to `bat, test` only
