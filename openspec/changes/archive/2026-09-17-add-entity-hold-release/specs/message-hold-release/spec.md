## ADDED Requirements

### Requirement: Held is a first-class message status
The system SHALL define message status `6` as `Held`, meaning a message was captured durably but deliberately not processed because its route is paused. `Held` SHALL be distinct from status `0` (`New`), which remains reachable when processing is interrupted unexpectedly.

#### Scenario: Held is distinguishable from interrupted
- **WHEN** a message is held because its route is paused
- **THEN** its status is `6`, not `0`, so it is distinguishable from a message whose processing was interrupted before it began

### Requirement: Message statuses have a single source of truth
The system SHALL persist the message status catalogue, including `Held` and `Retry Scheduled`, in a status lookup table, and consumers SHALL derive status labels from it rather than from hardcoded literals.

#### Scenario: Dashboard shows the Held label
- **WHEN** the dashboard renders a message at status `6`
- **THEN** it displays the label sourced from the status catalogue

#### Scenario: Adding a status requires no frontend literal
- **WHEN** a new status is added to the catalogue
- **THEN** the dashboard renders its label without a code change to a hardcoded status map

### Requirement: Lifecycle timestamps use a single clock
All message lifecycle timestamps SHALL be recorded in Coordinated Universal Time, regardless of whether they are set by the database or by application code. A message's last-modified timestamp SHALL be populated when the message is inserted, not left unset until its first status change.

#### Scenario: Creation and modification timestamps are comparable
- **WHEN** a message is inserted and later updated
- **THEN** its creation and last-modified timestamps are expressed on the same clock, so the elapsed interval between them is accurate

#### Scenario: Age of a held message is accurate regardless of server configuration
- **WHEN** the age of the oldest held message is reported
- **THEN** the value is correct irrespective of the database server's local time zone

#### Scenario: A never-updated message has a defined last-modified timestamp
- **WHEN** a message has been inserted but has never had a status change
- **THEN** its last-modified timestamp is populated, so predicates over that column include the row

#### Scenario: Every row of a batch update is stamped
- **WHEN** several messages are updated in a single statement, as the release and retry sweeps do when claiming a batch
- **THEN** every affected message has its last-modified timestamp updated, not only one of them

### Requirement: Messages on a paused route are held, not dropped or dead-lettered
When a message's route is not live, the system SHALL insert the message into the message queue table at status `Held` and SHALL settle the transport message normally. It SHALL NOT leave the transport message unsettled, and it SHALL NOT invoke the orchestration.

#### Scenario: Paused route holds the message
- **WHEN** a message arrives for a route whose enabled flag is false
- **THEN** the message is persisted at status `6` with its full payload, the orchestration is not invoked, and the transport message is completed

#### Scenario: Pausing never dead-letters
- **WHEN** a route remains paused across many incoming messages
- **THEN** no message is dead-lettered as a consequence of the pause, and every message is retrievable from the message queue table

#### Scenario: Held messages produce no source notification
- **WHEN** a message is held
- **THEN** no response envelope is sent to the source system's response queue

### Requirement: The hold gate is applied in one place
The system SHALL apply the hold decision within the shared insert-and-process path used by all queue handlers, so that every ingestion entry point observes the same behaviour.

#### Scenario: All handlers honour the gate
- **WHEN** a message for a paused route arrives through any queue handler
- **THEN** it is held with identical behaviour regardless of which handler received it

### Requirement: A route is live only when it has no outstanding backlog
The system SHALL treat a route as live only when it is enabled and has no outstanding backlog, where backlog means messages at the Held status or the Retry Scheduled status. While an enabled route still has a backlog, newly arriving messages for that route SHALL also be held.

#### Scenario: Draining route continues to hold new arrivals
- **WHEN** a route is re-enabled while held messages remain
- **THEN** newly arriving messages for that route are also held, and are drained after the existing backlog

#### Scenario: Pending retries also hold new arrivals
- **WHEN** a route has a message scheduled for retry
- **THEN** newly arriving messages for that route are held, because the retrying message is an older change not yet applied to the destination

#### Scenario: Route becomes live when the backlog clears
- **WHEN** the last held or retry-scheduled message for an enabled route is resolved
- **THEN** subsequently arriving messages for that route are processed directly

#### Scenario: Replay never overtakes newer traffic
- **WHEN** a route is re-enabled and both held messages and new messages exist for the same business record
- **THEN** they are processed in ascending insertion order, so no older message is applied to the destination after a newer one

### Requirement: Held messages are released by a throttled sweep
The system SHALL release held messages using a scheduled sweep that claims a bounded batch per run. The batch size SHALL be configurable.

#### Scenario: Release is bounded per run
- **WHEN** a route has more held messages than the configured batch size
- **THEN** the sweep processes at most the batch size in one run and resumes on the next run

#### Scenario: Release proceeds without an operator request
- **WHEN** an operator enables a paused route
- **THEN** the sweep begins draining that route's held messages without any further operator action

### Requirement: Release claims work atomically and in order
The sweep SHALL claim held messages in ascending insertion order using an atomic claim so that concurrent sweeps cannot process the same message twice.

#### Scenario: Oldest messages are released first
- **WHEN** the sweep claims a batch for a route
- **THEN** it claims the held messages with the lowest insertion order first

#### Scenario: Concurrent sweeps do not double-process
- **WHEN** two sweep runs execute concurrently for the same route
- **THEN** no held message is claimed by both runs

### Requirement: Release stops at the oldest message awaiting retry
The sweep SHALL NOT release a held message while an older message on the same route is awaiting retry. Ordering is only preserved if every unapplied message ahead of a held one reaches the destination first, and a message scheduled for retry has not yet been applied.

The sweep SHALL still release held messages older than that message, so a single message awaiting retry delays only the part of the backlog behind it rather than stalling the whole route.

#### Scenario: A newer held message waits for an older retry
- **WHEN** a route has a message awaiting retry and a held message captured after it
- **THEN** the held message is not released until the older message is no longer awaiting retry

#### Scenario: Older held messages still drain
- **WHEN** a route has a held message captured before a message awaiting retry
- **THEN** that held message is released without waiting

### Requirement: Release continues past individual failures
The sweep SHALL NOT halt the drain when an individual message fails. A failed message SHALL follow the normal failure path and the sweep SHALL continue with the remaining batch, reporting released and failed counts.

#### Scenario: Poison message does not block the backlog
- **WHEN** a message fails during release
- **THEN** it is recorded at its normal failure status and the sweep continues releasing the remaining held messages

#### Scenario: Release outcome is reported
- **WHEN** a sweep run completes
- **THEN** it records how many messages were released successfully and how many failed

### Requirement: Held messages are replayed individually
The system SHALL replay each held message individually and SHALL NOT collapse multiple held messages for the same business record into a single operation.

#### Scenario: A create followed by updates replays in full
- **WHEN** held messages for one business record consist of a create followed by updates
- **THEN** each message is replayed in order, and the create is not discarded
