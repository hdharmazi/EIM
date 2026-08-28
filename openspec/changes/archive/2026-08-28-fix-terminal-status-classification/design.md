## Context

`EIMController.ProcessMessageAsync` wraps orchestration dispatch in two `catch` arms. The arm that fires determines the terminal status handed to `RecordFailure`:

```
catch (ApplicationException) → RecordFailure(..., MessageStatus.Failed)     // 3
catch (Exception)            → RecordFailure(..., MessageStatus.DeadLetter) // 4
```

`ApplicationException` is thrown in exactly four places, all internal plumbing checks: message row not found, `Type` column missing, no orchestration registered for the type, and no `ENVIRONMENTTABLE` row for the environment/system pair. Nothing a destination system does can produce one. `D365FSCClient` throws `D365FSCException`, which derives from `Exception`, so every destination rejection — including a well-formed OData `400` carrying a D365 infolog — lands on the Dead Letter arm.

`RecordFailure` then calls `DefaultFailureClassifier.Classify`, which already walks the inner-exception chain and answers `Retryable` or `Terminal` using HTTP status and OData error code. But when the answer is `Terminal`, the classifier's verdict is discarded and the caller-supplied `terminalStatus` is written instead:

```csharp
if (_failureClassifier is null || _retrySchedule is null
    || _failureClassifier.Classify(exception) == FailureDisposition.Terminal)
{
    UpdateMessageStatus(messageId, responseMessage, terminalStatus);  // from the catch block
    return;
}
```

So the engine computes the right distinction and throws it away.

The asynchronous leg does not have this problem. `BusinessEventOutcome` maps a D365 `MessageProcessingResult` of `Failed` to `MessageStatus.Failed`, so a business rejection reported by a business event already reaches status `3`. The same rejection reported synchronously reaches `4`. Two paths, one class of error, two statuses.

`MessageStatus.Failed` is documented as "Business error. Terminal; requires an operator to act." That comment describes the intended design; the code does the opposite.

Constraints:
- Status integers are persisted in `MESSAGEQUEUETABLE.STATUS`, joined to `STATUSTABLE` for labels, and consumed by the dashboard API and frontend. Values must not be renumbered.
- `MessageStatus.Reprocessable = [Failed, DeadLetter, Held]` is used by the reprocess action; both terminal statuses stay reprocessable.
- `d365-business-event-reconcile` treats `2` and `3` as terminal in SQL (`AND STATUS NOT IN (2, 3)`) and deliberately leaves `4` correctable by a late event. Moving business rejections from `4` to `3` therefore makes them *less* correctable by a late business event. This is intentional and must be called out.
- The `transient-failure-retry` capability spec lives in the unarchived `add-entity-hold-release` change (94/98 tasks). This change's delta applies on top of it.

## Goals / Non-Goals

**Goals:**
- One rule decides the terminal status, applied consistently, derived from information the engine already has.
- `4` (Dead Letter) means exactly one thing: we never got a definite answer and stopped trying. Every row in `4` is plausibly worth reprocessing unchanged.
- `3` (Failed) means the message will not succeed unchanged; something upstream must be corrected first.
- The synchronous and asynchronous paths agree on the status for the same class of failure.
- A failure the classifier does not recognise is visibly distinct in the response message and logs from a failure the destination positively rejected.

**Non-Goals:**
- Renaming statuses. `Failed` may be a vague label and `Rejected` or `Invalid` clearer, but `STATUSTABLE` rows, API contracts and frontend badges are out of scope here.
- Adding a fifth terminal status for "probably a bug". Considered and deferred — see Decisions.
- Adding a structured `FAILUREREASON` / `ERRORCODE` column. Valuable for grouping and triage, but a schema change and a separate change proposal.
- Migrating historic rows. Existing `4` rows keep their status.
- Changing which failures the classifier considers retryable. The retryable set is unchanged.

## Decisions

### 1. The classifier's verdict selects the terminal status

`RecordFailure` loses its `terminalStatus` parameter. The mapping becomes:

```
                         Classify(exception)
                                 │
                 ┌───────────────┴───────────────┐
                 ▼                               ▼
             Terminal                        Retryable
                 │                     ┌──────────┴──────────┐
                 ▼                     ▼                     ▼
            Failed (3)          attempts remain        attempts exhausted
                                       │                     │
                                       ▼                     ▼
                              RetryScheduled (7)      DeadLetter (4)
```

Both `catch` arms collapse to the same call. They are kept as two arms only because `ApplicationException` is still worth logging distinctly; if that proves not to be true during implementation, they merge into one.

*Alternative considered — widen the `catch` split by adding `catch (D365FSCException)`.* Rejected: it re-encodes the classifier's rules in a second place, and it does not help when the D365 exception is wrapped inside an aggregate or a mapper exception. The classifier already unwraps the chain.

*Alternative considered — leave routing alone and change `D365FSCException` to derive from `ApplicationException`.* Rejected: a one-line trick that happens to produce the right answer for D365 today, but it makes the status depend on an inheritance decision in `ARM.Integration.Core` rather than on a stated policy, and it silently mis-routes any future destination client.

### 2. Classification and scheduling are separated; the classifier is always present

`_failureClassifier` and `_retrySchedule` are both nullable today, and the constructor enforces both-or-neither. The null case is not "retry disabled for this environment" — it is the operator-initiated reprocess path, which deliberately wants a definite answer rather than a scheduled retry.

Routing the status on the classifier makes both-or-neither wrong. Without a classifier, a reprocess attempted while the destination is down would be recorded at `3`, which under the new semantics asserts that reprocessing unchanged is futile. That is exactly the confusion this change exists to remove.

The two concerns are separated instead. Classification is always available; only the schedule is optional:

```
   classifier   schedule    meaning                          terminal outcome
   ──────────   ────────    ─────────────────────────────    ────────────────
   present      present     normal pipeline                  3 / 7 / 4
   present      absent      operator reprocess —             3 if terminal
                            classify, but never schedule     4 if retryable
```

The "retryable but we are not going to try again" cell lands on `4`, which is precisely the new meaning of Dead Letter: no definite answer was obtained and we stopped. Reprocess therefore reports honestly without ever scheduling a retry.

The constructor guard relaxes from both-or-neither to "a schedule requires a classifier". Callers that previously passed neither now pass a classifier alone.

*Alternative considered — keep both-or-neither and record `3` when the classifier is absent.* Rejected: it knowingly mislabels a transient outage during reprocess as a permanent rejection.

### 3. Unrecognised failures stay at Failed, but are marked

`ClassifySingle` returns `Terminal` both for a positively terminal answer (`D365FSCException` with a non-retryable status, `JsonException`) and as its `_ =>` default for anything unrecognised. A `NullReferenceException` in a mapper is a bug, not bad source data, yet both land on `3`.

A third terminal status was considered and rejected for now: it adds a `STATUSTABLE` row, a frontend badge, a filter option and a reprocess-eligibility decision, for a bucket that should be empty in a healthy system. Instead, `IFailureClassifier` gains the ability to report *why* it said `Terminal` — positively rejected by the destination versus unrecognised — and `RecordFailure` uses that only to shape the recorded response message and the log level:

| Verdict | Status | Response message | Log |
|---|---|---|---|
| Terminal, positively rejected | `3` | `ERROR:<destination message>` (unchanged) | `LogWarning` — expected, actionable by business |
| Terminal, unrecognised | `3` | `ERROR:<message> (unclassified failure)` | `LogError` — likely a defect |
| Retryable, attempts remain | `7` | `... (attempt N, retrying)` (unchanged) | `LogWarning` (unchanged) |
| Retryable, exhausted | `4` | `... (retry attempts exhausted after N)` (unchanged) | `LogError` (unchanged) |

This keeps the door open: if the unclassified bucket turns out to be non-trivial in practice, promoting it to its own status later is a small step, because the classifier already reports it.

*Shape of the change to `IFailureClassifier`:* `FailureDisposition` stays a two-member enum so existing call sites and the retry sweep are unaffected; the reason is returned alongside it (an out parameter or a small record result). The exact shape is an implementation detail, but `Classify(Exception)` must keep working for callers that only care about retry-or-not.

### 4. Business rejections become less correctable by a late business event — accepted

`d365-business-event-reconcile` guards `AND STATUS NOT IN (2, 3)` so that a late event cannot move a row out of `3`, while `4` stays correctable by design. Under this change, a synchronous business rejection now sits in `3` and is no longer correctable by a late event.

This is correct: a message that D365 rejected at submission time was never accepted, so there is no asynchronous business event coming for it. The rows that legitimately need late correction are those that *were* submitted and then timed out — and those escalate to `4` via `AwaitingD365SweepHandler`, which is unaffected.

### 5. The mapping is extracted as a pure function so it can be tested

Nothing currently tests `RecordFailure`: it writes through `UpdateMessageStatus` straight to SQL, and `EIMController` takes a raw connection string, so there is no seam. The mapping table is the entire substance of this change and would otherwise ship unverified.

The decision is extracted into a pure function in `ARM.Integration.EIM/Retry` that takes the verdict and whether attempts remain, and returns the status to record, the suffix to append to the response message, and whether the failure should log at Error. `RecordFailure` becomes: classify, select, persist.

This is a deliberate exception to the "no helper for a one-time operation" rule. The helper has one caller, but it is the seam that makes the change verifiable, and the mapping table maps one-to-one onto its test cases.

*Alternative considered — a status-writer interface on `EIMController` that tests can fake.* Rejected as disproportionate: it changes the constructor and every caller to test one branch.

*Alternative considered — rely on the DEV2 verification in group 5.* Rejected: the mapping is the change, and manual verification does not survive the next edit.

### 6. No database or contract change

Status integers, `STATUSTABLE` rows, `MESSAGEQUEUETABLE` columns, the response envelope and the dashboard API all stay as they are. The change is confined to which integer is written.

## Risks / Trade-offs

- **Dashboard counts shift on deploy; alerting keyed on Dead Letter volume may fire or fall silent.** → Call it out in the release note. Dead Letter volume dropping is the expected signal, not an incident. Verify against a known set of rows in DEV2 before promoting.

- **Historic rows are not migrated, so `4` holds a mix of old and new semantics.** → Accepted. A migration would have to guess intent from free-text `RESPONSEMESSAGE`, which is worse than leaving history alone. Note the cutover date in the release note so operators can reason about older rows.

- **The failure path has no existing test coverage at all.** `EngineBoundaryTests` guards assembly boundaries only; no test exercises `RecordFailure`. → Addressed by decision 5. Until the extraction exists there is nothing to update, only something to add.

- **An operator's "reprocess" habit changes meaning.** Reprocessing a `3` row unchanged is now by definition futile — the payload or upstream data must change first. → Out of scope to enforce, but worth noting; a future change could require an edit step before reprocessing from `3`.

- **`IsPositivelyTerminal` becomes public surface.** It is currently a private short-circuit control for the exception-chain walk, tuned for that job. Exposing it makes it a contract. → Keep the exposure minimal — a reason enum on the classify result, not the predicate itself — so the walk logic stays free to change.

- **The classifier is now the single point of failure for status correctness.** A misclassification silently puts a message in the wrong terminal bucket. → Mitigated by the mapping table being directly testable, and by the unrecognised case logging at Error so gaps surface rather than hide.

## Migration Plan

1. Ship the code change; no schema step, no data step.
2. In DEV2, replay a known business rejection (the `Company` missing case from message #39) and confirm it lands at `3`; force a D365 outage or a `503` and confirm the row goes `7 → 7 → 4`.
3. Confirm `AwaitingD365SweepHandler` still escalates timeouts to `4`, and that a late business event still corrects a `4` row.
4. Promote. Rollback is a straight revert — no persisted state depends on the new behaviour.

## Open Questions

- Should reprocessing a `3` row require the payload to be edited first, or is the current one-click reprocess acceptable? Deferred, but it is the natural follow-on.
- Is `Failed` the right label once it means "rejected by the destination"? A `STATUSTABLE` label change is cheap; the word `Rejected` is clearer. Out of scope here, worth a decision.
- Do the four internal `ApplicationException` cases deserve their own status? They are configuration errors, not data errors, and they now share `3` with business rejections. Same argument as the unrecognised bucket — deferred for the same reason.
