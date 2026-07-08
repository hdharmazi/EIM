## ADDED Requirements

### Requirement: Simulation configuration display
The system SHALL provide a simulation console showing the current Service Bus configuration status and queue names sourced from `api/simulation/config` (D365Queue, CaptureToD365Queue, CaptureToD365ResponseQueue, D365ToCaptureQueue).

#### Scenario: Configuration shown
- **WHEN** a validated user opens the Simulation screen
- **THEN** it shows whether Service Bus is configured and the configured queue names (or "Not configured")

### Requirement: Scenario picker with dynamic fields
The system SHALL let the user select a simulation scenario and render its input fields dynamically, enabling the run action only when a scenario is selected.

#### Scenario: Select a scenario
- **WHEN** a user selects a scenario
- **THEN** the scenario's input fields are rendered and the Run action becomes enabled

### Requirement: Real-time SSE output streaming
The system SHALL run a simulation via `POST api/simulation/run` and stream results in real time using Server-Sent Events through the BFF proxy, displaying color-coded log output as events arrive without buffering the whole response.

#### Scenario: Streaming output
- **WHEN** a user runs a simulation
- **THEN** log events are appended to the output panel incrementally as they arrive from the SSE stream

#### Scenario: Clear output
- **WHEN** a user clears the output
- **THEN** the output panel is emptied

### Requirement: Sales order Excel import
The system SHALL allow importing a sales order from an Excel file via `POST api/simulation/salesorder/parse` and using the parsed result to populate a simulation run.

#### Scenario: Import parses file
- **WHEN** a user uploads a valid sales order Excel file
- **THEN** the file is parsed and its data is available to run the corresponding simulation scenario

#### Scenario: Invalid file reported
- **WHEN** a user uploads an invalid or unparseable file
- **THEN** an error is shown and no simulation is run
