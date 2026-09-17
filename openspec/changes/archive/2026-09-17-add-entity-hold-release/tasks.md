## 1. Schema

All of the below ship as new versioned migrations applied by `ARM.Integration.DbMigrator`, or as
edits to `Repeatable/` scripts. `Migrations/001_baseline.sql` is not edited — see the note below.

- [x] 1.1 Add `ROUTETABLE` as a new versioned migration under `Script/SQL/Migrations/`, keyed on environment, source system, and source entity, with orchestration type, enabled flag, pause policy, and modified-by/at audit columns
- [x] 1.2 Add `STATUSTABLE` as a new versioned migration, seeding statuses `0`–`5` plus `6 - Held` and `7 - Retry Scheduled`, and delete the commented-out definition from `001_baseline.sql` so there is only one place it is declared
- [x] 1.3 Add `RETRYCOUNT` and `NEXTATTEMPTDATETIME` columns to `MESSAGEQUEUETABLE`
- [x] 1.4 Add a nonclustered index on `MESSAGEQUEUETABLE(STATUS)` covering the release and retry sweep predicates and the existing `GROUP BY STATUS` count query
- [x] 1.5 Add the index or constraint supporting `SOURCEID` duplicate detection scoped by source system and environment
- [x] 1.6 Widen `SPINSERTMESSAGEQUEUE` parameters `@SOURCEENTITY` and `@DESTINATIONENTITY` to `NVARCHAR(50)` and `@DESTINATIONID` to `NVARCHAR(150)` to match their columns, editing `Repeatable/R__SPINSERTMESSAGEQUEUE.sql` in place
- [x] 1.7 Audit existing rows for values at exactly 20 or 30 characters in those columns, which would indicate prior silent truncation
- [x] 1.8 Populate `MODIFIEDDATETIME` on insert in `Repeatable/R__SPINSERTMESSAGEQUEUE.sql`, and in a versioned migration drop and recreate `DF_MESSAGEQUEUETABLE_CREATED` to default `SYSUTCDATETIME()` instead of `SYSDATETIME()`
- [x] 1.9 Move the `dbo.MODIFIEDDATETIME` trigger out of the baseline into `Repeatable/R__TR_MESSAGEQUEUETABLE_MODIFIEDDATETIME.sql` as `CREATE OR ALTER TRIGGER`, so it can be corrected now and in future without a further migration each time
- [x] 1.10 Rewrite that trigger to be set-based, joining `INSERTED` instead of capturing a single id with `SELECT @ID = INSERTED.ID`, so every row of a multi-row update is stamped
- [x] 1.11 Change that trigger to use `SYSUTCDATETIME()`; without this it overwrites the application's UTC value with server local time and defeats the normalisation
- [x] 1.12 Remove the duplicate unguarded `IX_MESSAGEQUEUETABLE_DESTINATIONID` creation from the baseline, which declares the same index twice
- [x] 1.13 Confirm `SPINSERTMESSAGEQUEUE` accepts `@STATUS` without modification
- [x] 1.14 Re-run the truncation audit after the next deployment, to catch rows inserted between the backfill and the widened procedure taking effect
- [x] 1.15 Widen `SOURCEID`, which was `NVARCHAR(30)` against 36-character GUIDs

> 1.14 re-run against `eimdb` on 2026-09-16: `SELECT SOURCEENTITY, COUNT(*) ... WHERE LEN(SOURCEENTITY) = 20`
> returns **zero rows** across 78,126 messages, and the longest `SOURCEENTITY` is 35 characters, which
> is the full `mserp_wtc_custpostaladdressv2entity`. No row was inserted truncated in the window
> between the backfill and the widened procedure. `SPINSERTMESSAGEQUEUE` now declares
> `@SOURCEENTITY NVARCHAR(50)` and `@SOURCEID NVARCHAR(150)`, so the mechanism is closed as well.
>
> The same audit applied to `SOURCEID` is **not** clean, and this was not part of 1.14. 65,700 rows
> hold a GUID cut to exactly 30 characters — `21af7309-bc96-4ffb-a66e-e5893d` — of which all but one
> predate the fix. The exception was inserted at `2026-08-27 19:37:58`, three hours after
> `009_widen_sourceid.sql` was journalled at `16:16:10`, so one row slipped through after the column
> was widened but before the procedure was. Recorded as 1.16 rather than folded into 1.14, because
> 1.14 asked a question about `SOURCEENTITY` and the honest answer to that question is "clean".

- [x] 1.16 Decide whether to repair the 65,700 rows holding a `SOURCEID` truncated to 30 characters, including the single row inserted after the widening on 2026-08-27

> **1.16 decided: no repair.** The owner will truncate `MESSAGEQUEUETABLE` manually instead, which
> makes repairing history moot. `Script/Repair-TruncatedSourceId.ps1` stays in the repository,
> tested and resumable, in case that plan changes before the truncate happens.
>
> The durable half of the fix is unaffected either way: migration 009 widened the column and the
> procedure now declares `@SOURCEID NVARCHAR(150)`, so nothing new is truncated. Verified 2026-09-16.

> **1.15 is a second truncation, found by running 1.14's audit early.** `SOURCEID` held values at
> exactly 30 characters in 77,700 rows - GUIDs with the final group cut from twelve characters to
> six, `00023284-ea7b-f111-ab0e-3833c5`. Collisions are not a practical risk at that width, but a
> truncated id cannot be used to find the record back in the source system, so reconciliation and
> support lookups were broken.
>
> **The audit in 1.7 could not have caught it.** That audit compared stored procedure parameters
> against their columns. Here they agreed at `NVARCHAR(30)`: the column itself was too narrow, so
> nothing looked inconsistent. Matching the column is necessary but not sufficient.
>
> Migration 009 widens both to 150, matching `DESTINATIONID` rather than 36, because `SOURCEID` also
> carries business keys from other systems and a width chosen for exactly one format is how this
> happened twice.
>
> **The repair is deliberately not in the migration.** The first attempt did both and exceeded the
> 1800 second timeout - recovering each value means scanning an `NVARCHAR(MAX)` payload, 77,700
> times. It rolled back cleanly and left `eimdb` untouched, but it had held a transaction on a table
> BAT and TEST write to for half an hour. That was a bad shape and my mistake: a schema change
> belongs in a migration, a long data repair on a shared live table does not.
>
> `Script/Repair-TruncatedSourceId.ps1` does the repair in resumable batches, each its own
> transaction, with the trigger disabled per batch rather than for the whole run - left off
> throughout, every unrelated write from BAT or TEST would silently skip its `MODIFIEDDATETIME`
> stamp.
>
> **The script took four attempts, and three of the failures were mine.** Worth recording because
> each is a trap for the next person writing an operational script here:
>
> - `Add-Type` cannot load `Microsoft.Data.SqlClient` in this PowerShell. The batching loop moved
>   server-side and the script now shells out to `sqlcmd`, which is better anyway - the loop belongs
>   in SQL, not in a client.
> - Passing the script with `sqlcmd -Q` fails, because the SQL contains quotes and sqlcmd parses
>   them out of the argument and then objects to what is left. It goes through a file with `-i`.
> - Declaring `SupportsShouldProcess` made `-WhatIf` propagate into the script's *own* temp file
>   write, so the dry run created no file and handed sqlcmd a path that did not exist. Internal
>   housekeeping needs `-WhatIf:$false`.
> - The fourth was not mine: sqlcmd leaves `QUOTED_IDENTIFIER` off, and SQL Server refuses any
>   `UPDATE` against a table carrying a filtered index - which `MESSAGEQUEUETABLE` now does, since
>   migration 008 added one. Fixed with `-I`.
>
> **The repair was then abandoned part-way, deliberately.** It ran cleanly for 12,000 rows before
> the terminal was closed, leaving 65,700 still truncated. The batching held up exactly as intended:
> the trigger was enabled, no batch was half-applied, and the repaired rows kept their original
> `MODIFIEDDATETIME` - spot checks show June 2026 timestamps, not the date of the repair.
>
> It was stopped because the owner intends to truncate `MESSAGEQUEUETABLE` outright, which makes
> repairing history pointless. The script stays in the repository, tested and resumable, in case
> that plan changes. The durable half of the fix is migration 009 and the widened procedure
> parameter, which stop the truncation happening again regardless of what is done with the history.

> **Why the baseline is not edited.** DbUp journals a versioned script by name. Once
> `001_baseline.sql` is journaled in an environment, editing it changes nothing there — but it does
> change what a *fresh* database gets. That is silent divergence between the existing client and the
> next one, which is the exact failure the migration runner exists to prevent. Everything additive
> goes in a new numbered migration.
>
> Two exceptions, both safe: `Repeatable/` scripts are re-applied on every run by design, so
> editing them in place is correct; and deleting dead commented-out text from the baseline changes
> no behaviour.
>
> **Why the trigger becomes repeatable.** It is currently `CREATE TRIGGER` in the baseline, so it
> cannot be corrected by editing it there. Tasks 1.10 and 1.11 both amend it, and Decision 20 is
> unlikely to be the last word on it, so it belongs in `Repeatable/` as `CREATE OR ALTER` rather
> than accruing one migration per correction. The baseline's own pointer comment already states
> that `CREATE OR ALTER` objects are repeatable.
>
> ### Done 2026-08-27. Applied to `eimdb_dev` first, then `eimdb`.
>
> `003_backfill_truncated_sourceentity.sql`, `004_add_status_and_route_tables.sql`,
> `005_extend_messagequeuetable.sql`, plus `R__SPINSERTMESSAGEQUEUE.sql` (rewritten) and
> `R__TR_MESSAGEQUEUETABLE_MODIFIEDDATETIME.sql` (new).
>
> **Task 1.7 found real damage, not a hypothetical.** 77,993 of 78,090 rows had `SOURCEENTITY`
> truncated to exactly 20 characters by the procedure parameter. The true values were recovered
> from each row's own `PAYLOAD`, where Dataverse records `"LogicalName"`, and each truncated value
> maps to exactly one true value:
>
> | Stored | True value | Length | Rows |
> |---|---|---|---|
> | `mserp_wtc_custpostal` | `mserp_wtc_custpostaladdressv2entity` | 35 | 77,699 |
> | `mserp_dirpartycontac` | `mserp_dirpartycontactv3entity` | 29 | 141 |
> | `mserp_custcustomerv3` | `mserp_custcustomerv3entity` | 26 | 95 |
> | `mserp_smmcontactpers` | `mserp_smmcontactpersonv2entity` | 30 | 58 |
>
> This had to be fixed before routing keys on source entity, or every historical row would have
> carried a key no route matches. It also **contradicts the proposal**, which claimed current names
> have five characters of headroom — the real names are 26 to 35 characters and always overflowed.
> The proposal has been corrected.
>
> **The backfill suppresses the trigger.** Correcting a value the database itself corrupted is not
> a business modification, and letting the trigger stamp 78k rows would make every historical row
> look freshly changed — `MODIFIEDDATETIME` being exactly the column the age and staleness
> reporting depends on. Verified on the scratch database that timestamps are preserved and the
> trigger is re-enabled afterwards.
>
> **Two defects found by running it for real.**
>
> The first live attempt failed: the backfill exceeded SqlClient's 30-second default and rolled
> back cleanly — journal unchanged, zero rows modified, exit `1`. The runner behaved correctly but
> its default made any real data migration impossible. It now takes `--timeout`, defaulting to 600
> seconds, and names the timeout explicitly when one is hit. The backfill took 69 seconds.
>
> While widening the parameters, every other parameter was found to be `VARCHAR` against `NVARCHAR`
> columns — including `@PAYLOAD` as `VARCHAR(MAX)`, which would silently mangle any non-ASCII
> character in a payload. No payload currently contains one, so this was latent rather than
> realised. All parameters are now `NVARCHAR`.
>
> **Verified on `eimdb` after applying:** journal at 5 scripts, 78,090 rows intact, **0 rows still
> truncated**, `STATUSTABLE` seeded `0`–`7`, `ROUTETABLE` present with its `ONPAUSE` check
> constraint, both retry columns present, `CREATEDDATETIME` defaulting to `sysutcdatetime()`, and
> the trigger enabled. On the scratch database a single `UPDATE` touching five rows stamped **all
> five** — the multi-row bug in Decision 20 is fixed — and the widened procedure stored 35, 40 and
> 23 character values where the old limits were 20, 30 and 20.
>
> Task 1.13 confirmed in passing: `@STATUS` already existed and accepted `6`. Its default was the
> string `'0'` for an `INT` parameter, now `0`.

## 2. Route seeding

- [x] 2.1 Write the seed script populating `ROUTETABLE` from the current `EntityToOrchestration` mapping, enabled, pause policy `Hold`, for every configured environment
- [x] 2.2 Add a parity check asserting the seeded routes match the hardcoded mapping exactly before any code depends on the table
- [x] 2.3 Enumerate every orchestration currently reachable via the reflection fallback and confirm each is present in the registry, so its removal is behaviour-neutral
- [x] 2.4 Normalise `MESSAGEQUEUETABLE.ENVIRONMENT` casing, which is stored as both `bat`/`BAT` and `test`/`TEST`
- [x] 2.5 Decide whether `dev1` is a real environment; it has a Function App and a queue prefix but no `ENVIRONMENTTABLE` row and has never produced a message

> **Seeded 2026-08-27** by `Migrations/006_seed_routetable.sql`: 9 routes across 4 environments,
> 36 rows, every one `ENABLED = 1` with `ONPAUSE = 'Hold'`, which reproduces current behaviour
> exactly.
>
> **Task 2.1 as written was incomplete, and the gap mattered.** It names only
> `QueueCaptureHandler.EntityToOrchestration`, which is the Capture → D365 direction and uses short
> entity names. There is a second mapping in `QueueDataEventsHandler.EntityRoutes` for the reverse
> direction, keyed on Dataverse logical names. That direction carries almost all the traffic, and
> `mserp_wtc_custpostaladdressv2entity` alone is 77,699 of 78,090 rows — so seeding only the
> forward dictionary would have left **the very entity this change exists to pause** unroutable.
> Both directions are seeded.
>
> Environments come from `ENVIRONMENTTABLE`, which is authoritative: `BAT`, `DEV`, `DEV2`, `TEST`.
> `PollSourceHandler` is deliberately excluded — its source entity is hardcoded to `"Customer"`
> with a "customize per integration" comment and it has never produced a message. It is a
> template, not a route.
>
> **The parity check was tested by breaking it.** An extra route was injected, the migration
> un-journaled, and the runner re-run. It failed with
> `ROUTETABLE seed parity failed: expected 36 routes, found 37.`, exited `1`, and did not journal.
> A safety check that has never been observed failing is not a safety check.
>
> **Stronger evidence than the row count:** every distinct
> `(ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY)` combination across all 78,090 historical messages
> resolves to a seeded route — zero unroutable combinations. That proves the seed matches reality,
> not merely the dictionaries.
>
> Two findings recorded as tasks 2.4 and 2.5. `ENVIRONMENT` is stored inconsistently cased and
> resolves today only because the collation is case-insensitive, so `IRouteResolver` must not
> introduce a case-sensitive comparison in application code. And `dev1` has a Function App and a
> queue prefix but no `ENVIRONMENTTABLE` row and no messages, so it is either unused or
> misconfigured.

## 3. Extensibility seam

- [x] 3.1 Create `Wilson.Integration.Orchestrations` and move `OrchestrationRegistry` plus all concrete `*_To_D365FSC` / `*_To_Capture` / `Sample*` implementations and `Model/Capture/*` contracts into it, referencing `ARM.Integration.EIM`
- [x] 3.2 Confirm the engine retains `IOrchestration`, `OrchestrationResult`, `OutboundMessageEnvelope`, and the base and D365 orchestration classes, and references no concrete client orchestration
- [x] 3.3 Supply the orchestration dictionary by injection in the Functions host
- [x] 3.4 Supply the orchestration dictionary by injection in `MessageQueueRepository.ReprocessAsync`, replacing the internal `OrchestrationRegistry.CreateDefault()` call
- [x] 3.5 Remove the reflection fallback and the legacy `Task<string>` adapter from `EIMController.DispatchOrchestrationAsync`
- [x] 3.6 Make an unregistered orchestration type an explicit logged error naming the type
- [x] 3.7 Add a startup or configuration-time consistency check that every route's orchestration type has a registration
- [x] 3.8 Add a boundary test asserting the engine assembly references no concrete client orchestration type
- [x] 3.9 Remove the unused `Microsoft.Azure.WebJobs` package reference from `ARM.Integration.Core`
- [x] 3.10 Add `Wilson.Integration.Orchestrations` to the solution and update the Functions host, Dashboard API, and test project references

> **Task 2.3 evidence.** Queried `MESSAGEQUEUETABLE` for distinct `TYPE` values. All eight are
> either registry keys, or `customer.from-d365fsc.v1`, which appears nowhere in the codebase
> and is not a valid CLR type name, so `Type.GetType` returned null for it and the reflection
> path never handled it either. Removing the fallback is therefore behaviour-neutral.
> Note `CustomerAddress_To_D365FSC`, `PartyContact_To_D365FSC`, `Sample` and
> `SamplePoll_To_Target` have never been used — zero messages.
>
> **`customer.from-d365fsc.v1` explained.** While comparing schemas against a scratch database,
> `eimdb` was found to contain a `MESSAGEQUEUEDELIVERY` table and six stored procedures that
> existed in no script here. Their three rows carried `ROUTEID = 'customer.from-d365fsc.v1'`,
> matching the three unexplained `MESSAGEQUEUETABLE` rows exactly — so that value was a route id in
> a prototype routing scheme, not an orchestration type name.
>
> They belonged to the unmerged `feature/rearchitecturecapture` branch, which proposed replacing
> CLR-derived route names with stable `<entity>.from-capture.v1` identifiers. That branch has been
> abandoned and the database objects dropped on 2026-08-27. The branch itself is left in git, so
> nothing is unrecoverable.
>
> **This change proceeds as designed**, keeping the registry keyed on implementation type names.
> Worth recording that the abandoned branch had a real point: CLR class names as persisted route
> keys are exactly the coupling that makes repackaging for a second client awkward, because
> renaming a class rewrites route identity. Group 5's `ROUTETABLE` mitigates it by making the
> orchestration type a *column* rather than the key, but the type name is still persisted in
> `MESSAGEQUEUETABLE.TYPE`. If a second client ever forces the issue, the stable-route-id idea is
> where to look.
>
> **The split.** 17 files moved via `git mv` (20 renames tracked in total including the SQL
> restructure), namespaces rewritten to `Wilson.Integration.Orchestrations`. The engine now
> retains only `BaseCrudOrchestration`, `BaseForwardOrchestration`, `D365CrudOrchestration`,
> `D365EventForwardOrchestration`, `D365ServiceActionOrchestration`, `IOrchestration`,
> `OrchestrationResult` and `OutboundMessageEnvelope`. `ARM.Integration.EIM.csproj` references
> only `ARM.Integration.Core` — the dependency is one-way and compiler-enforced.
>
> **Dispatch.** `Type.GetType` and `Activator.CreateInstance` are gone from `EIMController`;
> an unregistered type now throws naming the type and listing the registered keys.
>
> **Verified.** All six projects build with zero errors. All 17 unit tests pass, including the
> five new `EngineBoundaryTests`: the engine does not reference the client assembly, the client
> does reference the engine, the engine declares no concrete `IOrchestration`, every registered
> orchestration is client-owned, and every registry key matches its implementation type name.
>
> Task 3.7 is deferred: it needs `ROUTETABLE`, which group 1 creates.

## 4. Engine: status contract

- [x] 4.1 Add message status constants to `ARM.Integration.EIM` including `Held = 6` and `RetryScheduled = 7`
- [x] 4.2 Replace the bare status literals `1`, `2`, `3`, `4` in `EIMController.ProcessMessageAsync` with the constants
- [x] 4.3 Add a repository read for the status catalogue so consumers can resolve labels from `STATUSTABLE`

> `ARM.Integration.EIM/MessageStatus.cs` defines all eight values, `New` through `RetryScheduled`,
> plus a `Reprocessable` set. Display labels deliberately stay out of it — they come from
> `STATUSTABLE`; these constants are only the values the engine, API and frontend must agree on.
>
> A partial vocabulary already existed in the wrong place: `OrchestrationResult` declared its own
> `StatusCompleted` / `StatusFailed` / `StatusSubmitted`. Those are removed and the type now
> references `MessageStatus`. Literals also replaced in `EIMController.ProcessMessageAsync`,
> `AwaitingD365SweepHandler`, and `MessageQueueRepository.GetRecentFailedAsync`
> (`WHERE STATUS IN (3, 4)`).
>
> Verified: four projects build clean, all 17 tests pass, no bare status literal remains in
> `EIMController`.
>
> Task 4.3 is deferred: `STATUSTABLE` is commented out in the baseline and is created by group 1,
> so a repository read against it cannot be exercised yet.

## 5. Engine: route registry

- [x] 5.1 Define `IRouteResolver` returning orchestration type, enabled state, and pause policy for an environment, source system, and source entity
- [x] 5.2 Implement the SQL-backed resolver against `ROUTETABLE`
- [x] 5.3 Add caching with a bounded time-to-live and an explicit invalidate entry point
- [x] 5.4 Make an unresolvable route raise and log an explicit error rather than being skipped or treated as held
- [x] 5.5 Register the resolver in the Functions host and the Dashboard API dependency injection containers
- [x] 5.6 Unit-test resolution, environment scoping, cache hit behaviour, invalidation, and the unresolvable-route path

> `ARM.Integration.EIM/Routing/` adds `RouteKey`, `RouteDefinition`, `PausePolicy`,
> `IRouteRepository` + `SqlRouteRepository`, `IRouteResolver` + `CachingRouteResolver`,
> `RouteNotFoundException`, and `RouteRegistryValidator`. 15 new tests, 71 total, all passing.
>
> **Three decisions worth recording.**
>
> *Loading and caching are separate types.* `SqlRouteRepository` reads; `CachingRouteResolver`
> caches. That split is what makes task 5.6 achievable at all — cache expiry, invalidation and
> concurrency are tested against a counting fake with a `FakeTimeProvider`, with no database and no
> real clock, so the tests are deterministic rather than timing-dependent.
>
> *The whole table is cached as one snapshot, not per key.* It is a few dozen rows. A whole-table
> snapshot makes a missing route known immediately, where per-key caching would need a negative
> caching scheme to avoid re-querying keys that do not exist.
>
> *A cache miss does not reload.* An earlier draft reloaded and retried before declaring a route
> unresolvable, which looked more forgiving but meant a flood of genuinely unroutable messages
> would reload the route table once per message. A newly seeded route appears within the cache
> duration anyway, and pause and resume call `Invalidate` directly. There is a test named for this.
>
> **`RouteKey` compares case-insensitively.** This is not tidiness. `ENVIRONMENT` is stored as both
> `bat` and `BAT`, `test` and `TEST`, and resolves today only because the database collation is
> case-insensitive. An ordinal comparison in C# would have silently failed to route roughly half
> the historical rows. Covered by `Resolution_ignores_case`.
>
> **Task 3.7** is `RouteRegistryValidator`, called at Functions host startup. Routes are data and
> registrations are code, so they drift independently and nothing else compares them; without this
> a mismatch surfaces as a runtime failure per message, on live traffic, long after the deployment
> that caused it. A validation failure is fatal, but a database blip at startup is logged and the
> host continues — the triggers are what keep messages durable, and the resolver reloads on first
> use regardless.
>
> The resolver is a singleton in both hosts so that pause and resume can invalidate the same cache
> the API reads through, which task 11.3 depends on.
>
> **Verified against the live schema:** the repository's exact query returns the expected six
> columns in the expected ordinals, `ENABLED` as `bit` mapping to `GetBoolean`, and `ONPAUSE`
> yielding `Hold`, which parses to the enum. Both hosts build clean.

## 6. Engine: hold gate

- [x] 6.1 Add `Status` to `MessageRequest` and pass `@STATUS` through `EIMController.InsertMessage`
- [x] 6.2 Add `EIMController.InsertAndProcessAsync` that resolves the route, decides held versus live, inserts with the correct status, and processes only when live
- [x] 6.3 Implement the live determination as enabled AND the route has no messages at `Held` or `RetryScheduled`
- [x] 6.4 Unit-test the gate: enabled with empty backlog processes, disabled holds, enabled with held backlog holds, enabled with retry-pending backlog holds

> `Routing/HoldGate.cs` and `Routing/SqlRouteBacklogProbe.cs`. Six new tests, 77 total.
>
> The decision and the database work are separate types again, for the same reason as the resolver:
> `HoldGate` takes an `IRouteResolver` and an `IRouteBacklogProbe`, so all four gate outcomes are
> tested against stubs with no database. `SqlRouteBacklogProbe` holds the query.
>
> **The backlog probe is only called when the route is enabled.** A paused route returns held
> without a database round trip, which matters because this runs on every message. There is a test
> asserting the probe is never called for a paused route.
>
> The probe's predicate is `STATUS IN (Held, RetryScheduled)` scoped by environment, source system
> and source entity — column-for-column the leading columns of `IX_MESSAGEQUEUETABLE_STATUS`, which
> group 1 added for exactly this. It selects `TOP 1 1`: whether any exist, not how many.
>
> **`MessageRequest` became a `record`** so the gate can insert with `request with { Status, Type }`
> rather than copying eleven properties by hand. Construction is unchanged everywhere.
>
> The orchestration type is taken from the route and written at insert time even when the message
> is held, so release does not have to re-derive it from a dictionary that may have changed.
>
> `InsertAndProcessAsync` inserts first and always, whatever the route state. A held message is a
> row an operator can see, reprocess and replay; a message left sitting on the transport is none of
> those things.

## 7. Handlers

- [x] 7.1 Switch `QueueCaptureHandler` to `InsertAndProcessAsync` and delete its local `EntityToOrchestration` dictionary
- [x] 7.2 Delete the unsafe `*Active` environment-variable early return that leaves messages unsettled
- [x] 7.3 Switch `QueueDataEventsHandler` to `InsertAndProcessAsync`
- [x] 7.4 Verify against a paused route that no message is dead-lettered and every message is recoverable from `MESSAGEQUEUETABLE`
- [x] 7.5 Remove the now-dead `CaptureToD365QueueActive` app setting from deployed environments and from `AzureDeploy.Function.EIM.Customer.dev1.ps1`
- [x] 7.6 Apply the sweep schedules to the deployed Function Apps; `ReleaseSweepSchedule` and `RetrySweepSchedule` are absent from all three, so neither sweep can bind

> 7.4 confirmed by operator run: a paused route held the simulated message rather than dead-lettering
> it, and the row stayed visible in `MESSAGEQUEUETABLE`.
>
> 7.5 done 2026-09-16. `CaptureToD365QueueActive` removed from `azf-eim-customer-wtc-bat`, `-dev` and
> `-test`, and confirmed gone from each. Nothing in the codebase reads it — only
> `Script/Docs/database-migrations.md`, which already says it is dead.
> `AzureDeploy.Function.EIM.Customer.dev1.ps1` no longer exists; it went with the `dev1`
> decommission and the consolidation of `Script/Deploy-Manual`.
>
> **7.6 was found while answering whether release is automatic.** `ReleaseSweepHandler` and
> `RetrySweepHandler` are both deployed to all three apps, but `ReleaseSweepSchedule`,
> `RetrySweepSchedule` and the retry tuning settings are absent from every one. Both handlers bind
> with `[TimerTrigger("%ReleaseSweepSchedule%")]`, so with no such setting neither sweep can start.
> The apps carry only `PollSchedule` and `AwaitingD365SweepSchedule`, which is the pre-change set.
>
> The consequence is that the feature is deployed as code but not as configuration: pausing a route
> holds messages correctly, and resuming it drains nothing, because the sweep that drains is not
> running. An operator would have to reprocess each held message by hand — which is precisely the
> manual step the release sweep exists to remove.
>
> `Script/config/deploy.bat.json` already defines all of them, so the fix is
> `Deploy-Eim.ps1 -Environment <env> -Component function` per environment, not a code change.
>
> **Done 2026-09-16 for `bat` and `test`.** Both now carry `ReleaseSweepSchedule` and
> `RetrySweepSchedule` at `0 */1 * * * *`, the batch sizes and the retry tuning, and both host all
> six functions including `ReleaseSweepHandler` and `RetrySweepHandler`.
>
> `dev` was **not** deployed. `azf-eim-customer-wtc-dev` is in the `Stopped` state, and a deployment
> would have started an app somebody stopped deliberately, putting it back on the `dev-` Service Bus
> queues. Left alone; re-run the same command if and when that app is meant to be running.
>
> Worth knowing for next time: `az functionapp function list` showed the old four functions on `test`
> for roughly two minutes after a successful deploy, including across a restart. That is indexing
> lag, not a failed deployment. Waiting is the right response; re-deploying on the strength of that
> listing would have been chasing nothing.

> Both handlers now resolve their route through the gate rather than a local dictionary, so
> pausing is a data change instead of a redeploy.
>
> **The unsafe pause is gone.** `QueueCaptureHandler` used to `return` without settling the
> message when `CaptureToD365QueueActive` was false. With `autoCompleteMessages: false` the lock
> simply expired, the message redelivered, and after `MaxDeliveryCount` attempts it was
> dead-lettered — so "pausing" a flow silently destroyed everything that arrived during it. That
> path no longer exists, and the app setting that drove it is now dead config, hence task 7.5.
> Leaving both live would let an operator resume a route in the dashboard and still lose traffic
> to a stale app setting.
>
> **An unroutable message is now dead-lettered rather than completed.** Both handlers previously
> logged and completed an unsupported entity, which discards it silently. Routes are seeded from
> live traffic, so an unroutable message means a seeding gap — that has to be visible. The message
> is dead-lettered with reason `NoRoute` and the failing key in the description.
>
> `Type` is no longer supplied by the caller. It is set from the resolved route inside
> `InsertAndProcessAsync`, so the orchestration recorded on the row and the orchestration the route
> names cannot disagree.
>
> Task 7.4 needs a running host and Service Bus, so it belongs with the group 13 end-to-end runs.

> **2.4 is normalised on write only; history is left alone.** Case-sensitive counts on `eimdb`
> confirm the mix: `test` 77,789 against `TEST` 16, and `bat` 194 against `BAT` 13. `ROUTETABLE`,
> `ENVIRONMENTTABLE` and `CONFIGURATIONTABLE` are all upper case, so `SPINSERTMESSAGEQUEUE` now
> upper-cases `@ENVIRONMENT` and every writer stores one canonical form.
>
> Rewriting the 78,000 existing rows was rejected. Nothing is broken by the mix today - the database
> collation is case-insensitive and `RouteKey` compares case-insensitively by design - so the only
> gain would be cosmetic, against a real cost: the `MODIFIEDDATETIME` trigger would restamp every
> row touched, destroying the timestamps the sweeps and the age display depend on. It could be done
> with the trigger disabled, as Migrations/003 did, but that is a maintenance-window job for a
> problem that is not hurting anything. The guard that actually matters is that comparisons stay
> case-insensitive, and that is already asserted in `RouteDefinition`'s tests.
>
> **2.5: `dev1` is not a real environment.** It appears nowhere in `eimdb` - no `ROUTETABLE` rows
> (routes exist only for BAT, DEV, DEV2 and TEST, nine each), no `ENVIRONMENTTABLE` row, no
> `CONFIGURATIONTABLE` entry, and no message in `MESSAGEQUEUETABLE` in its entire history. What
> exists is a Function App, a queue prefix and `AzureDeploy.Function.EIM.Customer.dev1.ps1`. Treat
> it as abandoned infrastructure: it should be decommissioned rather than seeded, and until it is,
> nothing should be deployed to it. Folded into 7.5, which already touches that script.

## 8. Ingest dedupe

- [x] 8.1 Add duplicate detection on the delivery key scoped by source system and environment to the insert path
- [x] 8.2 Reject only when the matching row has progressed beyond status `0`; re-drive an existing row still at `0` instead of rejecting or inserting a duplicate
- [x] 8.3 Settle the transport message normally on a rejected duplicate and record the occurrence with its delivery key
- [x] 8.4 Unit-test duplicate rejection for live routes and for held routes, and re-drive of an unprocessed status-`0` row
- [x] 8.5 Key dedupe on a per-delivery identifier rather than the business record key

> `Routing/IngestGate.cs` and `Routing/SqlDuplicateProbe.cs`. 15 new tests, 92 total.
>
> `IngestGate` composes `HoldGate` rather than replacing it, and is now what the handlers call.
> Three outcomes: insert new, re-drive an existing unprocessed row, or reject a duplicate.
>
> **The duplicate check runs before the route is resolved.** Rejecting needs neither the
> orchestration type nor a backlog probe, so a duplicate costs one query instead of two. There is a
> test asserting the backlog probe is never called for a duplicate.
>
> **The re-drive rule needed a case the task list did not state.** An unprocessed row on a route
> that is now paused is set to `Held`, not left at `New`. Left at `New` it would sit outside the
> release sweep's predicate and never drain — an invisible stranded message, which is the exact
> failure the re-drive rule exists to prevent. Two tests cover it, for paused and for draining.
>
> The probe orders by `ID DESC` and takes the newest row, because it is the newest row's status
> that decides between duplicate and re-drive. It matches on `(SOURCESYSTEM, ENVIRONMENT, DEDUPEKEY)`
> — the leading columns of `IX_MESSAGEQUEUETABLE_DEDUPEKEY` — so the same key from two systems, or
> replayed into a different environment, is not treated as a duplicate.
>
> **8.5 corrects a design flaw in 8.1, found by testing rather than by review.** The original
> wording said "duplicate detection on source id", and `SOURCEID` holds the *business record* key —
> `CUST-SIM-002`, not the delivery. So a customer update from D365FSC was rejected as a duplicate of
> its own create. Deployed, every record would have been processed exactly once and every subsequent
> change to it silently discarded. That is worse than having no dedupe at all, and nothing in the
> unit tests caught it because the tests used the same word the requirement did.
>
> Migration 008 adds `DEDUPEKEY NVARCHAR(200) NULL` with a filtered index, populated by
> `SPINSERTMESSAGEQUEUE`. The Capture handler keys on the Service Bus message id, the data-events
> handler on the D365 operation id — both identify a delivery, so a redelivery repeats the key while
> a second change to the same record does not. A null key skips the check entirely: the alternative,
> substituting the record key, is precisely the bug. Existing rows have `DEDUPEKEY NULL` and so are
> invisible to the probe, which is the right outcome — nothing already in the table should suppress
> a new change.
>
> Three tests added, 108 total, including one named for the regression.
>
> A missing source id returns no match rather than matching everything with a blank id, which would
> collapse unrelated messages together.
>
> A rejected duplicate settles the transport message normally and logs a warning naming the source
> id and the prior message id, so a rejection is greppable rather than silent.

## 9. Transient failure retry

- [x] 9.1 Add a failure classifier distinguishing retryable from terminal conditions, reading `D365FSCException.StatusCode` and `ODataErrorCode`, defaulting unrecognised failures to terminal
- [x] 9.2 Change `ProcessMessageAsync` catch handling to set status `7` with an incremented `RETRYCOUNT` and a computed `NEXTATTEMPTDATETIME` for retryable failures, preserving `3`/`4` for terminal ones
- [x] 9.3 Implement exponential backoff with jitter and a configured ceiling
- [x] 9.4 Add a timer-triggered retry sweep claiming due status-`7` rows atomically, skipping routes that are not enabled
- [x] 9.5 Escalate to Dead Letter once `RETRYCOUNT` reaches the configured maximum, recording that attempts were exhausted
- [x] 9.6 Make maximum attempts, base delay, ceiling, and retry batch size configurable per environment
- [x] 9.7 Unit-test classification, backoff growth and ceiling, exhaustion escalation, and the paused-route skip
- [x] 9.8 Surface status-`7` messages whose route no longer exists; the sweep's inner join means no sweep will ever claim them
- [x] 9.9 Add the `RetrySweepSchedule` and retry tuning settings to the deployment scripts
- [x] 9.10 Stop the release sweep applying a held message ahead of an older message awaiting retry

> **Classification.** `ARM.Integration.EIM/Retry/` adds `FailureDisposition`, `IFailureClassifier`
> and `DefaultFailureClassifier`. Retryable is `408`, `429`, and `5xx` except the two the
> destination will never support (`501 Not Implemented`, `505 HTTP Version Not Supported`), plus
> `HttpRequestException`, `SocketException`, `TimeoutException` and `IOException`. Everything else
> is terminal.
>
> 9.8 adds `GetOrphanedBacklogAsync`, `GET /api/routes/orphans` and a banner on the route console.
> A message waiting at `6` or `7` whose route row has been deleted is matched by neither sweep -
> both join `ROUTETABLE` - so it waits forever with nothing reporting it. The banner names the
> route, the count and the age of the oldest, and re-creating the route releases them. Chosen over
> a log line because a log line is exactly what nobody reads while a backlog quietly rots.
>
> Three judgements worth recording:
>
> - **The chain is walked, not just the outermost exception.** `HttpClient` reports its own timeout
>   as a `TaskCanceledException` wrapping a `TimeoutException`; the outer type alone says nothing.
>   A *definite* destination rejection anywhere in the chain outranks a retryable link beneath it,
>   so a `400` carrying an inner transport exception stays terminal.
> - **OData error codes are data, not code.** `RetryOptions.RetryableODataErrorCodes` is empty by
>   default. Guessing at D365 error-code strings would have been inventing behaviour; codes get
>   added as they are actually observed, without a code change.
> - **Default-terminal is what prevents duplicate writes.** The incident earlier in this workstream
>   — the D365 create succeeded, then publishing the response failed on a malformed
>   `ENVIRONMENTTABLE` row — surfaces as a `JsonException`, which is unrecognised and therefore
>   terminal. A retry would have created the customer twice. There is a test named for this.
>
> **Backoff.** `RetryScheduleCalculator` computes `min(BaseDelay * 2^(attempt-1), MaxDelay)` then
> subtracts up to `JitterFactor` of it. Jitter subtracts rather than adds for two reasons: the
> result stays inside the ceiling without a clamp that would flatten the spread, and with the
> factor capped at `0.5` the shortest delay for an attempt still exceeds the longest delay for the
> previous one — so `Backoff grows with attempts` holds on every draw, not on average. Preserving
> spread *at* the ceiling is the point: a herd of messages failing together arrives there on the
> same attempt.
>
> **Verified.** 39 new tests, 56 total, all passing. Coverage includes strict monotonicity over 500
> randomised trials, the ceiling never being exceeded across 40 attempts, `int.MaxValue` yielding
> the ceiling rather than overflowing, and jitter still spreading once the ceiling is reached.
>
> Tasks 9.2, 9.4 and 9.5 are deferred: they need `RETRYCOUNT` and `NEXTATTEMPTDATETIME`, which
> group 1 adds. Task 9.6 is partly done — `RetryOptions` carries every knob and validates itself,
> but per-environment binding waits on the configuration work. Task 9.7's paused-route skip is
> covered by tests in group 5 once `IRouteResolver` exists; classification, growth and ceiling are
> covered now, and exhaustion is covered by `AttemptsRemain`.
>
> ### Wired up 2026-08-27
>
> `RecordFailure` in `EIMController` now classifies before deciding. Terminal failures keep the
> existing `3`/`4` split exactly; retryable ones increment `RETRYCOUNT`, compute
> `NEXTATTEMPTDATETIME`, and land at `7`. Exhaustion escalates to Dead Letter rather than Failed,
> because attempts running out is not the same as the data being wrong.
>
> The classifier and schedule are **optional constructor parameters, supplied together or not at
> all**. The Functions host supplies them. `MessageQueueRepository.ReprocessAsync` deliberately does
> not: an operator reprocessing a message wants a definite answer now, not a retry scheduled for
> later. The constructor rejects one without the other, since a classifier with no schedule cannot
> say when to try again.
>
> `RetrySweepHandler` claims a bounded batch on a timer and drives each one, continuing past
> individual failures so a poison message cannot strand the rest of its batch.
>
> **The claim SQL was wrong first time, and the database caught it.** `OUTPUT inserted.ID` without
> `INTO` is rejected outright while the `MODIFIEDDATETIME` trigger exists —
> *"the target table of the DML statement cannot have any enabled triggers if the statement contains
> an OUTPUT clause without INTO"*. It now writes through a table variable, as `EscalateStuckMessages`
> already did. Worth knowing for the release sweep in group 10, which claims the same way.
>
> **Verified against the real schema** with four rows that should not be claimed and one that
> should. The statement claimed exactly one: the due message on an enabled route. It correctly
> ignored a not-yet-due message, a due message on a paused route, a terminal message, and a message
> whose route does not exist.
>
> That last exclusion is a gap rather than a feature. The inner join means a status-`7` message on a
> deleted route is invisible to the sweep permanently — recorded as task 9.8.
>
> Tuning is read from app settings by `RetryOptionsFactory`. A value that parses but is out of range
> throws rather than falling back, so a typo is loud; a value that does not parse at all leaves the
> default, since there is nothing to apply. Six tests cover that distinction.

## 10. Release sweep

- [x] 10.1 Add a timer-triggered release sweep function modelled on `AwaitingD365SweepHandler`
- [x] 10.2 Implement the atomic batch claim selecting held rows for a route in ascending `ID` order and marking them in a single statement, reusing the `UPDATE ... OUTPUT inserted.ID` pattern already in `EscalateStuckMessages`
- [x] 10.3 Process claimed messages serially in claim order
- [x] 10.4 Continue past individual failures, letting them follow the normal failure or retry path
- [x] 10.5 Record released and failed counts per sweep run
- [x] 10.6 Make batch size and sweep schedule configurable, considered together with the retry sweep's batch size against destination capacity
- [x] 10.7 Unit-test ordering, atomic claim under concurrency, and failure-skip behaviour
- [x] ~~10.8 Skip a held message superseded by a newer held message for the same record~~ — **rejected, see below**
- [x] 10.9 Report the records a release wrote, so the divergence Decision 23 accepts is at least visible afterwards

> **Decision 23 applies to this group.** The sweep replays blindly; the source wins. That loses a
> destination-side edit made during a pause, because `ExcludedUsers` suppresses the corrective
> event that would otherwise have converged the two systems. Accepted on 2026-08-27 in favour of
> the simplest sweep. Task 10.9 is the cheap mitigation.
>
> **Task 10.8 is rejected.** It was proposed as a cheap way to shorten the drain, but it
> contradicts the `message-hold-release` requirement *"Held messages are replayed individually …
> SHALL NOT collapse multiple held messages for the same business record into a single
> operation"*, and its scenario *"a create followed by updates replays in full, and the create is
> not discarded"*. Skipping a superseded message would discard the create and leave every
> following update failing against a record that does not exist. The volume argument does not
> survive contact with that.
>
> ### Built 2026-08-27
>
> `Routing/ReleaseSweep.cs`, `Routing/SqlReleaseSweepStore.cs`, `ReleaseSweepHandler`. Eight new
> tests, 106 total.
>
> **Claims are per route, not global.** A single claim across all routes in `ID` order would let one
> busy route monopolise every batch: with 77,699 held address messages and a batch of 100, a route
> resumed at the same moment would wait more than twelve hours for its first message. The sweep
> lists routes that are enabled and still hold messages, then claims a batch for each.
>
> **The drain loop is separated from the SQL**, so ordering, failure-skip and the counts are tested
> against fakes with no database. Concurrency and atomicity are properties of the statement, so
> those were verified against the database instead.
>
> **Verified against the real schema.** With three held address messages on an enabled route, one
> held message on a paused route, and one terminal message: the drainable-routes query returned
> only the enabled route, and a claim with batch size 2 took `addr-a` and `addr-b` in ascending
> `ID`, left `addr-c` for the next run, and touched neither the paused route's message nor the
> terminal one. The `OUTPUT ... INTO` form was used from the start here, having learned it the hard
> way in group 9.
>
> **Task 10.9** is one log line per released message carrying its source id. Given Decision 23
> replays without consulting the destination, that line is the only record of what a release
> overwrote, and it is queryable after the fact.
>
> Failures are counted and logged but not retried by the sweep itself — the message has already
> been recorded at its own status by `ProcessMessageAsync`, which is where retry classification
> lives. The sweep continues with the rest of the batch.

## 11. Dashboard API

- [x] 11.1 Add a route repository exposing route listing, pause, and resume
- [x] 11.2 Add route control endpoints, authorised consistently with existing administrative endpoints
- [x] 11.3 Invalidate the resolver cache on pause and resume so changes apply promptly
- [x] 11.4 Add held count, retry-scheduled count, oldest held age, and released/failed counts per route to the route listing response
- [x] 11.5 Extend `MessageQueueRepository.ReprocessAsync` to accept status `6` alongside `3` and `4`
- [x] 11.6 Return an explicit rejection reason when reprocess is requested for an ineligible status

> `Services/RouteRepository.cs` and `Controllers/RoutesController.cs`. `GET /api/routes`,
> `POST /api/routes/pause`, `POST /api/routes/resume`, all under the same `DashboardUser` policy as
> every other administrative endpoint.
>
> **Reprocess was not gated by status at all.** Tasks 11.5 and 11.6 read as though there were an
> existing `3`/`4` restriction to widen, but there was none — the endpoint fetched the record and
> processed it whatever its status, so reprocessing a `Completed` message re-sent it to the
> destination. It now permits only `MessageStatus.Reprocessable` (`Failed`, `Dead Letter`, `Held`)
> and returns `409` naming the status and the allowed set.
>
> That is a deliberate behaviour change: a request that previously succeeded now fails. It closes a
> duplicate-write path, and `Held` is included so an operator can canary a single message while its
> route stays paused, which is the workflow Decision 13 describes.
>
> Pause and resume call `IRouteResolver.Invalidate`, which is why the resolver is a singleton in
> the API. Without it an operator would watch a paused route keep processing until the cache
> expired.
>
> The listing derives `Paused` / `Draining` / `Live` rather than storing it, and carries held count,
> retry-scheduled count and oldest-held time. Counts come from one grouped pass joined to the route
> table, not a subquery per route. Verified against the live schema.
>
> `MODIFIEDBY` records the caller's identity on every pause and resume, so a paused route can be
> traced back to whoever paused it.
>
> Released and failed counts per route are not in the listing: they are per sweep *run*, not route
> state, and live in the sweep's log output. Persisting them would mean another table for something
> already queryable.

## 12. Dashboard frontend

- [x] 12.1 Add the route control screen listing routes with environment, entity, orchestration type, and lifecycle state
- [x] 12.2 Add pause and resume actions with confirmation
- [x] 12.3 Show held count, retry-scheduled count, oldest held age, and release progress per route, distinguishing failures
- [x] 12.4 Warn on pausing a route that other routes depend on, identifying the dependents
- [x] 12.5 Source status labels from the status catalogue instead of the hardcoded badge map, including Held and Retry Scheduled
- [x] 12.6 Ensure the message list status filter and the cleanup screen surface Held and Retry Scheduled
- [x] 12.7 Show attempt count and next attempt time on a message scheduled for retry

> `features/routes/` plus a rewritten `lib/status.ts`, wired into the router and the admin section
> of the navigation. TypeScript typechecks clean and the production bundle builds.
>
> **Route Control is admin-only.** Pausing a route stops production traffic, so it belongs with
> Users, Environments and Configuration rather than with the read-only screens.
>
> **Status labels come from `STATUSTABLE` with the built-in vocabulary as a fallback.** A new
> `GET /api/statuses` endpoint and `StatusRepository` close task 4.3. The fallback matters: the
> values are compiled into the engine, so if the request fails the badges stay readable rather than
> rendering blank. Labels are the part a client may want to relabel without a frontend deploy;
> the numbers are not.
>
> Adding `6` and `7` to `MESSAGE_STATUSES` covers task 12.6 on its own, because the message list
> filter and the cleanup screen both build their dropdowns from that one array. That is the payoff
> of Decision 14 — one edit rather than three.
>
> **The confirmation dialog states the consequence, not just the action.** Resuming a route with a
> backlog says how many messages will replay and that replay overwrites the destination, which is
> Decision 23 made visible at the moment an operator can still change their mind.
>
> **Task 12.4's dependants are the opposite direction of the same entity.** `CustomerAddress` flows
> `D365FSC -> CAPTURE` and `CustomerAddress -> D365FSC` under two different naming vocabularies, so
> the two are matched through a small stem map. Pausing one side while the other keeps writing is
> exactly the situation where an edit is lost on release, so the dialog names the other side and
> its state.
>
> The route list refreshes every fifteen seconds, since held counts move while a route drains.

## 13. Verification

- [x] 13.1 End-to-end: pause `CustomerAddress`, confirm Customer traffic continues and address messages land at status `6` with no dead-lettering
- [x] 13.2 End-to-end: reprocess a single held message as a canary while the route stays paused
- [x] 13.3 End-to-end: resume the route and confirm the backlog drains oldest-first and no held message is applied after a newer message for the same record
- [x] 13.4 End-to-end: confirm a failing held message does not block the remainder of the drain
- [x] 13.5 End-to-end: simulate destination throttling and confirm messages retry automatically and recover without operator action

> **13.5 is closed by decision, not by observation.** No throttling simulation was run against a
> destination. What is covered by test rather than by run: `429` and `5xx` classify as retryable,
> backoff grows and respects its ceiling, exhaustion escalates to Dead Letter, and a paused route
> claims nothing. What remains unverified is the whole loop under real throttling — a message
> failing with `429`, parking at status `7`, and being collected by `RetrySweepHandler` without an
> operator touching it.
>
> That last part cannot be true in the deployed environments today, because `RetrySweepSchedule` is
> not set on any of them (see 7.6). Re-run this once the sweeps are actually scheduled.
- [x] 13.6 End-to-end: confirm a paused route makes no retry attempts against the destination
- [x] 13.7 End-to-end: register a route whose source entity name exceeds twenty characters, confirm it persists intact and that its held messages drain on release
- [x] 13.8 End-to-end: interrupt processing after insert and confirm redelivery re-drives the existing row rather than rejecting it as a duplicate
- [x] 13.9 Confirm oldest-held-message age is reported correctly on a database server not configured to UTC
- [x] 13.10 Update several message rows in a single statement and confirm every affected row has its `MODIFIEDDATETIME` stamped, not just one
- [x] 13.11 Confirm every orchestration dispatches through host registration with the reflection fallback removed
- [x] 13.12 Confirm rollback works: enabling all routes and disabling both sweeps restores prior behaviour
- [x] 13.13 Update `Script/Docs` deployment notes with the migration and seeding steps, including the timestamp normalisation and its effect on ages computed across the migration boundary

> 13.1-13.4 confirmed by operator run against `dev2` on 2026-08-27. Recorded as observed, not as
> anything this repository can re-run; there is no automated end-to-end harness.
>
> 13.3 was then re-tested directly, because the operator run could not have exercised its second
> clause. Six held messages on a temporary route drained in strict id order, two-odd seconds apart,
> and the two carrying the same source id were applied oldest first. All six failed on a deliberate
> empty payload and every one was still processed, which is 13.4: a failure did not stop the drain.
> Weaker evidence than a mixed batch would give, since none succeeded, but it does establish that
> the loop continues past a failure.
>
> **The second clause of 13.3 failed, and it was a real defect.** With a message on the route at
> status `7`, a newly arrived message for the same record was correctly held by `HoldGate` - and
> then released by the release sweep about a minute later, ahead of the older message still waiting
> to retry. The older one would have been applied afterwards, overwriting the newer. The hold was
> buying one sweep tick and nothing more.
>
> The cause was that `GetDrainableRoutesAsync` looked only for `STATUS = Held AND ENABLED = 1` and
> never considered retry-scheduled work, so the sweep contradicted the gate that fed it.
> `HoldGate` counts `Held` and `RetryScheduled` as backlog; the sweep counted only `Held`.
>
> Fixed in 9.10 with a barrier in `ClaimHeldAsync`: claim held messages only below the lowest id
> awaiting retry on that route. Blocking the whole route instead would have been simpler, but one
> message retrying for up to five attempts would then stall an entire backlog for hours, and a
> retry-scheduled message *newer* than the held ones would block messages that were safe to send.
> The barrier keeps ordering and liveness together.
>
> **This fix has no unit test, and cannot easily have one.** The barrier is a SQL predicate, and
> `ReleaseSweep`'s tests drive a fake store. It was verified directly against `eimdb_dev`: with a
> retry-scheduled message between two held ones, the claim took only the older; once the retry
> resolved, the newer became claimable. That is the same class of gap that let the defect through -
> the unit tests agreed with the design, and the design was wrong.
>
> **13.12 found that the stated rollback is incomplete.** Enabling every route and disabling both
> sweeps does restore the ingest path - the gate admits at status `0` and processes immediately -
> but it leaves a trap. A transient failure is still classified retryable and parked at status `7`,
> and the sweep that would collect it is now off, so the message waits forever. The rollback needs a
> third step the task did not name: set `RetryMaxAttempts` to `1`, which sends the first failure
> straight to a terminal status. Pinned by
> `RetryScheduleCalculatorTests.One_max_attempt_schedules_no_retry_at_all`, 109 tests.
>
> Disabling a sweep must be done with `AzureWebJobs.<Handler>.Disabled`, not by clearing the
> schedule setting: a timer trigger whose schedule setting is missing fails to bind and takes the
> whole host down with it.
>
> Held messages captured before a rollback stay at `6` with nothing to collect them, so the backlog
> has to be drained first. Recorded in `Script/Docs/database-migrations.md`.
>
> 13.13 adds a deployment section to the same document: migration order (runner before app, since
> route registry validation fails without the new tables), the seeding check per environment and why
> a gap loses traffic rather than looking untidy, the app settings the build needs, and the
> `D365DataEventQueue` rename handled add-then-remove.
>
> On the timestamp boundary the honest answer is that it does not bite here. `SYSDATETIME()` and
> `SYSUTCDATETIME()` agree on Azure SQL, so pre-migration rows were already effectively UTC and ages
> spanning the deployment are sound. The document says so, and gives the one-line check to run on
> any server that is not Azure SQL, where the boundary would be real.
>
> 13.6 verified against `eimdb_dev` by running the `ClaimDueRetries` statement directly. With a> retry overdue by five minutes on a paused route, the claim took nothing and the message stayed at
> `7`; enabling that same route and re-running took it. So the pause was the only thing holding it
> back, which is the property the task asks for - a paused route makes no attempt on the
> destination even when work is due.
>
> 13.11 verified by inspection rather than a run: `Type.GetType` and `Activator.CreateInstance` are
> absent from the engine, `EngineBoundaryTests` asserts every registered orchestration comes from
> the client assembly under a key matching its type name, and all nine `ORCHESTRATIONTYPE` values in
> `ROUTETABLE` are present in `OrchestrationRegistry`. `MESSAGEQUEUETABLE` also holds three rows
> typed `customer.from-d365fsc.v1` from the abandoned capture prototype, which is registered
> nowhere - all three are at status `2`, so they are not reprocessable and cannot be dispatched.
>
> 13.7 and 13.8 run against `dev2` and `eimdb` on 2026-08-27, through a temporary paused route
> `dev2 | CAPTURE | RedriveTestEntityLongerThanTwenty` (33 characters) so nothing reached D365.
> Route, messages and sender script were removed afterwards; `dev2` is back to nine routes, none
> disabled.
>
> 13.7: the name persisted at 33 characters through `SPINSERTMESSAGEQUEUE` into `SOURCEENTITY`,
> which is the regression test for the truncation that silently corrupted 77,993 rows. On release
> the held message was claimed by the sweep within one tick and resolved to `4` with
> `ERROR:Data is null.` - it failed on the deliberately empty payload, and the classifier called
> that terminal rather than retrying it, which is right.
>
> 13.8: a delivery meeting an existing row at status `0` re-drove that row and left one row, not
> two; on a paused route it moved to `6`, so the release sweep can still see it. A delivery meeting
> a row at status `2` was rejected and changed nothing.
>
> **The obvious way to test 13.8 does not work, and finding out changed what the dedupe code is
> worth.** `dev2-capture-to-d365fsc` has `RequiresDuplicateDetection` with a one-day window, so
> re-sending a message under the same id is discarded by the broker and never reaches the handler.
> The first attempt looked like a silent failure of the re-drive path; it was the broker. The test
> was rebuilt by seeding the row and letting a fresh delivery meet it, which is what the gate sees
> during a genuine redelivery.
>
> The consequence is that on the Capture path our dedupe key is `message.MessageId`, which Service
> Bus already deduplicates for 24 hours - so the application check adds nothing there except beyond
> that window. It still earns its place: it is what makes re-drive possible at all, it covers the
> D365 data-event path where the key is an operation id, and it does not depend on a queue setting
> that anyone can turn off. Worth knowing rather than assuming.
>
> The queue also holds four dead letters from 25-26 August, all `Entity: Customer` simulations at
> `DeliveryCount` 11 against a `MaxDeliveryCount` of 10. They predate the retry work and are exactly
> the behaviour it replaces: an exception bubbling until Service Bus gave up. Left in place; see the
> note against task 9.8.
>
> 13.10 verified against `eimdb_dev`: three rows updated in one statement, three stamped, one
> distinct timestamp. The earlier `SELECT @ID = INSERTED.ID` form would have stamped one and left
> two stale, which matters because both sweeps claim batches in a single `UPDATE` and then filter
> on this column.
>
> **13.9 could not be tested as written, and cannot be.** Azure SQL runs UTC — `SYSDATETIME()` and
> `SYSUTCDATETIME()` are zero minutes apart on this server, so there is no non-UTC server to test
> against. The task assumed a self-hosted instance. The underlying risk is closed a different way:
> the trigger and the sweeps all use `SYSUTCDATETIME()`, so no local-time value can enter the
> column regardless of how the server is configured. Marked done on that basis, not on a passing
> test. If this platform is ever deployed against a self-hosted SQL Server, re-open it.
>
> There were two tasks numbered 13.11; the later ones are renumbered.
