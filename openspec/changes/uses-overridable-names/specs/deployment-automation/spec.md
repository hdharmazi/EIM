## MODIFIED Requirements

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
