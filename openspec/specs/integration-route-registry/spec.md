# integration-route-registry Specification

## Purpose
TBD - created by archiving change add-entity-hold-release. Update Purpose after archive.
## Requirements
### Requirement: Routes are resolved from a data store
The system SHALL resolve an inbound message to an orchestration type using a persisted route registry keyed on environment, source system, and source entity, rather than a hardcoded mapping in host or handler code.

#### Scenario: Known entity resolves to an orchestration
- **WHEN** a message arrives with source system `CAPTURE` and entity `CustomerAddress` in environment `dev2`
- **THEN** the resolver returns the orchestration type registered for that combination

#### Scenario: Adding an entity requires no code change
- **WHEN** a new route row is added to the registry for an entity whose orchestration is already registered
- **THEN** messages for that entity are routed to that orchestration without redeploying the Functions host

### Requirement: Routes are scoped per environment
The route registry SHALL key routes on environment so that the same entity can be enabled in one environment and disabled in another.

#### Scenario: Paused in one environment only
- **WHEN** the `CustomerAddress` route is disabled in `prod` and enabled in `dev2`
- **THEN** `prod` messages for that entity are held and `dev2` messages are processed normally

### Requirement: Unresolvable routes are an explicit error
The system SHALL treat a message whose route cannot be resolved as an error and SHALL log it. An unresolvable route SHALL NOT be silently skipped and SHALL NOT be treated as an implicit hold.

#### Scenario: Unknown entity
- **WHEN** a message arrives with an entity that has no route registered for its environment and source system
- **THEN** the system records an error identifying the unresolved entity, and does not treat the message as held

### Requirement: Route resolution is cached
The system SHALL cache route resolution with a bounded time-to-live and SHALL NOT query the route store on every message.

#### Scenario: Repeated messages do not re-query
- **WHEN** many messages for the same route arrive within the cache time-to-live
- **THEN** the route store is queried at most once for that route within that window

#### Scenario: Operator change applies without waiting for expiry
- **WHEN** an operator changes a route's enabled state
- **THEN** the cache is invalidated so the change takes effect without waiting for the time-to-live to elapse

### Requirement: Route identifiers persist without truncation
Source entity and destination entity identifiers SHALL be persisted at their full configured length, so that a stored value always matches the route it was resolved from. Persistence SHALL NOT silently shorten an identifier.

#### Scenario: A long entity name round-trips intact
- **WHEN** a message is ingested for a route whose source entity name is longer than twenty characters
- **THEN** the persisted source entity equals the configured route's source entity exactly

#### Scenario: Long-named routes remain releasable
- **WHEN** messages are held for a route whose source entity name is longer than twenty characters and the route is subsequently enabled
- **THEN** those messages are matched by the release sweep and are drained

### Requirement: Routes carry an enabled state and a pause policy
Each route SHALL carry an enabled flag and a pause policy. The pause policy SHALL be persisted from the outset, and `Hold` SHALL be the only policy supported by this change.

#### Scenario: Default route state
- **WHEN** a route is seeded from the previously hardcoded mapping
- **THEN** it is enabled and its pause policy is `Hold`, reproducing existing behaviour

