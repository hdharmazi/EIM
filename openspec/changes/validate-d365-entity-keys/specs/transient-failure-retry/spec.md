## MODIFIED Requirements

### Requirement: Failures are classified as retryable or terminal
The system SHALL classify a processing failure as either retryable or terminal. Transport-level and destination-availability failures, including request timeouts, connection failures, and destination responses indicating throttling or server error, SHALL be classified retryable. Validation rejections, authorization failures, not-found responses, mapping or deserialization errors, and source messages missing a required D365 entity key field SHALL be classified terminal.

The classification SHALL determine the status recorded for the message. A terminal failure SHALL be recorded at status `3` (Failed). The status SHALL NOT be derived from the exception type observed at the catch site.

The classifier SHALL additionally report whether a terminal verdict was reached because the failure positively matched a known terminal condition, or because it matched no known condition at all. Both are recorded at status `3`; the distinction is used to shape the recorded response message and the log level.

#### Scenario: Throttling is retryable
- **WHEN** the destination system rejects a write with a throttling response
- **THEN** the failure is classified retryable

#### Scenario: Validation rejection is terminal
- **WHEN** the destination system rejects a write because the payload is invalid
- **THEN** the failure is classified terminal, positively matched, and the message is recorded at status `3`

#### Scenario: Missing entity key is terminal
- **WHEN** a D365 update or delete cannot be built because a key field is missing from the source message
- **THEN** the failure is classified terminal, positively matched, and the message is recorded at status `3` without the unclassified-failure marker

#### Scenario: Unrecognised failures default to terminal
- **WHEN** a failure cannot be matched to a known retryable condition
- **THEN** it is classified terminal and unmatched, and the message is recorded at status `3` with an error-level log entry, so an unclassified failure is surfaced to an operator rather than silently repeated

#### Scenario: Classification survives exception wrapping
- **WHEN** a destination rejection is wrapped inside another exception before reaching the classifier
- **THEN** the classification and the resulting status are the same as if it had been raised unwrapped
