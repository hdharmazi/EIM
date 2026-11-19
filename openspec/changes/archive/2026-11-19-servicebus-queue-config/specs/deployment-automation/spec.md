## MODIFIED Requirements

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

## ADDED Requirements

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
