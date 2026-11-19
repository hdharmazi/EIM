## Context

**Current flow** (spec `capture-salesorder-submit`):

1. Capture → Service Bus → `CaptureMessageProcessor` → `IngestGate`/`HoldGate` → route `CAPTURE/SalesOrderQueue` → `SalesOrder_To_D365FSC`.
2. `SalesOrder_To_D365FSC` derives from `ARM.Integration.EIM/Orchestration/D365ServiceActionOrchestration.cs`, which wraps `Data` into a `MessageQueueContract` and POSTs it to `WTC_SysMessageService/SendMessageWithResponse`. `D365FSCClient.CallServiceAsync` is called through `CreateStandaloneClient`, with no Polly retry.
3. The orchestration returns `OrchestrationResult.Submitted` (status 5) with `DESTINATIONID = CreatedTransactionId`.
4. D365 batch: `WTC_CaptureSalesOrderMessage` calls `WTC_CaptureSalesOrderProcessor::main`, which deserializes the JSON with `FormJsonSerializer` into `WTC_CapturePBLTableContract` and inserts `WTC_PBLTable`, `WTC_PBLLine` and `WTC_PBLTradeAllowance`. It then creates items/projects and the sales order(s), and transfers, in one transaction. The `SalesId` is written to `SysMessage.WTC_MessageResponseKey`.
5. The business event arrives on `<prefix>-d365fsc-businessevents`. `BusinessEventReconciler` sets status 2 or 3 and sends the only reply Capture ever gets (`BusinessEventResponse.Build`). `AwaitingD365SweepHandler` moves rows stuck at 5 for more than 4 hours to status 4, with no reply.

**Problems:**
- The reply depends on a batch run plus a business event.
- `MessageInfolog` is often empty (D365 change `surface-capture-processing-errors` addresses that).
- There is no reply on submit failure or a missing event.
- There is no idempotency: a retried submit inserts a duplicate `PBLId` and fails.
- There are no tests for the orchestration.

**D365 side:** the coordinated change `add-capture-salesorder-direct-service` in the D365 repo (`<D365 repo>/openspec/changes/`) adds a service:
- Endpoint: `WTC_Integration/WTC_CaptureSalesOrderService/CreateSalesOrder(CompanyId CompanyId, WTC_CapturePBLTableContract SalesOrder)`.
- It runs the same processor logic synchronously under `changecompany`, is idempotent on the original `PBLId`, and returns a result contract.

## Goals / Non-Goals

**Goals:**
- One synchronous call per order.
- A final Wilson status and a Capture reply in the same run.
- Real error text when D365 rejects an order.
- Safe retries and reprocessing.

**Non-Goals:** see proposal. Revise and Split are out; the reconciler and sweep are left in place; there are no route, SQL or deploy changes.

## Decisions

### D1. Rewrite `SalesOrder_To_D365FSC` as a direct `IOrchestration`; keep its name and registry key

`SalesOrder_To_D365FSC` stops deriving from `D365ServiceActionOrchestration` and implements `IOrchestration` itself, as `BaseCrudOrchestration` does.

Because the class name, registry key (`OrchestrationRegistry`) and route seed are unchanged, there is:
- no SQL,
- no `OrchestrationRegistry` change,
- no `RouteRegistryValidator` impact.

`D365ServiceActionOrchestration` stays untouched, with no subclasses, for future async message types (Revise/Split).

**Alternatives rejected:**
- *A new generic engine base class `D365SyncServiceOrchestration` in `ARM.Integration.EIM`.* There is only one consumer. The reply envelope and result parsing are specific to Capture/WTC, and `EngineBoundaryTests` keeps client specifics out of the engine.
- *A new orchestration name/route (`SalesOrderDirect_To_D365FSC`) with a seed migration so the routes could be flipped.* It adds SQL, a registry entry and PRD seeds for a rollback that redeploying the previous package already gives.
- *A config switch between the old and new paths.* It adds a `deploy.base.json` key and production-config surface, and the old D365 path keeps working for rollback anyway.

### D2. Request shape: pass-through `Data` as a typed D365 contract object

The request body is:

```json
{ "CompanyId": "<Data.dataAreaId>", "SalesOrder": { ...Data without dataAreaId... } }
```

- `SalesOrder` is the envelope `Data` object minus `dataAreaId`, forwarded verbatim (no field mapping). It includes `Line[]` and `TradeAllowance[]`, with the same JSON keys Capture sends today.
- D365 binds it to `WTC_CapturePBLTableContract` using the `DataMember` names. Those are the keys `FormJsonSerializer` already uses, so no renames are needed.
- A missing or blank `dataAreaId` throws `JsonException("Envelope Data.dataAreaId is missing.")` before any call. That is terminal.
- A missing `Data` object is terminal (`JsonException`), as today.

**Alternative:** `SalesOrderJson` as a JSON string that D365 deserializes with `FormJsonSerializer`, exactly as today. It is the lowest parsing risk but loses the typed service contract the request asks for. It is kept as the fallback if dev02 verification shows the service serializer rejects Capture's formats (see Risks). Switching is a one-line change to the body builder plus the D365 signature.

### D3. Response contract and outcome mapping

D365 returns HTTP 200 with:

```json
{ "Success": true, "PBLId": "PBL-1", "SalesId": "200007", "SalesIds": ["200007"],
  "AlreadyProcessed": false, "ErrorMessage": "" }
```

It may also include the `$id` and other members D365 adds; Wilson ignores unknown members. Parse case-insensitively with `System.Text.Json`.

| D365 outcome | Wilson status | DESTINATIONID | Capture reply |
|---|---|---|---|
| 200, `Success=true` (new or `AlreadyProcessed`) | 2 Completed (`OrchestrationResult.Completed(response, SalesId)`) | `SalesId` | Success |
| 200, `Success=false` | 3 Failed via `D365ServiceRejectedException(ErrorMessage)` | — | Failed (`StatusMessage = ErrorMessage`) |
| 200, `Success=true` but blank `SalesId` (defensive; D365 should never send it) | 3 Failed via `JsonException` | — | Failed ("D365 reported success without a SalesId") |
| 200, unparsable body or no `Success` member | 3 Failed via `JsonException` | — | Failed ("Unrecognised D365 response") |
| `D365FSCException` 401, 403, 404 (setup problem: credentials, privilege, service not deployed) | 3 (existing classifier) | — | **none** (D8: we fix the setup and reprocess; the reprocess sends the reply) |
| `D365FSCException` other 4xx (not 408/429) | 3 (existing classifier) | — | Failed |
| `D365FSCException` 5xx/408/429 (includes D365 database conflicts still failing after its own retries, which it now rethrows as HTTP 500), `HttpRequestException`, `TimeoutException`, `TaskCanceledException` from a timeout | 7 RetryScheduled, then 4 when exhausted (existing) | — | **none** (retry will reply) |

`DESTINATIONID = SalesId` cannot be confused by `BusinessEventReconciler`. Its `UpdateStatusByDestinationId` only touches rows `NOT IN (2,3)`, and these rows are always 2 or 3.

### D4. Capture reply sent by the orchestration (same envelope as today)

The reply is an `OutboundMessageEnvelope<JsonObject>`:
- `SourceId`, as today.
- `Action = "Reconcile"`, which keeps the action Capture receives today for sales orders.
- `Entity = SourceEntity ?? DestinationEntity`.
- `Status = "Success" | "Failed"`, with `StatusMessage`.
- `Data = { "SalesId", "SalesIds", "PBLId", "AlreadyProcessed" }`. On failure, `Data` is `{ "PBLId" }` only.

It is sent to `data.ResponseQueue` via `ServiceBusHelper` on the **source** environment connection, as in `BaseCrudOrchestration`. `CreatedTransactionId` is no longer included because there is no SysMessage (open question Q2).

Sending is **best-effort**: a reply failure is logged with `stepLog.LogError("SendResponse", …)` and does not change the outcome. A success reply failure must not turn a created order into a failed message.

A Failed reply is sent only for *definite terminal* outcomes that concern the order itself:
- `D365ServiceRejectedException`,
- `JsonException`,
- `D365FSCException` with a non-retryable status, **except** 401, 403 and 404 (D8), and except a `D365FSCException` whose `ODataErrorCode` is in the configured `RetryableODataErrorCodes` (the classifier retries those).

This is decided by a small private helper in the orchestration that mirrors `DefaultFailureClassifier`'s terminal rules. Transient failures get no reply, so Capture never sees "Failed" followed by "Success".

Build the envelope in a pure static method so it can be unit tested without Service Bus.

### D8. No Capture reply for setup failures (401, 403, 404)

401/403 mean D365 refused Wilson's credentials or permissions (expired secret, missing privilege). 404 on this endpoint means the service isn't deployed or isn't reachable. These are integration setup problems, not order problems, and a Capture user can't fix them.

- **Status is unchanged:** the message still goes to status 3 through the existing classifier, so it is visible on the Dashboard and can be reprocessed.
- **Reply is held back:** Wilson sends nothing to the response queue. After we fix the setup and reprocess, the normal reply goes out, so Capture gets exactly one reply per order. Capture never sees "Failed" followed by "Success", which could lead a user to re-key the order under a new `PBLId` and create a duplicate.
- **Trade-off:** during a setup outage Capture just waits, so we must notice it. Three pieces handle that:
  - **Log event (this change):** the orchestration writes a dedicated step `stepLog.LogError("SetupFailure", "D365 returned <status> for PBLId <id>; reply held until reprocess")`, only for 401/403/404. `OrchestrationLogger` forwards it to App Insights as a trace whose message starts with `[SetupFailure] `. (The isolated worker logs through the Functions host, so only the formatted message survives. Bat traces carry no `StepName` dimension.) It is distinct from the generic `SendResponse: Skipped` line that transient retries also write, so an alert on it does not fire on ordinary retries.
  - **Alert (manual go-live step, documented in `docs/deployment/production-go-live.md` §7):** a log search alert on the Automation Function App's Application Insights. The query counts `traces` where `message startswith '[SetupFailure] '` over 15 minutes, evaluates every 5 minutes, fires when the count is above 0, and notifies the production action group.
  - **Runbook (same doc):**
    - 401: check the D365 app registration secret.
    - 403: grant the `WTC_CaptureSalesOrderServiceMaintain` privilege.
    - 404: deploy the D365 service.
    - Then reprocess the status-3 `SalesOrderQueue` messages from the Dashboard.
- **Alternative rejected:** making 401/403/404 retryable in `DefaultFailureClassifier`. That changes retry behaviour for every route, and retrying can't fix a credential or deployment problem anyway.

### D5. `D365ServiceRejectedException` is terminal

- New `ARM.Integration.Core/Helpers/D365ServiceRejectedException.cs`, next to `MissingEntityKeyException`.
- Members: `public string ServiceMethod`, `public string? BusinessKey` (the PBLId). The message is the D365 `ErrorMessage`, or "D365 rejected the request." if it is blank.
- Add it to `DefaultFailureClassifier.IsPositivelyTerminal`. Unknown exceptions already fall to `Terminal`, but the convention requires explicit classification and a test.

The rejection is a business validation result (invalid customer, route, existing unprocessed PBLId). Retrying cannot help.

### D6. Idempotency and retries

- **Idempotency is owned by D365.** If an original `WTC_PBLTable` row for the `PBLId` exists and is processed, D365 returns `Success=true, AlreadyProcessed=true` with the existing SalesId(s). This makes the following safe:
  - the existing retry sweep (`RetrySweepHandler`, up to `MaxAttempts`),
  - a timeout where D365 finished after Wilson gave up,
  - Dashboard reprocess.
- **Wilson keeps its own dedupe.** `IngestGate` still rejects a Service Bus redelivery once the row is past status 0.
- **Ordering is unchanged:** one order per message, and orders are independent.

### D7. Timeouts

The call is synchronous and can run long: for `Quantity` = N, D365 creates N products/projects/sales orders in one transaction.
- Wilson keeps `D365FSCOptions.Timeout` (default 5 minutes) and does not add a separate setting.
- A Wilson-side timeout is treated as transient. The retry then hits D365 idempotency and returns the result.

## Risks / Trade-offs

- **[The D365 service serializer may handle Capture formats differently from `FormJsonSerializer`.]** Examples: `RequestDate` as `yyyy-MM-dd`, NoYes values as `"Yes"`/`"No"`, int64 `ResponsibleParty`, `IsBOM` int, and unknown members.
  → D365 verification tasks replay the three Capture sample variants (`docs/samples/sales-order-capture-full*.json`, local only) and `samples/create-salesorder-request*.json` in dev02. The fallback is D2's JSON-string alternative, a small change on both sides.
- **[D365 HTTP request limits may cut long `Quantity > 1` orders.]** → Measure the duration in dev02 with the largest realistic quantity. A timeout is safe (retry → `AlreadyProcessed`) but delays the reply.
- **[D365 must be deployed before Wilson in every environment.]** Wilson calling a missing operation gets a 404, so the message goes to status 3 with no reply to Capture (D8) and must be reprocessed once D365 is deployed. → Deployment order is a manual task. The old D365 path is untouched, so rolling back Wilson alone is safe.
- **[Capture may depend on `CreatedTransactionId` in the reply.]** → Open question Q2. If needed, Wilson can send `SalesId` in that slot or keep the key with an empty value.
- **[Rows already at status 5 at cutover.]** → The reconciler and sweep remain, so they finish normally.
- **[Dead-lettered orders still get no Capture reply after retries are exhausted.]** → Unchanged from today; out of scope.

- **[A `SetupFailure` trace could be sampled out]** (adaptive sampling in `Wilson.Integration.Automation/host.json` covers traces). → It is low-volume and logged at error level. If an outage ever goes unalerted, add `Trace` to `excludedTypes` (config change, separate). The go-live doc notes this.

## Migration Plan

1. Merge and deploy the D365 change to dev02 and verify it (D365 tasks).
2. Deploy D365 to UAT/BAT/TEST, then PRD, following the D365 release process.
3. Deploy Wilson via `pipelines/eim.yml` (bat, test), then `eim-prd.yml` once D365 PRD has the service.
4. Smoke test: Dashboard simulation `capture-salesorder-import` in bat. Expect status 2, a `SalesId` in DESTINATIONID, and a Success reply within seconds.

**Rollback:** redeploy the previous Wilson package. It calls the still-present `SendMessageWithResponse`, and rows created by the new path are unaffected.

## Decisions from review (2026-11-04)

- **Q1.** Ticket 4674. Branch `feature/4674/capture-salesorder-direct-service` from `main`.
- **Q2.** Capture reads neither `Data.CreatedTransactionId` nor `Action`. The plan is confirmed: keep `Action = "Reconcile"` and drop `CreatedTransactionId`.
- **Q3.** Typed `SalesOrder` contract confirmed.
- **Q4.** Return all `SalesIds`, with `SalesId` set to the last one.
- **Sequencing.** The D365 change (WP1) is built and reviewed first. The Wilson packages (WP2, WP3) wait for the user's go-ahead.

### Review round 2 (2026-11-09)

- **D8** (approved by the user): no Failed reply for D365 401/403/404.
- **Bowser minors:**
  - `IsDefiniteTerminal` honours the configured `RetryableODataErrorCodes`.
  - The stale `_companyId` wording in `SimulationService.cs` and `docs/integration/sales-order-excel-import.md` is corrected to `CompanyId`.

### Review round 3 (2026-11-09)

- **`SetupFailure` log event** (D8).
- **Bowser minor 1:** `IsDefiniteTerminal` stops at the first 401/403/404 link in the chain and returns false, matching the classifier's "first decisive link wins".
- **Bowser minor 3:** the `SendResponse` skipped wording no longer implies that every held reply will be retried.
- **Alert and runbook:** documented in the go-live doc.
- **Dashboard retry-code alignment (4.7):** stays a note; no work unless `RetryableODataErrorCodes` is introduced.

### Review round 4 (2026-11-09)

- The triage walk also treats `MissingEntityKeyException` as decisive, matching the classifier exactly.
- The runbook notes that a failed re-reprocess shows in the message log, not in the alert.
