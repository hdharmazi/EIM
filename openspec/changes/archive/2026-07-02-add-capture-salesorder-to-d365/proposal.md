## Why

Capture must push sales orders into D365 F&SC, but sales orders are not created via a
standard OData entity. They are submitted to a D365 message queue (`WTC_SysMessageService`)
that is processed asynchronously by a D365 batch, so the outcome is not known at submit time.
The existing Capture → D365 pipeline is synchronous (submit == done), which cannot represent
this "submitted, awaiting D365" state. We need a new integration plus an asynchronous
request-reply lifecycle driven by D365 business events.

## What Changes

- Add a new Capture → D365 integration for sales orders routed on envelope `Entity = "SalesOrderQueue"`.
- Submit path calls the custom D365 service action
  `WTC_Integration/WTC_SysMessageService/SendMessageWithResponse` (not OData CRUD), wrapping the
  order in the existing `MessageQueueContract` (`_companyId` / `_messageQueue` / `_messageType` /
  `_messageContent`). The order body is forwarded as a pass-through JSON string; `dataAreaId` is
  lifted out of `Data` to `_companyId` and removed from the content.
- Introduce message status `5 = Submitted / Awaiting D365`, stored after submit with the returned
  `CreatedTransactionId` persisted in `MESSAGEQUEUETABLE.DESTINATIONID`.
- **BREAKING**: Change `IOrchestration.ProcessAsync` to return an `OrchestrationResult`
  (`Status`, `Message`, `DestinationId`) instead of `string`, so an orchestration can report a
  non-terminal status and a correlation id. All existing orchestrations and the controller are updated.
- Add a business-event ingestion path: a Service Bus trigger on the generic D365 business-event
  queue that routes by `BusinessEventId`, correlates `CreatedTransactionId` to `DESTINATIONID`, and
  updates status (`Processed → 2`, `Failed`/`Cancelled → 3`, with `MessageInfolog` logged).
- Add a safety-net sweeper that escalates rows stuck at status `5` beyond a configurable timeout to
  status `4` (Dead Letter) and alerts.
- Surface status `5` ("Awaiting D365") in the dashboard.

## Capabilities

### New Capabilities
- `capture-salesorder-submit`: Route, map, and submit Capture sales orders to the D365
  `SendMessageWithResponse` service action, recording the returned transaction id and the
  awaiting-confirmation status.
- `async-orchestration-result`: A structured orchestration result that lets orchestrations report
  their own terminal/non-terminal status and a destination correlation id, honored by the controller.
- `d365-business-event-reconcile`: Consume D365 message-processing business events to reconcile the
  final status of previously submitted async messages, plus a stuck-message safety sweeper.

### Modified Capabilities
<!-- No existing specs in openspec/specs/; all capabilities are new. -->

## Impact

- **New code**: `SalesOrder_To_D365FSC` orchestration + a `D365ServiceActionOrchestration` base;
  business-event trigger function + handler; stuck-message sweeper function; `OrchestrationResult` type.
- **Modified code**: `IOrchestration`, `BaseCrudOrchestration`, `BaseForwardOrchestration`, all
  concrete orchestrations, `EIMController` (status/destination handling + `UpdateStatusByDestinationId`),
  `QueueCaptureHandler` (route `SalesOrderQueue`), `Program.cs` (DI registration), dashboard status labels.
- **Data**: New `STATUS = 5` semantics; index on `MESSAGEQUEUETABLE.DESTINATIONID`.
- **Config**: New app settings for the business-event queue name and sweeper interval/timeout.
- **External**: Depends on D365 `WTC_SysMessageService` service action and the
  `SysMessageProcessorMessageProcessedBusinessEvent` business event being published to the queue.
