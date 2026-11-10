## Why

Capture sales orders reach D365 F&SC through SysMessageProcessor: Wilson calls
`WTC_SysMessageService/SendMessageWithResponse`, D365 queues a `SysMessage`, a batch processor
later deserializes it into `WTC_PBLTable` and creates the sales order, and Wilson only learns the
outcome from the `SysMessageProcessorMessageProcessedBusinessEvent`. That makes the flow slow and
opaque:

- Capture has to wait for a batch run before it gets a reply.
- Failed orders often arrive with an empty `MessageInfolog`.
- Capture gets no reply at all when the submit fails or the business event never arrives.
- Retrying after a timeout can submit the same order twice. The failure fixture shows `WTC_PBLTable` "record already exists".

D365 will expose a new custom service (WTC model, coordinated change of the same id in the D365
repo). It takes the full order payload and writes `WTC_PBLTable`/lines and the sales order
directly in one synchronous, idempotent call. Wilson must switch the sales-order route to it.

Ticket: 4674 (Req1197 Capture integration).

## What Changes

- `SalesOrder_To_D365FSC` now calls `api/services/WTC_Integration/WTC_CaptureSalesOrderService/CreateSalesOrder`, sending `{ "CompanyId": <dataAreaId>, "SalesOrder": <Data without dataAreaId> }`. It no longer calls `WTC_SysMessageService/SendMessageWithResponse`. **BREAKING** for the D365 contract; requires the D365 change deployed first.
- The orchestration is synchronous:
  - On success the message is `Completed` (status 2) and `DESTINATIONID` holds the `SalesId`.
  - When D365 rejects the order the message is `Failed` (status 3), with the D365 error text in the message log.
  - It no longer parks at `AwaitingD365` (status 5).
- Wilson replies to Capture on the response queue itself, straight after the call:
  - **Success** carries `SalesId`, `SalesIds`, `PBLId` and `AlreadyProcessed`.
  - **Failed** carries the D365 error text.
  - It uses the same envelope shape Capture receives today from the business-event reconcile.
- New `D365ServiceRejectedException`, classified **terminal** in `DefaultFailureClassifier`.
- Transient failures (D365 5xx/408/429, timeouts, network) still go through the existing retry schedule. The D365 service is idempotent on `PBLId`, so retries and Dashboard reprocessing are now safe.
- New unit tests for the request build, response parsing, reply envelope and classification.

## Non-goals

- Revise (`CaptureReviseSalesOrder`) and Split (`CaptureSplitSalesOrder`) flows. They stay on SysMessageProcessor and have no Wilson route today.
- Removing `D365ServiceActionOrchestration`, `BusinessEventReconciler` or the AwaitingD365 sweep. They remain for rows already at status 5 at cutover and for future async types.
- Route, registry, SQL, deployment config or new Function triggers. The route `SalesOrderQueue → SalesOrder_To_D365FSC` and its registry key are unchanged.
- Notifying Capture when a transiently failing order is finally dead-lettered after retries are exhausted. This matches today's behaviour.
- Typed C# contracts for the order body. Wilson keeps forwarding `Data` as pass-through JSON.
- Dashboard UI changes.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `capture-salesorder-submit`: submit target, request shape, synchronous result, Capture reply and rejection classification change. Routing is unchanged.

## Impact

- **Code:**
  - `Wilson.Integration.Orchestrations/Orchestrations/SalesOrder_To_D365FSC.cs` (rewritten)
  - New `Wilson.Integration.Orchestrations/Orchestrations/CaptureSalesOrderService.cs` (request/response/reply helpers)
  - New `ARM.Integration.Core/Helpers/D365ServiceRejectedException.cs`
  - `ARM.Integration.EIM/Retry/DefaultFailureClassifier.cs`
- **Tests:** `ARM.Integration.EIM.Tests/FailureClassifierTests.cs` and new `Wilson.Integration.Tests/SalesOrderToD365FscTests.cs`.
- **External contracts:**
  - The D365 endpoint and request body change.
  - The Capture reply now arrives synchronously. `Data.CreatedTransactionId` is no longer sent; `PBLId`, `SalesIds` and `AlreadyProcessed` are added.
- **Cross-repo:** depends on the D365 change `add-capture-salesorder-direct-service` in the WTC model (D365 repo, branch from `development`). It must be deployed to each D365 environment before Wilson.
- **Unchanged but affected:** Dashboard simulation `capture-salesorder-import` (same route, faster reply) and Dashboard reprocess (now idempotent).
