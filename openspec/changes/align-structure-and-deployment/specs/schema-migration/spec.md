## MODIFIED Requirements

### Requirement: Migrations are applied as an explicit deployment step
Migrations SHALL be applied by an explicit step in the deployment process. Application processes SHALL NOT apply migrations at startup. In the pipeline, migrations SHALL run at most once per run, from the shared stage, before any environment's application code is deployed, and only when the run was started manually with database migrations selected. The deployment agent's address SHALL be allowed on the SQL server firewall only for the duration of the migration step, and the rule SHALL be removed even when the migration fails.

#### Scenario: Application startup does not alter schema
- **WHEN** an application process starts against a database that is behind
- **THEN** it does not alter the schema

#### Scenario: Production migration is gated
- **WHEN** migrations are to be applied to a production database
- **THEN** the deployment process requires explicit approval before applying them

#### Scenario: Concurrent invocations do not conflict
- **WHEN** two runner invocations target the same database at the same time
- **THEN** only one applies migrations and the other does not corrupt or duplicate the journal

#### Scenario: Migrations run once per run from the shared stage
- **WHEN** a manual run with "run database migrations" selected deploys to `nonprod`, `dev`, `bat` and `test`
- **THEN** the migrator runs once, in the `Deploy_shared` stage, after its approval and before any code is deployed, and the `dev`, `bat` and `test` stages do not run it

#### Scenario: Merge run leaves the schema alone
- **WHEN** a run is triggered by a merge to `main`
- **THEN** no migration is applied

#### Scenario: Firewall rule removed after a failed migration
- **WHEN** the migration step fails after the agent's address was added to the SQL firewall
- **THEN** the firewall rule added for that run is removed and the stage fails naming the migration step

#### Scenario: Migrator credentials stay out of logs
- **WHEN** the migrator runs in the pipeline
- **THEN** the connection string is passed through the child process environment, and no log line contains it

## ADDED Requirements

### Requirement: Migrations stay compatible with code still running in later stages
Every versioned migration SHALL be compatible with the application build it is deployed with and with the previous build, because later environments continue to run the previous build until their stages are approved. The database is shared by every non-production ENVIRONMENT value it holds (`DEV`, `DEV2`, `BAT`, `TEST`), including values that have no deployment configuration or Function App, so a migration SHALL be treated as affecting all of them at once. Removing or renaming a column, table or procedure that the previous build uses SHALL be done only in a later migration, after every environment runs code that no longer uses it.

#### Scenario: Migration reaches every ENVIRONMENT value
- **WHEN** `Deploy_shared` applies a migration that seeds route rows
- **THEN** the rows for `DEV`, `DEV2`, `BAT` and `TEST` are present in the same run, and the migration does not depend on a stage per ENVIRONMENT value

#### Scenario: Additive migration with older code
- **WHEN** a migration adds a nullable column in `Deploy_shared` and the `test` stage of that run is rejected
- **THEN** the `test` Automation app, still on the previous build, continues to process messages against the migrated database

#### Scenario: Destructive change deferred
- **WHEN** a change stops using a column
- **THEN** the column is dropped by a migration in a later change, not in the same change that stops using it
