## Why

A message that D365 F&SC rejects for a business-data reason — for example `CustomerAddress_To_D365FSC` message #39, rejected with *"Write validation failed for table row of type 'WTC_CustPostalAddressV2Entity'. Infolog: Warning: Field 'Company' must be filled in."* — is recorded as status `4` (Dead Letter) instead of status `3` (Failed). This is not a deliberate policy: the terminal status is chosen by which `catch` block caught the exception, and `D365FSCException` derives from `Exception` rather than `ApplicationException`, so every destination error falls to the Dead Letter arm. In practice status `3` is reachable only by our own internal configuration errors, and status `4` is a mixed bag of permanent business rejections, genuine bugs, and messages that legitimately exhausted their retries.

The consequence is operational: an operator looking at Dead Letter cannot tell which rows are worth retrying, and a bulk "reprocess all Dead Letter" action would re-fail immediately for the majority of rows. The engine already computes the information needed to separate these — `DefaultFailureClassifier` distinguishes retryable from terminal failures — but `RecordFailure` discards that answer when it is `Terminal`.

## What Changes

- Terminal status selection moves from the `catch` block that caught the exception to the failure classifier's verdict. A failure the classifier calls `Terminal` is recorded as `3` (Failed); a retryable failure that exhausts its attempt allowance is recorded as `4` (Dead Letter).
- The two `catch` arms in `EIMController.ProcessMessageAsync` become equivalent and the `terminalStatus` parameter is removed from `RecordFailure`.
- The status vocabulary is given explicit, documented meanings that the engine, API and frontend agree on:
  - `3` **Failed** — the destination gave a definite negative answer, or the message could not be understood. Reprocessing the identical payload will not succeed; the source data or configuration must change first.
  - `4` **Dead Letter** — we never obtained a definite answer and gave up. Reprocessing is plausible once the underlying condition clears.
- Failures the classifier cannot recognise (a `NullReferenceException` from a mapper, for instance) are distinguished from positively terminal destination rejections in the recorded response message and log, so a bug is not mistaken for bad source data. They remain at status `3`.
- The synchronous failure path is brought into line with the asynchronous one: `BusinessEventOutcome` already maps a D365 `MessageProcessingResult` of `Failed` to status `3`, so the same class of business rejection now yields the same status regardless of which leg reported it.
- **BREAKING** (operational, not API): rows that would previously have been written as `4` for a business rejection are now written as `3`. Dashboard counts, saved filters and any alerting keyed on Dead Letter volume will shift. Historic rows are not migrated.

## Capabilities

### New Capabilities
- `terminal-status-semantics`: Defines what each terminal message status means, which failures map to which status, and the invariant that Dead Letter implies reprocessing is plausible while Failed implies it is not. Covers consistency between the synchronous orchestration path, the asynchronous business-event reconciliation path, and the retry-exhaustion path.

### Modified Capabilities
- `transient-failure-retry`: The classification requirement is extended so that the classifier's verdict determines the terminal status, not merely whether a retry is scheduled. The existing wording "takes the existing failure path" is replaced with an explicit mapping to status `3`. Note this capability's spec currently lives in the unarchived `add-entity-hold-release` change; the delta applies on top of it.

## Impact

- `ARM.Integration.EIM/Controller/EIMController.cs` — `ProcessMessageAsync` catch arms and `RecordFailure` signature and body.
- `ARM.Integration.EIM/MessageStatus.cs` — corrected documentation comments for `Failed` and `DeadLetter`; no value changes.
- `ARM.Integration.EIM/Retry/DefaultFailureClassifier.cs` — `IsPositivelyTerminal` is promoted from a private walk-control detail to information the caller can act on. No change to which failures are considered retryable.
- `ARM.Integration.EIM/Retry/FailureDisposition.cs` — doc comments referencing the old dual terminal path.
- `ARM.Integration.EIM.Tests` — existing engine boundary and retry tests assert the current Dead Letter outcome for terminal failures and will need updating; new tests cover the mapping table.
- Dashboard: no code change required. Status labels come from `STATUSTABLE`; the distribution of rows across `3` and `4` changes.
- No database schema change and no message contract change.
