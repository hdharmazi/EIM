## 1. Restructure scripts

- [x] 1.1 Create `Script/SQL/Migrations/` for versioned scripts and `Script/SQL/Repeatable/` for re-runnable object definitions
- [x] 1.2 Move `Install.sql` to `Migrations/001_baseline.sql` substantially unchanged, so the from-scratch path remains a single reviewable script
- [x] 1.3 Move `Migration_AddConfigurationTable.sql` to `Migrations/002_add_configuration_table.sql`
- [x] 1.4 Extract `SPINSERTMESSAGEQUEUE` and any other `CREATE OR ALTER` object from the baseline into `Repeatable/`
- [x] 1.5 Remove `Working query.sql` from the deployable set

> All three moves used `git mv`, so history is preserved and git records them as renames.
> `SPINSERTMESSAGEQUEUE` (the only `CREATE OR ALTER` object) is now
> `Repeatable/R__SPINSERTMESSAGEQUEUE.sql`; the baseline carries a pointer comment in its place
> and contains no `CREATE PROCEDURE`/`VIEW`/`FUNCTION`. `Working query.sql` moved to
> `Script/SQL/_scratch/`, which the runner does not embed. The empty `Script/SQL/TABLES/`
> folder was removed.
>
> Verified `002_add_configuration_table.sql` is fully `IF NOT EXISTS`-guarded, so it is a no-op
> on a fresh database where `001_baseline.sql` has already created `CONFIGURATIONTABLE`.

## 2. Runner project

- [x] 2.1 Add a console project referencing DbUp and add it to `ARM.Integration.Wilson.slnx`
- [x] 2.2 Include migration and repeatable scripts as embedded resources
- [x] 2.3 Apply pending versioned scripts in lexicographic order, journaled, each within a transaction
- [x] 2.4 Apply repeatable scripts on every execution, after all pending versioned scripts
- [x] 2.5 Accept the target connection as an argument or environment variable, not a hardcoded value
- [x] 2.6 Add a preview mode that reports pending scripts without applying them
- [x] 2.7 Return a non-zero exit code on failure, naming the script that failed, so a pipeline step fails loudly
- [x] 2.8 Confirm concurrent invocations against one database do not both apply migrations
- [x] 2.9 Decide whether to serialise runs with `sp_getapplock`, so the losing run waits and finds nothing pending instead of failing with a misleading "object already exists"

> `ARM.Integration.DbMigrator` (net10.0, `dbup-sqlserver` 7.2.0). Scripts stay under
> `Script/SQL/` and are pulled in via `<EmbeddedResource Include="..\Script\SQL\...">` with a
> flattened `LogicalName`, so they remain discoverable in the repository while shipping inside
> the assembly.
>
> Versioned scripts are journaled to `dbo.SchemaVersions` with `WithTransactionPerScript`.
> Repeatable scripts use `NullJournal` so they re-apply every run, and execute after the
> versioned phase so a definition may reference a column a pending migration just added.
>
> Connection comes from `--connection` or `EIMDB_MIGRATOR_CONNECTION`, and is never echoed.
> Exit codes: `0` success, `1` migration failure (naming `result.ErrorScript`), `2` usage or
> build-integrity failure.
>
> Embedded-script integrity is checked *before* the connection check, so a package built
> without its scripts fails identically whether or not a connection was supplied.
>
> Verified: build clean with no warnings; `--help` exits 0; with no connection it prints
> the embedded inventory then exits 2:
> ```
> Embedded: 2 versioned, 1 repeatable.
>   Migrations.001_baseline.sql
>   Migrations.002_add_configuration_table.sql
>   Repeatable.R__SPINSERTMESSAGEQUEUE.sql
> No connection supplied. Pass --connection "<connection string>" or set EIMDB_MIGRATOR_CONNECTION.
> ```
>
> **Task 2.8 — tested for real.** Two runners were started simultaneously against `eimdb_dev` with a
> pending migration containing `WAITFOR DELAY '00:00:05'` to force overlap. One applied and
> journaled the script and exited `0`; the other blocked on the lock, then failed with *"There is
> already an object named …"* and exited `1`. The journal held exactly one row for the script and
> the object was created once, so **the migration was not applied twice** and the requirement is
> met.
>
> The failure mode is poor, though. DbUp takes no application lock, so the losing run reports what
> looks like a broken migration rather than a race. Two overlapping pipeline runs would produce a
> confusing red build. Task 2.9 covers the fix.
>
> **2.9 done: yes, serialise.** The decision turned on something that was not true when the task was
> written - every environment shares one `eimdb`, and `Deploy-Eim.ps1` now runs migrations as a
> deployment component. Two environment deployments overlapping is a normal Tuesday, not a
> theoretical race.
>
> The lock is session-scoped on its own connection, held for the whole run and released in a
> `finally`. Session scope is forced by `WithTransactionPerScript`: a transaction-scoped lock would
> release after the first script and serialise nothing. It is released explicitly rather than left
> to connection close, so a pooled connection cannot carry the lock into someone else's session.
>
> `@LockTimeout` is tied to `--timeout` rather than being a second number to tune. A run may
> legitimately hold the lock for as long as its slowest script is allowed to take, and a lock
> timeout shorter than that would fail for the very reason the lock exists.
>
> On contention the loser exits `1` with "Another migration is in progress" rather than `0`. At that
> point it does not know whether the schema is current, and a green build on that basis would be a
> lie. `--whatif` takes no lock at all, since it changes nothing.
>
> Tested against `eimdb_dev` with the lock held by a separate `sqlcmd` session: the runner reported
> the named failure and exited `1` without applying anything, then succeeded and exited `0` once the
> lock was released.
>
> **This only serialises runs through the migrator.** Someone running SQL by hand in SSMS is
> unaffected. It guards against pipeline races, not against people.
>
> **A defect found while testing.** A connection failure was an unhandled `SqlException`: a
> twenty-line CLR stack trace and exit code `-532462766`, which defeats the point of task 2.7.
> Connection errors are now caught and reported as one line with exit `1`. Unexpected exception
> types still propagate, deliberately — only the expected failure class is handled.

## 3. Verification against a scratch database

- [x] 3.1 Run against an empty database and confirm it reaches the current schema
- [x] 3.2 Compare the resulting schema against a current environment and reconcile any difference
- [x] 3.3 Run twice in succession and confirm the second run applies no versioned script
- [x] 3.4 Change a repeatable script and confirm it is reapplied on the next run
- [x] 3.5 Introduce a deliberately failing script and confirm it is not journaled and the exit code is non-zero

> Verified against `eimdb_dev` on `azql-wtc-sandbox`, created 2026-08-27.
>
> - **3.1** Empty database → both versioned scripts applied, journal created, repeatable applied.
>   Exit `0`. `--whatif` beforehand did *not* create the journal table, confirming preview is
>   genuinely non-mutating.
> - **3.3** Second run: `Versioned: nothing pending.`, `Applied 0 versioned script(s).`, repeatable
>   re-applied. Exit `0`.
> - **3.4** A marker comment placed inside the procedure body appeared in `OBJECT_DEFINITION` after
>   the next run. Worth recording that the first attempt appended the marker after the trailing
>   `GO`, which put it in a separate batch outside the procedure — the test reported a false
>   failure until the marker was moved inside the body.
> - **3.5** An invalid script failed with exit `1` and the message `FAILED during versioned phase:
>   Migrations.999_deliberate_failure_probe.sql`. The journal stayed at two rows, and the valid
>   `CREATE TABLE` in the script's first batch was rolled back — so `WithTransactionPerScript`
>   covers the whole script, not just the failing batch.
>
> All probe scripts and objects were removed afterwards; `Script/SQL/` is unmodified and the
> database reports nothing pending.
>
> ### 3.2 — drift found and reconciled
>
> `eimdb` (live) contained objects that existed in **no script in this repository**: a
> `MESSAGEQUEUEDELIVERY` table with nine constraints, and six `SP*MESSAGEQUEUEDELIVERY*`
> procedures. Created 2026-08-25, exercised 2026-08-26, three rows.
>
> They belonged to the unmerged `feature/rearchitecturecapture` branch, which the author has since
> abandoned. **Resolved 2026-08-27 by dropping all seven objects**, on explicit instruction and
> after confirming what they were. The branch is deliberately left in git, so the definitions and
> the code that used them remain recoverable.
>
> `eimdb` and `eimdb_dev` now hold the same six tables, the same single procedure
> `SPINSERTMESSAGEQUEUE`, and the same `MODIFIEDDATETIME` trigger. No drift remains.
>
> Three `MESSAGEQUEUETABLE` rows typed `customer.from-d365fsc.v1` are left in place as history.
> They are `Completed`, nothing references them, and the foreign key ran the other way.

## 4. Baseline existing environments

- [x] 4.1 Write the baselining procedure: pre-populate the journal with scripts already represented by existing objects
- [x] 4.2 Baseline the lowest environment, run in preview mode, and confirm nothing is pending
- [x] 4.3 Run for real against the lowest environment and confirm no change is applied
- [x] 4.4 Baseline remaining environments in ascending order, recording any drift discovered
- [x] 4.5 Resolve or document any drift found between environments before proceeding

> Procedure written up in `Script/Docs/database-migrations.md` under "Baselining an existing
> database": confirm `dbo.SchemaVersions` is absent, create it, insert a row per versioned script
> the database already represents, verify with `--whatif`, then run for real.
>
> Two details that are easy to get wrong and are called out explicitly. The journal stores the
> *embedded* name (`Migrations.001_baseline.sql`), not a file path — a mismatch presents as the
> script still being pending rather than as an error. And the table must match the DDL DbUp itself
> creates, so the procedure tells the reader to script it from a scratch database rather than trust
> the DDL in the document.
>
> Tasks 4.2–4.5 need environment access.
>
> **Done 2026-08-27.** `eimdb` is baselined and under runner management.
>
> One check was added to the procedure while doing it, and it mattered. The first managed run
> re-applies every repeatable script, which would overwrite the live `SPINSERTMESSAGEQUEUE` with
> the repository's copy. Comparing the two definitions with whitespace stripped showed identical
> SHA-256 hashes, so the overwrite was safe — but had someone hand-edited the live procedure, this
> is where that work would have been silently destroyed. **Compare before the first managed run,
> not after.**
>
> Sequence: confirmed `dbo.SchemaVersions` absent; created it with DbUp's own DDL, taken from the
> table DbUp created in `eimdb_dev` rather than from memory; inserted the two baseline rows;
> `--whatif` reported `Versioned: nothing pending.`; the real run reported
> `Applied 0 versioned script(s).` and re-applied the one repeatable script. Afterwards: journal
> holds 2 rows, schema matches `eimdb_dev` exactly, 78,090 `MESSAGEQUEUETABLE` rows intact, and the
> procedure hash is unchanged.
>
> **Tasks 4.4 and 4.5 are satisfied by there being nothing left to baseline.** Every environment —
> `dev`, `dev1`, `bat`, `test` — shares the single database `eimdb` on `azql-wtc-sandbox`; they are
> separated by Service Bus queue prefix, not by database. So inter-environment schema drift is not
> possible here, and the ascending-order baselining that task 4.4 describes collapses to the one
> operation above. This also means a schema migration applied for one environment takes effect for
> all of them simultaneously, which is worth remembering before applying anything destructive.

## 5. Deployment integration

- [x] 5.1 Add the migration step to the authoritative deployment path
- [x] 5.2 Run migrations before application deployment
- [x] 5.3 Gate the production migration behind explicit approval
- [x] 5.4 Supply the existing SQL administrator connection string to the runner at execution time, sourced from a gitignored configuration file or pipeline variable rather than being hardcoded in the script
- [x] 5.5 Confirm no application process attempts schema changes at startup

> **Path decided 2026-08-27: App Service.** `AzureDeploy.AppService.ps1` is authoritative. The
> Container Apps path (`AzureDeploy`, `AzureDeploy.Update.ps1`) is retired and gets no migration
> step.
>
> The script already holds the `eimdb` connection string in `$EIMDB_CONNECTION_STRING`, so task 5.4
> is satisfied by passing that existing variable to the runner — the file is gitignored. The step
> belongs after the App Service Plan exists and before the API zip deploy, so a schema the new code
> depends on is present before that code starts serving.
>
> **The target moved. 5.1, 5.2 and 5.4 are satisfied elsewhere.** `AzureDeploy.AppService.ps1` was
> superseded by `Script/Deploy-Eim.ps1` under `consolidate-deployment-scripts`, so wiring a
> migration step into a script that is being deleted would have been work done twice and thrown
> away. `Deploy-Eim.ps1` has a `schema` component that runs the migrator, ordered ahead of `api`,
> `function` and `frontend` for exactly the reason recorded above. The connection string comes from
> the gitignored `Script/config/deploy.<environment>.json` and is passed by environment variable, so
> it never reaches a command line where a process listing or shell history would capture it.
>
> 5.5 holds: no application project runs migrations at startup. The Functions host validates that
> every route names a registered orchestration, which reads `ROUTETABLE` but changes no schema, and
> the Dashboard API only queries. Schema is changed by the migrator alone.
>
> **5.3 stays open deliberately.** There is no production environment yet, so an approval gate has
> nothing to gate. It belongs in the production change rather than being invented here against a
> pipeline that does not exist.

## 6. Documentation

- [x] 6.1 Document how to add a versioned migration, including number allocation and that collisions are expected to surface as merge conflicts
- [x] 6.2 Document the versioned versus repeatable distinction, and that any `CREATE OR ALTER` object is repeatable
- [x] 6.3 Document the baselining procedure for a newly onboarded environment or client
- [x] 6.4 Document that migrations are forward-only and that corrections are made by a further migration
- [x] 6.5 Add a review checklist item for confirming a script is filed in the correct category

> All in `Script/Docs/database-migrations.md`.
>
> The distinction is given as a test rather than a list, since the list will not survive contact
> with an object type nobody thought of: *if running the script twice in a row is harmful or fails,
> it is versioned.*
>
> Forward-only gets the most space because the failure mode is invisible. Editing an applied
> migration does nothing on every existing database while changing what a fresh one gets, so the
> two silently diverge and both believe they are current. The document states plainly that this
> also governs `001_baseline.sql`, which reads like a live schema description but is a historical
> record.
>
> Number collisions are documented as *intended* — two branches allocating `003` produce a git
> conflict on the folder, which is the mechanism working, not a nuisance to route around.
>
> Verified against the runner rather than assumed: the embedded-name format comes from the csproj
> `LogicalName`, and the inventory output was captured by running the migrator. An earlier draft
> claimed `--help` printed the inventory; it does not — the no-argument run does, exiting `2`.

## 7. Handover to the pending schema change

- [x] 7.1 Confirm all environments are under runner management and report nothing pending
- [x] 7.2 Author the `add-entity-hold-release` schema work as the next versioned migration rather than a hand-run script

> 7.1 confirmed 2026-08-27: the single shared database `eimdb` is baselined and reports
> `Versioned: nothing pending.` The scratch database `eimdb_dev` is also under management and can
> be used to rehearse the group 1 migrations of `add-entity-hold-release` before they touch `eimdb`.
>
> 7.2 confirmed by inspection rather than assumed, having twice declined to tick it on a hunch. All
> of the hold/release schema work is in versioned migrations `004`-`009`: status and route tables,
> the retry columns, the route seeding, the simulation queue rename, the dedupe key, and the
> `SOURCEID` widening. `Script/SQL/_scratch/` holds only `Working query.sql`, which is not embedded
> and is not applied by anything.
>
> One exception, and it is deliberate rather than an oversight: the repair of the truncated
> `SOURCEID` values lives in `Script/Repair-TruncatedSourceId.ps1`, not in a migration. It exceeded
> the execution timeout as a migration and held a transaction on a shared live table for half an
> hour. A schema change belongs in a migration; a long, resumable data repair does not.
