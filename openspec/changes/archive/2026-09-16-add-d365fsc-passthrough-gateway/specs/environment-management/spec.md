## ADDED Requirements

### Requirement: Extended environment name and value capacity
Environment names SHALL support up to 30 characters and environment values SHALL support up to 4000
Unicode characters.

#### Scenario: Longer environment name accepted
- **WHEN** an administrator creates a record whose environment name exceeds 10 characters
- **THEN** the record is created and retrievable

#### Scenario: Larger value accepted
- **WHEN** an administrator saves a value longer than 1000 characters
- **THEN** the value is persisted without truncation

#### Scenario: Existing records unaffected
- **WHEN** records created before the capacity increase are read
- **THEN** their system, environment and value are returned unchanged

### Requirement: Case-insensitive environment identity
A system and environment pair SHALL identify exactly one record regardless of the casing used, and
the store SHALL NOT permit two records that differ only by case.

#### Scenario: Lookup ignores casing
- **WHEN** a record stored as `DEV` is requested as `dev`
- **THEN** the same record is returned

#### Scenario: Case-variant duplicate refused
- **WHEN** an administrator attempts to create a record that differs from an existing record only by case
- **THEN** the creation is refused as a duplicate

### Requirement: Gateway connection profiles
A connection profile SHALL be able to serve the pass-through gateway rather than the integration
pipeline, distinguished by its system value, so that gateway traffic uses a separate set of
credentials.

#### Scenario: Gateway profile stored alongside the integration profile
- **WHEN** both an integration profile and a gateway profile exist for the same environment
- **THEN** both are listed and are independently editable

#### Scenario: Profiles ordered together
- **WHEN** an administrator views the environment list
- **THEN** the gateway and integration profiles for a given environment appear adjacent to one another
