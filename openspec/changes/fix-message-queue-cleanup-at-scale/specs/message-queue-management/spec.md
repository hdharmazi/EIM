## MODIFIED Requirements

### Requirement: Cleanup utility
The system SHALL provide a cleanup screen showing current record counts by status and allowing deletion via `api/messagequeue/cleanup` filtered by status and/or age (older-than days), requiring at least one filter and explicit confirmation. When both a status and an age filter are supplied, the system SHALL delete only records matching both conditions.

#### Scenario: Counts shown
- **WHEN** the cleanup screen loads
- **THEN** it displays current record counts per status

#### Scenario: Cleanup requires a filter
- **WHEN** a user attempts cleanup with no status and no age filter
- **THEN** the action is blocked and the user is told at least one filter is required

#### Scenario: Confirmed cleanup deletes records
- **WHEN** a user provides a filter and confirms in the confirmation dialog
- **THEN** matching eligible records and their log details are permanently deleted and the result is reported

#### Scenario: Single filter deletes matching records
- **WHEN** a user provides only a status filter, or only an age filter, and confirms
- **THEN** only eligible records matching that one condition are deleted

#### Scenario: Combined filters delete the intersection of matches
- **WHEN** a user provides both a status filter and an age filter and confirms
- **THEN** only records matching the status *and* older than the cutoff are deleted

#### Scenario: No matches is reported distinctly
- **WHEN** a confirmed cleanup matches no records
- **THEN** the outcome is reported as "no records matched" and is visibly distinct from a completed deletion

## ADDED Requirements

### Requirement: Cleanup deletes in bounded, durable batches

Cleanup SHALL delete in bounded batches, each committed independently, rather than as a single
all-or-nothing operation. A batch SHALL delete a record's log details before the record itself, so
that no batch can leave a log detail without its parent.

#### Scenario: A large backlog is deleted successfully

- **WHEN** an operator runs cleanup against a backlog far larger than a single database operation can delete within its timeout
- **THEN** the records are deleted and the operation reports success

#### Scenario: Completed batches survive an interruption

- **WHEN** a cleanup run is interrupted partway through
- **THEN** the batches already completed remain deleted, and a subsequent run resumes with the records that remain

#### Scenario: No orphaned log details

- **WHEN** any cleanup run finishes, completes partially, or is interrupted
- **THEN** no log detail exists whose parent record is absent

### Requirement: Cleanup reports progress and is resumable

A cleanup request SHALL be bounded in duration and SHALL report both how many records it deleted and
how many eligible records remain. The system SHALL continue automatically until no eligible records
remain, without requiring the operator to repeat the action.

#### Scenario: Progress is reported

- **WHEN** a cleanup request reaches its duration bound before all eligible records are deleted
- **THEN** it reports the number deleted and the number remaining, rather than failing

#### Scenario: Work continues to completion

- **WHEN** a cleanup request reports records remaining
- **THEN** the system continues automatically until none remain, and the operator sees the cumulative total

#### Scenario: Operator sees the run progressing

- **WHEN** a cleanup run spans multiple requests
- **THEN** the screen shows the running total rather than appearing idle

### Requirement: Bulk cleanup cannot delete records that other mechanisms depend on

When no status is selected, cleanup SHALL delete only records in the Completed and Failed statuses.
Records that are unprocessed, in progress, held for a paused route, scheduled for retry, awaiting
asynchronous confirmation, or dead-lettered SHALL NOT be deleted by an unfiltered-by-status request,
regardless of age.

#### Scenario: Age-only cleanup spares held messages

- **WHEN** an operator runs cleanup with an age filter and no status filter, and held messages older than the cutoff exist
- **THEN** those held messages are not deleted and remain available for release

#### Scenario: Age-only cleanup spares in-flight work

- **WHEN** an operator runs cleanup with an age filter and no status filter
- **THEN** records scheduled for retry, awaiting asynchronous confirmation, unprocessed, or in progress are not deleted

#### Scenario: Age-only cleanup deletes terminal records

- **WHEN** an operator runs cleanup with an age filter and no status filter
- **THEN** Completed and Failed records older than the cutoff are deleted

### Requirement: A protected status can be deleted only by explicit acknowledgement

Selecting a protected status explicitly SHALL be refused with a message identifying that status,
and SHALL proceed only when the request carries an explicit acknowledgement. A refusal SHALL be
distinguishable from a request that matched no records.

#### Scenario: Protected status is refused by name

- **WHEN** an operator selects a protected status and confirms cleanup without acknowledging the protection
- **THEN** the request is refused, nothing is deleted, and the message identifies the selected status

#### Scenario: Acknowledged deletion proceeds

- **WHEN** an operator repeats the request for that status carrying the explicit acknowledgement
- **THEN** records of that status matching the filter are deleted

#### Scenario: Orphaned in-progress records are clearable

- **WHEN** an operator explicitly selects the in-progress status, acknowledges the protection, and confirms
- **THEN** those records are deleted

#### Scenario: Acknowledgement does not widen the selection

- **WHEN** an operator acknowledges a protected status and also supplies an age filter
- **THEN** only records of that status older than the cutoff are deleted, and records of other statuses are unaffected
