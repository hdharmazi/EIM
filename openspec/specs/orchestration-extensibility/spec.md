# orchestration-extensibility Specification

## Purpose
TBD - created by archiving change add-entity-hold-release. Update Purpose after archive.
## Requirements
### Requirement: The engine holds no reference to client orchestration implementations
Engine code SHALL NOT construct, name, or otherwise depend on any concrete orchestration implementation. Orchestration implementations SHALL be supplied to the engine by the host process.

#### Scenario: Engine builds without client orchestrations
- **WHEN** the engine's source is compiled without any concrete orchestration implementation present
- **THEN** compilation succeeds

#### Scenario: Boundary is enforced automatically
- **WHEN** engine code is changed to reference a concrete orchestration implementation
- **THEN** an automated boundary check fails, identifying the offending reference

### Requirement: Hosts supply orchestration implementations
Each host process SHALL supply the set of available orchestration implementations, keyed by orchestration type name, to the engine at composition time. Every host that processes or reprocesses messages SHALL supply the same set.

#### Scenario: Message processing host supplies orchestrations
- **WHEN** the message processing host starts
- **THEN** it supplies the orchestration set, and messages dispatch to the correct implementation

#### Scenario: Reprocessing host supplies orchestrations
- **WHEN** an operator reprocesses a message from the dashboard
- **THEN** the reprocessing host uses its supplied orchestration set rather than constructing one internally

#### Scenario: Hosts do not diverge
- **WHEN** an orchestration is available in one host
- **THEN** it is available in every host that processes or reprocesses messages

### Requirement: Orchestrations are resolved without assuming a namespace
The system SHALL resolve an orchestration solely from the supplied registration keyed by type name. It SHALL NOT locate an orchestration by constructing a type name from a fixed namespace.

#### Scenario: A client-owned orchestration resolves
- **WHEN** an orchestration implementation declared outside the engine's namespace is registered under its type name
- **THEN** messages for that type dispatch to it successfully

### Requirement: Unregistered orchestration types fail explicitly
When a message names an orchestration type that is not registered, the system SHALL record an explicit error identifying the missing type and SHALL mark the message failed. It SHALL NOT attempt any alternative resolution mechanism.

#### Scenario: Missing registration is reported clearly
- **WHEN** a message names an orchestration type that no host registration provides
- **THEN** the message is marked failed and the log identifies the unregistered type by name

### Requirement: Registrations and routes are consistent
The configured routes and the supplied orchestration registrations SHALL be verified against each other, so that a route naming an unregistered orchestration is detected before messages are processed.

#### Scenario: Route naming an unregistered orchestration is detected
- **WHEN** a route names an orchestration type that no host registration provides
- **THEN** the inconsistency is reported at configuration or startup time rather than on first message

