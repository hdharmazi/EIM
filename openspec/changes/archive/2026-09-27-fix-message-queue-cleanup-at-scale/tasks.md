## 1. Eligibility rules

- [x] 1.1 Define the default eligible set as `MessageStatus.Completed` and `MessageStatus.Failed`, expressed in terms of the `MessageStatus` constants rather than literals.
- [x] 1.2 Define every other status as protected, with a short reason per status suitable for surfacing to an operator (for example, Held: "a paused route's backlog").
- [x] 1.3 Add a pure, unit-testable helper that resolves a request into an eligible status set, or a refusal naming the protected status. Keep it free of SQL so it can be tested directly.

## 2. Batched delete

- [x] 2.1 Replace the two unbounded deletes in `CleanupAsync` with a batch claim: `INSERT INTO @Ids SELECT TOP (@BATCH) ID ... WHERE STATUS IN (<eligible>) AND (<filter>) ORDER BY ID`.
- [x] 2.2 Delete `MESSAGELOGDETAIL` rows for the claimed ids, then the claimed parents, inside one transaction per batch. Keep both deletes in the same batch — there is no foreign key, so splitting them into separate passes can orphan log details.
- [x] 2.3 Set an explicit `CommandTimeout` on every command; do not rely on the 30-second default.
- [x] 2.4 Introduce a batch size constant, initially 2,000, sited so it is trivial to change after measurement.
- [x] 2.5 Loop batches until no rows are affected or a wall-clock budget of roughly 20 seconds elapses (reduced from 60 during verification so progress updates more often).
- [x] 2.6 Return deleted count and remaining count. Compute `remaining` once, after the loop, using the same eligibility and filter predicate.
- [x] 2.7 No index is added. If task 7.4 shows a batch is slow, an index on `(STATUS, CREATEDDATETIME)` is the additive follow-up — it needs no code change.

## 3. Filter semantics

- [x] 3.1 Change the filter combination from `OR` to `AND`, reversing `2026-09-17-fix-messagequeue-cleanup-filters`.
- [x] 3.2 Apply the eligible status set as a separate, always-present `AND` constraint, independent of the operator's own status filter.
- [x] 3.3 Retain the distinct "no records matched" outcome — it, not the `OR`, is what resolved the original ambiguity, and the reversal depends on it remaining.

## 4. API

- [x] 4.1 Extend the cleanup response with `remaining` alongside `deleted`.
- [x] 4.2 Accept an explicit acknowledgement parameter for deleting a protected status.
- [x] 4.3 Refuse a protected status without acknowledgement, returning a message that names the status and states what depends on it. Ensure this is distinguishable in the response from a zero-match result.
- [x] 4.4 Confirm the "at least one filter" validation still applies unchanged.

## 5. Frontend

- [x] 5.1 Continue issuing cleanup requests while `remaining` is greater than zero, accumulating the total deleted.
- [x] 5.2 Show a running total and remaining count during a multi-request run, so a long run does not appear idle.
- [x] 5.2a Hold the run in a module-level store rather than the page component, so it survives navigation and a second run cannot start while one is in flight. Found during verification.
- [x] 5.3 Keep all statuses in the dropdown; add a second acknowledgement step in the confirmation dialog when a protected status is selected, naming the status and its dependency.
- [x] 5.4 Update the page copy: the two filters now narrow each other, and an unfiltered-by-status run deletes only Completed and Failed.
- [x] 5.5 Surface a refusal distinctly from "no records matched" and from a completed deletion.
- [x] 5.5a Report a stopped run with its HTTP status and correlation id, show it inline until the next run, treat "nothing deleted but rows remain" as a stop rather than a completion, and keep filter values across a reload. Found during verification.

## 6. Tests

- [x] 6.1 Test the eligibility helper directly: default set, each protected status refused by name, acknowledged status accepted.
- [x] 6.2 Assert an age-only request leaves Held, RetryScheduled, AwaitingD365, New, Processing, and DeadLetter rows intact.
- [x] 6.3 Assert an age-only request deletes Completed and Failed rows older than the cutoff.
- [x] 6.4 Assert status plus age deletes the intersection, not the union — the direct regression test for the `AND` reversal.
- [x] 6.5 Assert no `MESSAGELOGDETAIL` row survives whose `MESSAGEID` is absent from `MESSAGEQUEUETABLE`, after a full run and after a run stopped mid-way.
- [x] 6.6 Assert a run stopped by the time budget reports a non-zero `remaining`, and that resuming completes the work.
- [x] 6.7 Assert an acknowledged protected-status request combined with an age filter does not touch other statuses.
- [x] 6.8 Run the full solution build and test suite.

## 7. Verification

- [x] 7.1 Deploy API and frontend together — the client depends on the new `remaining` field.
- [x] 7.2 Verify against a **populated** table, before truncating anything. A near-empty table makes any implementation look correct, including the current one. Verified 2026-09-24/26 against `eimdb`: 78,091 Completed, 47 Failed, 22 Dead letter, 547,566 log detail rows.
- [x] 7.3 Run cleanup with no status and `olderThanDays = 7`. Confirm it spans more than one batch, the total climbs, and `remaining` reaches zero. A single-batch run proves nothing about batching. Four runs (three interrupted by page reloads, fixed in 5.2a) cleared the backlog to 75 Completed; every interruption kept committed batches and created no orphaned log rows (165 pre-existing before and after).
- [x] 7.4 Record the observed per-batch duration. This is the measurement that decides whether the batch size needs changing and whether the rejected index is worth adding. Roughly 20 seconds per 2,000-row batch. The cost appears to be LOB deletion rather than row lookup, so the rejected index is not expected to help.
- [x] 7.5 Confirm held, retry-scheduled, and awaiting rows still exist after the run, and that a paused route still drains correctly when re-enabled. `eimdb` held no Held, RetryScheduled or AwaitingD365 rows; all 22 protected Dead letter rows survived, and the integration tests cover the other protected statuses. Cleanup does not touch the release sweep, so route draining is unaffected.
- [x] 7.6 Truncate the remaining backlog directly if desired — the data is disposable, and doing it after 7.3 keeps the verification meaningful. Not required: the backlog is cleared.
