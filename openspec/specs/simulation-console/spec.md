# simulation-console Specification

## Purpose

Provide the integration simulation console: Service Bus configuration display, scenario picker with dynamic fields, real-time SSE run output, and sales-order Excel import.

## Requirements

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

### Requirement: Simulation can be switched off and is off unless enabled
The dashboard API SHALL enable simulation only when the configuration value `Simulation:Enabled` parses as boolean `true`. An absent, empty, `false` or unparseable value SHALL mean off. When simulation is off:
- `SimulationController` SHALL NOT be exposed, so every `/api/simulation/*` route (`config`, `run`, `salesorder/parse`) SHALL answer 404 to an authenticated, validated user.
- `SimulationService` and `SalesOrderExcelParser` SHALL NOT be registered.
- `SimulationService`, if constructed, SHALL refuse to run: it SHALL yield one error event stating that simulation is disabled, and SHALL NOT create a Service Bus client or send a message, even when `Simulation:ServiceBusConnectionString` is set.
- The SPA SHALL NOT show the Simulation navigation entry, and the `/simulation` route SHALL show a notice that simulation is not available in this environment instead of the console.

`appsettings.json` SHALL set `Simulation:Enabled` to `false`. `appsettings.Development.json` SHALL set it to `true` for local development.

#### Scenario: Setting absent
- **WHEN** the dashboard API starts with no `Simulation:Enabled` value
- **THEN** `POST /api/simulation/run` from a validated user returns 404 and no Service Bus message is sent

#### Scenario: Setting false
- **WHEN** `Simulation:Enabled` is `false`
- **THEN** the controller feature built for the API does not contain `SimulationController`

#### Scenario: Unparseable value
- **WHEN** `Simulation:Enabled` is `yes`
- **THEN** simulation is off

#### Scenario: Setting true
- **WHEN** `Simulation:Enabled` is `true`
- **THEN** the controller feature contains `SimulationController`, and `GET /api/simulation/config` returns the configured queue names as before

#### Scenario: Service guard with a connection string present
- **WHEN** `SimulationService.RunAsync` is called with `Simulation:Enabled` absent and `Simulation:ServiceBusConnectionString` set
- **THEN** it yields exactly one error event stating that simulation is disabled, and creates no Service Bus client

#### Scenario: Deep link with simulation off
- **WHEN** a validated user opens `/simulation` while `/api/me` reports `features.simulation` as `false`
- **THEN** the SPA shows the "not available in this environment" notice and makes no `/api/simulation/*` request

#### Scenario: Production dashboard
- **WHEN** the production dashboard API is deployed with `Simulation__Enabled=false`
- **THEN** no user, Admin or otherwise, can publish a simulated message through the dashboard
