## Context

`SalesOrder_To_D365FSC` (via `D365ServiceActionOrchestration`) submits an order to the D365 F&SC
message processor and returns `OrchestrationResult.Submitted(response, createdTransactionId)`.
`EIMController.UpdateMessageStatus` writes `STATUS = 5` (AwaitingD365) and stores the
`CreatedTransactionId` in `MESSAGEQUEUETABLE.DESTINATIONID` — the correlation key, backed by
`IX_MESSAGEQUEUETABLE_DESTINATIONID`.

D365 later raises `SysMessageProcessorMessageProcessedBusinessEvent` onto
`%D365BusinessEventQueue%`. `QueueMessageQueueBusinessEventHandler` consumes it and is supposed to
move the row 5 → 2 or 5 → 3. `AwaitingD365SweepHandler` escalates rows left at 5 past
`AwaitingD365StuckTimeoutHours` to 4 (Dead Letter).

A captured production event body shows the defect precisely:

```json
{
  "BusinessEventId": "SysMessageProcessorMessageProcessedBusinessEvent",
  "CreatedTransactionId": 186021,
  "MessageState": "Queued",
  "MessageStateLabel": "Queued",
  "MessageProcessingResult": "Processed",
  "MessageProcessingResultLabel": "Processed",
  "MessageInfolog": "Transfer order 200007 created ... has been created.",
  "MessageResponseKey": "200007",
  "MessageResponseKeyFieldName": "SalesId",
  "MessageQueue": "CaptureIntegration",
  "MessageType": "CaptureCreateSalesOrder",
  "EventId": "788D6D3D-...",
  "EventTimeIso8601": "2026-08-28T00:02:20.1697223Z",
  "EventTime": "/Date(1787875340000)/",
  "MessageProcessorMessagePageUrl": "https://.../?mi=display:SysMessageProcessorMessage&q=..."
}
```

The handler switches on `MessageState`. On a *successful* message `MessageState` is the queue
lifecycle position and reads `"Queued"`; the terminal outcome is `MessageProcessingResult`.
`"Queued"` matches no arm, so `status` is `null`, the handler logs "non-terminal state" at
Information, completes the message, and returns. Success events are discarded. The row stays at 5
until the sweep escalates it to 4, so orders D365 created successfully surface to operators as
Dead Letter.

Failures behave differently, and this matters. A dev2 log stream line on 2026-08-28 reads:

```
[BusinessEventTrace] Correlating BusinessEventId=SysMessageProcessorMessageProcessedBusinessEvent
  CreatedTransactionId=186013 MessageState=Failed -> STATUS=3
```

So `MessageState` *does* carry the outcome on the failure path, and failure reconciliation works
today. The defect is specific to success. It also confirms delivery, parsing and correlation all
function — the handler is reached and gets as far as deriving a status.

The corresponding failure payload was captured shortly after and shows both fields agreeing
(`MessageProcessingResult` and `MessageState` are each `"Failed"`), with
`MessageInfolog = "Cannot create a record in PBL table (WTC_PBLTable)... The record already
exists."`. Two further details matter: `MessageResponseKey` and `MessageResponseKeyFieldName`
arrive as **empty strings** rather than being omitted, so a null check alone is not enough; and
`DESTINATIONID` was confirmed populated on real rows, so correlation is sound.

Two aggravating factors made this expensive to find. First, all six give-up paths call
`CompleteMessageAsync` and log at Information or Warning with no processing step and no
dead-letter, so nothing in the dashboard or the queue shows an event was received and dropped.
Second, `MessageInfolog` is populated on success as well as failure, so its presence is not a
failure signal.

The event body also confirms `CreatedTransactionId` arrives as a JSON number, `EventTime` uses
the legacy `/Date(ms)/` form, and the payload carries roughly two dozen fields the current
contract does not model.

A second, independent gap sits in the same handler. The response-queue send is guarded by
`if (status == 3 && !string.IsNullOrWhiteSpace(awaiting.ResponseQueue))`, so only failures are
reported back to Capture. `BaseCrudOrchestration` — the synchronous path — sends a
`Status = "Success"` envelope on the happy path, so the asynchronous path is the odd one out.
Even with the mapping fixed, Capture would see a sales order vanish into D365 and never learn the
`SalesId`. The same block hardcodes `Entity = "SalesOrder"` in a handler that is otherwise
entity-agnostic, and omits `Action`.

## Goals / Non-Goals

**Goals:**

- Reconcile from the correct field so a processed D365 message reaches status 2 and a failed one
  reaches status 3.
- Make every give-up path visible in logs and, where the event is recoverable, on the queue's
  dead-letter sub-queue rather than silently completed.
- Preserve the D365 business key (`SalesId`) so an operator can find the order in D365.
- Close the loop with the source system: report success to `RESPONSEQUEUE`, not just failure.
- Prevent late or duplicate events from regressing an already-reconciled row.
- Cover the mapping and payload binding with tests using the captured payload as a fixture, since
  this handler currently has none.

**Non-Goals:**

- Changing the D365-side business event subscription, the queue name, or the trigger binding.
- Backfilling rows already stuck at 5 or wrongly escalated to 4.
- Fixing the `MODIFIEDDATETIME` trigger defects (server-local time; single-row stamping on
  multi-row updates). Those are real and affect the sweep's cutoff, but they are already folded
  into `add-entity-hold-release` Decision 20 and are out of scope here.
- Adding an end-to-end correlation id across queue → row → D365 call → event. Tracked separately.
- Adding a `RESPONSEKEY` column. See Decision 4.

## Decisions

### Decision 1: Map the outcome from `MessageProcessingResult` only

`MessageProcessingResult` ∈ `{Processed, Failed, Cancelled, Error}` → `{2, 3, 3, 3}`. Matching is
`Trim()` + `OrdinalIgnoreCase`. `MessageState` is recorded on the processing step for diagnosis
but never drives status.

Both captured payloads confirm the field is populated on both terminal outcomes:

| | `MessageProcessingResult` | `MessageState` |
| --- | --- | --- |
| Success (txn 186021) | `Processed` | `Queued` |
| Failure (txn 186013) | `Failed` | `Failed` |

*Alternative considered — fall back to `MessageState` when `MessageProcessingResult` is not
terminal.* Implemented briefly, then removed once the failure payload landed. The fallback was
insurance against failures carrying their outcome only on `MessageState`, which the failure
payload disproves. Keeping it would have added a real hazard: a hypothetical in-flight event with
`MessageState = "Processed"` and an empty result would mark the row Completed before D365 had
finished. Insurance against a disproven risk is not worth a live one.

*Alternative considered — read `MessageState` and treat `Queued` as terminal-success.* Rejected:
`Queued` genuinely is non-terminal, and D365 emits the event more than once across a message's
lifetime. Treating it as success would mark rows Completed before D365 finished.

*Alternative considered — use `MessageProcessingResultLabel`.* Rejected: `*Label` fields are
localised display strings. The unlabelled field is the enum name and is stable.

### Decision 2: Non-terminal events are acknowledged, not dropped

An event with no recognised `MessageProcessingResult` is completed (so Service Bus does not
redeliver it) but writes an Information processing step against the correlated row and leaves
status at 5. An *unrecognised* (non-empty, unmapped) value additionally logs at Warning, because
that means D365 grew a state the handler does not know about.

*Alternative considered — abandon the message so it is retried.* Rejected: the outcome will not
change on redelivery; it would spin until the delivery count is exhausted and then dead-letter
a perfectly normal progress event.

### Decision 3: Correlation misses dead-letter; parse failures dead-letter; unknown event ids complete

Three classes of give-up, three treatments:

| Case | Treatment | Why |
| --- | --- | --- |
| Body is not valid JSON, or `CreatedTransactionId` missing/zero | `DeadLetterMessageAsync` with reason | Unrecoverable in code but the payload must be inspectable |
| `CreatedTransactionId` matches no `DESTINATIONID` | `DeadLetterMessageAsync` with reason | Almost always means the submit side never stored the id — the operator needs the event kept |
| `BusinessEventId` is one we do not handle | `CompleteMessageAsync` + Information log | Expected and high-volume if the subscription is broad; dead-lettering would flood the DLQ |

All three log the message id, `BusinessEventId`, correlation id where known, and the raw body
truncated to a bounded length (2 KB) — the captured payload's `MessageContent` alone is ~9 KB and
`MessageProcessorMessagePageUrl` is another ~1 KB, so logging the body whole is not viable.

*Alternative considered — dead-letter everything unhandled.* Rejected, per the table's third row.

*Alternative considered — keep completing correlation misses (current behaviour).* Rejected: this
is exactly the class of failure that has to survive for diagnosis, and Service Bus DLQ is the
cheapest durable place to keep it.

### Decision 4: Record the response key in `MESSAGELOG` and a processing step, not a new column

`MESSAGELOG` is `nvarchar(max)` and already carries the reconciliation text. On completion it
becomes `D365 Processed. SalesId=200007 (TransactionId=186021)`; a processing step records the
same. No migration, and the value is visible in the dashboard's existing message-log and
processing-steps views.

`DESTINATIONID` is explicitly **not** overwritten with the sales order number. It is the
correlation key, it is indexed for that purpose, and D365 may emit further events against the
same transaction id.

*Alternative considered — add a `RESPONSEKEY` column.* Deferred. It is the cleaner model and makes
the sales order searchable, but it needs a migration, a `QueueMessageContract` field, an API
change and a frontend column. Revisit if operators ask to search by `SalesId`.

### Decision 5: Monotonic status guard inside the SQL UPDATE

`UpdateStatusByDestinationId` gains a `WHERE` predicate so the transition is decided by the
database, not by a read-then-write in the handler. The handler may run concurrently with the
sweep and with a second event for the same transaction id, and `maxConcurrentCalls` is a host
setting that can be raised.

Permitted transitions to a terminal status: from `5` (AwaitingD365), `4` (Dead Letter — the spec
requires escalated rows stay correctable), `7` (RetryScheduled), `1` (Processing) and `0` (New).
Blocked: `2` and `3` are terminal and are never rewritten by a later event.

```
WHERE DESTINATIONID = @DESTINATIONID AND STATUS NOT IN (2, 3)
```

Zero rows affected is not an error. The handler distinguishes the two zero-row cases it can
already tell apart — no row matched the `DESTINATIONID` at all (dead-letter, Decision 3) versus
the row exists but is already terminal (Warning + processing step, message completed) — by
resolving the row via `GetMessageIdByDestinationId` first, which it already does.

*Alternative considered — read status in the handler and branch.* Rejected: races with the sweep.

*Alternative considered — allow `2` → `3`.* Rejected as a silent data-quality problem. If D365
sends a `Failed` after a `Processed` for the same transaction id, that contradiction should be
raised to an operator, not resolved automatically. It is logged at Warning.

### Decision 6: Extend the contract; do not model the whole event

Add only the fields with a use: `MessageProcessingResult`, `MessageProcessingResultLabel`,
`MessageStateLabel`, `MessageResponseKey`, `MessageResponseKeyFieldName`, `EventId`,
`EventTimeIso8601`, `MessageProcessorMessagePageUrl`. `EventTime` is left unmodelled — the
`/Date(1787875340000)/` form does not bind to `DateTime` and `EventTimeIso8601` carries the same
instant in a usable format. `MessageContent` is left unmodelled: it is the ~9 KB echo of the
submitted payload, already stored on the row.

`[JsonNumberHandling(JsonNumberHandlingAttribute.AllowReadingFromString)]` is applied to
`CreatedTransactionId`. The captured payload sends it as a number, but D365 serialisation of
64-bit values is not guaranteed stable across versions and the current code fails closed on a
quoted value.

### Decision 8: Send a response on both outcomes, after the status write, best-effort

The `status == 3` guard is dropped. Both outcomes build an `OutboundMessageEnvelope<JsonObject>`
and send it to `awaiting.ResponseQueue` when that is non-empty:

```
SourceId      = awaiting.SourceId
Action        = "Reconcile"
Entity        = awaiting.SourceEntity ?? awaiting.DestinationEntity
Status        = "Success" | "Failed"
StatusMessage = evt.MessageInfolog          // populated on success too, so always carried
Data          = { CreatedTransactionId, <MessageResponseKeyFieldName>: <MessageResponseKey> }
```

`Entity` comes from the row instead of the current hardcoded `"SalesOrder"`, because the handler
is keyed on `BusinessEventId` and will reconcile every D365 message type routed through
`SysMessageProcessor`, not just sales orders. `MessageResponseKeyFieldName` is used as the `Data`
property name so the source system receives `{"SalesId": "200007"}` rather than a generic key it
has to interpret.

Ordering: the status write happens first, then the send. The row is the system of record and must
reflect D365 even if Service Bus is unavailable. The send is wrapped so a failure logs at Error,
writes a processing step, and leaves the status alone — the existing behaviour on the failure
path, now applied to both.

*Alternative considered — send first, then update status.* Rejected: a send that succeeds followed
by a failed status write leaves the row at 5 and the sweep escalates a row Capture has already been
told succeeded. That is the worse of the two inconsistencies.

*Alternative considered — abandon the business event when the response send fails, so it retries.*
Rejected: the status write is already committed and is guarded monotonically (Decision 5), so a
redelivery would not re-send the response anyway — it would land on the already-terminal branch.
The message is completed and the failure is surfaced as an Error log and a processing step for an
operator to replay manually.

*Alternative considered — reuse `BaseCrudOrchestration`'s send.* Rejected: that code runs inside
the orchestration during synchronous processing and has no access to the business event; the
asynchronous confirmation arrives in a different process invocation entirely.

### Decision 7: Test against the captured payload, not a synthetic one

The verbatim event body is checked in as `TestData/SysMessageProcessorMessageProcessedBusinessEvent.json`.
The bug was that a hand-written mental model of the payload disagreed with the real one; a
synthetic fixture would reproduce the same mistake. Outcome mapping is extracted into a pure
static function so it can be tested without Service Bus or SQL.

## Risks / Trade-offs

- **The handler may not be receiving events at all.** → **Resolved 2026-08-28**: a dev2 log stream
  line proves delivery, parsing and correlation all work. Task 1.1 is satisfied.
- **A failure payload has never been captured.** → **Resolved 2026-08-28**: captured and checked in
  as a second fixture. Failures populate `MessageProcessingResult`, so Decision 1 reads that field
  alone.
- **New dead-lettering could fill the DLQ** if `DESTINATIONID` is never populated (i.e.
  `ExtractCreatedTransactionId` does not match the shape of the `SendMessageWithResponse`
  response). → **Resolved 2026-08-28**: `DESTINATIONID` confirmed populated on real rows.
- **Rows already wrongly escalated to 4 stay wrong.** → Out of scope by choice; the spec keeps 4
  correctable, so a replay of retained events would fix them. Call it out at handover.
- **`Error` is a guessed member of the D365 result enum.** → Unrecognised values are handled
  safely (Decision 2) rather than crashing, so a wrong guess degrades to a Warning, not a failure.
- **Monotonicity means a genuine `Processed`-then-`Failed` sequence is not applied.** → Deliberate
  (Decision 5); logged at Warning for an operator.
- **Capture starts receiving success messages it has never received before.** If it treats an
  unexpected `Status = "Success"` on `CaptureToD365QueueResponse` as an error, this turns a silent
  gap into a visible one on their side. → Confirm with the Capture owner before deploying; the
  envelope deliberately matches the shape `BaseCrudOrchestration` already sends for synchronous
  flows, so it should be a shape Capture already handles.
- **A success response could be sent twice** if two `Processed` events arrive for one transaction
  id. → The monotonic guard means the second event finds the row already at `2` and takes the
  already-terminal branch (task 5.8), which does not send.
- **The sweep's cutoff still reads a column stamped in server-local time by a trigger.** → Known,
  tracked in `add-entity-hold-release`; it changes *when* escalation happens, not whether
  reconciliation works, so it does not block this fix.

## Migration Plan

No schema change, no configuration change, no data migration. Deploy the Functions host; the fix
takes effect on the next business event. Rollback is a redeploy of the previous build — the only
persisted difference is richer `MESSAGELOG` text and additional `MESSAGELOGDETAIL` rows, both of
which the old build tolerates. Events dead-lettered by the new build remain on the DLQ and can be
resubmitted after a rollback.

The one externally visible change is the new success message on `RESPONSEQUEUE` (Decision 8).
That is not rolled back by a redeploy — messages already sent stay sent — so the Capture-side
confirmation in the Open Questions is a deploy gate, not a follow-up.

## Open Questions

- Does the D365 `SysMessageProcessorMessageProcessingResult` enum have members beyond
  `Processed`, `Failed`, `Cancelled`? Confirm with the D365 team so the mapping is exhaustive
  rather than inferred.
- Does Capture currently consume `CaptureToD365QueueResponse`, and does it tolerate a
  `Status = "Success"` envelope it has never received on this flow? Blocking for Decision 8.
- Does D365 emit this event more than once per message (for example on `Queued` and again on
  `Processed`)? The design assumes it may. Confirming it changes only the expected DLQ/log volume.
- ~~Is `DESTINATIONID` actually populated today?~~ **Confirmed populated, 2026-08-28.**
