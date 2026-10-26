## 1. Implementation

- [x] 1.1 Add `serviceBusQueues` to `script/deployment/config/deploy.base.json`. List the five entries in today's order: `d365fsc-dataevent`/`D365FscDataEventQueue`, `d365fsc-businessevents`/`D365BusinessEventQueue`, `d365fsc-to-capture`/`D365ToCaptureQueue`, `capture-to-d365fsc`/`CaptureToD365Queue`, `capture-to-d365fsc-response`/`CaptureToD365QueueResponse`. Do not set `requiresSession` or `duplicateDetection`, and leave `deploy.bat.json`, `deploy.test.json` and `deploy.prd.json` unchanged. Proven by: `DeploymentConfigTests.Shipped_service_bus_queues_are_pinned` (2.6).
- [x] 1.2 In `script/deployment/Deploy-Wilson.ps1`, add these constants next to `$ServiceBusSecretName`:
  - `$QueueDuplicateDetectionWindow = 'PT5M'`
  - `$RequiredQueueSettings`: the five setting names
  - `$QueueEntryKeys = @('name', 'setting', 'requiresSession', 'duplicateDetection')`
  - `$SimulationQueueSettings`: the ordered `Simulation__*` → setting map from design D6

  Proven by: `Test-DeployWilson.ps1` (2.1–2.5).
- [x] 1.3 In `Deploy-Wilson.ps1`, add the pure `Get-QueueConfigProblems($Config, [string]$Prefix)` with every rule in design D5. Each message names the configuration (`$Config.environment`) and the entry index, name, key or setting. Proven by: the validation assertions in 2.3.
- [x] 1.4 In `Deploy-Wilson.ps1`, replace `Get-QueueNames` with two pure helpers:
  - `Get-ServiceBusQueues($Config, [string]$Prefix)`: throws the joined `Get-QueueConfigProblems` messages when there are any. Otherwise returns `[pscustomobject]@{ Name; Setting; RequiresSession; DuplicateDetection }` per entry, in config order, with `Name = "$Prefix-$name"` and the defaults: sessions off, duplicate detection on.
  - `Get-QueueSettings($Queues)`: returns an ordered map from setting to name for the entries that have a setting.

  Remove `Get-QueueNames` completely. Proven by: 2.1.
- [x] 1.5 `Get-AutomationSettings` (`Deploy-Wilson.ps1`): write the queue settings from `Get-QueueSettings (Get-ServiceBusQueues $Config $Names.QueuePrefix)`, in the same position as today. Proven by: the unchanged `bat` and `test` Automation settings assertions in 2.4.
- [x] 1.6 Simulation (`Deploy-Wilson.ps1`):
  - Replace `Get-SimulationQueuePrefixes` with `Get-SimulationTargets($Config, $AllConfigs)`. It returns an ordered map from queue prefix to merged configuration: the owner first, then each configuration that `uses` it.
  - Change `Get-DashboardApiSettings($Config, $Names, $SimulationTargets)`:
    - The allowed prefixes are the map's keys. When the map is missing, use the configuration's own prefix and configuration.
    - Keep today's error messages.
    - Resolve the simulation queues with `Get-ServiceBusQueues $SimulationTargets[$prefix] $prefix`, then fill the four `Simulation__*` keys through `$SimulationQueueSettings`.
  - Update both callers: `Invoke-PreflightComponent` (the backend drift branch) and `Invoke-SettingsComponent`.

  Proven by: the simulation assertions in 2.4.
- [x] 1.7 `Get-InfraPlan` (`Deploy-Wilson.ps1`): loop over `Get-ServiceBusQueues $Config $Names.QueuePrefix` and skip every queue in `$Existing.Queues`. Build the create arguments:
  - today's arguments
  - then `--enable-session true` when `RequiresSession`
  - then `--duplicate-detection true --duplicate-detection-history-time-window $QueueDuplicateDetectionWindow` when `DuplicateDetection`
  - then `-o none`

  The label is `create queue <name>`, plus ` (sessions)` and/or ` (no duplicate detection)` when they apply. Proven by: the plan assertions in 2.2.
- [x] 1.8 `Get-ExistingResources` (`Deploy-Wilson.ps1`): change the signature to `($Config, $Names, [bool]$IsOwner)` and probe every `Get-ServiceBusQueues $Config $Names.QueuePrefix` name. Update the caller in `Invoke-InfraComponent` and the stub in `Test-DeployWilson.ps1` (currently `function Get-ExistingResources { ... }`, around line 645). Proven by: 2.2 and the existing infra `-WhatIf` stub test still passing.
- [x] 1.9 Preflight wiring (`Deploy-Wilson.ps1`):
  - `Get-OfflinePreflightProblems`: always add `Get-QueueConfigProblems $Config (Get-ResourceNames $Config <owner> -SkipValidation).QueuePrefix`. Use the owner from `$AllConfigs` when `uses` is set, as `Get-IsolationProblems` does.
  - `Get-RequiredConfigPaths`: add `serviceBusQueues` when `infra` or `settings` is selected, for owners and using configurations alike.

  Proven by: 2.3 (offline preflight and placeholder assertions).
- [x] 1.10 Docs: `docs/deployment/deployment.md`.
  - Replace the "five queues per prefix" paragraph and the **Queues.** paragraph with a `serviceBusQueues` section covering:
    - the entry keys and their defaults
    - `<queuePrefix>-<name>` naming
    - the create arguments, including duplicate detection PT5M
    - that existing queues are never changed, and that sessions and duplicate detection cannot change after creation
    - that a session queue needs a session-aware function
    - that an environment file replaces the whole list
    - how to add a queue: normally in base, together with its function and setting
    - that renaming a queue creates a new one
  - Extend the `preflight` row of the components table with the queue validation.

  Proven by: review.
- [x] 1.11 Docs: `script/README.md`. Add one sentence saying the Service Bus queues are listed in `serviceBusQueues` in `deploy.base.json`, with a link to the deployment.md section. Proven by: review.

## 2. Tests

- [x] 2.1 `script/deployment/Test-DeployWilson.ps1`, resolver:
  - Replace every `Get-QueueNames` use (lines ~329, 383, 391, 407) with `(Get-ServiceBusQueues <config> '<prefix>').Name`.
  - Assert that `bat`, `test` and `prd` resolve to today's five names in order.
  - Assert that all shipped queues have `RequiresSession = $false` and `DuplicateDetection = $true`.
  - Assert that `Get-QueueSettings` returns the five settings in today's order.
- [x] 2.2 `Test-DeployWilson.ps1`, infra plan:
  - For `prdmck` with nothing existing, each `create queue prdmck-*` step's `Args` contain today's arguments and `--duplicate-detection true --duplicate-detection-history-time-window PT5M`, and do not contain `--enable-session`.
  - An override entry with `requiresSession: true` adds `--enable-session true` and the label ` (sessions)`.
  - `duplicateDetection: false` removes both duplicate-detection arguments and adds ` (no duplicate detection)`.
  - With all `bat-*` queues existing, no step names a `bat-*` queue.
  - With only `test-d365fsc-dataevent` existing, exactly four `test-*` creates are planned.
  - An override adding `extra-queue`/`ExtraQueue` plans `create queue prdmck-extra-queue`.
  - Keep the existing "plan deletes nothing" assertions.
- [x] 2.3 `Test-DeployWilson.ps1`, validation (`Get-QueueConfigProblems`, plus `Get-OfflinePreflightProblems` for one case). Assert one named problem each for:
  - missing, non-list and empty list
  - non-object entry
  - unknown key `duplicateDetecton`
  - blank name and invalid name `Capture To D365`
  - a resolved name over 260 characters
  - case-insensitive duplicate names
  - invalid and case-insensitive duplicate settings
  - string `"true"` for `requiresSession`
  - each of the five required settings missing (override list without it)

  Also assert that the committed `bat`, `test` and `prd` have no problems, and that `Get-ServiceBusQueues` throws on an invalid list.
- [x] 2.4 `Test-DeployWilson.ps1`, settings:
  - The `bat` and `test` Automation queue settings equal today's values.
  - An override with `ExtraQueue` adds that setting and leaves the other five unchanged.
  - An entry without `setting` writes no setting.
  - `Get-SimulationTargets` for `bat` has the keys `bat, test`; update the existing `Get-SimulationQueuePrefixes` assertion.
  - The `bat` simulation maps `Simulation__CaptureToD365ResponseQueue = bat-capture-to-d365fsc-response`.
  - Simulating on `test` resolves from `test`'s configuration. Prove it with an `EIM_DEPLOY_CONFIG_OVERRIDES`-style override on `test` that changes a non-required entry, or by asserting the target map's value is `test`'s config object.
  - Simulating on `prd` still throws with the existing message.
- [x] 2.5 Run `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1` and keep it green.
- [x] 2.6 `Wilson.Integration.Tests/DeploymentConfigTests.cs`, add:
  - `Every_configuration_lists_the_required_service_bus_queues`: for each merged configuration, the names are unique and the settings are unique (case-insensitive) and include `RequiredSettings.Queues`.
  - `Shipped_service_bus_queues_are_pinned`: the base list is exactly the five name/setting pairs, with no `requiresSession: true` and no `duplicateDetection: false`.
  - `Every_service_bus_trigger_binds_to_a_configured_queue_setting`: use `AutomationFunctionNamesTests.Functions()` and `ServiceBusTriggerAttribute.QueueName` `%X%` → `X` in the base settings.
- [x] 2.7 Run `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj` and `dotnet build ARM.Integration.Wilson.slnx`, and keep both green. The EIM and Gateway test projects are unaffected; run them only if the build touches them.

## 3. Deploy and verify (manual, not Luigi's)

- [ ] 3.1 Run `Deploy-Wilson.ps1 -Environment bat -Component infra -WhatIf` and then `-Environment test -Component infra -WhatIf`, from a package built on this branch. Confirm that no `create queue` step is listed, so the existing queues are untouched.
- [ ] 3.2 Read the live duplicate-detection window of the existing queues (read-only):
  ```
  az servicebus queue list -g d365fsc-integration-nonprod --namespace-name asb-eim-wtc-nonprod --query "[].{n:name,dd:requiresDuplicateDetection,w:duplicateDetectionHistoryTimeWindow,s:requiresSession}" -o table
  ```
  Record the window in the PR. If it differs from `PT5M`, decide whether `$QueueDuplicateDetectionWindow` should match it before `prd` `infra` runs. Existing queues are not changed either way.
- [ ] 3.3 Run `Deploy-Wilson.ps1 -Environment prd -Component infra -WhatIf` and confirm the five `create queue prd-*` steps (no suffix). After the real `prd` `infra` run, confirm with `az servicebus queue show` that each `prd-*` queue has `requiresDuplicateDetection = true`, window `PT5M`, `requiresSession = false`, lock `PT1M`, TTL `P14D` and max delivery 10.
- [ ] 3.4 After the next `settings` run on `bat`, confirm the preflight drift check reports no Automation or dashboard queue setting as differing (names only).
