## ADDED Requirements

### Requirement: Environment list with filter
The system SHALL provide an environment settings list screen sourced from `api/environment`, showing System, Environment, and Value, with filtering by System and Environment.

#### Scenario: List displayed
- **WHEN** a validated user opens the Environments screen
- **THEN** the list shows all environment records with System, Environment, and (masked) Value

#### Scenario: Filter by system and environment
- **WHEN** a user filters by System and/or Environment
- **THEN** only matching records are shown

### Requirement: Masked value reveal
Environment values SHALL be masked by default and revealable per row.

#### Scenario: Reveal a value
- **WHEN** a user toggles reveal on a masked value
- **THEN** the raw value is shown until toggled back

### Requirement: Create environment record
The system SHALL allow creating an environment record via `POST api/environment`.

#### Scenario: Create succeeds
- **WHEN** a user submits a valid new System/Environment/Value
- **THEN** the record is created and appears in the list

### Requirement: Edit environment record
The system SHALL allow editing an existing record via `PUT api/environment/{system}/{environment}`.

#### Scenario: Edit succeeds
- **WHEN** a user updates a record's value and saves
- **THEN** the change is persisted and reflected in the list

### Requirement: Delete environment record
The system SHALL allow deleting a record via `DELETE api/environment/{system}/{environment}` with confirmation.

#### Scenario: Delete with confirmation
- **WHEN** a user confirms deletion of a record
- **THEN** the record is removed from the list
