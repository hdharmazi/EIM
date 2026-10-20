# transient-failure-retry Specification

## Purpose

Distinguish failures worth attempting again from failures that are not, schedule bounded retries
with exponential backoff, and escalate once the attempt allowance is exhausted — so that a brief
destination outage does not require operator intervention, while a genuine rejection still does.
## Requirements
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

### Requirement: Retryable failures are scheduled for another attempt
A message whose processing fails retryably SHALL be recorded at status `7` (Retry Scheduled) with an incremented attempt count and a next-attempt time computed using exponential backoff with jitter. It SHALL NOT be recorded as Dead Letter while attempts remain.

#### Scenario: First retryable failure schedules a retry
- **WHEN** a message fails retryably for the first time
- **THEN** its status is `7`, its attempt count is `1`, and its next-attempt time is in the future

#### Scenario: Backoff grows with attempts
- **WHEN** a message fails retryably on successive attempts
- **THEN** each scheduled delay is longer than the previous one, up to a configured ceiling

#### Scenario: Successful retry completes normally
- **WHEN** a scheduled retry succeeds
- **THEN** the message reaches its normal completion status and is no longer scheduled for retry

### Requirement: Retries are performed by a scheduled sweep
The system SHALL perform retries using a scheduled sweep that claims a bounded batch of messages at status `7` whose next-attempt time has passed, using an atomic claim so concurrent runs cannot process the same message twice.

#### Scenario: Only due messages are retried
- **WHEN** the retry sweep runs
- **THEN** it claims only messages at status `7` whose next-attempt time has passed

#### Scenario: Retry volume is bounded
- **WHEN** more messages are due for retry than the configured batch size
- **THEN** the sweep processes at most the batch size and resumes on the next run

#### Scenario: Concurrent retry sweeps do not double-process
- **WHEN** two retry sweep runs execute concurrently
- **THEN** no message is claimed by both runs

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

### Requirement: Retries respect route state
The retry sweep SHALL NOT process messages belonging to a route that is not enabled. Such messages SHALL remain at status `7` until the route is enabled.

#### Scenario: Paused route suspends retries
- **WHEN** a route is paused while some of its messages are scheduled for retry
- **THEN** no retry attempt is made against the destination for that route

#### Scenario: Resuming a route resumes retries
- **WHEN** a paused route with retry-scheduled messages is enabled
- **THEN** those messages become eligible for retry again

### Requirement: Retry configuration is adjustable per environment
Maximum attempt count, base delay, backoff ceiling, and retry batch size SHALL be configurable per environment.

#### Scenario: Environment-specific retry policy
- **WHEN** retry settings differ between two environments
- **THEN** each environment applies its own configured values

