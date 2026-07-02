## Context

The existing Capture → D365 F&SC pipeline (`QueueCaptureCustomer` → `QueueCaptureHandler` →
`EIMController` → `*_To_D365FSC` orchestration) is **synchronous**: an orchestration performs an
OData CRUD call and, on return, `EIMController.ProcessMessageAsync` stamps
`MESSAGEQUEUETABLE.STATUS = 2 (Completed)`.

Sales orders do not fit this model. They are submitted to a D365 message queue via the custom
service action `WTC_Integration/WTC_SysMessageService/SendMessageWithResponse`, which returns only a
`CreatedTransactionId`. The actual order creation happens later in a D365 batch, so the true
outcome (`Processed` / `Failed` / `Cancelled`) arrives asynchronously via the D365 business event
`SysMessageProcessorMessageProcessedBusinessEvent`.

Relevant existing building blocks:
- `D365FSCClient.CallServiceAsync(method, payload)` — generic POST to a custom service action.
- `MessageQueueContract` — already models `_companyId` / `_messageQueue` / `_messageType` / `_messageContent`.
- `MESSAGEQUEUETABLE.DESTINATIONID` (nvarchar(150)) — unused, already an `SPINSERTMESSAGEQUEUE` param.
- `QueueDataEventsHandler` — precedent for a Service Bus trigger that routes D365 events by a key.
- Status convention: `0 New, 1 Processing, 2 Completed, 3 Failed, 4 Dead Letter`.

## Goals / Non-Goals

**Goals:**
- Submit Capture sales orders to D365 via the service action, forwarding the order body verbatim.
- Represent the async lifecycle with a dedicated `5 = Submitted / Awaiting D365` status and persist
  the `CreatedTransactionId` for later correlation.
- Reconcile final status from D365 business events (push), not polling.
- Provide a safety net for events that never arrive.
- Make the orchestration contract able to express non-terminal status + correlation id (reusable
  foundation for future async integrations).

**Non-Goals:**
- Strongly-typed modeling of the sales-order body (pass-through only; no field mapping).
- Forwarding the D365 result back to Capture (planned as a separate future change).
- Replacing the existing synchronous CRUD integrations' behavior (they keep completing at `2`).
- Timer-based `GetMessageState` polling (kept only as an optional manual/fallback tool).

## Decisions

### D1. Submit via a service-action orchestration, not CRUD
Introduce `D365ServiceActionOrchestration<TSource>` extending `BaseCrudOrchestration<TSource,
MessageQueueContract>`; `ExecuteActionAsync` calls `CallServiceAsync("SendMessageWithResponse", body)`.
`SalesOrder_To_D365FSC` derives from it.
*Alternative considered:* overriding `D365CrudOrchestration.ExecuteActionAsync` — rejected, its CRUD
key/verb logic is irrelevant and would be dead code. A dedicated base is reusable for future
`WTC_SysMessageService` message types.

### D2. Option A — lift `dataAreaId` out of `Data`
`_companyId = Data.dataAreaId`; remove `dataAreaId` from the object; `_messageContent =` the
remaining object serialized. Reproduces the confirmed sample content (which has no `dataAreaId`).

### D3. Option 2 (Fork 2) — pass-through `JsonNode`, not a typed contract
No field mapping is required; the body (~40 header fields + `Line[]`/`TradeAllowance[]`) is wrapped
and forwarded. `JsonNode` keeps EIM immune to Capture schema additions and reproduces the payload
exactly. *Alternative:* full typed `CaptureSalesOrderMessageContract` — rejected as high-maintenance
with no mapping benefit.

### D4. Option C — `OrchestrationResult` return type (breaking, deliberate)
`IOrchestration.ProcessAsync` returns `OrchestrationResult { int Status = 2; string Message;
string? DestinationId; }`. `EIMController` honors `result.Status` (instead of hard-coding `2`) and
persists `result.DestinationId` into `DESTINATIONID`. Existing orchestrations wrap their payload in a
`Completed (2)` result, preserving current behavior. The reflection-fallback dispatch branch is
updated to adapt legacy `string` returns.
*Alternative:* a marker interface `IDeferredOrchestration` — rejected; chosen the explicit result
type as the correct long-term foundation.

### D5. Status `5 = Submitted / Awaiting D365`
`SalesOrder_To_D365FSC` returns `Status = 5`, `DestinationId = CreatedTransactionId`. This
distinguishes "handed to D365" from "confirmed processed".

### D6. Reconcile via business events, routed by `BusinessEventId`
New Service Bus trigger on `%D365BusinessEventQueue%` (`[env]-d365fsc-businessevent`) + handler that
routes by `BusinessEventId` (mirrors `QueueDataEventsHandler`). For
`SysMessageProcessorMessageProcessedBusinessEvent`, it correlates `CreatedTransactionId → DESTINATIONID`
and updates the existing row — it does **not** insert a new message. Mapping: `Processed → 2`,
`Failed`/`Cancelled → 3` (log `MessageInfolog`). The update is idempotent and not gated on the row
currently being `5`, so late/duplicate events still reconcile correctly.

### D7. Stuck-message sweeper
A timer function escalates rows where `STATUS = 5 AND MODIFIEDDATETIME < now - {StuckTimeout}` to
`STATUS = 4 (Dead Letter)` and alerts. Default `StuckTimeout = 4h`, sweep every 15–30 min, both
configurable. Escalation is non-terminal: a late business event can still correct a swept row.

### D8. Correlation storage & indexing
`CreatedTransactionId` (confirmed globally unique) is stored in `DESTINATIONID`. Add an index on
`DESTINATIONID` to support `UpdateStatusByDestinationId` lookups.

## Risks / Trade-offs

- Lost/unpublished business event → order stuck at `5`. → Sweeper escalates + alerts; `GetMessageState`
  retained as a manual reconcile fallback.
- At-least-once event delivery (duplicates/out-of-order). → Idempotent, status-agnostic update.
- `OrchestrationResult` is a breaking interface change touching every orchestration. → Default
  `Status = 2` and a legacy-return shim keep existing integrations behavior-identical; covered by build.
- Dashboard shows unknown status `5`. → Add an "Awaiting D365" label in the dashboard repository/UI.
- Pass-through forwards Capture bytes verbatim (no normalization). → Acceptable; D365 is the validator
  and returns `MessageInfolog` on rejection.
