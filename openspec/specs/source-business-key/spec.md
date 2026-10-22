# source-business-key Specification

## Purpose

Give every routed message a source identifier equal to the business record it concerns (for example the address location id for a customer address), so operators and the release sweep can find all messages for one record together. The delivery identifier (operation id) remains the dedupe key, and is used as the source identifier only as a logged fallback when the business key is missing.

## Requirements

### Requirement: A routed source entity resolves to its business record key

The system SHALL set a message's source identifier from a routed source entity to that entity's
business record key, so that all messages concerning one record can be found together in the
integration. The identifier SHALL be the same for every change to a given record. For customer
address creations and updates the business record key SHALL be the address location identifier, the
same key already used for customer address deletions.

#### Scenario: A customer address creation is identified by its location

- **WHEN** a customer address `OnExternalCreated` event carrying address location identifier `000146087` is ingested
- **THEN** its persisted source identifier is `000146087`, not the event's operation identifier

#### Scenario: A customer address update is identified by its location

- **WHEN** a customer address `OnExternalUpdated` event carrying address location identifier `000146087` is ingested
- **THEN** its persisted source identifier is `000146087`

#### Scenario: The forwarded address message carries the same identifier

- **WHEN** a customer address creation or update with address location identifier `000146087` is forwarded
- **THEN** the forwarded `CustomerAddress` message's source identifier is `000146087` and equals its `AddressLocationId` data field

#### Scenario: Repeated changes to one record share an identifier

- **WHEN** several separate changes to the same customer address, each with a distinct operation identifier, are ingested
- **THEN** every resulting message carries the same source identifier
- **AND** none of them is rejected as a duplicate

#### Scenario: Other routed entities with a populated key are unaffected

- **WHEN** a customer, contact person, party contact, or customer address deletion event carrying a non-blank business key is ingested
- **THEN** its source identifier is that key, resolved from the same attribute as before this change

### Requirement: Every routed entity has a registered business key

Each source entity the system routes SHALL have a registered business key attribute, and each
registered business key SHALL belong to a routed entity. The correspondence SHALL be verified
automatically, so that a missing or misspelled registration is reported rather than silently replaced
by a fallback identifier.

#### Scenario: A routed entity without a registered key is reported

- **WHEN** a source entity is routable but has no registered business key attribute
- **THEN** the automated check fails and identifies the entity by name

#### Scenario: A misspelled registration is reported

- **WHEN** a registered business key names an entity that is not routable
- **THEN** the automated check fails and identifies the registered entity name alongside the routable entity names

#### Scenario: A registered attribute is present in a representative payload

- **WHEN** the registered key attribute for each routed entity is resolved against a representative payload for that entity
- **THEN** it yields a non-empty value

#### Scenario: A routed entity without a representative payload is reported

- **WHEN** a source entity is routable but no representative payload exists for it
- **THEN** the automated check fails and identifies the entity by name rather than skipping it

### Requirement: A missing business key falls back visibly to the delivery identifier

When the business key for a routed message cannot be resolved because the key attribute is absent, null, blank, or whitespace, or because the event carries no target attributes, the system SHALL still ingest the message, SHALL set its source identifier to the event's operation identifier, and SHALL log a warning that names the entity, the business key field, and the fallback identifier used. The system SHALL NOT persist an empty source identifier. Resolving the business key SHALL NOT itself cause ingestion to fail.

#### Scenario: A missing key falls back with a warning

- **WHEN** a customer address event is ingested whose target attributes do not contain `mserp_addresslocationid`
- **THEN** the message is ingested with its operation identifier as the source identifier
- **AND** exactly one warning is logged naming `mserp_wtc_custpostaladdressv2entity`, `mserp_addresslocationid`, and that operation identifier

#### Scenario: A blank key falls back rather than producing an empty identifier

- **WHEN** a routed event is ingested whose business key attribute is present but empty or whitespace
- **THEN** its source identifier is the operation identifier, not an empty string, and a warning is logged

#### Scenario: An event without target attributes does not fail key resolution

- **WHEN** an event for a routed entity carries no target attributes
- **THEN** business key resolution yields no key rather than raising an error, the source identifier is the operation identifier, and a warning is logged

#### Scenario: A resolved key logs no warning

- **WHEN** a routed event is ingested whose business key attribute is populated
- **THEN** no fallback warning is logged

#### Scenario: An unregistered entity yields no key rather than an error

- **WHEN** business key resolution is asked for an entity with no registration
- **THEN** it yields no key and raises no error
