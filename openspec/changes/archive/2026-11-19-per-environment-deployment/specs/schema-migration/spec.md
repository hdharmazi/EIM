## RENAMED Requirements

- FROM: `### Requirement: Migrations are safe on every stamp's database`
- TO: `### Requirement: Migrations are safe on every environment's database`

## MODIFIED Requirements

### Requirement: Migrations are applied as an explicit deployment step
Migrations SHALL be applied by an explicit step in the deployment process. Application processes SHALL NOT apply migrations at startup.

In the pipeline, migrations SHALL run:
- at most once per run
- from the stage of the configuration that owns the database (`Deploy_bat` in `eim`, `Deploy_prd` in `eim-prd`)
- before any application code in that stage
- only when the run was started manually with database migrations selected

The migrator SHALL authenticate with the deployer's Entra identity, as defined in `sql-identity-access`, and SHALL NOT use a SQL login or a secret variable.

The deployment agent's address SHALL be allowed on the SQL server firewall only for the duration of the database step. The rule SHALL be removed even when the migration fails.

There SHALL be no per-developer or `dev` deployment database. Developers apply migrations to a local SQL Server database by running `ARM.Integration.DbMigrator` on their own machine. Only migrations merged to `main` reach the shared `eimdb`, through the pipeline.

#### Scenario: Application startup does not alter schema
- **WHEN** an application process starts against a database that is behind
- **THEN** it does not alter the schema

#### Scenario: Production migration is gated
- **WHEN** migrations are to be applied to a production database
- **THEN** the deployment process requires explicit approval before applying them

#### Scenario: Concurrent invocations do not conflict
- **WHEN** two runner invocations target the same database at the same time
- **THEN** only one applies migrations and the other does not corrupt or duplicate the journal

#### Scenario: Migrations run once per run from the owner stage
- **WHEN** a manual `eim` run with "run database migrations" selected deploys to `bat` and `test`
- **THEN** the migrator runs once, in the `Deploy_bat` stage, after its approval and before any code is deployed, and the `Deploy_test` stage does not run it

#### Scenario: Local migration does not touch the shared database
- **WHEN** a developer runs `ARM.Integration.DbMigrator` with `EIMDB_MIGRATOR_CONNECTION` pointing at a local SQL Server `eimdb`
- **THEN** the migrations are applied to the local database only, and the shared `eimdb` journal is unchanged

#### Scenario: Merge run leaves the schema alone
- **WHEN** a run is triggered by a merge to `main`
- **THEN** no migration is applied

#### Scenario: Firewall rule removed after a failed migration
- **WHEN** the migration step fails after the agent's address was added to the SQL firewall
- **THEN** the firewall rule added for that run is removed and the stage fails naming the migration step

#### Scenario: Migrator connection holds no secret
- **WHEN** the migrator runs in the pipeline
- **THEN** `EIMDB_MIGRATOR_CONNECTION` in the child process environment names the server and database with `Authentication=Active Directory Default`, has no `Password`, `User ID` or access token, and `AZURE_TOKEN_CREDENTIALS` is `AzureCliCredential`

### Requirement: Migrations stay compatible with code still running in later stages
Every versioned migration SHALL be compatible with the application build it is deployed with and with the previous build, because later environments continue to run the previous build until their stages are approved.

The database owned by `bat` is used by every non-production ENVIRONMENT value it holds (`DEV`, `DEV2`, `BAT`, `TEST`). That includes values that have no deployment configuration or Function App, and `test`, which uses `bat`'s database. A migration SHALL therefore be treated as affecting all of them at once.

Removing or renaming a column, table or procedure that the previous build uses SHALL be done only in a later migration, after every environment runs code that no longer uses it.

#### Scenario: Migration reaches every ENVIRONMENT value
- **WHEN** `Deploy_bat` applies a migration that seeds route rows
- **THEN** the rows for `DEV`, `DEV2`, `BAT`, `TEST` and `PRD` are present in the same run, and the migration does not depend on a stage per ENVIRONMENT value

#### Scenario: Additive migration with older code
- **WHEN** a migration adds a nullable column in `Deploy_bat` and the `Deploy_test` stage of that run is rejected
- **THEN** the `test` Automation app, still on the previous build, continues to process messages against the migrated database

#### Scenario: Destructive change deferred
- **WHEN** a change stops using a column
- **THEN** the column is dropped by a migration in a later change, not in the same change that stops using it

### Requirement: Migrations are safe on every environment's database
The same versioned migrations SHALL be applied to every database that an owner configuration resolves:
- the non-production `eimdb`, by `eim` from `Deploy_bat`
- the production database `sqldb-eim-wtc-prd`, by `eim-prd` from `Deploy_prd`
- the database of any isolated environment, from its own stage

These databases SHALL share one migration history. A migration SHALL NOT branch on the database name, the server or the ENVIRONMENT values present. Every migration SHALL be safe and idempotent both on a database that has run every earlier migration for years and on a database created empty by the `sql` component.

A parity check in a seed migration SHALL count only the rows that migration is responsible for, never the whole table. A seed that lists ENVIRONMENT values SHALL include `PRD` alongside `BAT`, `DEV`, `DEV2` and `TEST`.

#### Scenario: New production database
- **WHEN** the migrator runs against an empty `sqldb-eim-wtc-prd` created by `sql`
- **THEN** migrations 001 to the latest apply in order without error, and the journal lists the same scripts as `eimdb`

#### Scenario: A new database migrates from 001 with no manual step
- **WHEN** the migrator runs against a database created empty, with no trigger, procedure or table created by hand
- **THEN**:
  - `002a_create_modifieddatetime_trigger.sql` creates the `MODIFIEDDATETIME` trigger on `MESSAGEQUEUETABLE` before `003_backfill_truncated_sourceentity.sql` disables and re-enables it
  - every versioned script and then every repeatable script applies without error
  - a second run applies no versioned script

#### Scenario: The trigger bootstrap on a database that already has the trigger
- **WHEN** the migrator runs against `eimdb`, which has the `MODIFIEDDATETIME` trigger and has journaled `003`
- **THEN** `002a` is applied as the pending script, changes nothing but the journal, and the repeatable trigger script then re-applies its definition without error

#### Scenario: Production migration in its own stage
- **WHEN** an `eim-prd` run with `runMigrations` selected is approved at `eim-wtc-prd`
- **THEN** the migrator runs once, in `Deploy_prd`, against `sql-eim-wtc-prd`/`sqldb-eim-wtc-prd`, before any production code is deployed

#### Scenario: Migrator always targets the configured database
- **WHEN** `schema` runs for `prd`
- **THEN** the migrator connection is built from the resolved `prd` names, so it cannot point at `azql-wtc-sandbox`
