## ADDED Requirements

### Requirement: Address deletions are recognised from a dedicated soft-delete entity

D365 F&SC does not emit a delete event for customer postal addresses. The system SHALL treat events
on the dedicated soft-delete source entity `mserp_wtc_custpostaladdressdeletedv2entity` as customer
address deletions, and SHALL route them to Capture.

#### Scenario: A soft-delete event is routed

- **WHEN** a D365 data event arrives whose primary entity is `mserp_wtc_custpostaladdressdeletedv2entity`
- **THEN** the system routes it to an orchestration rather than discarding it as an unsupported entity

#### Scenario: The New/Update route is unaffected

- **WHEN** a D365 data event arrives whose primary entity is `mserp_wtc_custpostaladdressv2entity`
- **THEN** it is handled exactly as before, with its action derived from the event's message name

### Requirement: The delete action is constant, not derived from the event message name

Soft-delete events arrive with a message name indicating an update. The system SHALL emit
`Action = "Delete"` for every event on the soft-delete entity, regardless of the message name carried
by the event. The action SHALL NOT be derived from the message name for this entity.

#### Scenario: An update-shaped event yields a delete

- **WHEN** a soft-delete event arrives with message name `OnExternalUpdated`
- **THEN** the message forwarded to Capture has `Action` equal to `Delete`

#### Scenario: Any other message name yields a delete

- **WHEN** a soft-delete event arrives with a message name indicating creation or deletion instead of update
- **THEN** the message forwarded to Capture still has `Action` equal to `Delete`

#### Scenario: Changed-field tracking does not suppress fields

- **WHEN** a soft-delete event is forwarded and the event's changed-field list omits one of the mapped address fields
- **THEN** that field is still populated in the forwarded message from the event's attributes

### Requirement: Deletions are forwarded on the existing customer address entity and contract

The forwarded message SHALL use the same destination entity name and the same data contract as
customer address creations and updates, so that Capture sees a single entity carrying three actions.

#### Scenario: Envelope identifies the customer address entity

- **WHEN** a soft-delete event is forwarded to Capture
- **THEN** the envelope's entity is `CustomerAddress`, its data fields match those sent for a creation or update of the same address, and its creation timestamp is taken from the source event

### Requirement: A deletion is identified by the address location business key

The system SHALL set the forwarded message's source identifier to the address location identifier
carried by the event, rather than to an operation identifier. Because that value identifies the
record to delete, the system SHALL reject an event whose address location identifier is missing or
blank rather than substituting a fallback identifier.

#### Scenario: Source identifier is the business key

- **WHEN** a soft-delete event carrying address location identifier `000146089` is forwarded
- **THEN** the forwarded message's source identifier is `000146089`

#### Scenario: Missing business key is rejected

- **WHEN** a soft-delete event arrives whose address location identifier is absent or blank
- **THEN** the event is not forwarded to Capture and no fallback identifier is substituted

#### Scenario: Identifier survives persistence

- **WHEN** a soft-delete event is persisted and later released or reprocessed
- **THEN** the persisted source entity name equals `mserp_wtc_custpostaladdressdeletedv2entity` exactly, without truncation

### Requirement: Only events flagged as deleted are forwarded

The system SHALL forward an event from the soft-delete entity only when its deleted flag indicates a
deletion. An event on that entity whose flag indicates otherwise SHALL NOT be forwarded, and SHALL be
recorded as an anomaly that is distinguishable from an ordinary skipped message.

#### Scenario: Flagged as deleted

- **WHEN** a soft-delete event carries a deleted flag whose value means yes
- **THEN** the message is forwarded to Capture with `Action` equal to `Delete`

#### Scenario: Not flagged as deleted

- **WHEN** a soft-delete event carries a deleted flag whose value does not mean yes
- **THEN** no message is sent to Capture, and the message record carries a step logged at warning severity naming the observed flag value

#### Scenario: An anomaly is not reported as a routine skip

- **WHEN** an event is withheld because its deleted flag was unexpected
- **THEN** the recorded reason distinguishes it from a message withheld because no tracked field changed

### Requirement: The deletion route is independently controllable

The deletion route SHALL be registered as its own route, so that it can be enabled, disabled, held,
and observed independently of the customer address creation and update route.

#### Scenario: Deletions pause independently

- **WHEN** an operator disables the customer address deletion route
- **THEN** deletion events are held and customer address creations and updates continue to flow

#### Scenario: Creations and updates pause independently

- **WHEN** an operator disables the customer address creation and update route
- **THEN** creations and updates are held and deletion events continue to flow

#### Scenario: Pausing one address route warns about the other

- **WHEN** an operator pauses either customer address route in the console
- **THEN** the console identifies the other customer address route as a dependent route

#### Scenario: Route is available in every environment

- **WHEN** the route registry is seeded
- **THEN** a deletion route exists and is enabled in each configured environment
