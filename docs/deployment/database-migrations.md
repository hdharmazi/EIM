# Database migrations

Schema for `eimdb` is applied by `ARM.Integration.DbMigrator`, a console runner built on DbUp.
No application process changes the schema at startup.

The migrator is required for a new database and for every later release that adds a migration. An
empty Azure SQL database does not contain the tables, indexes, routing rows, stored procedure, or
trigger used by the API, Wilson.Integration.Automation, and Gateway. Deploying code without first applying migrations
produces table-not-found errors; it is not a supported deployment mode.

`script/deployment/Deploy-Wilson.ps1 -Environment bat -Component myip, schema, myipremove` runs the migrator,
using the self-contained `ARM.Integration.DbMigrator.exe` carried in the deployment package. `schema` belongs to
configurations that own their database (`bat`, `prd`); a configuration with `uses` (`test`) rejects it, because
it uses its owner's database. See [deployment.md](deployment.md).

The migrator signs in with Entra ID as the deployer: the script passes
`Authentication=Active Directory Default` and `AZURE_TOKEN_CREDENTIALS=AzureCliCredential`, so it uses the Azure
CLI identity (the service connection's principal in the pipeline, your `az login` by hand). That identity must be
in the SQL server's Entra admin group. No SQL password is involved.

Local development uses its own database (see "Local database" below), never the shared `eimdb`.

## The production database

Production has its own database, `sqldb-eim-wtc-prd` on `sql-eim-wtc-prd` in `d365fsc-integration-prod`,
created by the `sql` component ([production-go-live.md](production-go-live.md)) with the default collation, the
same case-insensitive collation as `eimdb` (`SQL_Latin1_General_CP1_CI_AS`). Route lookups rely on it: the
Automation host sends ENVIRONMENT `prd` and the rows say `PRD`.

- `schema` runs there from `prd`, in the `Deploy_prd` stage of `pipelines/eim-prd.yml`, behind the production
  approval.
- It runs every migration from `001`, exactly the scripts `eimdb` runs. No migration branches on which database
  it is in. A database created empty migrates with no manual step: `002a` creates the `MODIFIEDDATETIME`
  trigger that `003` needs (see "The one exception" below).
- Migration `015_seed_prd_routes.sql` seeds its ten `PRD` routes (the address delete route disabled with
  `Hold`). On `eimdb` the same rows are inert: no non-production message carries `PRD`.
- `001`, `002` and `006` also seed non-production rows there (a sample `ENVIRONMENTTABLE` row,
  `SimulationEnvironment` configuration rows, a developer `USERTABLE` admin and the BAT/DEV/DEV2/TEST routes).
  They are inert on production and stay; the go-live steps review the `USERTABLE` row.
- The `PRD` `ENVIRONMENTTABLE` profiles hold D365 credentials and are entered through the dashboard, never by
  migration.

## When migrations run, and the expand/contract rule

The pipeline (`pipelines/eim.yml`) runs migrations **once per run, in the `Deploy_bat` stage, before bat's
code**, and only on a manual run with `runMigrations` ticked. A merge to `main` never migrates, and `Deploy_test`
never migrates. The stage's database step wraps the migrator in `myip` / `myipremove` (in a `finally`), so the
agent's IP is on the SQL firewall only while `schema` runs.

One `eimdb` holds the rows of every ENVIRONMENT value: `DEV`, `DEV2`, `BAT` and `TEST`. These are database
values, not deployment configurations; `DEV2` has no Function App and no deployment configuration of its own,
but its rows are migrated with everyone else's. After `Deploy_bat` migrates, `test` keeps running the previous
build until its stage is approved, possibly for days, and a rejected stage leaves it there.
So every migration must work with:

- the new build and the build still running in later stages, and
- every ENVIRONMENT value's rows, including `DEV2`.

That means **expand/contract**:

- **Expand** in one release: add tables, procedures, nullable columns or columns with defaults.
- **Contract** in a later release: remove or rename only after every environment runs code that no longer
  uses the old shape.

A release that cannot be expand-only, such as the `014` route seed below, has to be run by hand in separate
steps. Do not tick `runMigrations` for it.

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

### The one exception: `002a_create_modifieddatetime_trigger.sql`

`003_backfill_truncated_sourceentity.sql` disables and re-enables the `MODIFIEDDATETIME` trigger on
`MESSAGEQUEUETABLE`. That trigger is created only by the repeatable
`R__TR_MESSAGEQUEUETABLE_MODIFIEDDATETIME.sql`, and repeatables run after every versioned script. On
`eimdb` the trigger already existed when `003` ran, so nothing failed. On a database created empty (the
production `sqldb-eim-wtc-prd`, a local `eimdb`, a new client) `003` failed, because the trigger did not
exist yet.

`003` is applied, so it cannot be edited. Instead, `002a_create_modifieddatetime_trigger.sql` is inserted
between `002` and `003`. It creates the trigger only when it is missing, with the same body as the
repeatable:

- **Order.** DbUp sorts by embedded name with an ordinal comparison, so `Migrations.002_…` sorts before
  `Migrations.002a_…`, which sorts before `Migrations.003_…`. On an empty database `002a` runs before `003`.
- **Existing databases.** On `eimdb`, which already has the trigger and has journaled `003`, the next run
  applies `002a` as a pending script. DbUp applies any unjournaled script, whatever its position. It does
  nothing there except write its journal row.
- **Ownership.** The repeatable script still owns the definition. It runs after every versioned script and
  re-applies the current body with `CREATE OR ALTER`, so the copy in `002a` is only a bootstrap.

This is the **only** allowed exception to the next-number rule. An inserted script is applied out of
numeric order on every database that has already passed that point, so it is safe only when, like `002a`,
it is a no-op wherever the later migrations have already run. Every other new migration takes the next
free three-digit number.

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

Against Azure SQL, sign in with Entra ID rather than a password. This is the form `Deploy-Wilson.ps1` uses, with
your Azure CLI identity (which must be in the server's Entra admin group):

```powershell
$env:AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
$env:EIMDB_MIGRATOR_CONNECTION = 'Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<database>;Authentication=Active Directory Default;Encrypt=True'
dotnet run --project ARM.Integration.DbMigrator -- --whatif
```

Against a local SQL Server, SQL or Windows authentication works as before (see "Local database" below).

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Success, or `--whatif` completed |
| `1` | A script failed. The failing script name is printed to stderr |
| `2` | Usage error, or the build is not carrying its scripts |

Each versioned script runs in its own transaction. A failure part-way through a run leaves earlier
scripts applied and journaled, and the failing script neither applied nor journaled. Fix forward
and re-run; the already-applied scripts are skipped.

## Local database

There is no deployed development environment. A developer runs the hosts on their own machine
against a local `eimdb`, built by the same migrator:

1. Install SQL Server Developer or Express, or use LocalDB (`(localdb)\MSSQLLocalDB`).
2. Create the database:

   ```sql
   CREATE DATABASE eimdb;
   ```

3. Apply every migration:

   ```powershell
   $env:EIMDB_MIGRATOR_CONNECTION = 'Server=localhost;Database=eimdb;Integrated Security=true;TrustServerCertificate=true'
   dotnet run --project ARM.Integration.DbMigrator -- --whatif   # optional preview
   dotnet run --project ARM.Integration.DbMigrator
   ```

   For LocalDB, use `Server=(localdb)\MSSQLLocalDB` instead of `Server=localhost`.

4. Point `EIMDB` in your `local.settings.json` at the same database.

Run the migrator again after pulling new migrations. A migration you are writing can be applied,
dropped and re-applied locally as often as needed (drop and recreate the database to start over).
Only merged migrations reach the shared `eimdb`, through the pipeline's `Deploy_bat` stage.

The `eimdb_dev` catalog on the shared SQL server is a leftover of the old development setup and
will be dropped by hand. Do not point a local host at it, or at the shared `eimdb`.

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

## Deploying the customer address delete route

Migration `014_seed_customer_address_delete_route.sql` seeds a route naming
`CustomerAddressDelete_To_Capture`. This release **reverses the usual order: deploy code first,
then migrate.** Wilson.Integration.Automation runs `RouteRegistryValidator` at startup and refuses to start when a
route names an orchestration it has not registered. If the route row exists while the previous build
starts — a restart, or a Consumption scale-out — the host is down, not degraded.

The pipeline runs `schema` in `Deploy_bat`, before any Automation code, so this release must **not** be
deployed with `runMigrations` ticked. Deploy the code first, then migrate in a separate invocation:

```powershell
# 1. Code. The new build validates cleanly: no route names the new orchestration yet.
#    A pipeline run with runMigrations cleared, or by hand from the package:
./Deploy-Wilson.ps1 -Environment bat -Component backend, automation   # reprocess uses the same registry
./Deploy-Wilson.ps1 -Environment test -Component automation

# 2. Confirm each host started and logged "Route validation passed" before continuing.

# 3. Schema. 014 is the only pending versioned script expected.
./Deploy-Wilson.ps1 -Environment bat -Component myip, schema, myipremove
```

The schema is shared, so step 3 needs every environment's Automation app to be on the new build.

`014` throws unless exactly four rows exist for the new route (BAT, DEV, DEV2, TEST). Confirm:

```sql
SELECT ENVIRONMENT, ORCHESTRATIONTYPE, ENABLED, ONPAUSE
FROM dbo.ROUTETABLE
WHERE SOURCEENTITY = N'mserp_wtc_custpostaladdressdeletedv2entity';
```

Between step 1 and the route cache refreshing after step 3 (up to 60 seconds), the new build knows
the entity but finds no route, so any address deletion in that window is dead-lettered on the
data-event queue with reason `NoRoute`. Keep the gap short, then resubmit those dead letters.
Before this release they were silently discarded, so nothing is lost relative to today.

### Before enabling in a shared environment

Messages sent to Capture cannot be recalled. Before the route carries real traffic in BAT or TEST,
the Capture team must have confirmed:

- a delete message whose `SourceId` is the `AddressLocationId` (e.g. `000146089`) rather than an
  operation GUID is accepted;
- whether a `Delete` needs the full address body or only `DataAreaId`, `CustomerAccount` and
  `AddressLocationId`.

Until then, keep the route paused in those environments (`ENABLED = 0`, `ONPAUSE = 'Hold'`) so
deletions are captured and held rather than sent or lost.

### Verifying end to end

Delete a customer address in F&SC, then:

```sql
SELECT TOP 5 m.ID, m.ENVIRONMENT, m.SOURCEID, m.STATUS, m.CREATEDDATETIME
FROM dbo.MESSAGEQUEUETABLE m
WHERE m.SOURCEENTITY = N'mserp_wtc_custpostaladdressdeletedv2entity'
ORDER BY m.ID DESC;

SELECT d.NAME, d.DETAIL
FROM dbo.MESSAGELOGDETAIL d
WHERE d.MESSAGEID = <ID from above>
ORDER BY d.ID;
```

Expect `SOURCEID` equal to the address location id, a `DetermineAction` step of `Delete`, and a
`Result` step whose envelope has `"Action":"Delete"` and `"Entity":"CustomerAddress"`. Save the
payload from `MESSAGEQUEUETABLE.PAYLOAD` over the test fixture
`Wilson.Integration.Tests/TestData/CustomerAddressDeleted.OnExternalUpdated.json` and re-run the
tests.

### Watching the deleted-flag guard

An event on the delete entity whose `mserp_wtc_isdeleted` is not `Yes` is not forwarded; it is
completed with a Warning-level `Result` step. Over the first cycle, count them:

```sql
SELECT m.ENVIRONMENT, COUNT(*) AS Skipped, MAX(d.CREATEDDATETIME) AS Latest
FROM dbo.MESSAGELOGDETAIL d
JOIN dbo.MESSAGEQUEUETABLE m ON m.ID = d.MESSAGEID
WHERE m.SOURCEENTITY = N'mserp_wtc_custpostaladdressdeletedv2entity'
  AND d.NAME = N'Result'
  AND d.DETAIL LIKE N'skipped:%mserp_wtc_isdeleted%'
GROUP BY m.ENVIRONMENT;
```

Zero is the expected answer. Any row means F&SC can set the flag back to `No` on this entity, and
what an "undelete" should do in Capture becomes an open design question.

### Rolling back

Disable the route: `UPDATE dbo.ROUTETABLE SET ENABLED = 0 WHERE SOURCEENTITY =
N'mserp_wtc_custpostaladdressdeletedv2entity';` Deletions are then held, not lost. To roll code
back to a build without `CustomerAddressDelete_To_Capture`, delete the four route rows **first**;
otherwise that build fails route validation at startup.

## Review checklist

When reviewing a pull request that touches `script/SQL/`:

- [ ] Is the script filed in the correct category? `CREATE OR ALTER` objects belong in
      `Repeatable/`; everything else in `Migrations/`.
- [ ] Is the migration number the next free one, padded to three digits? The only inserted script is
      `002a` (see "The one exception"); reject any other.
- [ ] Does the change modify an **existing** file under `Migrations/`? If so, is it genuinely
      behaviour-neutral? Any additive edit to an applied migration must be rejected.
- [ ] Is the change forward-only, with no down script and no destructive rewrite of history?
- [ ] Is it expand-only, so the previous build and every ENVIRONMENT value (including `DEV2`) keep
      working? If not, does the PR say how it must be deployed instead of a `runMigrations` run?
- [ ] Has it been run against a scratch database, and run twice to confirm the second run applies
      nothing?
- [ ] If a repeatable object was edited, does the file still contain its complete definition rather
      than a fragment?
- [ ] Does a per-environment seed list `PRD` as well as `BAT`, `DEV`, `DEV2` and `TEST`? Production
      runs every migration on its own database, so a seed without `PRD` leaves production unrouted.
- [ ] Is the migration safe on both the non-production `eimdb` and the production `sqldb-eim-wtc-prd`?
      Every migration runs on both, unchanged; a parity check must count only the rows the migration
      itself seeds (filter by ENVIRONMENT), never the whole table.
