## MODIFIED Requirements

### Requirement: Controller honors the orchestration result
The controller SHALL set the message status from `OrchestrationResult.Status` (not a hard-coded
value) and SHALL persist `OrchestrationResult.DestinationId` when present. The controller SHALL dispatch only to host-supplied orchestration registrations. It SHALL NOT resolve orchestrations reflectively by type name, and it SHALL NOT adapt legacy `Task<string>` return values.

#### Scenario: Non-terminal status is honored
- **WHEN** an orchestration returns `Status = 5` with a `DestinationId`
- **THEN** the controller sets the row status to `5` and stores the `DestinationId` in `DESTINATIONID`

#### Scenario: Unregistered orchestration type fails explicitly
- **WHEN** a message names an orchestration type that no host registration provides
- **THEN** the controller records an explicit error naming the unregistered type and marks the message failed, without attempting reflective resolution
