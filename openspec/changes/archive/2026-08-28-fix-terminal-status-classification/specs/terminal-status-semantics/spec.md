## ADDED Requirements

### Requirement: Terminal statuses carry distinct, documented meanings
The system SHALL treat status `3` (Failed) and status `4` (Dead Letter) as distinct terminal outcomes with the following meanings, and SHALL apply them consistently across every path that records a terminal failure.

Status `3` (Failed) SHALL mean the message will not succeed if reprocessed unchanged: the destination gave a definite negative answer, or the message could not be understood or routed. Correcting the source data or the configuration is required before reprocessing is worthwhile.

Status `4` (Dead Letter) SHALL mean no definite answer was ever obtained and the system stopped trying: the destination was unavailable, unreachable or unresponsive across the allowed attempts, or an acknowledgement never arrived. Reprocessing the identical payload is plausible once the underlying condition clears.

#### Scenario: Failed implies reprocessing unchanged is futile
- **WHEN** a message is at status `3`
- **THEN** the destination has either rejected it or it could not be understood, and reprocessing the identical payload would produce the same outcome

#### Scenario: Dead Letter implies reprocessing unchanged is plausible
- **WHEN** a message is at status `4`
- **THEN** the failure was an absence of a definite answer rather than a rejection, and reprocessing the identical payload may succeed once the destination recovers

#### Scenario: Both terminal statuses remain operator-recoverable
- **WHEN** an operator views a message at status `3` or status `4`
- **THEN** the reprocess action is available for both

### Requirement: The terminal status is selected by failure classification
The terminal status recorded for a failed message SHALL be determined by the classification of the failure, and SHALL NOT be determined by the exception type observed at the catch site.

A failure classified terminal SHALL be recorded at status `3` (Failed). A failure classified retryable SHALL be recorded at status `7` (Retry Scheduled) while attempts remain, and at status `4` (Dead Letter) once the attempt allowance is exhausted.

#### Scenario: Destination rejects a write for a data reason
- **WHEN** the destination rejects a write with a validation error, such as a required field being absent
- **THEN** the message is recorded at status `3` and the destination's error text is recorded as the response message

#### Scenario: Destination is unavailable and attempts are exhausted
- **WHEN** a message fails retryably on every allowed attempt because the destination is unavailable
- **THEN** the message is recorded at status `4` and the response message states that retry attempts were exhausted

#### Scenario: Internal configuration error
- **WHEN** processing fails because no orchestration is registered for the message type, or no environment record exists
- **THEN** the message is recorded at status `3`

#### Scenario: Exception type does not influence the status
- **WHEN** two failures of the same classification are raised as different exception types
- **THEN** both are recorded at the same terminal status

### Requirement: Terminal status is consistent across synchronous and asynchronous paths
A business rejection by the destination SHALL be recorded at the same status regardless of whether it was reported synchronously on the submission response or asynchronously by a business event.

#### Scenario: Synchronous rejection
- **WHEN** the destination rejects a submission and reports the rejection on the response to that submission
- **THEN** the message is recorded at status `3`

#### Scenario: Asynchronous rejection
- **WHEN** the destination accepts a submission and later reports a failed processing result by business event
- **THEN** the message is recorded at status `3`

#### Scenario: Missing acknowledgement is not a rejection
- **WHEN** a submitted message receives no terminal business event within the configured timeout
- **THEN** it is escalated to status `4`, not status `3`, because no definite answer was obtained

### Requirement: Unrecognised failures are distinguishable from destination rejections
A failure that cannot be matched to a known condition SHALL be recorded at status `3`, and SHALL be distinguishable from a positively rejected failure in both the recorded response message and the emitted log, so that a defect is not mistaken for invalid source data.

#### Scenario: Positively rejected failure
- **WHEN** the destination returns a definite negative answer
- **THEN** the message is recorded at status `3` with the destination's error text and a warning-level log entry

#### Scenario: Unrecognised failure
- **WHEN** a failure matches no known retryable or positively terminal condition
- **THEN** the message is recorded at status `3`, the response message marks it as an unclassified failure, and an error-level log entry is emitted

### Requirement: Operator reprocess classifies but does not schedule
An operator-initiated reprocess SHALL classify failures by the same rules as automatic processing, and SHALL NOT schedule a retry. A terminal failure SHALL be recorded at status `3`. A retryable failure SHALL be recorded at status `4`, because no further attempt will be made.

#### Scenario: Reprocess hits a destination rejection
- **WHEN** an operator reprocesses a message and the destination rejects it for a data reason
- **THEN** the message is recorded at status `3`

#### Scenario: Reprocess hits an unavailable destination
- **WHEN** an operator reprocesses a message and the destination is unavailable
- **THEN** the message is recorded at status `4` and no retry is scheduled

#### Scenario: Reprocess never leaves a message scheduled
- **WHEN** an operator reprocesses a message and it fails for any reason
- **THEN** the message is not left at status `7`
