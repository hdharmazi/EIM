## Why

Cleanup does not work and cannot work. `MessageQueueRepository.CleanupAsync` deletes every matching
row and every matching log detail inside a single transaction on a `SqlCommand` that never sets
`CommandTimeout`, so it inherits the 30-second default. With ~70,000 queue rows and roughly ten
`MESSAGELOGDETAIL` step rows each — many carrying whole serialized JSON documents in
`NVARCHAR(MAX)` — one run asks the database to delete ~670,000 rows and several gigabytes of LOB
data in one atomic statement pair. It times out, rolls back, and deletes nothing. Because nothing is
deleted, the table never shrinks, so every subsequent attempt fails identically and slightly slower.
The operator sees a red "Cleanup failed" toast after a long pause.

The existing backlog is disposable and may be truncated directly, so the fix does not have to
survive a 670,000-row first run. It does have to stop the same failure recurring as volume rebuilds,
which an all-or-nothing transaction guarantees it will.

Two further problems only become visible once cleanup actually runs. `MESSAGEQUEUETABLE` is not a
log — it is the system of record for hold/release, retry scheduling, asynchronous D365
reconciliation, and duplicate detection. A filter of "everything older than seven days" deletes
held backlogs, scheduled retries, and messages awaiting confirmation. And the `OR` filter
combination means a deliberately narrow request silently widens into the broadest possible one.

## What Changes

- Delete in bounded batches, each in its own transaction, with an explicit `CommandTimeout`, instead
  of one unbounded atomic delete. Partial progress becomes the mechanism rather than a hazard.
- Bound each request by a wall-clock budget and return `{ deleted, remaining }`, continuing until
  none remain. A run can therefore exceed the platform request timeout without failing.
- Guard the default purge set. With no status selected, only `Completed` and `Failed` are eligible.
  `New`, `Processing`, `DeadLetter`, `AwaitingD365`, `Held`, and `RetryScheduled` are never deleted
  in bulk.
- Allow deliberate deletion of a protected status. Naming one explicitly is refused with a message
  identifying that status, and proceeds only when the request carries an explicit acknowledgement.
  An operator can still clear rows orphaned in `Processing` or `New`.
- **BREAKING**: combine the status and age filters with `AND` rather than `OR`. This reverses a
  decision made in `2026-09-17-fix-messagequeue-cleanup-filters` — see Impact.

No migration. An index on `(STATUS, CREATEDDATETIME)` was considered and rejected — see Impact.

## Capabilities

### New Capabilities
<!-- None. This corrects and extends an existing screen and endpoint. -->

### Modified Capabilities
- `message-queue-management`: the "Cleanup utility" requirement changes its filter combination from
  `OR` to `AND`, gains a protected-status guard with an explicit override, and gains batched,
  resumable execution that reports progress rather than succeeding or failing atomically.

## Impact

**Code**
- `ARM.Integration.Dashboard.API/Services/MessageQueueRepository.cs` — `CleanupAsync` becomes a
  batch loop with a time budget and returns a progress result rather than a single count.
- `ARM.Integration.Dashboard.API/Controllers/MessageQueueController.cs` — response shape gains
  `remaining`; protected statuses are refused without an acknowledgement flag.
- `ARM.Integration.Dashboard.Frontend/src/features/messagequeue/api.ts` and `CleanupPage.tsx` —
  continue until `remaining` is zero, show cumulative progress, and carry the acknowledgement for a
  protected status.

**Data**
- No migration. An index on `(STATUS, CREATEDDATETIME)` would turn each batch's predicate from a
  scan into a seek, but the current backlog is disposable and can be truncated, so the table starts
  near empty and a scan over it is negligible. Adding an index for a cost that does not currently
  exist is speculative. It remains a trivial additive follow-up requiring no code change if batches
  are ever observed to slow down — task 7.4 records the measurement that would trigger it.

**Reversing a previous decision**

`2026-09-17-fix-messagequeue-cleanup-filters` deliberately changed this combination from `AND` to
`OR`, because `AND` reported `Deleted 0 record(s)` in a way operators could not predict. That
reasoning held while every status was equally deletable. It does not survive the protected-status
guard: `status = Held, olderThanDays = 30` under `OR` means *every* held row of any age, plus
everything older than thirty days of any status — the widest possible delete, produced from the
narrowest possible request, on the exact path where an operator has just acknowledged a protected
status.

That change carried two remedies for the original complaint, and the other one stands: the distinct
"No records matched" outcome, which is what actually resolved the ambiguity. Restoring `AND` keeps
it. The cost is that "purge this status, or anything ancient" becomes two runs instead of one;
given one of those runs can now delete a held backlog, two is the right number.

**Corrected during analysis, not a defect**
- `CREATEDDATETIME` was initially suspected of holding server-local time against a UTC cutoff.
  Migration `005_extend_messagequeuetable.sql` already replaced the default with `SYSUTCDATETIME()`.
  There is no clock defect and no change is needed.

**Out of scope**
- Scheduled automatic retention. A timer-driven sweep reusing this batch logic is the natural next
  change; this one restores the manual capability first.
- Rows orphaned in `Processing` or `New` by an interrupted host are reachable only by an operator
  naming that status explicitly. Detecting them automatically is a separate concern.
