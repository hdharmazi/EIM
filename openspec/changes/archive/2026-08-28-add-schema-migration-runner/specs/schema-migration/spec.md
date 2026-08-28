## ADDED Requirements

### Requirement: Databases record the migrations applied to them
The system SHALL record, in each target database, which migration scripts have been applied to it. The schema version of a database SHALL be determinable by querying that database alone.

#### Scenario: Applied migrations are recorded
- **WHEN** a versioned migration is applied to a database
- **THEN** the database records that migration as applied, together with when it was applied

#### Scenario: Schema version is discoverable
- **WHEN** an operator needs to know which migrations a database has received
- **THEN** the answer is available from the database itself without comparing its objects to source files

### Requirement: Applying migrations is repeatable and safe
Running the migration runner against a database SHALL apply only the versioned migrations that database has not already received, in order. Running it against a current database SHALL make no change.

#### Scenario: Running twice changes nothing the second time
- **WHEN** the runner is executed twice in succession against the same database
- **THEN** the second execution applies no versioned migration and reports nothing pending

#### Scenario: A database several versions behind is brought forward
- **WHEN** the runner is executed against a database missing several migrations
- **THEN** the missing migrations are applied in order, and none that were already applied are reapplied

#### Scenario: A failing migration does not leave a partial application recorded
- **WHEN** a migration fails partway through
- **THEN** it is not recorded as applied, and the runner reports the failure identifying the script

### Requirement: A database can be built from nothing
The system SHALL be able to produce the current schema in an empty database by applying migrations from the beginning.

#### Scenario: Empty database reaches current schema
- **WHEN** the runner is executed against an empty database
- **THEN** the resulting schema matches that of a current environment

### Requirement: Versioned and repeatable migrations are distinguished
The system SHALL distinguish migrations that run exactly once from those that run on every execution. Definitions of stored procedures, views, and functions SHALL be treated as repeatable so that the deployed object always matches its source file.

#### Scenario: Structural change runs once
- **WHEN** a migration that creates or alters a table, column, index, or seeds data has already been applied
- **THEN** it is not applied again

#### Scenario: Procedure definition is reapplied every run
- **WHEN** the runner executes against a current database and a stored procedure definition has changed in source
- **THEN** the procedure is reapplied so the deployed definition matches the source file

#### Scenario: Repeatable scripts run after pending versioned scripts
- **WHEN** a repeatable script depends on a column introduced by a pending versioned migration
- **THEN** the versioned migration is applied first, and the repeatable script succeeds

### Requirement: Existing databases are brought under management without recreating objects
The system SHALL support baselining an existing database, so that migrations already represented by its current objects are treated as applied and are not attempted again.

#### Scenario: Baselined database reports nothing pending
- **WHEN** an existing database is baselined and the runner is executed against it
- **THEN** no migration is applied and nothing is reported as pending

#### Scenario: Baselining does not attempt to recreate existing objects
- **WHEN** the runner is executed against a baselined database containing objects created before the runner existed
- **THEN** no attempt is made to recreate those objects

### Requirement: Migrations accompany the code that requires them
Migration scripts SHALL be carried within the deployed runner artifact, so that a given build applies exactly the migrations corresponding to its source revision.

#### Scenario: A build carries its own migrations
- **WHEN** a build of the runner is deployed
- **THEN** it applies the migrations present at the source revision it was built from, and no others

### Requirement: Migrations are applied as an explicit deployment step
Migrations SHALL be applied by an explicit step in the deployment process. Application processes SHALL NOT apply migrations at startup.

#### Scenario: Application startup does not alter schema
- **WHEN** an application process starts against a database that is behind
- **THEN** it does not alter the schema

#### Scenario: Production migration is gated
- **WHEN** migrations are to be applied to a production database
- **THEN** the deployment process requires explicit approval before applying them

#### Scenario: Concurrent invocations do not conflict
- **WHEN** two runner invocations target the same database at the same time
- **THEN** only one applies migrations and the other does not corrupt or duplicate the journal

### Requirement: Migrations are forward-only
The system SHALL NOT provide automated reversal of applied migrations. Corrections SHALL be made by applying a further migration.

#### Scenario: Correcting a mistaken migration
- **WHEN** an applied migration is found to be incorrect
- **THEN** the correction is made by a subsequent migration rather than by reversing the original
