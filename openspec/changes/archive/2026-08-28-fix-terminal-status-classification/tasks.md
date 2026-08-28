## 1. Classifier reports its reason

- [x] 1.1 Add a terminal-reason concept to `ARM.Integration.EIM/Retry` distinguishing a positively matched terminal verdict from an unmatched one, without changing the two members of `FailureDisposition`.
- [x] 1.2 Extend `IFailureClassifier` so a caller can obtain the disposition together with the reason, keeping the existing `Classify(Exception)` signature working for callers that only need retry-or-not.
- [x] 1.3 Update `DefaultFailureClassifier` to surface the reason, reusing the existing `IsPositivelyTerminal` logic; do not change which failures are considered retryable.
- [x] 1.4 Add unit tests covering: `D365FSCException` at `400` is terminal and positively matched; `D365FSCException` at `503` is retryable; `JsonException` is terminal and positively matched; `NullReferenceException` is terminal and unmatched; a `D365FSCException` at `400` wrapped in another exception is still terminal and positively matched.

## 2. Route the terminal status on the classifier

- [x] 2.1 Add a pure selector in `ARM.Integration.EIM/Retry` mapping a `FailureVerdict` and whether attempts remain to the status to record, the response-message suffix, and whether to log at Error.
- [x] 2.2 Relax the `EIMController` constructor guard from both-or-neither to "a schedule requires a classifier", and default the classifier internally so the reprocess call sites need no change.
- [x] 2.3 Rewrite `RecordFailure` to classify, select, then persist; remove its `terminalStatus` parameter.
- [x] 2.4 Update both `catch` arms in `EIMController.ProcessMessageAsync` to call `RecordFailure` without a status; keep the `ApplicationException` arm only if it still adds distinct logging, otherwise merge the arms.
- [x] 2.5 Confirm the retryable branch is unchanged when a schedule is present: attempt increment, `RetryScheduled` while attempts remain, `DeadLetter` with the exhaustion message when they run out.
- [x] 2.6 Verify no attempt count is incremented on a terminal verdict, and none is incremented when no schedule is configured.

## 3. Correct the status documentation

- [x] 3.1 Update the XML doc comments on `MessageStatus.Failed` and `MessageStatus.DeadLetter` to state the meanings agreed in the spec; leave the integer values unchanged.
- [x] 3.2 Update the doc comment on `FailureDisposition.Terminal`, which currently describes a dual `Failed`/`DeadLetter` path.
- [x] 3.3 Confirm `MessageStatus.Reprocessable` still contains both terminal statuses.

## 4. Test the mapping

- [x] 4.1 Add a test class for the selector covering the full mapping table.
- [x] 4.2 Assert a positively rejected terminal verdict yields status `3`, no message suffix, and no Error log.
- [x] 4.3 Assert an unclassified terminal verdict yields status `3`, an unclassified-failure marker in the message, and an Error log.
- [x] 4.4 Assert a retryable verdict with attempts remaining yields status `7` and a retrying marker.
- [x] 4.5 Assert a retryable verdict with attempts exhausted yields status `4` and an exhaustion marker.
- [x] 4.6 Assert a retryable verdict with no schedule configured yields status `4` without incrementing an attempt count.
- [x] 4.7 Confirm the existing `d365-business-event-reconcile` tests still pass, in particular that a `Failed` business event maps to status `3` and that the reconcile SQL guard on statuses `2` and `3` is unchanged.
- [x] 4.8 Run the full `ARM.Integration.EIM.Tests` suite.

## 5. Verify in DEV2

- [x] 5.1 Replay the `CustomerAddress_To_D365FSC` case with `Company` absent and confirm the row lands at status `3` with the D365 infolog visible in the dashboard detail pane.
- [x] 5.2 Force a retryable D365 failure and confirm the row progresses `7 → 7 → 4` and that the exhaustion message is recorded.
- [x] 5.3 Reprocess a message from the dashboard while the destination is unavailable and confirm it lands at status `4` without being left at `7`.
- [x] 5.4 Confirm `AwaitingD365SweepHandler` still escalates an unacknowledged submission to status `4`.
- [x] 5.5 Confirm a late `Failed` business event still reconciles an awaiting row to status `3`, and that a late event still corrects a row the sweep escalated to `4`.
- [x] 5.6 Confirm the dashboard status filters, counts and badges render correctly for the new distribution with no frontend change.

## 6. Release

- [ ] 6.1 Write a release note stating that business rejections move from Dead Letter to Failed, that Dead Letter volume is expected to drop, that historic rows are not migrated, and the cutover date.
- [ ] 6.2 Check whether any alerting or saved dashboard filter is keyed on Dead Letter volume and adjust it.
- [x] 6.3 Record in repository memory that the terminal status is routed on the classifier verdict and that `catch`-block-derived status selection was the original defect.
