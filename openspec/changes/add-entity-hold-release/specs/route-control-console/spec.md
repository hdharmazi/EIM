## ADDED Requirements

### Requirement: Route list with state
The system SHALL provide a screen listing configured routes with their environment, source system, source entity, orchestration type, enabled state, and lifecycle state of `Paused`, `Draining`, or `Live`.

#### Scenario: Routes displayed with state
- **WHEN** an operator opens the route control screen
- **THEN** each route is shown with its environment, entity, orchestration type, enabled state, and lifecycle state

#### Scenario: Draining is distinguishable from live
- **WHEN** a route is enabled but still has held messages
- **THEN** it is shown as `Draining` rather than `Live`

### Requirement: Pause and resume a route
The system SHALL allow an authorised operator to pause and resume an individual route, and SHALL require explicit confirmation before applying the change.

#### Scenario: Pause a single entity
- **WHEN** an operator pauses the `CustomerAddress` route and confirms
- **THEN** that route becomes `Paused` and other routes on the same queue remain unaffected

#### Scenario: Resume a route
- **WHEN** an operator resumes a paused route and confirms
- **THEN** the route becomes `Draining` and its held messages begin releasing without further action

#### Scenario: Change takes effect promptly
- **WHEN** an operator pauses or resumes a route
- **THEN** the change is applied to message processing without waiting for a cache expiry interval

### Requirement: Held and drain progress are observable
The system SHALL display, per route, the current held message count, the count of messages scheduled for retry, the age of the oldest held message, and the number of messages released and failed since the route was resumed.

#### Scenario: Operator sees the backlog shrinking
- **WHEN** a resumed route is draining
- **THEN** the screen shows the held count decreasing and the released count increasing

#### Scenario: Operator sees release failures
- **WHEN** messages fail during release
- **THEN** the failed count is shown and is visually distinguished

#### Scenario: Stalled drain is visible
- **WHEN** a route's held count is not decreasing
- **THEN** the operator can observe this from the displayed held count and release rate

#### Scenario: Retry-stalled route is diagnosable
- **WHEN** a route is not live solely because messages are scheduled for retry
- **THEN** the screen shows the retry-scheduled count so the cause of the stall is identifiable

### Requirement: Backlog on a deleted route is surfaced
Both sweeps match messages against the registered routes, so a message whose route no longer exists is never claimed by either. The system SHALL show operators any messages waiting at Held or Retry Scheduled whose route is not registered, so that they cannot wait indefinitely without anyone noticing.

#### Scenario: Stranded messages are reported
- **WHEN** messages are waiting at Held or Retry Scheduled and no route is registered for their environment, source system and source entity
- **THEN** the console shows the route they belong to, how many messages are stranded, and how long the oldest has been waiting

#### Scenario: No warning when nothing is stranded
- **WHEN** every waiting message has a registered route
- **THEN** no such warning is shown

### Requirement: Held messages are inspectable
The system SHALL allow operators to filter the message queue list to held messages and to open a held message to inspect its payload and processing log.

#### Scenario: Filter to held messages
- **WHEN** an operator filters the message queue list by the `Held` status
- **THEN** only held messages are listed

#### Scenario: Inspect a held message
- **WHEN** an operator opens a held message
- **THEN** its metadata, payload, and any recorded log detail are displayed

### Requirement: Pausing warns about dependent routes
The system SHALL warn an operator when pausing a route that other configured routes depend on, because messages for the dependent entities may fail rather than wait.

#### Scenario: Warning on pausing a depended-upon route
- **WHEN** an operator pauses a route that another route depends on
- **THEN** a warning identifies the dependent routes before the pause is confirmed
