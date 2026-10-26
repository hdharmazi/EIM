## Context

`script/deployment/Deploy-Wilson.ps1` owns every Service Bus queue a configuration uses. Today all five queues come from one hard-coded function:

```powershell
function Get-QueueNames([string]$Prefix) {
    [ordered]@{
        D365FscDataEventQueue      = "$Prefix-d365fsc-dataevent"
        D365BusinessEventQueue     = "$Prefix-d365fsc-businessevents"
        D365ToCaptureQueue         = "$Prefix-d365fsc-to-capture"
        CaptureToD365Queue         = "$Prefix-capture-to-d365fsc"
        CaptureToD365QueueResponse = "$Prefix-capture-to-d365fsc-response"
    }
}
```

It has four callers:

| Caller | Use |
|---|---|
| `Get-AutomationSettings` | writes the five settings on the Automation app |
| `Get-DashboardApiSettings` | writes four `Simulation__*` settings with the queues of `dashboard.simulationQueuePrefix` |
| `Get-InfraPlan` | creates each missing queue with `az servicebus queue create`: max size 1024, TTL P14D, lock PT60S, max delivery 10, dead-lettering on expiration, no partitioning, and no duplicate detection |
| `Get-ExistingResources` | probes each queue with `az servicebus queue show` |

`QueuePrefix` is `names.queuePrefix` or the environment name. `bat` and `test` share `asb-eim-wtc-nonprod`, so the prefix is what keeps their queues apart.

The live queues in `asb-eim-wtc-nonprod` were checked read-only for the ticket. All of them have:
- `requiresDuplicateDetection = true`
- lock PT1M (the same as PT60S)
- TTL P14D
- max delivery 10
- no sessions

So the script's create path diverges from reality: `prd` would get queues without duplicate detection. Neither sessions nor duplicate detection can be changed after a queue is created.

The application code depends on the setting names, not the queue names:
- `Wilson.Integration.Orchestrations/Configuration/RequiredSettings.cs` → `RequiredSettings.Queues` lists the five setting names. `Wilson.Integration.Automation/Program.cs` fails host startup when any of them is missing.
- The Automation functions bind `%CaptureToD365Queue%`, `%D365FscDataEventQueue%` and `%D365BusinessEventQueue%`.
- `DataEventProcessor` sends to the queue named by `D365ToCaptureQueue`. `QueueCaptureCustomer` replies to the queue named by `CaptureToD365QueueResponse`.
- The Dashboard API reads exactly four keys: `Simulation:D365FscDataEventQueue`, `Simulation:CaptureToD365Queue`, `Simulation:CaptureToD365ResponseQueue` and `Simulation:D365ToCaptureQueue`. Note `ResponseQueue` against the Automation `QueueResponse`.

Reference: Wastequip's `EIM/script/deployment/config/deploy.base.json` has `serviceBusQueues: [{ "name", "requiresSession", "duplicateDetection" }]`. `Deploy-Wastequip.ps1` → `Confirm-ServiceBus` creates only missing queues, with `--duplicate-detection true --duplicate-detection-history-time-window PT5M` unless `duplicateDetection` is `false`, and `--enable-session true` when `requiresSession` is `true`. It leaves existing queues as they are. The user asked for this change to follow that model closely.

## Goals / Non-Goals

**Goals:**
- The queue list lives in configuration, `serviceBusQueues`, shaped like Wastequip's.
- New queues get duplicate detection (PT5M) by default, and sessions on request, with today's other create arguments.
- Existing queues are never changed.
- Queue creation, existence probes, Automation settings and simulation settings all come from that one list.
- Preflight rejects a list the code cannot run with, before any Azure call.
- The resolved names and setting values for `bat`, `test` and `prd` stay exactly as they are today.

**Non-Goals:** see `proposal.md`. In short:
- no change to existing queues
- no drift warning
- no per-queue tuning of size, TTL, lock, delivery count or window
- no application code change
- no naming or `uses` change (owned by `uses-overridable-names`)

## Decisions

### D1. Config shape: a top-level `serviceBusQueues` list, as in Wastequip

`deploy.base.json` gets this list:

```json
"serviceBusQueues": [
  { "name": "d365fsc-dataevent",            "setting": "D365FscDataEventQueue" },
  { "name": "d365fsc-businessevents",       "setting": "D365BusinessEventQueue" },
  { "name": "d365fsc-to-capture",           "setting": "D365ToCaptureQueue" },
  { "name": "capture-to-d365fsc",           "setting": "CaptureToD365Queue" },
  { "name": "capture-to-d365fsc-response",  "setting": "CaptureToD365QueueResponse" }
]
```

The order matches today's `Get-QueueNames`, so the plan order and the settings order do not change.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `name` | string, required | | The queue name **without** the prefix. The resolved name is `<QueuePrefix>-<name>`. |
| `setting` | string, optional | none | The app setting that carries the resolved name on the Automation app. Without it the queue is created and probed, but no setting is written. |
| `requiresSession` | JSON boolean, optional | `false` | `--enable-session true` on create |
| `duplicateDetection` | JSON boolean, optional | `true` | `--duplicate-detection true --duplicate-detection-history-time-window PT5M` on create |

The key is top-level, like Wastequip's, rather than nested under `serviceBus` (which holds `sku`). Keeping the name lets the two repositories read the same.

Wilson deviates from Wastequip in three ways, and each is needed here:
- `name` is a suffix, because the prefix separates `bat` and `test` on the shared namespace.
- `setting` exists, because Wilson's code finds its queues through app settings.
- The five settings the code requires are enforced (D5).

Alternatives considered:
- **A map keyed by setting name**, so an environment file could add one queue through object merge. Rejected for two reasons. It differs from Wastequip. And a new queue nearly always comes with a new function in the shared package, so it belongs in `deploy.base.json` anyway.
- **Full names with a `{prefix}` token.** Rejected: one more mini-language for no gain. Every queue in Wilson is per environment.
- **Keep the list in the script, with flags only in config.** Rejected: that does not meet the goal of adding a queue without a code change.

### D2. Merge semantics: an environment file replaces the whole list

`Merge-ConfigObject` replaces lists, and that stays. An environment that adds or adjusts a queue copies the full base list into its `deploy.<name>.json` and edits it. This is the same rule `automation.gatedFunctions` already follows. Preflight (D5) catches a copy that drops a required queue.

The shipped environment files do not override `serviceBusQueues`. The docs explain how to add a queue:
- normally, add it in `deploy.base.json`, together with the function and setting that use it
- only when one environment must differ, put a full-list override in that environment's file

### D3. Create arguments: today's arguments, plus Wastequip's session and duplicate-detection switches

`Get-InfraPlan` creates each missing queue with today's arguments:

```
servicebus queue create -g <rg> --namespace-name <ns> -n <prefix>-<name>
  --max-size 1024 --default-message-time-to-live P14D --lock-duration PT60S --max-delivery-count 10
  --enable-dead-lettering-on-message-expiration true --enable-partitioning false
```

It then appends, in this order:
- `--enable-session true` when `requiresSession` is `true`
- `--duplicate-detection true --duplicate-detection-history-time-window PT5M` unless `duplicateDetection` is `false`

and finishes with `-o none`.

The existing arguments stay as they are because they already match the live queues: TTL P14D, lock PT1M (= PT60S), delivery 10. Wastequip's `Confirm-ServiceBus` passes no TTL or dead-lettering flags, but copying that would make new Wilson queues differ from the live ones. The user asked to keep the create arguments, so only the session and duplicate-detection switches come from Wastequip.

The window is a script constant, `$QueueDuplicateDetectionWindow = 'PT5M'`, as in Wastequip. It is not a config key.

The window on the existing live queues was not captured in the read-only check. It is only a manual verification item (task 3.2): existing queues are not changed, so a different value there needs no code. If the user wants `prd` to match a different live window, the constant changes.

The step label stays `create queue <prefix>-<name>`, so the existing plan assertions keep passing. A suffix is added only for non-default queues: ` (sessions)` and/or ` (no duplicate detection)`. That way `-WhatIf` shows the irreversible choices.

### D4. Existing queues are left as found

As before, and as in Wastequip, `Get-InfraPlan` plans no step for a queue that `Get-ExistingResources` reports as existing. It does not update or recreate it, whatever its sessions, duplicate detection or other properties. The probe stays a plain existence check (`Test-Az servicebus queue show`), now over the resolved list.

A warn-only drift report was considered and left out. Wastequip has none, and the user asked to deviate from Wastequip only where Wilson needs it. It is listed under Open questions.

### D5. Validation: a pure `Get-QueueConfigProblems`, run in offline preflight

`Get-QueueConfigProblems($Config, [string]$Prefix)` returns messages that name the offending entry by index and value. It never includes secrets, because there are none here. It is pure, so `Test-DeployWilson.ps1` covers it directly.

`Get-OfflinePreflightProblems` calls it on every run, before sign-in. That run uses the configuration with its overrides and its resolved `QueuePrefix`.

Each problem is reported for a configuration `deploy.<env>.json`:

| Problem | Rule |
|---|---|
| Missing or empty list | `serviceBusQueues` is absent, not a list, or empty |
| Entry is not an object | the entry is not a JSON object |
| Unknown key | the entry has a key other than `name`, `setting`, `requiresSession`, `duplicateDetection`. A typo such as `duplicateDetecton: false` would otherwise create an irreversible queue silently. |
| Blank or invalid name | `name` is blank or does not match `^[a-z0-9]+(-[a-z0-9]+)*$`. Lowercase and hyphenated, like every current queue. |
| Resolved name too long | `<prefix>-<name>` exceeds 260 characters, the Service Bus entity-path limit |
| Duplicate name | two entries resolve to the same name, compared case-insensitively. Service Bus names are case-insensitive. |
| Invalid setting name | `setting` is present but does not match `^[A-Za-z_][A-Za-z0-9_]*$`. Valid as an app setting and a `%name%` binding. |
| Duplicate setting | two entries name the same `setting`, compared case-insensitively. App settings are case-insensitive. |
| Flag not a boolean | `requiresSession` or `duplicateDetection` is present and is not a JSON boolean. Same rule as `automation.triggersEnabled`. |
| Required setting missing | `D365FscDataEventQueue`, `D365BusinessEventQueue`, `D365ToCaptureQueue`, `CaptureToD365Queue` or `CaptureToD365QueueResponse` is not the `setting` of any entry. These five are `$RequiredQueueSettings` in the script, mirroring `RequiredSettings.Queues`; `DeploymentConfigTests` keeps the two in step. |

Sessions on a required queue are not rejected. Wilson's triggers are not session-aware, so the docs say plainly that `requiresSession: true` needs a session-enabled function. The C# test in D7 checks the shipped list has no sessions.

`Get-RequiredConfigPaths` adds `serviceBusQueues` when `infra` or `settings` is selected, so a `<...>` placeholder in the list is reported like any other.

The pure builders also guard themselves. `Get-ServiceBusQueues` throws on the first problem, using the same messages, so a code path that skips preflight cannot build a bad plan. Every changing component already brings preflight in.

### D6. One resolver feeds every caller

`Get-QueueNames` is removed. These pure helpers replace it:

- `Get-ServiceBusQueues($Config, [string]$Prefix)` returns, in config order, `[pscustomobject]@{ Name; Setting; RequiresSession; DuplicateDetection }`, with the defaults applied and `Name = "$Prefix-$($entry.name)"`. It throws on any `Get-QueueConfigProblems` problem.
- `Get-QueueSettings($Queues)` returns an ordered map from setting to resolved name, for the entries that have a `setting`.

Callers:

| Caller | Change |
|---|---|
| `Get-AutomationSettings` | Writes `Get-QueueSettings (Get-ServiceBusQueues $Config $Names.QueuePrefix)` where the five fixed keys were. For the shipped config the keys, values and order are unchanged. |
| `Get-InfraPlan` | Loops over `Get-ServiceBusQueues $Config $Names.QueuePrefix` (D3, D4). |
| `Get-ExistingResources` | Gains `$Config`: `Get-ExistingResources($Config, $Names, [bool]$IsOwner)`. It probes every resolved name. `Invoke-InfraComponent` passes `$Config`. |
| `Get-DashboardApiSettings` | See below. |

For `Get-DashboardApiSettings`, the simulation keeps a fixed table in the script. The Dashboard API reads exactly these keys:

```powershell
$SimulationQueueSettings = [ordered]@{
    Simulation__D365FscDataEventQueue      = 'D365FscDataEventQueue'
    Simulation__CaptureToD365Queue         = 'CaptureToD365Queue'
    Simulation__CaptureToD365ResponseQueue = 'CaptureToD365QueueResponse'
    Simulation__D365ToCaptureQueue         = 'D365ToCaptureQueue'
}
```

All four source settings are required (D5), so the lookup always succeeds after validation.

The queues are resolved from the configuration that owns `dashboard.simulationQueuePrefix`. For prefix `test`, that is `test`'s merged configuration. An environment file that overrides the list therefore also gets the right simulation names.

To support this:
- `Get-SimulationQueuePrefixes($Config, $AllConfigs)` is replaced by `Get-SimulationTargets($Config, $AllConfigs)`. It returns an ordered map from prefix to configuration: the owner first, then each configuration that `uses` it.
- `Get-DashboardApiSettings($Config, $Names, $SimulationTargets)` takes that map. Without it, the default is the configuration's own prefix and configuration.
- The allowed-prefix check and its message are unchanged; the prefixes are the map's keys.
- The callers in `Invoke-PreflightComponent` and `Invoke-SettingsComponent` pass `Get-SimulationTargets`.

Alternative considered: keep `Get-SimulationQueuePrefixes` and resolve every prefix with the owner's list. That is simpler, but it would silently use the wrong names when a using configuration overrides the list. Rejected.

### D7. Test coverage

`script/deployment/Test-DeployWilson.ps1` is pure and dot-sourced, with no Azure calls. It covers:
- the resolved queues for `bat`, `test` and `prd`
- the plan arguments: default, sessions, no duplicate detection
- no step for existing queues
- every validation rule in D5
- list replacement through an override
- an added sixth queue that flows to the plan and the settings
- the simulation mapping and target configuration
- unchanged Automation settings for `bat`

The existing uses of `Get-QueueNames` move to `Get-ServiceBusQueues` and `Get-QueueSettings`.

`Wilson.Integration.Tests/DeploymentConfigTests.cs` reads the committed files, so a bad edit fails the build. New tests:
- Every configuration (merged base + own file) has a `serviceBusQueues` list whose `setting` values include every entry of `RequiredSettings.Queues`, with unique names and settings.
- The base list is exactly the five shipped suffix/setting pairs, with no sessions and duplicate detection not turned off. Renaming a shipped queue would create a new empty queue and strand messages on the old one, so it has to be a deliberate test change.
- Every `ServiceBusTriggerAttribute.QueueName` of the form `%X%` on an Automation function names a `setting` in the base list. This uses `AutomationFunctionNamesTests.Functions()`.

### D8. Compatibility and rollout

- **Deployed apps:** for the shipped config, `settings` writes identical Automation and dashboard values, so no app restart changes behaviour. `DeploymentConfigTests` and the `bat` settings assertion guard this.
- **Existing queues:** nothing changes. `bat-*` and `test-*` already exist, so `infra` plans no queue step for them.
- **`prd`:** the first `infra` creates `prd-*` with duplicate detection (PT5M) and no sessions, matching non-production.
- **Package:** the package carries `Deploy-Wilson.ps1` and `config/` together (`New-DeploymentPackage.ps1`), so a package always has a matching script and list. An older package keeps its hard-coded list and is unaffected.
- **Rollback:** revert the commit. Queues created in the meantime keep duplicate detection, which is harmless and matches non-production.
- No SQL migrations, pipeline changes or Azure changes outside `infra`.

### D9. Message-processing behaviour

The change affects the broker only, not message handling.

With duplicate detection on, Service Bus silently drops a second message with the same `MessageId` sent within PT5M. What this means for each sender:
- **Wilson's own senders** (`ARM.Integration.Core/Helpers/ServiceBusHelper.cs`) set `MessageId` to a new GUID unless a caller passes one, and no production caller does. Retries and reprocessing therefore always use a new id and are never dropped.
- **The dashboard simulation** uses `SIM-<guid>`.
- **External senders** (D365 data and business events, Capture) that reuse a `MessageId` within five minutes would have the repeat dropped by the broker. That is the same behaviour bat and test already have, and it is the reason the queues use duplicate detection.

There is no change to terminal or transient classification, retries, `IngestGate`/`HoldGate`, idempotency or ordering. Sessions stay off for every shipped queue.

## Risks / Trade-offs

- **[A wrong flag creates an irreversible queue]** → Unknown keys and non-boolean flags fail preflight. `-WhatIf` labels show non-default queues. The C# test pins the shipped list. The docs state that sessions and duplicate detection cannot change later.
- **[An environment override drops a required queue, since lists replace]** → Offline preflight fails, naming the missing setting. `DeploymentConfigTests` checks every committed configuration.
- **[A renamed `name` creates a new queue and strands the old one]** → The C# test pins the five shipped pairs. The docs explain that renaming means a new queue and a manual drain of the old one.
- **[The live window differs from PT5M]** → Existing queues are untouched, so nothing breaks. Only `prd` gets PT5M. Task 3.2 records the live value. Changing the constant is a one-line follow-up if wanted.
- **[A `uses` configuration overrides the list with different names]** → The simulation resolves through the target configuration (D6), and the owner's queues are unaffected. The configuration's own Automation app reads its own list.
- **[Coordination with `uses-overridable-names`, which also edits `Deploy-Wilson.ps1`]** → The two changes touch different functions. That change edits naming, `uses` and the inherited keys; this one edits the queue helpers, `Get-InfraPlan`'s queue loop, `Get-ExistingResources`' queue probe and the simulation helpers. The deltas are on different requirements. Whichever lands second rebases. Expect textual conflicts only around `Get-ExistingResources` and `Get-InfraPlan`.

## Open Questions

- **Drift warning.** Should preflight or `infra` warn, never fix, when an existing queue's `requiresSession` or `requiresDuplicateDetection` differs from the configuration? Wastequip has no such warning, so it is out of scope here. It would cost one `queue show` per queue, which is already made.
