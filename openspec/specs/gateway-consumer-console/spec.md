# gateway-consumer-console Specification

## Purpose
TBD - created by archiving change add-d365fsc-passthrough-gateway. Update Purpose after archive.
## Requirements
### Requirement: Gateway consumer list
The dashboard SHALL provide a screen listing gateway consumers, showing the consumer identifier,
description, system profile, enabled state, expiry and whether key verification is active.

#### Scenario: Consumers listed
- **WHEN** an authorised administrator opens the Gateway Consumers screen
- **THEN** all consumers are listed with their identifier, profile, enabled state and expiry

#### Scenario: Unverified identity surfaced
- **WHEN** a consumer has no key hash recorded
- **THEN** the list indicates that the consumer's identity is unverified

### Requirement: Gateway consumer management
The dashboard SHALL allow creating, editing and deleting gateway consumers, including toggling
enabled state and setting an expiry date.

#### Scenario: Consumer created
- **WHEN** an administrator submits a new consumer with an identifier and system profile
- **THEN** the consumer is created and appears in the list

#### Scenario: Consumer disabled
- **WHEN** an administrator toggles a consumer to disabled
- **THEN** the change is persisted and subsequent gateway requests for that consumer are refused

### Requirement: Function key registered without being stored
The dashboard SHALL allow an administrator to register a consumer's function key so that identity
verification can be enabled without database access. The submitted value SHALL be hashed on the
server, only the hash SHALL be persisted, and the raw value SHALL NOT be stored, logged or returned
by any endpoint.

Before storing, the dashboard SHALL verify the key against the configured gateway application and
SHALL refuse to store a key that cannot be shown to be accepted by it. The verification
request SHALL omit the consumer identifier header so that it is refused by the gateway before any
consumer lookup, grant evaluation, or environment credential resolution occurs.

#### Scenario: Key registered
- **WHEN** an administrator submits a function key for a consumer and the gateway application accepts it
- **THEN** its hash is stored, the consumer is reported as verified, and the response contains no key material

#### Scenario: Key is never readable afterwards
- **WHEN** an administrator views a consumer after registering a key
- **THEN** the screen reports only whether identity is verified, and no endpoint returns the key or its hash

#### Scenario: A hash submitted in place of a key is rejected
- **WHEN** an administrator submits a value that is already a hexadecimal digest rather than a key
- **THEN** the request is refused, because storing the hash of a hash would silently never match

#### Scenario: A key no gateway issued is refused
- **WHEN** the gateway application rejects the submitted key as unauthorised
- **THEN** the key is not stored, the consumer's existing key hash is left unchanged, and the administrator is told the key was rejected

#### Scenario: Verification does not reach D365 or read credentials
- **WHEN** the dashboard verifies a key against a gateway application
- **THEN** no D365 request is made, no environment credential is read, and no idempotency record is written

#### Scenario: A responder that is not the gateway is never accepted
- **WHEN** the address answers but the response does not carry the gateway's correlation header
- **THEN** the key is not stored and the result is reported as unable to check, because every application under the same domain answers and "not refused" is not proof of a gateway

### Requirement: Permission matrix per environment
The dashboard SHALL present permissions for a selected consumer and environment as a matrix of
resources against verbs, where each ticked cell corresponds to one permission grant.

#### Scenario: Verb granted
- **WHEN** an administrator ticks GET for a data entity
- **THEN** a permission grant is created for that consumer, environment, resource and verb

#### Scenario: Verb revoked
- **WHEN** an administrator unticks a previously granted verb
- **THEN** the corresponding grant is deleted and the consumer is refused that operation

#### Scenario: Resource removed when last verb is revoked
- **WHEN** an administrator unticks the final granted verb for a resource
- **THEN** the resource no longer appears in the matrix

#### Scenario: Environment scoping
- **WHEN** an administrator switches the selected environment
- **THEN** the matrix shows only the grants belonging to that environment

### Requirement: DELETE presented but not grantable
The permission matrix SHALL display the DELETE verb in a disabled state, so that the authorization
model is visible without the verb being grantable.

#### Scenario: DELETE cannot be ticked
- **WHEN** an administrator attempts to tick DELETE
- **THEN** the control is unavailable and no grant is created

### Requirement: Entity selection from D365 metadata
When adding a data entity to the matrix, the dashboard SHALL offer entity names retrieved from the
selected environment's D365 OData service document, and SHALL store the canonical name as returned
by D365. Multiple entities SHALL be selectable in one operation.

#### Scenario: Entity chosen from the picker
- **WHEN** an administrator types part of an entity name
- **THEN** matching entity names from the selected environment are offered for selection

#### Scenario: Canonical spelling stored
- **WHEN** an administrator selects an entity from the picker
- **THEN** the grant stores the entity name exactly as D365 reports it

#### Scenario: Bulk selection
- **WHEN** an administrator selects several entities and applies a verb
- **THEN** one explicit grant is created per selected entity

#### Scenario: Metadata unavailable
- **WHEN** the service document cannot be retrieved for the selected environment
- **THEN** the screen reports the failure and does not silently fall back to unvalidated free text

### Requirement: Service and action grants
The dashboard SHALL allow adding custom service operations and bound actions as free text, with an
action that verifies the target is recognised by the selected environment.

#### Scenario: Service operation added
- **WHEN** an administrator enters a service group, service and operation and grants it
- **THEN** a grant is created with resource type SERVICE

#### Scenario: Target verified
- **WHEN** an administrator uses the verify action on an entered service operation
- **THEN** the screen reports whether the selected environment recognises it

### Requirement: Grant provenance displayed
The permission screen SHALL show who created each grant and when.

#### Scenario: Provenance visible
- **WHEN** an administrator inspects a grant
- **THEN** the creating identity and creation timestamp are shown

### Requirement: Administrative authorization
Gateway consumer and permission screens SHALL be restricted to administrators.

#### Scenario: Non-administrator denied
- **WHEN** a validated non-administrator user requests a gateway management screen or endpoint
- **THEN** access is refused

### Requirement: Production grant notification
Creating or deleting a permission grant for a production environment SHALL raise a notification
event, without blocking the operation.

#### Scenario: Production grant recorded and notified
- **WHEN** an administrator grants a verb in a production environment
- **THEN** the grant is applied and a notification event is raised identifying the administrator, consumer, resource and verb

#### Scenario: Non-production grant not notified
- **WHEN** an administrator grants a verb in a non-production environment
- **THEN** the grant is applied and no notification event is raised

### Requirement: Gateway application recorded from verification
The dashboard SHALL record when a consumer's key was verified and against which gateway application,
determined by the verification attempt rather than supplied by the administrator. A consumer whose key
predates verification, or whose key was recorded without being checked, SHALL report its verification
state as unknown rather than assuming one.

#### Scenario: Accepting application recorded
- **WHEN** the gateway application accepts a submitted key
- **THEN** that application and the time of verification are stored against the consumer and shown in the consumer list

#### Scenario: Application not asserted by the administrator
- **WHEN** an administrator registers a key
- **THEN** no field is offered for declaring the gateway application, because a declared value could contradict the key

#### Scenario: Legacy registration reports unknown
- **WHEN** a consumer has a key hash recorded but no verification result
- **THEN** its verification state is shown as unknown and is visually distinct from a verified consumer

#### Scenario: Unchecked registration is not shown as verified
- **WHEN** a key was stored without being checked against a gateway application
- **THEN** the consumer is shown as recorded but unchecked, and never as verified

### Requirement: Unreachable gateway distinguished from rejected key
The dashboard SHALL distinguish a key that the gateway refused from a key that could not be checked
because the gateway was unreachable. Both SHALL refuse to store the key, and the administrator SHALL be
told which of the two occurred.

#### Scenario: Gateway not reachable
- **WHEN** the gateway application fails to respond or times out
- **THEN** the key is not stored and the administrator is told the key could not be checked, not that it was rejected

#### Scenario: Slow gateway is not a rejection
- **WHEN** the gateway application does not answer within the configured timeout
- **THEN** it counts as unreachable rather than as having rejected the key

#### Scenario: A loopback gateway records the key without verifying it
- **WHEN** the configured gateway address is a loopback address
- **THEN** the key is stored without being probed, and is recorded as unchecked rather than verified, because a local Functions host does not enforce keys and would accept any value

### Requirement: Verification reported as a point in time
The dashboard SHALL present key verification as having occurred at a recorded time rather than as a
continuing guarantee, because a key may be deleted in Azure after it is registered.

#### Scenario: Verification date shown
- **WHEN** an administrator views a consumer whose key was verified
- **THEN** the date of verification is shown alongside the verified state

#### Scenario: Key registration warns about replacement
- **WHEN** an administrator opens the key dialog for a consumer that already has a key registered
- **THEN** the dialog states that registering a different key will stop the current key being accepted

