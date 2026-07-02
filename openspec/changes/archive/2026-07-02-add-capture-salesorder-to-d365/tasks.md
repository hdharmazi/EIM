## 1. Orchestration result contract (Option C)

- [x] 1.1 Add `OrchestrationResult` type (`int Status = 2`, `string Message`, `string? DestinationId`) in `ARM.Integration.EIM/Orchestration`
- [x] 1.2 Change `IOrchestration.ProcessAsync` to return `Task<OrchestrationResult>`
- [x] 1.3 Update `BaseCrudOrchestration` to return a Completed (`2`) `OrchestrationResult` wrapping its payload
- [x] 1.4 Update `BaseForwardOrchestration` to return a Completed (`2`) `OrchestrationResult`
- [x] 1.5 Update `EIMController.ProcessMessageAsync` to set status from `result.Status` and persist `result.DestinationId` into `DESTINATIONID`
- [x] 1.6 Update the reflection-fallback branch in `DispatchOrchestrationAsync` to adapt legacy `string`/results into `OrchestrationResult`
- [x] 1.7 Build the solution and confirm all existing orchestrations compile and still complete at status `2`

## 2. Sales-order submit orchestration (Phase 1)

- [x] 2.1 Add `D365ServiceActionOrchestration<TSource>` base (extends `BaseCrudOrchestration<TSource, MessageQueueContract>`, `ExecuteActionAsync` calls `CallServiceAsync`)
- [x] 2.2 Implement `SalesOrder_To_D365FSC`: deserialize envelope, lift `dataAreaId` → `_companyId`, strip it, pass-through `Data` JSON into `_messageContent` (Options A + pass-through)
- [x] 2.3 Set `_messageQueue = "CaptureIntegration"`, `_messageType = "CaptureCreateSalesOrder"`, method `SendMessageWithResponse`
- [x] 2.4 Parse `CreatedTransactionId` from the response and return `OrchestrationResult { Status = 5, DestinationId = CreatedTransactionId }`
- [x] 2.5 Register `["SalesOrderQueue"] = "SalesOrder_To_D365FSC"` in `QueueCaptureHandler.EntityToOrchestration`
- [x] 2.6 Register `["SalesOrder_To_D365FSC"] = new SalesOrder_To_D365FSC()` in `ARM.Integration.EIM.Customer/Program.cs`

## 3. Business-event reconciliation (Phase 2)

- [x] 3.1 Add a business-event contract model for `SysMessageProcessorMessageProcessedBusinessEvent` (`BusinessEventId`, `CreatedTransactionId`, `MessageState`, `MessageInfolog`)
- [x] 3.2 Add `EIMController.UpdateStatusByDestinationId(createdTransactionId, status, log)` (idempotent, not gated on current status)
- [x] 3.3 Add `QueueBusinessEventHandler` (routes by `BusinessEventId`) and its Service Bus trigger function on `%D365BusinessEventQueue%`
- [x] 3.4 Map `MessageState`: `Processed → 2`; `Failed`/`Cancelled → 3` with `MessageInfolog` logged; correlate to `DESTINATIONID`; do not insert a new row
- [x] 3.5 Add the `%D365BusinessEventQueue%` (`[env]-d365fsc-businessevent`) setting to `local.settings.json`/config

## 4. Stuck-message safety sweeper

- [x] 4.1 Add a timer-triggered sweeper function that finds rows `STATUS = 5 AND MODIFIEDDATETIME < now - {StuckTimeout}`
- [x] 4.2 Escalate matched rows to `STATUS = 4` (Dead Letter) and emit an alert/log
- [x] 4.3 Add `StuckTimeout` and sweep-interval app settings (defaults: 4h timeout, 15–30 min interval)

## 5. Data & dashboard

- [x] 5.1 Add an index on `MESSAGEQUEUETABLE.DESTINATIONID` and document `STATUS = 5` in `Script/SQL/Install.sql`
- [x] 5.2 Add an "Awaiting D365" label for `STATUS = 5` in the dashboard status mapping

## 6. Verification

- [x] 6.1 Build the full solution and resolve any errors
- [x] 6.2 Validate the change with `openspec validate add-capture-salesorder-to-d365 --strict`
