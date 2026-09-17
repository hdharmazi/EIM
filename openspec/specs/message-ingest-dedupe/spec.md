# message-ingest-dedupe Specification

## Purpose
TBD - created by archiving change add-entity-hold-release. Update Purpose after archive.
## Requirements
### Requirement: Duplicate inbound messages are rejected on delivery key
The system SHALL reject an inbound message whose delivery key has already been ingested **and processed beyond ingest** for the same source system and environment, rather than inserting a second message queue row for it.

A delivery key identifies one *delivery* of a change - the source system's operation id, or the transport message id. It SHALL NOT be the business record key, because every change to a given record shares that key and deduplicating on it would reject legitimate updates. Where a source offers no stable delivery key, the message SHALL be ingested without a duplicate check rather than deduplicated on a substitute.

#### Scenario: Re-sent message is not ingested twice
- **WHEN** the source sends a message whose delivery key matches an already-ingested message that has progressed beyond ingest for the same source system and environment
- **THEN** no additional message queue row is created and the transport message is settled normally

#### Scenario: A later change to the same record is ingested
- **WHEN** the source sends a change for a business record that has already been ingested and processed, under a new delivery key
- **THEN** the message is ingested normally and is not rejected as a duplicate

#### Scenario: Duplicate rejection is recorded
- **WHEN** a duplicate is rejected
- **THEN** the system records that a duplicate was received, identifying the delivery key, so the occurrence is diagnosable

#### Scenario: Distinct messages are unaffected
- **WHEN** the source sends messages with distinct delivery keys
- **THEN** each is ingested normally

#### Scenario: A message with no delivery key is always ingested
- **WHEN** an inbound message carries no delivery key
- **THEN** it is ingested without a duplicate check

### Requirement: Messages ingested but never processed are re-driven, not rejected
When an inbound message matches an existing row that has not progressed beyond ingest, the system SHALL re-drive that existing row rather than rejecting the message or creating a duplicate row. A failure between insertion and processing SHALL NOT leave a message permanently unprocessed.

#### Scenario: Crash between insert and processing self-corrects
- **WHEN** a message was inserted but processing never began, and the source system's transport redelivers it
- **THEN** the existing row is processed rather than the message being rejected as a duplicate

#### Scenario: No duplicate row is created on re-drive
- **WHEN** an existing unprocessed row is re-driven
- **THEN** no additional message queue row is created for that delivery key

### Requirement: Duplicate rejection applies while a route is held
Duplicate rejection SHALL apply to messages arriving for a held route, so that a pause window does not accumulate duplicates that would all be replayed on release.

#### Scenario: Re-sends during a pause do not pile up
- **WHEN** the source repeatedly re-sends the same delivery key while its route is paused
- **THEN** only one held message exists for that delivery key

#### Scenario: Release does not emit duplicate destination writes
- **WHEN** a paused route that received re-sends is released
- **THEN** each distinct delivery key results in at most one write to the destination system

