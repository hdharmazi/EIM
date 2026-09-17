## Context

`MessageQueueRepository.CleanupAsync` builds its `WHERE` clause by joining the
status and age conditions with `AND`:

```csharp
if (status.HasValue) conditions.Add("STATUS = @STATUS");
if (olderThanDays.HasValue) conditions.Add("CREATEDDATETIME < @CUTOFF");
var where = string.Join(" AND ", conditions);
```

Reported symptom: selecting both a status and an age filter and confirming
delete returns a "success" toast reporting `Deleted 0 record(s).`. Nothing is
wrong with the SQL — it correctly returns zero rows when no record satisfies
*both* conditions simultaneously — but that is not how an operator reads
"filter by status X, and also by age", especially on a cleanup screen whose
purpose is bulk history purge. The screen's copy ("Leave as All to target all
statuses" / "Leave empty to ignore age") describes each filter in isolation
and never states how they combine, so there is no way for a user to predict
the AND behavior short of reading the API.

`CREATEDDATETIME` itself is populated correctly (UTC, per
`Script/SQL/Migrations/005_extend_messagequeuetable.sql`), so this is not a
data/timezone bug — confirmed by checking the column's default constraint and
migration history before deciding the fix.

## Goals / Non-Goals

**Goals:**
- Make "select a status and an age filter, then delete" behave as a broad
  purge: delete rows matching the status OR older than the cutoff, when both
  are supplied.
- Make the zero-match outcome visibly distinct from a normal success, so a
  user is not left guessing whether cleanup "worked."
- Keep the single-filter behavior (status only, or age only) unchanged.

**Non-Goals:**
- No change to what "delete" does once rows are selected (still hard delete,
  still cascades to `MESSAGELOGDETAIL` first, still requires at least one
  filter).
- No new filter types (e.g., environment, source system) on this screen.
- No change to `api/messagequeue/cleanup`'s route, verbs, or request shape —
  only the WHERE-clause semantics and response messaging.

## Decisions

### Combine filters with OR, not AND
When both `status` and `olderThanDays` are supplied, build
`WHERE STATUS = @STATUS OR CREATEDDATETIME < @CUTOFF` instead of `AND`.
Single-filter calls are unaffected (only one condition exists, so AND/OR is
moot). This matches the screen's actual use case — "purge this dead-letter
status, and also anything ancient regardless of status" — better than a
narrow intersection that most callers cannot predict and that silently
produces `deleted: 0` when the intersection happens to be empty.

Alternative considered: keep `AND` and add UI copy explaining the
intersection semantics. Rejected — it fixes the confusion but not the actual
capability gap (an operator who wants to purge both a specific status and
old records currently has no single action that does both; they'd need two
separate cleanup runs). Changing to OR delivers that capability directly and
still requires confirmation before any delete.

### Distinguish a zero-match result in the response message
`CleanupController.Cleanup` already returns `{ success, deleted, message }`.
Keep the shape, but have the frontend render a distinct toast variant when
`deleted === 0` (e.g., "No records matched the selected filter(s)." as an
informational/warning toast) instead of the generic
"Deleted 0 record(s)." success toast. No API contract change is needed since
`deleted` already carries this information — this is a frontend-only
presentation decision.

### No backend flag to opt back into AND
Considered adding a `combineMode=and|or` query parameter for callers that
want the old narrow behavior. Rejected as unnecessary complexity — there is
exactly one caller (the Cleanup Queue page), and the proposal's premise is
that AND was already unusable in practice. Revisit only if a second caller
with a genuine need for intersection semantics appears.

## Risks / Trade-offs

- [Broadening from AND to OR means a combined filter now deletes *more* rows
  than before, including rows an operator might not have intended (e.g., a
  recent row that happens to match the status) → Mitigation]: the
  confirmation dialog already shows the count context (status counts card
  above the form); update its copy to state the OR semantics explicitly so
  the scope is clear before confirming. Behavior is opt-in per filter
  selection — a user who wants only "old records regardless of status" still
  gets exactly that by leaving status at "All."
- [Existing automated tests, if any, assert AND semantics → Mitigation]:
  search `ARM.Integration.EIM.Tests` and any Dashboard.API test project for
  `CleanupAsync` coverage before changing the SQL; update or add tests
  alongside the fix.

## Migration Plan

No data migration. This is a behavior fix in application code:
1. Update `CleanupAsync`'s WHERE-clause join to OR when both filters are
   present.
2. Update `CleanupPage.tsx` copy and the zero-match toast.
3. Deploy Dashboard.API and Dashboard.Frontend together (the API behavior
   change and the UI copy describing it should ship in the same release to
   avoid a window where the UI describes the old AND behavior).

Rollback: revert the WHERE-clause change; no schema or data to unwind.

## Open Questions

- None — filter semantics, scope, and messaging are decided above.
