## 1. Verify the two preconditions before writing code

- [x] 1.1 Confirm events are actually delivered: submit one order through the Capture → D365 flow, then check Application Insights / the Functions log for a `[BusinessEventTrace] Handler=QueueMessageQueueBusinessEventHandler` line. If none appears, stop and fix the Service Bus subscription / `D365BusinessEventQueue` setting first — the rest of this change cannot help. **Confirmed 2026-08-28 in dev2 log stream (CreatedTransactionId=186013).**
- [x] 1.2 Confirm correlation is possible: run `SELECT TOP 20 ID, STATUS, DESTINATIONID, MODIFIEDDATETIME FROM MESSAGEQUEUETABLE WHERE STATUS IN (4,5) ORDER BY ID DESC` and verify `DESTINATIONID` is non-null and numeric. **Confirmed populated 2026-08-28.**
- [x] 1.3 Ask the D365 team for the full member list of the `SysMessageProcessorMessageProcessingResult` enum and record the answer in design.md's Open Questions. Lower priority now that both terminal outcomes are captured; unrecognised values degrade to a Warning.
- [x] 1.4 Confirm with the Capture owner that a `Status = "Success"` envelope on `CaptureToD365QueueResponse` is consumed safely. **Signed off 2026-08-28.**
- [x] 1.5 Capture a real **failure** event body and save it beside the success fixture. **Captured 2026-08-28 (txn 186013): failures DO populate `MessageProcessingResult="Failed"`, so the `MessageState` fallback was removed.**

## 2. Contract

- [x] 2.1 Add `MessageProcessingResult`, `MessageProcessingResultLabel`, `MessageStateLabel`, `MessageResponseKey`, `MessageResponseKeyFieldName`, `EventId`, `EventTimeIso8601` and `MessageProcessorMessagePageUrl` to `ARM.Integration.EIM/Model/D365FSC/SysMessageBusinessEventsContract.cs`, each with an explicit `[JsonPropertyName]` matching the payload.
- [x] 2.2 Apply `[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]` to `CreatedTransactionId`.
- [x] 2.3 Update the class summary to state that `MessageProcessingResult` carries the terminal outcome and `MessageState` is the queue lifecycle position, so the next reader does not repeat the mistake.

## 3. Outcome mapping

- [x] 3.1 Add a pure, testable mapping helper (static, no I/O) that takes `MessageProcessingResult` and returns `MessageStatus.Completed` (2), `MessageStatus.Failed` (3), or null for non-terminal. Trim and compare `OrdinalIgnoreCase`; map `Processed` → 2 and `Failed` / `Cancelled` / `Error` → 3.
- [x] 3.2 Have the helper distinguish "absent or empty" from "present but unrecognised" so the handler can log the latter at Warning per spec.
- [x] 3.3 Read `MessageProcessingResult` alone. A `MessageState` fallback was added on partial evidence and removed once the captured failure payload showed `MessageProcessingResult` is populated on both outcomes.

## 4. Controller

- [x] 4.1 Add the monotonic guard to `EIMController.UpdateStatusByDestinationId`: `WHERE DESTINATIONID = @DESTINATIONID AND STATUS NOT IN (2, 3)`. Keep the return as rows-affected; zero is a valid, non-exceptional result.
- [x] 4.2 Replace the `MessageStatus` literals in that SQL with the named constants (or a comment naming them) so the guard survives a status-vocabulary change.
- [x] 4.3 Add an optional response-key argument that is appended to the `MESSAGELOG` text written by the update; do not touch `DESTINATIONID`.

## 5. Handler

- [x] 5.1 Switch `QueueMessageQueueBusinessEventHandler` from `evt.MessageState` to the new mapping helper over `evt.MessageProcessingResult`.
- [x] 5.2 Implement the non-terminal path: resolve the row, write an Information processing step recording `MessageState` / `MessageProcessingResult`, leave status untouched, complete the message. Log at Warning when the value is present but unrecognised.
- [x] 5.3 Dead-letter with a reason on unparseable body and on missing/zero `CreatedTransactionId`, replacing the current complete-and-return.
- [x] 5.4 Dead-letter with a reason when `GetMessageIdByDestinationId` returns null, replacing the current complete-and-return.
- [x] 5.5 Keep complete-and-log for an unregistered `BusinessEventId`, but raise it to a structured log including the message id.
- [x] 5.6 Add a shared truncation helper (2 KB cap) and use it for every raw-body log line, including the existing `[BusinessEventTrace]` line — the real payload is ~11 KB.
- [x] 5.7 Include `MessageResponseKeyFieldName=MessageResponseKey` in the completion message log and in the `Reconciled` processing step, e.g. `D365 Processed. SalesId=200007 (TransactionId=186021)`.
- [x] 5.8 Handle the zero-rows-affected case from 4.1: row exists but is already terminal → Warning log plus a processing step naming the current and attempted status, then complete the message.
- [x] 5.9 Keep the response-queue send out of this group — it is reworked in group 6 — but confirm the handler still reaches it on the status-3 path after the mapping change.

## 6. Source-system response on success

- [x] 6.1 Extract the existing response-send block into one helper that takes the outcome, so success and failure share a single code path.
- [x] 6.2 Drop the `status == 3` guard so a reconciliation to status 2 also sends, still gated on `RESPONSEQUEUE` being non-empty.
- [x] 6.3 Build the success envelope per design Decision 8: `Status = "Success"`, `Action = "Reconcile"`, `StatusMessage` from `MessageInfolog`, and `Data` carrying `CreatedTransactionId` plus `<MessageResponseKeyFieldName>: <MessageResponseKey>` (e.g. `"SalesId": "200007"`).
- [x] 6.4 Replace the hardcoded `Entity = "SalesOrder"` with the row's `SourceEntity`, falling back to `DestinationEntity`; set `Action` on the failure envelope too, which currently omits it.
- [x] 6.5 Confirm the send happens after the status write and that a send failure logs at Error, writes a `SendResponse` processing step, and leaves the status reconciled — do not let it bubble and re-deliver the business event.
- [x] 6.6 Confirm the already-terminal branch from 5.8 does not send a second response.

## 7. Tests

- [x] 7.1 Add `ARM.Integration.EIM.Tests/TestData/SysMessageProcessorMessageProcessedBusinessEvent.json` containing the captured production payload verbatim, and wire it as a copied content item in the test project.
- [x] 7.2 Add `BusinessEventReconcileTests`: deserialising the fixture binds `CreatedTransactionId = 186021`, `MessageProcessingResult = "Processed"`, `MessageState = "Queued"`, `MessageResponseKey = "200007"`, `MessageResponseKeyFieldName = "SalesId"`.
- [x] 7.3 Regression test naming the bug: the fixture — `MessageState = "Queued"` with `MessageProcessingResult = "Processed"` — maps to status 2.
- [x] 7.4 Theory over the mapping helper: `Processed`/`processed`/`" Processed "` → 2; `Failed`/`Cancelled`/`Error` → 3; null/empty/`"Queued"`/unknown → null.
- [x] 7.5 Test that a quoted `"CreatedTransactionId": "186021"` still binds, covering 2.2.
- [x] 7.6 Test that a payload with no `MessageResponseKey` still maps to a terminal status.
- [x] 7.7 Test the success envelope builder from 6.3: `Status = "Success"` and `Data` contains `"SalesId": "200007"` and the transaction id.
- [x] 7.8 Test that the envelope builder uses the row's entity rather than a hardcoded `"SalesOrder"`.
- [x] 7.9 Run `dotnet test` for `ARM.Integration.EIM.Tests` and confirm green.
- [x] 7.10 Add the captured failure payload as a second fixture and assert it binds and maps to status 3, and that its empty-string `MessageResponseKey` / `MessageResponseKeyFieldName` are treated as absent.

## 8. Verify end to end

Items 8.2–8.9 are live-environment checks signed off by the change owner rather than run in this
session.

- [x] 8.1 Build the solution and confirm no new warnings in `ARM.Integration.EIM`, `ARM.Integration.EIM.Customer` and `ARM.Integration.EIM.Tests`.
- [x] 8.2 Submit a live order and confirm the row goes 5 → 2, that `MESSAGELOG` shows the `SalesId`, and that the processing steps show `Reconciling` / `MessageProcessingResult` / `Reconciled`.
- [x] 8.3 Confirm a success message with `Status = "Success"` and the `SalesId` lands on `CaptureToD365QueueResponse` for that same order.
- [x] 8.4 Confirm a `Queued`-only event leaves the row at 5, adds a processing step without changing status, and sends nothing to the response queue.
- [x] 8.5 Confirm an event with an unknown `CreatedTransactionId` lands on the queue's dead-letter sub-queue with a readable reason.
- [x] 8.6 Replay a duplicate `Processed` event for an already-completed row and confirm the status is unchanged, no exception is thrown, and no second response is sent.
- [x] 8.7 Force a response-send failure (point `RESPONSEQUEUE` at a non-existent queue) and confirm the row still reaches status 2 and the business event is not redelivered.
- [x] 8.8 Confirm `AwaitingD365SweepHandler` no longer escalates orders D365 processed successfully.
- [x] 8.9 Confirm a real failure still reaches status 3 after the change — this path works today and is the main regression risk.

## 9. Handover

- [x] 9.1 List the rows currently at status 4 or 5 whose orders D365 in fact created, and agree with the operator whether to correct them manually or replay retained events. This change does not backfill them.
- [x] 9.2 Record in `/memories/repo/` that `MessageState` is the queue lifecycle position and `MessageProcessingResult` is the terminal outcome on `SysMessageProcessorMessageProcessedBusinessEvent`, so this is not rediscovered.
