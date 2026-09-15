## ADDED Requirements

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
enabled state and setting an expiry date. The function key itself SHALL NOT be created, displayed or
stored by the dashboard.

#### Scenario: Consumer created
- **WHEN** an administrator submits a new consumer with an identifier and system profile
- **THEN** the consumer is created and appears in the list

#### Scenario: Consumer disabled
- **WHEN** an administrator toggles a consumer to disabled
- **THEN** the change is persisted and subsequent gateway requests for that consumer are refused

#### Scenario: Key never handled by the dashboard
- **WHEN** an administrator views or edits a consumer
- **THEN** no function key value is shown, entered or stored, and the screen directs key management to Azure

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
