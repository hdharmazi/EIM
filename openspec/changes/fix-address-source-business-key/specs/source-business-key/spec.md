## ADDED Requirements

### Requirement: A routed source entity resolves to its business record key

The system SHALL identify a message from a routed source entity by that entity's business record
key. The identifier SHALL be the same for every change to a given record, so that all messages
concerning one record can be found together.

#### Scenario: A customer address message is identified by its location

- **WHEN** a customer address event is ingested and forwarded
- **THEN** its persisted source identifier and the source identifier on the forwarded message are both the address location identifier

#### Scenario: Repeated changes to one record share an identifier

- **WHEN** several separate changes to the same customer address are ingested
- **THEN** every resulting message carries the same source identifier

#### Scenario: Other routed entities are unaffected

- **WHEN** a customer, contact person, or party contact event is ingested
- **THEN** its source identifier is unchanged from before this change

### Requirement: Every routed entity has a registered business key

Each source entity the system routes SHALL have a registered business key attribute. The
correspondence between routed entities and registered keys SHALL be verified automatically, so that
a missing or misspelled registration is reported rather than silently replaced by a fallback
identifier.

#### Scenario: A routed entity without a registered key is reported

- **WHEN** a source entity is routable but has no registered business key attribute
- **THEN** the discrepancy is reported by an automated check that identifies the entity by name

#### Scenario: A misspelled registration is reported

- **WHEN** a registered key names an entity that is not routable, or a routable entity is registered under a different spelling
- **THEN** the automated check fails and identifies both the routed name and the registered name

#### Scenario: A registered attribute is present in real payloads

- **WHEN** the registered key attribute for a routed entity is checked against a representative payload for that entity
- **THEN** the attribute is present and yields a non-empty value

### Requirement: Falling back to a delivery identifier is deliberate and visible

The system SHALL NOT fall back to a delivery-scoped identifier as an undetected consequence of a
missing business key registration. Where an entity deliberately has no business record key, the
system MAY identify its messages by a delivery-scoped identifier.

#### Scenario: An unregistered entity still produces a message

- **WHEN** a message is ingested for an entity that has deliberately no business key
- **THEN** the message is still ingested and identified by its delivery identifier rather than rejected

#### Scenario: A missing registration does not reach production unnoticed

- **WHEN** a routed entity's business key registration is absent
- **THEN** the automated correspondence check fails before the fallback can be relied upon in a running environment
