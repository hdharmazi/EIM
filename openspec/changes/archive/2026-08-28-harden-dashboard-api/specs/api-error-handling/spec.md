## ADDED Requirements

### Requirement: Failures do not disclose internal detail
The API SHALL NOT return exception messages, stack traces, or other internal diagnostic detail to callers. Failure responses SHALL carry a message safe for display.

#### Scenario: Unhandled failure is not disclosed
- **WHEN** an unhandled exception occurs while serving a request
- **THEN** the response contains a generic message and no exception text or stack trace

#### Scenario: Handled failure is not disclosed
- **WHEN** an operation fails in a way the controller handles explicitly
- **THEN** the response contains a message safe for display rather than the underlying exception text

#### Scenario: Detail is retained server-side
- **WHEN** a request fails for any reason
- **THEN** the full diagnostic detail is recorded in the server logs

### Requirement: Failures carry a correlation identifier
Every failure response SHALL include an identifier that also appears in the corresponding server-side log entry, so a reported failure can be located without reproducing it.

#### Scenario: Identifier returned to the caller
- **WHEN** a request fails
- **THEN** the response includes a correlation identifier

#### Scenario: Identifier locates the log entry
- **WHEN** an operator is given a correlation identifier from a failure
- **THEN** the corresponding server-side log entry can be found using it

### Requirement: Failure responses keep the established shape
Failure responses SHALL retain the response shape the existing client already handles, so that error handling in the browser application requires no change.

#### Scenario: Existing client handles failures unchanged
- **WHEN** the browser application receives a failure response
- **THEN** it surfaces the failure using its existing error handling, without modification
