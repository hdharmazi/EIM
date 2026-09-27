## Context

`CleanupAsync` issues two deletes inside one transaction on commands that never set
`CommandTimeout`:

```csharp
var deleteLogsSql     = $"DELETE FROM MESSAGELOGDETAIL WHERE MESSAGEID IN (SELECT ID FROM MESSAGEQUEUETABLE WHERE {where})";
var deleteMessagesSql = $"DELETE FROM MESSAGEQUEUETABLE WHERE {where}";
```

At current volume that is approximately:

```
  MESSAGEQUEUETABLE      ~70,000 rows   PAYLOAD + MESSAGELOG, both NVARCHAR(MAX)
          │
          ▼  ~10 step rows per message
  MESSAGELOGDETAIL      ~670,000 rows   DETAIL NVARCHAR(MAX), holding whole
                                        serialized MappedPayload and Result documents
```

Every orchestration writes `Processing`, `ResolveEnvironments`, `DetermineAction`, `MappedPayload`,
`SendToQueue` twice, and `Result`. Two of those are complete JSON documents. One transaction is
therefore asked to remove several gigabytes of LOB data while holding locks on a table that live
handlers are inserting into. The 30-second default expires, the `catch` rolls back, and nothing is
deleted — so the next attempt faces the same volume. The failure is permanent, not intermittent.

No index supports `CREATEDDATETIME`, and the `OR` between conditions prevented a seek in any case.

`MESSAGEQUEUETABLE` is not a diagnostic log. It is the system of record for:

| Mechanism | What it needs the row for |
|---|---|
| Hold / release | `Held` rows are a paused route's backlog; the sweep drains them when it resumes |
| Retry | `RetryScheduled` rows carry `NEXTATTEMPTDATETIME` and are claimed by the retry sweep |
| Reconciliation | `AwaitingD365` rows are waiting for an asynchronous business event |
| Duplicate detection | `SqlDuplicateProbe` matches redeliveries on `DEDUPEKEY` |
| Operator re-drive | `MessageStatus.Reprocessable = [Failed, DeadLetter, Held]` |

An age-only filter today deletes all of these.

## Goals / Non-Goals

**Goals:**
- Make cleanup complete, and keep completing as volume rebuilds.
- Make progress durable, so an interruption costs one batch rather than the whole run.
- Make bulk deletion incapable of destroying held backlogs, scheduled retries, or in-flight
  reconciliations.
- Keep deliberate, single-status deletion available for rows orphaned in `Processing` or `New`.

**Non-Goals:**
- Clearing the existing backlog through the application. The current data is disposable and can be
  truncated directly, so the fix is sized for steady state rather than for a one-off 670,000-row
  run.
- Scheduled automatic retention. The batch logic here is what a timer sweep would reuse, but adding
  the timer, its configuration, and its per-environment schedule is a separate change.
- Redacting rows in place (clearing `PAYLOAD`/`MESSAGELOG` while keeping the row). Deletion is the
  screen's existing and intended semantic.
- Detecting orphaned `Processing` rows automatically.
- Extracting the purge into `ARM.Integration.EIM` for sharing. Nothing shares it yet, and moving it
  is mechanical when something does.

## Decisions

### 1. Batch loop with per-batch transactions

Each iteration claims a bounded set of parent ids, deletes their children, then deletes them:

```sql
DECLARE @Ids TABLE (ID BIGINT);

INSERT INTO @Ids (ID)
SELECT TOP (@BATCH) ID
FROM dbo.MESSAGEQUEUETABLE
WHERE STATUS IN (<eligible>)            -- the guard, always present
  AND (<operator filter>)
ORDER BY ID;

DELETE FROM dbo.MESSAGELOGDETAIL  WHERE MESSAGEID IN (SELECT ID FROM @Ids);
DELETE FROM dbo.MESSAGEQUEUETABLE WHERE ID        IN (SELECT ID FROM @Ids);
```

Batch size 2,000, giving roughly 20,000 child rows per iteration and ~35 iterations for the current
backlog. Ordering by `ID` makes each batch a contiguous range of the clustered index and makes the
overall run oldest-first.

*Alternative considered:* raising `CommandTimeout` and keeping one transaction. It converts a fast
failure into a slow one — the platform request timeout (~230s on Azure App Service) sits behind the
command timeout, and the transaction log and lock duration sit behind that. It also keeps
all-or-nothing semantics, so a failure at 95% completion still deletes nothing.

### 2. A wall-clock budget, with the client continuing

A single request stops after roughly 20 seconds and returns `{ deleted, remaining }`. The client
re-issues until `remaining` is zero, accumulating the total for display.

The budget was first set at 60 seconds. Verification against the real backlog showed the page
appeared frozen for the first minute, so it was cut to 20: each extra request costs one `COUNT`,
which is negligible beside a 2,000-row batch.

The loop runs in a module-level store rather than in the page component. Verification also showed
that navigating away and back left a run going in the background while presenting a fresh, enabled
Delete button, inviting a second concurrent run. Holding the run outside the component keeps
progress visible across navigation and blocks a second start. A full page reload still stops the
loop; committed batches remain and the next run resumes from what is left.

This decouples the size of a run from the platform request timeout (~230s on Azure App Service):
the work is bounded per request but unbounded overall. An abandoned run leaves the table consistent
and the next run resumes where it stopped, because completed batches are already committed.

The existing backlog does not need this — it is disposable and can be truncated. It is retained
because the failure being fixed is one that recurs with volume, and a fix that only works below some
unstated row count reproduces the original defect on a delay.

### 3. No index

An index on `(STATUS, CREATEDDATETIME)` was considered. Batching turns one predicate evaluation into
many, so a scan per batch is a real cost at volume.

It is rejected because the volume it addresses is not going to exist: the backlog is disposable, the
table starts near empty, and scanning a small table is negligible. `IX_MESSAGEQUEUETABLE_STATUS`
already exists and leads on `STATUS`, so the guard's status predicate is served even without a new
index; only the date range would scan.

Adding an index is additive, requires no code change, and can be applied at any later point. Task
7.4 records the per-batch duration that would justify it, so the decision is deferred with a trigger
rather than made blindly.

### 4. The guard is a default, not a prohibition

```
  status = All        eligible = { Completed, Failed }
                      protected statuses unreachable in bulk

  status = Held       refused: "Held is protected and is not deleted by default."
       │
       └─ request repeated with an explicit acknowledgement ──▶ eligible = { Held }
```

The dropdown keeps every status, and choosing a protected one produces a message naming it rather
than a silent zero-match. This resolves the tension between wanting a guard and wanting orphaned
`Processing` and `New` rows to be clearable: bulk accidents become impossible while deliberate
surgery stays available, one status at a time, behind a second acknowledgement.

*Alternative considered:* listing only `Completed` and `Failed` in the dropdown. Less code and the
rule would be self-evident, but it removes the only route to an orphaned `Processing` row.

### 5. `AND`, reversing `2026-09-17-fix-messagequeue-cleanup-filters`

Under `OR`, `status = Held, olderThanDays = 30` means every held row of any age, plus everything
older than thirty days of any status. The narrowest request a protected status can express produces
the widest possible delete, immediately after the operator acknowledged they were touching something
protected.

The earlier change fixed two things; only one of them was the `OR`. The distinct "No records
matched" outcome is what resolved the reported ambiguity, and it remains. Restoring `AND` therefore
reintroduces predictable intersection semantics without reintroducing the silent-zero confusion.

The lost capability — "this status, or anything ancient, in one action" — becomes two runs. Since
one of those runs can now delete a held backlog, two is the correct number.

## Risks / Trade-offs

**A partially completed run leaves data half-deleted.** By design, and safe: children are deleted
before their parents within each batch, so no batch can orphan a `MESSAGELOGDETAIL` row. There is no
foreign key enforcing this, so the ordering must stay inside the batch rather than being split into
two passes. → Assert in test that no `MESSAGELOGDETAIL` row survives whose `MESSAGEID` is absent
from `MESSAGEQUEUETABLE`.

**Deleting while handlers insert.** Batches take `TOP (@BATCH) ... ORDER BY ID` over old rows while
new rows arrive at the high end of the clustered index, so contention is minimal. Bounded batches
keep any lock escalation short-lived. → Keep the batch small enough that a single iteration commits
well inside a lock window; 2,000 is a starting point, not a tuned value.

**The acknowledgement becomes routine.** A confirmation an operator sees often is a confirmation
they stop reading. → The refusal names the specific status and states what depends on it, rather
than being a generic "are you sure"; a `Held` refusal should say the rows are a paused route's
backlog.

**Reversing a prior decision may reintroduce the original complaint.** → The "No records matched"
outcome is explicitly retained and covered by a scenario; the reversal is recorded in the spec delta
so the next reader sees why the semantics changed twice.

**Batch size is a guess.** 2,000 is chosen without measurement, on an unknown service tier. → Make
it a constant that is trivial to change, and report per-request `deleted` so a slow environment is
visible rather than inferred.

**Truncating the backlog hides the bug rather than proving the fix.** A near-empty table makes any
implementation look correct, including the current one. → Verify against a populated table before
truncating, or seed enough rows afterwards to force more than one batch. A single-batch run proves
nothing about batching.

## Migration Plan

No database migration.

1. Deploy the API and frontend together — the client depends on the new `remaining` field.
2. Verify against a populated table: run cleanup with no status and `olderThanDays = 7` and confirm
   it completes across more than one batch, with the total climbing and `remaining` reaching zero.
3. Confirm held, retry-scheduled, and awaiting rows still exist afterwards.
4. Truncate the remaining backlog directly if desired. The data is disposable, and doing this
   *after* step 2 keeps the verification meaningful.

**Rollback:** restore the previous `CleanupAsync`. There are no data implications, because rows
already deleted were rows the operator asked to delete.

## Open Questions

- What is the real per-batch duration on the target tier? Determines whether 2,000 is right, and is
  the measurement that would justify revisiting the rejected index. Answerable only by step 2 above.
- Should the age filter apply to `CREATEDDATETIME` or `MODIFIEDDATETIME`? A row created long ago but
  retried yesterday is arguably not old. `CREATEDDATETIME` is the current behaviour and is retained
  here unexamined.
- ~~Does anything outside the dashboard call `api/messagequeue/cleanup`?~~ Resolved: the dashboard
  frontend is the only caller in the repository, so the `AND` semantics change has no hidden consumer.
