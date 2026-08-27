## MODIFIED Requirements

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
