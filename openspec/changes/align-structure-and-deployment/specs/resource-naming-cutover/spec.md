## ADDED Requirements

### Requirement: Stateless resources are recreated under the naming pattern
The Automation Function Apps, the gateway Function App, the App Service plan, their storage accounts and Application Insights, and the Log Analytics workspace SHALL be created under the derived names, side by side with the legacy resources, and SHALL NOT reuse a legacy name. The SQL server, the database, the Service Bus namespace and its queues, the Key Vault, the resource group, the dashboard API `wtc-dashboard-api-dev` and the dashboard static web app `wtc-dashboard-frontend-dev` SHALL keep their current names through configuration overrides; their renames are deferred.

| Legacy | New |
|---|---|
| `azf-eim-customer-wtc-{env}` | `azf-eim-automation-wtc-{env}` |
| `stwtceimcustomer{env}` | `stwtceimauto{env}` |
| `appins-eim-customer-wtc-{env}` | `appins-eim-automation-wtc-{env}` |
| `asp-appservice-p0v3-linux-dev` | `asp-eim-wtc-nonprod` |
| `azf-d365fsc-gateway-wtc-nonprod` | `azf-eim-gateway-wtc-nonprod` |
| `stwtcd365gwnonprod` | `stwtceimgwnonprod` |
| `appins-d365fsc-gateway-wtc-nonprod` | `appins-eim-gateway-wtc-nonprod` |
| (none) | `log-eim-wtc-nonprod` |

`{env}` is a deployment configuration (`bat`, `test`), not a database ENVIRONMENT value; the `DEV2` ENVIRONMENT value has no Function App before or after this change. `dev` is not a deployment configuration: no `azf-eim-automation-wtc-dev`, `stwtceimautodev` or `appins-eim-automation-wtc-dev` SHALL be created, and the legacy dev resources are retired with no replacement.

#### Scenario: New app alongside the legacy app
- **WHEN** `infra` runs for `bat` while `azf-eim-customer-wtc-bat` exists
- **THEN** `azf-eim-automation-wtc-bat` is created and the legacy app is not modified

#### Scenario: No dev Automation resources
- **WHEN** the resource names are derived for every deployment configuration
- **THEN** none of them is `azf-eim-automation-wtc-dev`, `stwtceimautodev` or `appins-eim-automation-wtc-dev`

#### Scenario: Kept resources untouched
- **WHEN** `infra` runs for any configuration
- **THEN** no SQL server, database, Service Bus namespace, queue, Key Vault, resource group, dashboard API or dashboard static web app is created under a new name, renamed or deleted

### Requirement: The dashboard is deployed in place
The dashboard API `wtc-dashboard-api-dev` and the static web app `wtc-dashboard-frontend-dev` SHALL be updated in place by `settings`, `backend` and `frontend`. The change SHALL NOT create a second dashboard API or static web app, SHALL NOT run a dashboard cutover, and SHALL NOT change the static web app's hostname or the dashboard's Entra app registration. Apart from the gateway settings (`Gateway__BaseUrl`, `Gateway__AppName` and the `Gateway__Applications__<n>__*` verification targets), the dashboard API's settings SHALL keep their names and values.

#### Scenario: No second dashboard
- **WHEN** `infra` runs for `nonprod`
- **THEN** no resource named `app-eim-dashboard-api-wtc-nonprod` or `swa-eim-dashboard-wtc-nonprod` is created

#### Scenario: Dashboard points at the new gateway
- **WHEN** `settings` runs for `nonprod`
- **THEN** `wtc-dashboard-api-dev` has `Gateway__AppName=azf-eim-gateway-wtc-nonprod`, and its `ConnectionStrings__EIMDB`, `AzureAd__*` and `Simulation__*` settings keep their values

#### Scenario: Dashboard not part of decommissioning
- **WHEN** `remove -Environment nonprod -WhatIf` runs
- **THEN** neither `wtc-dashboard-api-dev` nor `wtc-dashboard-frontend-dev` is listed for deletion

### Requirement: New Automation triggers start disabled
When settings are applied to an Automation app, every gated function (the Automation queue and timer functions) SHALL have `AzureWebJobs.<function>.Disabled` set to `true` if the setting is absent, and SHALL keep its existing value if present. Deploying Automation code to an app missing any gated setting SHALL be refused.

#### Scenario: First settings run disables triggers
- **WHEN** `settings` runs for `bat` on a new `azf-eim-automation-wtc-bat`
- **THEN** all six gated functions have `AzureWebJobs.<function>.Disabled=true`

#### Scenario: Later settings run preserves cutover
- **WHEN** `settings` runs again after cutover enabled the triggers
- **THEN** the gated settings keep their current values

#### Scenario: Deploy refused without gates
- **WHEN** the `automation` component runs against an app that lacks `AzureWebJobs.RetrySweepHandler.Disabled`
- **THEN** the component fails naming the setting and deploys nothing

### Requirement: Cutover keeps a single consumer per queue
`Deploy-Wilson.ps1 -Component cutover` for an environment SHALL move queue and timer processing from the legacy Automation app to the new app. Before enabling anything on the new app, it SHALL:
1. verify the new app is healthy and that its database, Service Bus and queue settings match the legacy app's
2. record which gated functions are enabled on the legacy app
3. disable all gated functions on the legacy app and confirm that they report disabled
4. wait at least the queue lock duration

Only then SHALL it enable on the new app exactly the functions that were enabled on the legacy app. At no time SHALL a gated function be enabled on both apps.

#### Scenario: Successful cutover
- **WHEN** an operator runs `cutover` for `bat` with all six functions enabled on `azf-eim-customer-wtc-bat`
- **THEN** all six are disabled on the legacy app, the lock duration elapses, and all six are enabled on `azf-eim-automation-wtc-bat`

#### Scenario: Disabled function stays disabled
- **WHEN** `AwaitingD365SweepHandler` is disabled on the legacy app at cutover
- **THEN** it remains disabled on the new app

#### Scenario: Settings mismatch blocks cutover
- **WHEN** the new app's `D365FscDataEventQueue` differs from the legacy app's
- **THEN** cutover fails naming the setting, without showing values, and changes no trigger state

#### Scenario: Retired setting ignored by parity
- **WHEN** the legacy app has `PollSchedule` and the new app does not, and every other compared setting matches
- **THEN** the parity check passes and cutover proceeds

#### Scenario: Failure leaves both disabled
- **WHEN** cutover fails after disabling the legacy triggers
- **THEN** no trigger is enabled on the new app, and the script prints the commands to rerun cutover or roll back

#### Scenario: Cutover rerun
- **WHEN** cutover is run again after it succeeded
- **THEN** no trigger state changes

### Requirement: Rollback reverses a cutover
`Deploy-Wilson.ps1 -Component rollback` for an environment SHALL perform the following steps, in order:
1. disable all gated functions on the new app and confirm that they report disabled
2. wait at least the queue lock duration
3. enable on the legacy app exactly the functions that were enabled on the new app

It SHALL refuse when the legacy app no longer exists.

#### Scenario: Rollback restores legacy processing
- **WHEN** an operator runs `rollback` for `bat` after cutover
- **THEN** the new app's gated functions are disabled first, and then the same functions are enabled on `azf-eim-customer-wtc-bat`

#### Scenario: Rollback after decommission
- **WHEN** `rollback` runs for `test` and `azf-eim-customer-wtc-test` has been deleted
- **THEN** the script refuses and changes nothing

### Requirement: Gateway keys carry over to the new gateway
`cutover` for `nonprod` SHALL copy every named host key and every named function key of `D365Data` and `D365Service` from the legacy gateway to the new gateway, with the same names and values. It SHALL exclude the master key and system keys. It SHALL print key names only. Consumers SHALL be able to switch by changing only the base URL.

#### Scenario: Consumer key accepted by the new gateway
- **WHEN** keys have been copied and a consumer calls `https://azf-eim-gateway-wtc-nonprod.azurewebsites.net/api/v1/d365fsc/...` with its existing key
- **THEN** the gateway authorises the request against the same `GATEWAYCONSUMER` key hash

#### Scenario: Master key not copied
- **WHEN** keys are copied
- **THEN** the new gateway's master key differs from the legacy gateway's

#### Scenario: Key values not shown
- **WHEN** the key copy runs
- **THEN** the output lists key names and no key values

### Requirement: Legacy resources are removed only manually
`Deploy-Wilson.ps1 -Component remove` SHALL delete only the resources named in the configuration's `legacy` section, and only of the types Function App, App Service plan, storage account and Application Insights. It SHALL NOT delete an App Service plan that still hosts any app. It SHALL refuse to run when:
- the `TF_BUILD` environment variable is set
- it has not first been run with `-WhatIf` for the same configuration
- the operator does not confirm the configuration name
- any gated function is enabled on the configuration's legacy Automation app or on any app in its `legacy.retiredAutomationApps`
- a legacy name equals a current derived or overridden name

The pipeline SHALL NOT invoke `remove`.

#### Scenario: Pipeline cannot decommission
- **WHEN** `remove` runs on an Azure DevOps agent
- **THEN** it refuses and deletes nothing

#### Scenario: Legacy still consuming
- **WHEN** `remove` runs for `bat` while a gated function on `azf-eim-customer-wtc-bat` is enabled
- **THEN** it refuses, naming the function

#### Scenario: Shared plan still in use
- **WHEN** `remove` runs for `nonprod` and `wtc-dashboard-api-dev` is still on `asp-appservice-p0v3-linux-dev`
- **THEN** the plan is not deleted, `wtc-dashboard-api-dev` is named as the remaining app, and the other legacy entries are processed

#### Scenario: Kept name listed as legacy
- **WHEN** the `legacy` section names `wtc-dashboard-api-dev`, which is also the configured dashboard API name
- **THEN** `remove` refuses that entry

#### Scenario: Stateful resources never removed
- **WHEN** a `legacy` entry names a SQL server, Service Bus namespace, Key Vault, resource group or static web app
- **THEN** `remove` refuses that entry

### Requirement: The legacy dev Automation app is retired without a replacement
The legacy dev resources (`azf-eim-customer-wtc-dev`, `stwtceimcustomerdev`, `appins-eim-customer-wtc-dev`) SHALL be listed in the `legacy.resources` of `deploy.nonprod.json`, and `azf-eim-customer-wtc-dev` SHALL be listed in its `legacy.retiredAutomationApps`. No deployment configuration named `dev` SHALL exist. `remove` for `nonprod` SHALL delete the legacy dev resources under every guard of the manual `remove`, and SHALL refuse while any gated function on a retired app is enabled. `cutover` and `rollback` SHALL NOT change the trigger state of a retired app. `verify` for `nonprod` SHALL report each retired app's state and gated trigger states, and SHALL report a missing retired app as not found without failing. The `dev-*` Service Bus queues and every `DEV`/`DEV2` database row SHALL be left unchanged.

#### Scenario: Retired app still consuming
- **WHEN** `remove` runs for `nonprod` while `QueueCaptureCustomer` is enabled on `azf-eim-customer-wtc-dev`
- **THEN** it refuses, naming `azf-eim-customer-wtc-dev` and `QueueCaptureCustomer`, and deletes nothing

#### Scenario: Retired app disabled
- **WHEN** every gated function on `azf-eim-customer-wtc-dev` is disabled and `remove -WhatIf` runs for `nonprod`
- **THEN** `azf-eim-customer-wtc-dev`, `stwtceimcustomerdev` and, if it exists, `appins-eim-customer-wtc-dev` are listed for deletion alongside the legacy gateway resources

#### Scenario: Retired Insights already absent
- **WHEN** `appins-eim-customer-wtc-dev` does not exist and `remove` runs for `nonprod`
- **THEN** it is reported as not found and the other entries are processed

#### Scenario: Retired app stopped but triggers enabled
- **WHEN** `azf-eim-customer-wtc-dev` is stopped but its `AzureWebJobs.<function>.Disabled` settings are absent or `false`
- **THEN** `remove` for `nonprod` refuses, naming the enabled functions

#### Scenario: Dev configuration rejected
- **WHEN** an operator runs `Deploy-Wilson.ps1 -Environment dev` with any component
- **THEN** the script fails naming the unknown configuration `dev` and listing `bat`, `nonprod` and `test`, and changes nothing

#### Scenario: Dev queues and rows untouched
- **WHEN** `infra`, `settings`, `cutover` or `remove` runs for any configuration
- **THEN** no `dev-*` queue is created, changed or deleted and no database row is changed
