# async-orchestration-result Specification

## Purpose

Allow orchestrations to return a structured result that carries the resulting message status, a log
message, and an optional destination correlation id, enabling asynchronous (non-terminal) message
states while preserving existing synchronous completion behavior.

## Requirements

### Requirement: Orchestrations return a structured result
`IOrchestration.ProcessAsync` SHALL return an `OrchestrationResult` carrying the resulting message
status, a log message, and an optional destination correlation id, instead of a bare string.

#### Scenario: Result exposes status, message, and destination id
- **WHEN** an orchestration completes processing
- **THEN** it returns an `OrchestrationResult` with a `Status`, a `Message`, and an optional `DestinationId`

### Requirement: Default result preserves existing completion behavior
Synchronous orchestrations SHALL default the result status to `2` (Completed) so existing
integrations behave identically to before this change.

#### Scenario: Existing CRUD orchestration completes at status 2
- **WHEN** an existing `*_To_D365FSC` or `*_To_Capture` orchestration finishes successfully
- **THEN** its `OrchestrationResult.Status` is `2` and the message row is marked Completed

### Requirement: Controller honors the orchestration result
The controller SHALL set the message status from `OrchestrationResult.Status` (not a hard-coded
value) and SHALL persist `OrchestrationResult.DestinationId` when present.

#### Scenario: Non-terminal status is honored
- **WHEN** an orchestration returns `Status = 5` with a `DestinationId`
- **THEN** the controller sets the row status to `5` and stores the `DestinationId` in `DESTINATIONID`

#### Scenario: Legacy reflection-dispatched orchestration still works
- **WHEN** an orchestration is dispatched via the reflection fallback and returns a legacy string
- **THEN** the controller adapts it into a Completed (`2`) result without error
