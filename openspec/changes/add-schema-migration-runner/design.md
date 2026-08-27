## Context

Database schema is currently deployed by hand. `Script/SQL/` contains:

```
  Install.sql                        create-everything, partially IF-NOT-EXISTS guarded
  Migration_AddConfigurationTable.sql   one ad-hoc migration
  Working query.sql                  developer scratch
```

No database records what has been applied to it. Determining whether an environment is current requires inspecting its objects and comparing them to the scripts by eye.

The system runs across multiple environments (`ENVIRONMENTTABLE` distinguishes dev, sandbox, and production tiers), and the platform is intended to be repackaged for additional clients. That multiplies environments by clients, with each client potentially on a different engine version.

Two properties of the existing SQL shape this design:

- `SPINSERTMESSAGEQUEUE` is written as `CREATE OR ALTER PROCEDURE`. It is inherently re-runnable and must not be applied only once, or the deployed procedure will drift from the file in source control.
- The data access layer is raw ADO.NET with stored procedures. There is no ORM model, so any tool requiring one is a poor fit.

`add-entity-hold-release` is the immediate forcing function: its schema step is the largest this project has had and must reach every environment.

## Goals / Non-Goals

**Goals:**

- Every database records which migrations it has received.
- Running the runner repeatedly is safe and produces no change when the database is current.
- A database created from nothing reaches the current schema by running the runner.
- Schema version is tied to the deployed code version and cannot be silently skipped.
- Stored procedures, views, and functions always match their files in source control after a deployment.
- Adding a migration is a routine, documented action rather than a judgement call.

**Non-Goals:**

- Down-migrations or automated rollback. Migrations are forward-only (Decision 5).
- Migrating the data access layer to an ORM.
- Schema drift detection or reconciliation against an authoritative model. This is a migration-based approach, not a state-based one (Decision 1).
- Automatic migration on application startup (Decision 3).
- Changing any application behaviour. The runner applied to a current database reproduces today's schema exactly.
- Cross-client schema variation. All clients are assumed to share one schema; per-client schema branches are out of scope.

## Decisions

### 1. DbUp, over the alternatives

DbUp is a small library that executes ordered SQL scripts and records them in a journal table.

*Alternatives considered:*

- **EF Core Migrations.** Requires a code-first entity model. The data layer is raw ADO.NET with stored procedures, so adopting EF purely for migrations would mean modelling the entire schema and would still handle stored procedures awkwardly. Rejected as disproportionate and mis-shaped.
- **SSDT / DACPAC.** Declarative: define the desired schema, let the tool diff and generate a deployment script. Integrates well with Azure DevOps, which is already in use. Rejected because generated diffs can be surprising or destructive on a schema the tool did not create, seed data is awkward to express, and a state-based model makes data migrations — such as seeding the status catalogue — second-class.
- **Flyway.** Mature, with a native versioned/repeatable distinction. Rejected on dependency grounds: it introduces an external runtime or CLI into a .NET-only toolchain for a capability DbUp already provides.
- **A bespoke runner.** Roughly fifty lines of PowerShell over a version table. Tempting at four environments, but reimplements ordering, journaling, transaction handling, and concurrency control — each with edge cases that are cheap to get wrong and expensive to discover.

DbUp matches the existing style: raw SQL files, no model, minimal ceremony.

### 2. Migrations are split into versioned and repeatable

```
  VERSIONED (run once, journaled)        REPEATABLE (run every deployment)
  ------------------------------         --------------------------------
  CREATE TABLE                           CREATE OR ALTER PROCEDURE
  ALTER TABLE ... ADD                    CREATE OR ALTER VIEW
  CREATE INDEX                           CREATE OR ALTER FUNCTION
  INSERT seed data
```

Journaling a stored procedure is the most common way this style of tooling is misused: the procedure is applied once, then edited in the file without ever being reapplied, and the deployed object silently falls behind. `SPINSERTMESSAGEQUEUE` is already `CREATE OR ALTER` and belongs in the repeatable set from the outset.

Repeatable scripts run after all pending versioned scripts, so a procedure referencing a newly added column is applied after that column exists.

### 3. The runner executes from the deployment pipeline, not from application startup

```
  Startup migration            Pipeline migration
  -----------------            ------------------
  N replicas boot              single execution
  -> N concurrent attempts     explicit, ordered step
  schema changes implicitly    auditable; approvable before production
```

Automatic migration on startup is convenient in development and hazardous in production: multiple API or Functions instances starting together contend for the same migration, and schema change becomes an implicit side effect of a deployment rather than a decision. A separate console project invoked as a pipeline step keeps it explicit and gateable.

DbUp also takes a lock, so a concurrent invocation waits rather than corrupting; this is defence in depth, not the primary control.

### 4. Migration scripts are embedded resources in the runner assembly

Scripts ship inside the built artifact rather than being read from a directory at run time. A given build of the runner therefore contains exactly the migrations that correspond to that commit — a deployment cannot accidentally apply a newer or older script set than the code it accompanies, and there is no dependency on file layout on the deploying machine.

### 5. Migrations are forward-only

Down-migrations are not written. Changes are additive: columns and objects are deprecated rather than dropped, and destructive statements are avoided.

This matches the rollback strategy already adopted in `add-entity-hold-release`, which states that schema additions are additive and need not be reverted. Recovery from a bad migration is a new forward migration, not a reversal.

### 6. Existing databases are baselined; new databases start from a baseline script

```
  fresh database    -> 001_baseline.sql, then 002, 003, ...
  existing database -> journal pre-populated with the scripts already
                       represented by its current objects; runner starts
                       from the first genuinely pending script
```

`Install.sql` becomes `001_baseline.sql` substantially unchanged, so the from-scratch path stays in a single reviewable script.

Baselining is the only step in this change requiring per-environment care. It is performed once per existing database and verified before the runner is allowed to apply anything.

### 7. Script naming carries order and intent

A zero-padded numeric prefix followed by a description, ordered lexicographically, with versioned and repeatable scripts in separate folders. Numbers are allocated by the author; a collision is a merge conflict, which is the desired outcome rather than a silent reordering.

### 8. The runner uses the existing SQL administrator credential for now

The runner needs schema-altering rights, which are broader than the application's runtime rights. Ideally it would authenticate as a dedicated principal holding only DDL permissions, or via a managed identity.

For this change it reuses the existing SQL administrator account already present in the `EIMDB` connection string. This is a deliberate interim choice: separating the credential depends on the deferred Managed Identity work, and blocking the runner on that would leave schema deployment manual for longer than necessary.

Consequences accepted for now:

- The runner holds administrative rights over the database, so a faulty migration has no permission boundary limiting its blast radius. Mitigated by forward-only additive migrations (Decision 5) and by applying to lower environments first.
- The credential is shared with the application rather than being distinct and separately rotatable.
- The connection string must be supplied to the runner at execution time and must not be committed. This is the same requirement the deployment scripts already have and currently violate.

This is recorded as intentional debt to be retired alongside the Managed Identity work, not as the intended end state.

## Risks / Trade-offs

- **Incorrect baselining causes the first run to attempt recreating existing objects** -> Baseline each environment individually, run the runner in a preview or what-if mode first, and verify the pending list is empty on a database believed current before applying anything anywhere else.
- **A repeatable script is misfiled as versioned** -> Reviewer checklist and documentation; any `CREATE OR ALTER` object is repeatable by definition. The symptom is silent drift rather than an error, so it is worth an explicit review step.
- **Forward-only means a bad migration cannot be reversed** -> Additive changes only, applied to a lower environment first, with the pipeline gated before production. Recovery is a corrective forward migration.
- **Two developers allocate the same migration number** -> Surfaces as a merge conflict on the filename, which is the intended behaviour. Documented so it is understood rather than worked around.
- **The runner becomes a place where ad-hoc production fixes are laundered** -> Documentation states that migrations are reviewed code. `Working query.sql` is removed from the deployable set to discourage scratch SQL from entering the pipeline.
- **A migration succeeds on one environment and fails on another because of pre-existing drift** -> Baselining surfaces existing drift at the point it is least costly. Any divergence found is resolved before the environment is brought under the runner.
- **The runner holds administrative database rights (Decision 8)** -> Forward-only additive migrations, lower environments first, and a gated production step. Retire alongside the Managed Identity work.

## Migration Plan

Introducing the runner is itself staged, and no step changes application behaviour.

1. **Restructure scripts.** `Install.sql` becomes `Migrations/001_baseline.sql`; `Migration_AddConfigurationTable.sql` becomes `002_add_configuration_table.sql`; `SPINSERTMESSAGEQUEUE` moves into the repeatable folder. `Working query.sql` leaves the deployable set.
2. **Add the runner project.** Console application, DbUp, embedded scripts, added to the solution. Not yet wired into any pipeline.
3. **Verify against a scratch database.** Run against an empty database and confirm the resulting schema matches a current environment.
4. **Baseline the lowest environment.** Pre-populate its journal, run the runner, confirm zero pending and no changes applied.
5. **Baseline remaining environments**, lowest to highest, resolving any drift discovered.
6. **Wire into deployment.** Add the migration step to the deployment scripts, gated before production.
7. **Document.** How to add a versioned migration, how to add a repeatable one, and how to baseline a new environment.

**Rollback.** Until step 6, the runner is inert and can be abandoned by deleting the project; script relocation is cosmetic. After step 6, removing the migration step returns deployment to its current manual process, and the journal table is harmless if left in place.

## Open Questions

- **Which pipeline runs it?** `Script/Deploy-Manual/` contains App Service and Container Apps variants alongside manual scripts. The migration step belongs in whichever path is authoritative, and that has not yet been decided.
- **Are existing environments actually identical?** Baselining will reveal any drift between them. If drift exists, reconciling it is additional work this change cannot scope in advance.
- **Should the dashboard surface the schema version?** Reading the journal would let operators confirm which schema an environment is on. Useful once multiple clients exist, unnecessary before then.
