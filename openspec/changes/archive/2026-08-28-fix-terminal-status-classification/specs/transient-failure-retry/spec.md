## MODIFIED Requirements

### Requirement: Failures are classified as retryable or terminal
The system SHALL classify a processing failure as either retryable or terminal. Transport-level and destination-availability failures, including request timeouts, connection failures, and destination responses indicating throttling or server error, SHALL be classified retryable. Validation rejections, authorization failures, not-found responses, and mapping or deserialization errors SHALL be classified terminal.

The classification SHALL determine the status recorded for the message. A terminal failure SHALL be recorded at status `3` (Failed). The status SHALL NOT be derived from the exception type observed at the catch site.

The classifier SHALL additionally report whether a terminal verdict was reached because the failure positively matched a known terminal condition, or because it matched no known condition at all. Both are recorded at status `3`; the distinction is used to shape the recorded response message and the log level.

#### Scenario: Throttling is retryable
- **WHEN** the destination system rejects a write with a throttling response
- **THEN** the failure is classified retryable

#### Scenario: Validation rejection is terminal
- **WHEN** the destination system rejects a write because the payload is invalid
- **THEN** the failure is classified terminal, positively matched, and the message is recorded at status `3`

#### Scenario: Unrecognised failures default to terminal
- **WHEN** a failure cannot be matched to a known retryable condition
- **THEN** it is classified terminal and unmatched, and the message is recorded at status `3` with an error-level log entry, so an unclassified failure is surfaced to an operator rather than silently repeated

#### Scenario: Classification survives exception wrapping
- **WHEN** a destination rejection is wrapped inside another exception before reaching the classifier
- **THEN** the classification and the resulting status are the same as if it had been raised unwrapped

### Requirement: Retries are bounded and escalate on exhaustion
The system SHALL stop retrying a message once its attempt count reaches the configured maximum and SHALL escalate it to Dead Letter, recording that attempts were exhausted. Exhaustion of a retryable failure SHALL be the only route by which the synchronous processing path records status `4` (Dead Letter).

#### Scenario: Exhausted message escalates
- **WHEN** a message fails retryably and its attempt count has reached the configured maximum
- **THEN** it is recorded as Dead Letter with a log entry stating that retry attempts were exhausted

#### Scenario: Escalated message is operator-recoverable
- **WHEN** a message has escalated after exhausting retries
- **THEN** an operator can reprocess it through the existing reprocess action

#### Scenario: Terminal failure never escalates to Dead Letter
- **WHEN** a message fails with a terminal classification
- **THEN** it is recorded at status `3` and no attempt count is incremented
