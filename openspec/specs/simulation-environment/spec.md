# simulation-environment Specification

## Purpose

Model simulation targets as configurations under the `SimulationEnvironment` category and let a validated user select one per simulation run in the simulation console. The selected environment supplies the Service Bus queue names for the run, while the connection string remains server-side.

## Requirements

### Requirement: Simulation environment as a configuration
The system SHALL represent a simulation environment as a configuration under the `SimulationEnvironment` category whose fields include the four Service Bus queue names used by a simulation run: `D365Queue`, `CaptureToD365Queue`, `CaptureToD365ResponseQueue`, and `D365ToCaptureQueue`. A simulation environment SHALL NOT store a Service Bus connection string; the server-side `Simulation:ServiceBusConnectionString` is used for all environments.

#### Scenario: Environment provides the four queue fields
- **WHEN** a simulation environment configuration is used
- **THEN** it provides the four queue names as fields (D365Queue, CaptureToD365Queue, CaptureToD365ResponseQueue, D365ToCaptureQueue)

#### Scenario: Missing required field reported
- **WHEN** a selected simulation environment is missing one of the four required queue fields at run time
- **THEN** the system reports a clear error and does not send messages

### Requirement: Simulation environment selection
The system SHALL provide the list of active `SimulationEnvironment` configurations to any validated user for selection in the simulation console, and SHALL allow a validated user to select one whose queue names are displayed.

#### Scenario: Active environments listed for selection
- **WHEN** a validated user opens the simulation console
- **THEN** the active `SimulationEnvironment` configurations are available in an environment dropdown

#### Scenario: Selecting an environment populates queues
- **WHEN** a validated user selects a simulation environment
- **THEN** the four queue names for that environment are shown

### Requirement: Environment-scoped simulation run
The system SHALL run a simulation against the queue names of the environment selected for that run, using the server-side connection string. Selecting an environment SHALL affect only the selecting user's run and SHALL NOT change a shared or global setting. The run action SHALL be disabled until an environment is selected.

#### Scenario: Run uses selected environment queues
- **WHEN** a user runs a simulation with environment "BAT" selected
- **THEN** messages are sent to and listened on the queue names defined by "BAT"

#### Scenario: Run blocked without selection
- **WHEN** no simulation environment is selected
- **THEN** the Run action is disabled

#### Scenario: No environments defined
- **WHEN** no `SimulationEnvironment` configurations exist
- **THEN** the simulation console prompts the user to create one and the Run action remains disabled

#### Scenario: Selection is per-run
- **WHEN** one user selects environment "DEV"
- **THEN** another user's environment selection is unaffected
