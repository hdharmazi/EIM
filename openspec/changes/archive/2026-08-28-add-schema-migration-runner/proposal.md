## Why

Nothing in the repository can answer "what schema version is this database on?". `Script/SQL/Install.sql` is a single create-everything script, `Migration_AddConfigurationTable.sql` is an ad-hoc addition, and both are run by hand against each environment. There is no record in any database of what has been applied.

For one client and a careful developer this works. It stops working across *clients x environments*, where each client may run a different engine version: a schema mismatch becomes invisible until it surfaces as a runtime failure in production. Code version and schema version have to move together, and today only one of them is tracked.

`add-entity-hold-release` brings this to a head. Its first step creates a table, activates and seeds a status catalogue, adds two columns, adds two indexes, widens three stored-procedure parameters, and changes a column default — the largest schema change this project has had, and it must land on every environment. Introducing the runner now costs almost nothing extra, because the migration script is being written regardless; it just gains a destination that records itself.

## What Changes

- **A migration runner is introduced** as a standalone console project using DbUp, executed from the deployment pipeline rather than from application startup.
- **Databases record their own schema version** in a journal table. Re-running the runner is a no-op; running it against a database several versions behind brings it forward in order.
- **Migrations are split into versioned and repeatable.** Versioned scripts (create table, add column, add index, seed data) run once and are journaled. Repeatable scripts (`CREATE OR ALTER PROCEDURE`, views, functions) re-run on every deployment so the database object always matches the file in source control. `SPINSERTMESSAGEQUEUE` is already written as `CREATE OR ALTER` and becomes the first repeatable script.
- **`Install.sql` becomes `001_baseline.sql`**, the starting point for a database created from scratch. Existing databases are baselined by pre-populating the journal so the runner does not attempt to recreate objects that already exist.
- **Migration scripts are embedded resources** in the runner assembly, so schema version is welded to code version and cannot drift or be forgotten during a deployment.
- **Migrations are forward-only.** Destructive changes are avoided; columns and objects are deprecated rather than dropped. This matches the additive rollback strategy already adopted in `add-entity-hold-release`.
- **The deployment scripts under `Script/Deploy-Manual/` gain a migration step**, so applying schema changes stops being a separate manual act.

No application behaviour changes. The runner applied to a current database produces the schema that exists today.

**Sequencing:** this change should land before `add-entity-hold-release` step 1, so that its schema work becomes the first journaled migration rather than another hand-run script.

## Capabilities

### New Capabilities

- `schema-migration`: Versioned and repeatable database migrations, journaled per database, applied in order by a runner executed from the deployment pipeline.

### Modified Capabilities

_None. No existing capability specifies how database schema is deployed._

## Impact

**New project**
- A migration runner console project referencing DbUp, with migration scripts as embedded resources.
- Added to `ARM.Integration.Wilson.slnx`.

**Scripts** (`Script/SQL/`)
- `Install.sql` becomes `Migrations/001_baseline.sql`.
- `Migration_AddConfigurationTable.sql` becomes `Migrations/002_add_configuration_table.sql`.
- `SPINSERTMESSAGEQUEUE` and any other `CREATE OR ALTER` object move into a repeatable script folder.
- `Working query.sql` is developer scratch and is removed from the deployable set.

**Deployment** (`Script/Deploy-Manual/`, pipeline)
- A migration step runs before application deployment.
- Production runs are gated so schema changes are explicit and approvable.

**Existing environments**
- Each existing database is baselined once, marking already-applied migrations as applied. This is the only step requiring manual care.

**Documentation** (`Script/Docs`)
- How to add a migration, the versioned versus repeatable distinction, and the baselining procedure.
