# Database migrations

Schema for `eimdb` is applied by `ARM.Integration.DbMigrator`, a console runner built on DbUp.
No application process changes the schema at startup.

The migrator is required for a new database and for every later release that adds a migration. An
empty Azure SQL database does not contain the tables, indexes, routing rows, stored procedure, or
trigger used by the API, EIM.Customer, and Gateway. Deploying code without first applying migrations
produces table-not-found errors; it is not a supported deployment mode.

`script/deployment/Deploy-Eim.ps1 -Component schema` runs the migrator. The code-only Azure DevOps
pipelines intentionally do not run it because they promote application packages without receiving a
database connection string.

## Where scripts live

| Folder | Category | Applied |
|---|---|---|
| `script/SQL/Migrations/` | Versioned | Once, in filename order, recorded in `dbo.SchemaVersions` |
| `script/SQL/Repeatable/` | Repeatable | Every run, after all pending versioned scripts |
| `script/SQL/_scratch/` | Neither | Never — not embedded, not deployed |

Scripts stay in the repository as ordinary `.sql` files but are compiled into the runner as
embedded resources, so a given build always carries exactly the migrations of its source revision.
A build cannot drift from its scripts.

The embedded name is the folder prefix plus the filename — `Migrations.003_add_routetable.sql`,
`Repeatable.R__SPINSERTMESSAGEQUEUE.sql`. That embedded name is what gets written to the journal,
so **renaming an applied script makes the runner treat it as new** and try to apply it again.
Don't rename applied migrations.

## Versioned or repeatable?

**Repeatable** is for anything expressed as `CREATE OR ALTER` — stored procedures, views, functions,
triggers. The whole definition lives in one file, that file is edited in place, and the current
state of the object is always visible in one place in the repository. Name these `R__<object>.sql`.

**Versioned** is for everything else: tables, columns, indexes, constraints, data seeding, and
backfills. These describe a *transition* rather than a state, so they can only be applied once.

The test is simple: if running the script twice in a row is harmful or fails, it is versioned.

Repeatable scripts run after versioned ones, so a procedure definition may safely reference a
column that a pending migration in the same run just added.

## Adding a versioned migration

1. Create `script/SQL/Migrations/NNN_short_description.sql`, where `NNN` is the next free
   three-digit number.
2. Write the forward change only.
3. Build the runner and confirm your script appears in the embedded inventory it prints on startup.
4. Verify against a scratch database before opening a pull request.

Numbers are allocated by taking the next unused one. If two people allocate the same number on
different branches, git raises an add/add conflict on the folder at merge time. **That conflict is
the intended safety mechanism** — resolve it by renumbering the migration that merges second.
Renumbering an unmerged migration is safe precisely because it has not been applied anywhere yet.

Order is lexicographic on the embedded name, not numeric. `010` sorts after `009` correctly, but
`10` would sort before `9`. Always pad to three digits.

## Migrations are forward-only

There are no down scripts. A mistake is corrected by writing a further migration, never by editing
an applied one.

Editing an applied migration does not do what it looks like it does. DbUp journals by script name,
so an edited script that is already recorded will not re-run — the change silently does nothing on
every existing database, while a freshly created database gets the edited version. The result is
two different schemas that both believe they are current. This is the single most damaging mistake
available here, and it leaves no trace.

The same reasoning applies to `001_baseline.sql`: it is a historical record of the schema at the
point the runner was introduced, not a live description of the current schema. Do not edit it to
add things. The only safe edits are ones that change no behaviour, such as deleting dead
commented-out text.

## Running the runner

```powershell
# Preview — reports pending scripts, makes no changes
dotnet run --project ARM.Integration.DbMigrator -- --connection "<connection string>" --whatif

# Apply
dotnet run --project ARM.Integration.DbMigrator -- --connection "<connection string>"
```

The connection may instead come from the `EIMDB_MIGRATOR_CONNECTION` environment variable, so a
pipeline can supply it without it appearing in a command line. It is never echoed to output.

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Success, or `--whatif` completed |
| `1` | A script failed. The failing script name is printed to stderr |
| `2` | Usage error, or the build is not carrying its scripts |

Each versioned script runs in its own transaction. A failure part-way through a run leaves earlier
scripts applied and journaled, and the failing script neither applied nor journaled. Fix forward
and re-run; the already-applied scripts are skipped.

## Baselining an existing database

A database that already has the schema — because it was built by hand before the runner existed —
must be told which migrations it already represents. Otherwise the first run tries to apply
`001_baseline.sql` against objects that exist and fails.

Baselining pre-populates the journal so those scripts are treated as already applied.

**Do this per environment, lowest first.**

### 1. Confirm the journal does not already exist

```sql
SELECT OBJECT_ID('dbo.SchemaVersions');   -- expect NULL
```

A non-null result means the environment is already under runner management. Stop, and run
`--whatif` instead.

### 2. Create the journal table

The runner creates this table itself on a database it manages from empty. When baselining you are
creating it ahead of that, so **the definition must match what DbUp expects exactly**.

Rather than trusting the DDL below, get it from the tool: run the migrator against an empty scratch
database, then script out the table it created and use that. The DDL DbUp 7.2 produces is:

```sql
CREATE TABLE [dbo].[SchemaVersions] (
    [Id]         INT IDENTITY(1,1) NOT NULL
                 CONSTRAINT [PK_SchemaVersions_Id] PRIMARY KEY,
    [ScriptName] NVARCHAR(255) NOT NULL,
    [Applied]    DATETIME NOT NULL
);
```

### 3. Insert the scripts this database already represents

The values are the *embedded* names, not the file paths.

```sql
INSERT INTO dbo.SchemaVersions (ScriptName, Applied) VALUES
    ('Migrations.001_baseline.sql',                SYSUTCDATETIME()),
    ('Migrations.002_add_configuration_table.sql', SYSUTCDATETIME());
```

Insert a row for each versioned script whose effect is already present, and **only** those. A
migration that the database does not yet reflect must be left out so it applies on the next run.

Repeatable scripts are never journaled, so they need no baseline rows. They will simply re-apply.

Confirm the names match what the runner reports. Running it with no arguments prints its full
embedded inventory before it needs a connection, then exits `2`:

```powershell
dotnet run --project ARM.Integration.DbMigrator
```

```
Embedded: 2 versioned, 1 repeatable.
  Migrations.001_baseline.sql
  Migrations.002_add_configuration_table.sql
  Repeatable.R__SPINSERTMESSAGEQUEUE.sql
No connection supplied. Pass --connection "<connection string>" or set EIMDB_MIGRATOR_CONNECTION.
```

### 4. Verify

```powershell
dotnet run --project ARM.Integration.DbMigrator -- --connection "<connection string>" --whatif
```

Expect `Versioned: nothing pending.` Anything else means the baseline rows are wrong — most often a
typo in a script name, which presents as that script still being pending.

### 5. Run for real

```powershell
dotnet run --project ARM.Integration.DbMigrator -- --connection "<connection string>"
```

This applies no versioned script and re-applies every repeatable one. Confirm the repeatable
objects are unchanged afterwards.

### Recording drift

Baselining environments in ascending order tends to expose drift, because an object present in one
environment and absent in another shows up as a script that cannot be baselined consistently.
Record any difference found rather than silently correcting it — the difference itself is evidence
about how the environments diverged, and correcting it belongs in its own migration.

## Onboarding a new client

A new client starts from an empty database and needs no baselining. Create the database, then run
the migrator once. It applies every versioned migration in order, then every repeatable script.

This only holds while `001_baseline.sql` remains unedited. If additive changes are made to the
baseline instead of to new migrations, a new client and an existing one end up on different
schemas.

## Deploying the hold and release change

Migrations `004`–`008` and the route seeding belong to the hold/release work. They are additive
and safe to apply while the previous build is still running: nothing existing reads the new
columns, and every route is seeded enabled, so behaviour does not change until the new build is up.

**Run the migrator before deploying the application, not after.** The new build reads
`ROUTETABLE`, `STATUSTABLE` and `MESSAGEQUEUETABLE.DEDUPEKEY` on startup and will fail its route
registry validation without them.

| Script | Adds |
|---|---|
| `004_add_status_and_route_tables.sql` | `STATUSTABLE`, `ROUTETABLE` |
| `005_extend_messagequeuetable.sql` | `RETRYCOUNT`, `NEXTATTEMPTDATETIME`, status index |
| `006_seed_routetable.sql` | One route per environment/source/entity seen in live traffic |
| `007_rename_simulation_queue_field.sql` | `Simulation:D365Queue` → `D365FscDataEventQueue` |
| `008_add_dedupe_key.sql` | `DEDUPEKEY` and its filtered index |

After running, confirm the seeding covers every environment you expect:

```sql
SELECT ENVIRONMENT, COUNT(*) FROM dbo.ROUTETABLE GROUP BY ENVIRONMENT;
```

A missing route is not a cosmetic gap. An arriving message with no route is dead-lettered with
reason `NoRoute` rather than processed, so a seeding hole turns into lost traffic.

### App settings the new build requires

`ReleaseSweepSchedule`, `ReleaseSweepBatchSize`, `RetrySweepSchedule`, `RetrySweepBatchSize`,
`RetryMaxAttempts`, `RetryBaseDelaySeconds`, `RetryMaxDelaySeconds`, and `D365FscDataEventQueue`.

`D365FscDataEventQueue` is a **rename** of `D365DataEventQueue`. A timer or queue trigger whose
setting is absent fails to bind and takes the whole host down, so add the new name alongside the
old one, deploy, confirm the functions are indexed, and only then remove the old name.

`CaptureToD365QueueActive` is dead and should be removed; it no longer does anything.

### Timestamps across the migration boundary

`001` wrote `CREATEDDATETIME` with `SYSDATETIME()` and the old trigger stamped `MODIFIEDDATETIME`
with server local time. Both now use `SYSUTCDATETIME()`. Where a database server is not set to UTC,
rows written before the migration and rows written after are on different clocks, and any age
computed across that boundary — the oldest-held age on the route console, most visibly — is wrong
by the server's offset.

On Azure SQL this is moot: the server runs UTC, so `SYSDATETIME()` and `SYSUTCDATETIME()` agree and
the historical rows were already effectively UTC. Verify rather than assume, on any server that is
not Azure SQL:

```sql
SELECT DATEDIFF(MINUTE, SYSUTCDATETIME(), SYSDATETIME());   -- expect 0
```

A non-zero result means ages spanning the deployment are unreliable until the pre-migration rows
age out. It does not affect the sweeps, which compare only values written after the change.

### Rolling back

There is no schema rollback and none is needed — every change is additive. Behaviour is reverted
with configuration:

1. **Enable every route.** `UPDATE dbo.ROUTETABLE SET ENABLED = 1;` With no route paused and no
   backlog, the hold gate admits each message at status `0` and processes it immediately, which is
   the pre-change path.
2. **Disable both sweeps** by adding `AzureWebJobs.ReleaseSweepHandler.Disabled` and
   `AzureWebJobs.RetrySweepHandler.Disabled`, both `true`. Leave the schedule settings in place —
   deleting them stops the triggers binding and the host fails to start.
3. **Set `RetryMaxAttempts` to `1`.** Without this the first two steps leave a trap: a transient
   failure is still parked at status `7` awaiting a retry that a disabled sweep will never collect.
   At `1`, the first failure goes straight to a terminal status instead.

Step 3 is the one that is easy to miss, and it is the one that strands messages. The behaviour is
pinned by `RetryScheduleCalculatorTests.One_max_attempt_schedules_no_retry_at_all`.

Held messages already captured before a rollback stay at status `6`. Nothing collects them once
the release sweep is disabled, so drain the backlog first, or re-enable the sweep long enough to
clear it.

## Review checklist

When reviewing a pull request that touches `script/SQL/`:

- [ ] Is the script filed in the correct category? `CREATE OR ALTER` objects belong in
      `Repeatable/`; everything else in `Migrations/`.
- [ ] Is the migration number the next free one, padded to three digits?
- [ ] Does the change modify an **existing** file under `Migrations/`? If so, is it genuinely
      behaviour-neutral? Any additive edit to an applied migration must be rejected.
- [ ] Is the change forward-only, with no down script and no destructive rewrite of history?
- [ ] Has it been run against a scratch database, and run twice to confirm the second run applies
      nothing?
- [ ] If a repeatable object was edited, does the file still contain its complete definition rather
      than a fragment?
