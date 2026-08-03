## ADDED Requirements

### Requirement: Named configuration records
The system SHALL store configurations as records identified by a category and a name unique within that category, with an optional display name, a set of editable key/value fields, and an active flag. The field set SHALL NOT be fixed by database schema; fields SHALL be stored as a JSON object so any category can define its own fields.

#### Scenario: Configuration identified by category and name
- **WHEN** a configuration is created
- **THEN** it is uniquely identified by its category and name, and its fields are stored as key/value pairs

#### Scenario: Dynamic fields
- **WHEN** an administrator adds or removes fields on a configuration
- **THEN** the fields are persisted without any database schema change

#### Scenario: Duplicate name within category rejected
- **WHEN** an administrator creates a configuration whose name already exists in the same category
- **THEN** the system rejects the request and reports a conflict

### Requirement: Admin management of configurations
The system SHALL allow administrators to list, create, edit, and delete configurations through an admin-only screen and API. Non-admin users SHALL NOT be able to create, edit, or delete configurations.

#### Scenario: Admin creates a configuration
- **WHEN** an administrator submits a new configuration with a category, unique name, and fields
- **THEN** the configuration is saved and appears in the configuration list for that category

#### Scenario: Admin edits a configuration
- **WHEN** an administrator changes the fields, display name, or active flag of a configuration and saves
- **THEN** the updated values are persisted

#### Scenario: Admin deletes a configuration
- **WHEN** an administrator deletes a configuration
- **THEN** the configuration is removed and no longer appears in lists

#### Scenario: Non-admin is denied management
- **WHEN** a non-admin user attempts to create, edit, or delete a configuration
- **THEN** the system denies the operation

### Requirement: Configuration retrieval by category
The system SHALL provide the list of active configurations for a given category to any validated user, for use in selection UIs.

#### Scenario: List active configurations by category
- **WHEN** a validated user requests configurations for a category
- **THEN** the active configurations in that category are returned

#### Scenario: Inactive configurations excluded from selection
- **WHEN** a configuration is marked inactive
- **THEN** it is excluded from the category selection list while remaining visible in the admin list
