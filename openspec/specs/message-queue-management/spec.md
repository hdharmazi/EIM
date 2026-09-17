# message-queue-management Specification

## Purpose

Provide the core Message Queue screens: filtered/searchable list, message detail (log, payload, processing steps), payload editing, reprocess, and a cleanup utility.
## Requirements
### Requirement: Message list with filter and search
The system SHALL provide a message queue list screen supporting status filtering and free-text search over source ID, type, and system, sourced from `api/messagequeue`. The list SHALL show ID, type, source/destination system, source ID, environment, payload preview, status, and modified time.

#### Scenario: Filter by status
- **WHEN** a user selects a status filter
- **THEN** the list shows only messages with the selected status

#### Scenario: Search messages
- **WHEN** a user enters a search term
- **THEN** the list shows only messages matching the term across source ID, type, or system

#### Scenario: Status deep link from dashboard
- **WHEN** the list is opened with a status query parameter
- **THEN** the list initializes with that status filter applied

#### Scenario: Error rows highlighted
- **WHEN** a message has an error
- **THEN** its row is visually flagged as an error

### Requirement: Message detail view
The system SHALL provide a message detail screen showing metadata, error/message log, payload, and processing steps, sourced from `api/messagequeue/{id}` and `api/messagequeue/{id}/log-details`.

#### Scenario: Detail displayed
- **WHEN** a user opens a message detail
- **THEN** the screen shows type, source/destination, queues, environment, timestamps, log, formatted payload, and numbered processing steps

#### Scenario: Payload pretty-printed
- **WHEN** the payload is valid JSON
- **THEN** it is displayed formatted and syntax-highlighted

### Requirement: Payload edit
The system SHALL allow editing and saving a message payload via `api/messagequeue/{id}/payload`.

#### Scenario: Save edited payload
- **WHEN** a user edits the payload and saves
- **THEN** the updated payload is persisted and the detail view reflects the change

### Requirement: Reprocess message
The system SHALL allow reprocessing a message via `api/messagequeue/{id}/reprocess` with a confirmation step. Reprocess SHALL be permitted for messages at the Held status in addition to the failed and dead-letter statuses, so that an operator can validate a fix against a single held message before resuming its route.

#### Scenario: Reprocess with confirmation
- **WHEN** a user confirms reprocessing a message
- **THEN** the reprocess request is sent and the outcome (success or failure) is shown

#### Scenario: Reprocess a held message as a canary
- **WHEN** a user reprocesses a single message at the Held status while its route remains paused
- **THEN** that message is processed and its outcome is shown, and other held messages for the route remain held

#### Scenario: Reprocess rejected for ineligible status
- **WHEN** a user attempts to reprocess a message whose status is not eligible for reprocessing
- **THEN** the request is rejected and the reason is reported

### Requirement: Cleanup utility
The system SHALL provide a cleanup screen showing current record counts by status and allowing deletion via `api/messagequeue/cleanup` filtered by status and/or age (older-than days), requiring at least one filter and explicit confirmation. When both a status and an age filter are supplied, the system SHALL delete records matching either condition (status match OR age match), not only records matching both.

#### Scenario: Counts shown
- **WHEN** the cleanup screen loads
- **THEN** it displays current record counts per status

#### Scenario: Cleanup requires a filter
- **WHEN** a user attempts cleanup with no status and no age filter
- **THEN** the action is blocked and the user is told at least one filter is required

#### Scenario: Confirmed cleanup deletes records
- **WHEN** a user provides a filter and confirms in the confirmation dialog
- **THEN** matching records and their log details are permanently deleted and the result is reported

#### Scenario: Single filter deletes matching records
- **WHEN** a user provides only a status filter, or only an age filter, and confirms
- **THEN** only records matching that one condition are deleted

#### Scenario: Combined filters delete the union of matches
- **WHEN** a user provides both a status filter and an age filter and confirms
- **THEN** records matching the status, or older than the cutoff, or both, are deleted

#### Scenario: No records match the filter
- **WHEN** a confirmed cleanup matches zero records
- **THEN** the result is reported as a distinct "no records matched" outcome rather than a generic success message

