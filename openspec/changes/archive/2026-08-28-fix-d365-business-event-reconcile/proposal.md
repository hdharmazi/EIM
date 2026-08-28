## Why

Sales orders submitted to D365 F&SC sit at `STATUS = 5` (AwaitingD365) forever and are eventually
escalated to Dead Letter by `AwaitingD365SweepHandler`, even though D365 processed them
successfully. `QueueMessageQueueBusinessEventHandler` reads the wrong field: it maps the outcome
from `MessageState`, but in a real `SysMessageProcessorMessageProcessedBusinessEvent` payload for a
*successful* message `MessageState` is the queue-lifecycle state (`"Queued"`) while the terminal
outcome lives in `MessageProcessingResult` (`"Processed"`). Success events therefore fall through
the `_ => null` arm, are logged as "non-terminal state", completed, and discarded.

Failures are unaffected: a dev2 log stream on 2026-08-28 shows
`MessageState=Failed -> STATUS=3`, so `MessageState` does carry the outcome on the failure path and
that path reconciles today. The defect is specific to success — which is why it presents as
"D365 created the order and the dashboard says Dead Letter".

The handler also silently `CompleteMessageAsync`es on six different give-up paths with only
information-level logs, so the failure is invisible to operators — there is no counter, no
processing step, and no dead-letter, which is why this went undiagnosed.

Separately, the same handler only notifies the source system on failure: the response-queue send
is guarded by `if (status == 3 ...)`. Even once reconciliation works, a successfully created sales
order produces no `Status = "Success"` message back to Capture and never returns the `SalesId`.
The synchronous path (`BaseCrudOrchestration`) does send a success envelope, so the asynchronous
path is silently the odd one out.

## What Changes

- Map the reconciliation outcome from `MessageProcessingResult` (not `MessageState`).
  `Processed` → `2` (Completed); `Failed` / `Cancelled` / `Error` → `3` (Failed). Captured success
  and failure payloads both confirm this field is populated, so `MessageState` is retained only as
  diagnostic context on the processing step.
- Treat non-terminal events (`MessageProcessingResult` absent or not one of the mapped values)
  as an acknowledged progress signal: log a processing step against the correlated row and
  complete the message, leaving `STATUS = 5` untouched.
- Extend `SysMessageBusinessEventsContract` with the fields the real payload carries and the
  operator actually needs: `MessageProcessingResult`, `MessageProcessingResultLabel`,
  `MessageStateLabel`, `MessageResponseKey`, `MessageResponseKeyFieldName`, `EventId`,
  `EventTimeIso8601`, `MessageProcessorMessagePageUrl`. Enable
  `JsonNumberHandling.AllowReadingFromString` so a quoted `CreatedTransactionId` still binds.
- Persist the D365 business key (`MessageResponseKey`, e.g. `SalesId = "200007"`) on the
  correlated row so operators can find the order in D365 without decoding a transaction id.
- Send a success response to the row's `RESPONSEQUEUE` on successful reconciliation, carrying the
  `SalesId` and the transaction id, mirroring the envelope shape the synchronous path already
  emits. Fix the failure envelope at the same time: it currently hardcodes `Entity = "SalesOrder"`
  in a handler that is otherwise entity-agnostic, and omits `Action`.
- Guard the status write so reconciliation is monotonic: a late or duplicate event cannot
  regress a row that already reached `2`, and cannot overwrite `3` with `2`. Escalated
  Dead Letter (`4`) rows remain correctable, as the current spec requires.
- Make every give-up path observable: unparseable body, unregistered `BusinessEventId`, missing
  `CreatedTransactionId`, and unmatched `DESTINATIONID` are logged at Warning with the raw body
  truncated, and unmatched correlations are dead-lettered with a reason instead of completed,
  so they are recoverable rather than lost.
- Add unit tests over the outcome mapping and payload binding using the real captured payload as
  a fixture. There are currently zero tests for this handler.

Not included: changing which queue the trigger listens on, or the D365-side business event
subscription. If the handler is producing no `[BusinessEventTrace]` log at all, that is a
delivery/configuration problem; this change adds the diagnostics needed to distinguish
"not delivered" from "delivered and dropped", and task 1 verifies delivery before the code
change is judged.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `d365-business-event-reconcile`: the terminal-outcome source field changes from `MessageState`
  to `MessageProcessingResult`; non-terminal events become an explicit acknowledged case; status
  updates become monotonic; the D365 response key is recorded; successful reconciliation now
  notifies the source system on its response queue; and give-up paths gain required observability
  and dead-lettering behaviour.

## Impact

- `ARM.Integration.EIM.Customer/QueueMessageQueueBusinessEventHandler.cs` — outcome mapping,
  observability, dead-lettering, response-key persistence, success response-queue notification.
- `ARM.Integration.EIM/Model/D365FSC/SysMessageBusinessEventsContract.cs` — new fields, number
  handling.
- `ARM.Integration.EIM/Controller/EIMController.cs` — `UpdateStatusByDestinationId` gains a
  monotonic status guard and an optional response-key write.
- `ARM.Integration.EIM.Tests` — new `BusinessEventReconcileTests` plus a captured payload fixture.
- No schema change: `MESSAGEQUEUETABLE.DESTINATIONID` and `MESSAGELOG` already exist and the
  response key is written into existing columns.
- **BREAKING for downstream consumers of `CaptureToD365QueueResponse`**: Capture will begin
  receiving a `Status = "Success"` message per reconciled order where previously it received only
  failures. Confirm Capture tolerates it before deploying.
- Operational: rows currently stuck at `5`/`4` for orders D365 already processed will need a
  one-off replay or manual correction; the change does not backfill them.
