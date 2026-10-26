## MODIFIED Requirements

### Requirement: The legacy dev Automation app is retired without a replacement
The legacy dev resources (`azf-eim-customer-wtc-dev`, `stwtceimcustomerdev`, `appins-eim-customer-wtc-dev`) SHALL be listed in the `legacy.resources` of `deploy.nonprod.json`, and `azf-eim-customer-wtc-dev` SHALL be listed in its `legacy.retiredAutomationApps`. No deployment configuration named `dev` SHALL exist.
- `remove` for `nonprod` SHALL delete the legacy dev resources under every guard of the manual `remove`, and SHALL refuse while any gated function on a retired app is enabled.
- `cutover` and `rollback` SHALL NOT change the trigger state of a retired app.
- `verify` for `nonprod` SHALL report each retired app's state and gated trigger states, and SHALL report a missing retired app as not found without failing.
- The `dev-*` Service Bus queues and every `DEV`/`DEV2` database row SHALL be left unchanged.

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
- **THEN** the script fails naming the unknown configuration `dev` and listing `bat`, `nonprod`, `prd`, `prdshared` and `test`, and changes nothing

#### Scenario: Dev queues and rows untouched
- **WHEN** `infra`, `settings`, `cutover` or `remove` runs for any configuration
- **THEN** no `dev-*` queue is created, changed or deleted and no database row is changed

## ADDED Requirements

### Requirement: Cutover and rollback need a configured legacy resource
A shared-scope `cutover` SHALL be refused, changing nothing, when the configuration has no `legacy.gatewayApp`. The message SHALL state that the configuration has no legacy gateway and there are no keys to copy. An environment-scope `rollback` SHALL be refused, changing nothing, when the configuration has no `legacy.automationApp`. The message SHALL state that there is no legacy Automation app to roll back to.

#### Scenario: Production shared cutover
- **WHEN** an operator runs `cutover` for `prdshared`
- **THEN** the script refuses, stating that `prdshared` has no legacy gateway, and copies no key

#### Scenario: Rollback without a legacy app
- **WHEN** an operator runs `rollback` for `prd`
- **THEN** the script refuses, stating that `prd` has no legacy Automation app, and changes no trigger state
