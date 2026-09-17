## MODIFIED Requirements

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
