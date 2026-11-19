# resource-naming Specification

## Purpose

Defines the naming pattern under which the stateless Azure resources are created, the existing resources that keep their names (the dashboard is deployed in place), and how the Automation trigger state is set from the configuration.

## Requirements

### Requirement: Stateless resources are created under the naming pattern
The script SHALL create these resources under their resolved names, as defined in `deployment-automation`:
- the Automation Function Apps, with their storage accounts and Application Insights
- the gateway Function App, with its storage account and Application Insights
- the App Service plan
- the Log Analytics workspace

The script SHALL read, change and create only resources whose names it resolves for the configuration. It SHALL NOT read, change or delete any other resource in the resource group.

The existing non-production resources SHALL keep their current names through `names` overrides in `deploy.bat.json`. They SHALL NOT be renamed:
- the resource group `d365fsc-integration-nonprod`
- the Service Bus namespace `asb-eim-wtc-nonprod` and its queues
- the Key Vault `kv-d365fsc-wtc-nonprod`
- the SQL server `azql-wtc-sandbox` and the database `eimdb`
- the dashboard API `wtc-dashboard-api-dev`
- the dashboard static web app `wtc-dashboard-frontend-dev`

| Resource | `bat` | `test` |
|---|---|---|
| Automation Function App | `azf-eim-automation-wtc-bat` | `azf-eim-automation-wtc-test` |
| Automation storage | `stwtceimautobat` | `stwtceimautotest` |
| Automation Insights | `appins-eim-automation-wtc-bat` | `appins-eim-automation-wtc-test` |
| App Service plan | `asp-eim-wtc-bat` | `bat`'s |
| Gateway Function App | `azf-eim-gateway-wtc-bat` | `bat`'s |
| Gateway storage | `stwtceimgwbat` | `bat`'s |
| Gateway Insights | `appins-eim-gateway-wtc-bat` | `bat`'s |
| Log Analytics workspace | `log-eim-wtc-bat` | `bat`'s |

A configuration name such as `bat` or `test` is not a database ENVIRONMENT value. The `DEV2` ENVIRONMENT value has no Function App.

`dev` is not a deployment configuration. None of these SHALL be created:
- `azf-eim-automation-wtc-dev`
- `stwtceimautodev`
- `appins-eim-automation-wtc-dev`

#### Scenario: New app created
- **WHEN** `infra` runs for `bat`
- **THEN** `azf-eim-automation-wtc-bat`, `stwtceimautobat` and `appins-eim-automation-wtc-bat` exist, and no Function App, storage account or Insights resource in the resource group other than the ones `bat` resolves is read, changed or deleted

#### Scenario: No dev Automation resources
- **WHEN** the resource names are resolved for every deployment configuration
- **THEN** none of them is `azf-eim-automation-wtc-dev`, `stwtceimautodev` or `appins-eim-automation-wtc-dev`

#### Scenario: Kept resources untouched
- **WHEN** `infra` runs for `bat` or `test`
- **THEN** none of these is created under a new name, renamed or deleted:
  - the SQL server or database
  - the Service Bus namespace or any queue
  - the Key Vault or the resource group
  - the dashboard API or the dashboard static web app

#### Scenario: Dev queues and rows untouched
- **WHEN** `infra`, `settings`, `schema` or `sqlusers` runs for any configuration
- **THEN** no `dev-*` queue is created, changed or deleted, and `sqlusers` changes no database row

### Requirement: The dashboard is deployed in place
The dashboard API `wtc-dashboard-api-dev` and the static web app `wtc-dashboard-frontend-dev` SHALL be updated in place by `settings`, `backend` and `frontend` for `bat`. The change SHALL NOT:
- create a second dashboard API or static web app
- change the static web app's hostname
- change the dashboard's Entra app registration

`infra` SHALL assign a system-assigned managed identity to `wtc-dashboard-api-dev` when it has none, and SHALL make no other change to either resource.

The dashboard API's settings SHALL keep their names and values, with these exceptions:
- `Gateway__BaseUrl` and `Gateway__AppName` point at the gateway the configuration resolves.
- `ConnectionStrings__EIMDB` becomes the managed-identity connection string defined in `sql-identity-access`.
- `settings` SHALL delete every `Gateway__Applications__<n>__*` setting, because the configuration writes none.
- The `Simulation__*` settings follow the dashboard simulation setting, and `Simulation__ServiceBusConnectionString` becomes a Key Vault reference.

#### Scenario: No second dashboard
- **WHEN** `infra` runs for `bat`
- **THEN** no resource named `app-eim-dashboard-api-wtc-bat` or `swa-eim-dashboard-wtc-bat` is created

#### Scenario: Dashboard points at the new gateway
- **WHEN** `settings` runs for `bat`
- **THEN** `wtc-dashboard-api-dev` has:
  - `Gateway__AppName=azf-eim-gateway-wtc-bat`
  - a `ConnectionStrings__EIMDB` value for `azql-wtc-sandbox`/`eimdb` with `Authentication=Active Directory Managed Identity`
- **AND** its `AzureAd__*` settings keep their values

#### Scenario: Leftover verification targets removed
- **WHEN** `settings` runs for `bat` and `wtc-dashboard-api-dev` has `Gateway__Applications__0__Name` and `Gateway__Applications__0__BaseUrl`
- **THEN** both settings are deleted, no `Gateway__Applications__*` setting is written, and no setting outside `Gateway__Applications__*` and `Simulation__*` is deleted

### Requirement: Automation trigger state follows the configuration
The configuration key `automation.triggersEnabled` (a JSON boolean) SHALL decide the state of every gated function, meaning every Automation queue and timer function in `automation.gatedFunctions`. Its values are:
- `deploy.base.json`: `false`, so a configuration that does not set it starts with every trigger off.
- `deploy.bat.json` and `deploy.test.json`: explicitly `true`.
- `deploy.prd.json`: explicitly `false` until production go-live.

Every deployment configuration SHALL set the key explicitly.

On every run, `settings` SHALL write `AzureWebJobs.<function>.Disabled` for every gated function:
- `false` when `automation.triggersEnabled` is `true` and the function is not listed in `automation.keepDisabled`
- `true` otherwise

Existing values SHALL be overwritten, so the configuration is the only source of the trigger state.

Deploying Automation code to an app that lacks any gated setting SHALL be refused.

Preflight SHALL fail before anything is written, naming the key or function, when:
- `automation.triggersEnabled` is missing or is not a JSON boolean, or
- `automation.keepDisabled` names a function that is not in `automation.gatedFunctions`.

#### Scenario: Non-production triggers on
- **WHEN** `settings` runs for `bat` with `automation.triggersEnabled` `true` and an empty `automation.keepDisabled`
- **THEN** all six gated functions on `azf-eim-automation-wtc-bat` have `AzureWebJobs.<function>.Disabled=false`

#### Scenario: Function kept disabled
- **WHEN** `settings` runs for `test` with `automation.triggersEnabled` `true` and `automation.keepDisabled` `["AwaitingD365SweepHandler"]`
- **THEN** `AzureWebJobs.AwaitingD365SweepHandler.Disabled` is `true` and the other five gated settings are `false`

#### Scenario: Triggers off by configuration
- **WHEN** `settings` runs for `prd` with `automation.triggersEnabled` `false`
- **THEN** all six gated functions on `azf-eim-automation-wtc-prd` have `AzureWebJobs.<function>.Disabled=true`, whatever `automation.keepDisabled` lists

#### Scenario: Portal change overwritten
- **WHEN** an operator has set `AzureWebJobs.QueueCaptureCustomer.Disabled=true` in the portal on `azf-eim-automation-wtc-bat`, and `settings` runs for `bat` with `automation.triggersEnabled` `true` and `QueueCaptureCustomer` not in `automation.keepDisabled`
- **THEN** the setting is written back to `false`

#### Scenario: Deploy refused without gates
- **WHEN** the `automation` component runs against an app that lacks `AzureWebJobs.RetrySweepHandler.Disabled`
- **THEN** the component fails naming the setting and stating that `settings` must run first, and deploys nothing

#### Scenario: Trigger switch not a boolean
- **WHEN** the merged configuration for `bat` has `automation.triggersEnabled` set to `"yes"` and `settings` runs
- **THEN** preflight fails naming `automation.triggersEnabled`, and no setting is written

#### Scenario: Unknown kept-disabled function
- **WHEN** `automation.keepDisabled` lists `RetrySweepHandlr` and `settings` runs for `test`
- **THEN** preflight fails naming `RetrySweepHandlr` and `automation.gatedFunctions`, and no setting is written
