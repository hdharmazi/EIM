## Why

Ticket #5246. `Deploy-Wilson.ps1` hard-codes the five Service Bus queues in `Get-QueueNames`. A queue cannot be added or adjusted without editing the script, and the queues it creates have no duplicate detection. Every live queue in `asb-eim-wtc-nonprod` has `requiresDuplicateDetection = true`, so the first new environment (`prd`) would get queues that behave differently from bat and test. Duplicate detection and sessions cannot be changed after a queue exists, so this has to be fixed before `infra` first runs for `prd`.

The Wastequip deployment already solves this with a `serviceBusQueues` list in `deploy.base.json` and `Confirm-ServiceBus`. Wilson should follow the same model.

## What Changes

- New `serviceBusQueues` list in `script/deployment/config/deploy.base.json`, modelled on Wastequip. Each entry has:
  - `name`: the queue name without the environment prefix
  - `setting`: the app setting that carries the resolved queue name (optional)
  - `requiresSession`: optional, default `false`
  - `duplicateDetection`: optional, default `true`

  The base file lists the five current queues with their current suffixes and setting names, so every resolved queue name and app setting value stays as it is today.
- Queue names still resolve as `<queuePrefix>-<name>`, because `bat` and `test` share one namespace. This is a Wilson-specific deviation from Wastequip, which uses full names.
- `infra` creates each missing queue from the list using the arguments it uses today: max size 1024, TTL P14D, lock PT60S, max delivery 10, dead-lettering on expiration, no partitioning. Following Wastequip, it also adds:
  - `--duplicate-detection true --duplicate-detection-history-time-window PT5M`, unless `duplicateDetection` is `false`
  - `--enable-session true` when `requiresSession` is `true`
- Existing queues are left exactly as they are. This matches Wastequip's `Confirm-ServiceBus`: no update and no recreate, even when their properties differ from the configuration.
- The Automation app settings come from the list's `setting` values. The existence probes in `Get-ExistingResources` and the dashboard simulation settings also use the list. The simulation keeps its fixed mapping to the four `Simulation__*` keys the Dashboard API reads, including `CaptureToD365QueueResponse` → `Simulation__CaptureToD365ResponseQueue`.
- Offline preflight validates the resolved list. It fails, changing nothing, when:
  - the list is empty
  - a name is blank, invalid or duplicated
  - a setting name is invalid or duplicated
  - a flag is not a JSON boolean
  - an entry has an unknown key
  - any of the five settings the code requires (`RequiredSettings.Queues`) is missing
- `Get-QueueNames` (hard-coded) is removed. The code reads the queues through new pure helpers instead.
- Tests:
  - `Test-DeployWilson.ps1` covers the plan arguments, the defaults, existing queues, validation, list replacement in an environment file and the simulation mapping.
  - `DeploymentConfigTests` checks that every configuration covers `RequiredSettings.Queues` and that every Automation `ServiceBusTrigger` binds to a configured setting.
- Docs: `docs/deployment/deployment.md` (queues, preflight, adding a queue) and `script/README.md`.

There are no breaking changes for deployed apps: the resolved queue names and the Automation and simulation setting values are unchanged.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `deployment-automation`: queue provisioning reads the queue list from configuration, with prefixed names. New queues get duplicate detection by default, and sessions on request. Existing queues are left as found. Preflight validates the list. Queue app settings (Automation and simulation) come from the list.

## Impact

- `script/deployment/Deploy-Wilson.ps1`:
  - `Get-QueueNames` is replaced
  - `Get-AutomationSettings`, `Get-DashboardApiSettings`, `Get-SimulationQueuePrefixes`, `Get-InfraPlan`, `Get-ExistingResources`, `Get-OfflinePreflightProblems` and `Get-RequiredConfigPaths` change
  - new constants and pure helpers
- `script/deployment/config/deploy.base.json`: new `serviceBusQueues`. Environment files are unchanged.
- `script/deployment/Test-DeployWilson.ps1`: updated and new assertions.
- `Wilson.Integration.Tests/DeploymentConfigTests.cs`: new tests.
- `docs/deployment/deployment.md`, `script/README.md`.
- Azure:
  - Nothing changes for the existing `bat-*` and `test-*` queues.
  - The `prd-*` queues are created with duplicate detection (PT5M) on the first `infra` run for `prd`.
- No change to application code, SQL, pipelines, `RequiredSettings` or the Dashboard API.

## Non-goals

- Changing or recreating existing queues. That includes:
  - turning on sessions
  - turning duplicate detection on or off
  - changing the duplicate detection window, TTL, lock duration or other properties
- Deleting queues: the script still deletes no queue.
- A drift warning when an existing queue's sessions or duplicate detection differ from the configuration. Wastequip has none, so it is left out (see Open questions in design.md).
- Per-queue overrides for size, TTL, lock duration, delivery count or the duplicate detection window. These stay fixed in the script, as in Wastequip.
- Topics, subscriptions or Service Bus namespace settings.
- Changing `names.queuePrefix`, the naming rules or `uses` semantics. Those belong to `uses-overridable-names`.
- Any Automation, Dashboard API or `RequiredSettings` code change. Using a session-enabled queue needs session-aware triggers, which is outside this change.
