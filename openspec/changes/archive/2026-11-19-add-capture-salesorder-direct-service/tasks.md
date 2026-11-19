## Prerequisite (manual, not Luigi)

- [ ] 0.1 D365 change `add-capture-salesorder-direct-service` (D365 repo, WTC model) is implemented, built and verified in dev02. The operation `WTC_Integration/WTC_CaptureSalesOrderService/CreateSalesOrder` exists, and its response member names match design D3.
- [x] 0.2 Create the Wilson branch `feature/4674/capture-salesorder-direct-service` from `main`. The current branch `feature/3874/engineering-app-sample-followup` has unrelated uncommitted files; do not carry them over.

## 1. Implementation — Engine (Luigi)

- [x] 1.1 Add `ARM.Integration.Core/Helpers/D365ServiceRejectedException.cs` (design D5):
  - Constructor `(string serviceMethod, string? businessKey, string? errorMessage)`.
  - Properties `ServiceMethod` and `BusinessKey`.
  - Message: `errorMessage`, or "D365 rejected the request." when blank.
- [x] 1.2 In `ARM.Integration.EIM/Retry/DefaultFailureClassifier.cs`, add `D365ServiceRejectedException => true` to `IsPositivelyTerminal`.

## 2. Implementation — Orchestration (Luigi)

- [x] 2.1 Add `Wilson.Integration.Orchestrations/Orchestrations/CaptureSalesOrderService.cs`, a static class of pure helpers:
  - `ServiceMethod = "api/services/WTC_Integration/WTC_CaptureSalesOrderService/CreateSalesOrder"`.
  - `BuildRequest(string payload) → (string? SourceId, string? PBLId, string CompanyId, string RequestJson)`:
    - Throws `JsonException` when `Data` is missing or `dataAreaId` is missing/blank.
    - Removes `dataAreaId` and emits `{"CompanyId":…, "SalesOrder":{…}}` (D2).
  - `ParseResult(string response) → CaptureSalesOrderServiceResult` (record: `Success`, `PBLId`, `SalesId`, `SalesIds`, `AlreadyProcessed`, `ErrorMessage`):
    - Case-insensitive; ignores unknown members.
    - Throws `JsonException` when the body is not JSON or has no `Success`, or when `Success = true` but `SalesId` is blank.
  - `BuildReply(QueueMessageContract message, string? sourceId, string status, string? statusMessage, CaptureSalesOrderServiceResult? result, string? pblId) → OutboundMessageEnvelope<JsonObject>` (D4: `Action = "Reconcile"`, `Entity = SourceEntity ?? DestinationEntity`).
  - `IsDefiniteTerminal(Exception) → bool` (D4 rule: rejection, `JsonException`, `D365FSCException` with a non-retryable status; mirror `DefaultFailureClassifier` statuses 408/429/≥500 except 501/505 as retryable).
- [x] 2.2 Rewrite `Wilson.Integration.Orchestrations/Orchestrations/SalesOrder_To_D365FSC.cs` as `IOrchestration` (D1, D3, D4):
  - Resolve source and destination connections, then `BuildRequest`.
  - Call D365 through `protected virtual Task<string> CallServiceAsync(string destinationConnection, string method, string body, CancellationToken)`. The default uses `D365FSCServiceCollectionExtensions.CreateStandaloneClient(...).CallServiceAsync`.
  - `ParseResult`. On `Success`, send the Success reply and return `OrchestrationResult.Completed(response, result.SalesId)`. On `!Success`, throw `D365ServiceRejectedException`.
  - In `catch`: when `IsDefiniteTerminal(ex)`, send the Failed reply; then rethrow.
  - Send replies through `protected virtual Task SendReplyAsync(string sourceConnection, string queue, string payload, CancellationToken)`. The default uses `ServiceBusHelper`. Calls are best-effort and wrapped in try/catch with `stepLog.LogError`. Skip when `ResponseQueue` is blank.
  - Log steps: `BuildRequest` (CompanyId, PBLId), `CallService`, `Result` (SalesId/AlreadyProcessed), `SendResponse`.
  - Update the class XML doc. Do not touch `D365ServiceActionOrchestration`, `OrchestrationRegistry` or any SQL.

## 3. Tests (Luigi)

- [x] 3.1 `ARM.Integration.EIM.Tests/FailureClassifierTests.cs`: `D365ServiceRejectedException` is terminal, both directly and when wrapped as an inner exception. Scenario: *Rejection is not retried*.
- [x] 3.2 New `Wilson.Integration.Tests/SalesOrderToD365FscTests.cs` for the request builder, using `Wilson.Integration.Tests/TestData/SalesOrderCapture.json` (copy of the Capture v2 sample) (copy it into `Wilson.Integration.Tests/TestData/` if the samples aren't reachable from the test output). Scenarios:
  - *Direct service request is constructed*
  - *dataAreaId becomes company id and is removed from the order*
  - *Missing dataAreaId is terminal*
  - *Unknown order fields are preserved*
  - *Nested collections are preserved* (56 lines, 1 trade allowance)
- [x] 3.3 Same file, result parsing. Scenarios:
  - *Created order completes the message*
  - *Already-processed order completes the message*
  - *Rejected order fails as terminal*
  - *Unrecognised response fails as terminal* (non-JSON, and JSON without `Success`)
  - `$id` member ignored
- [x] 3.4 Same file, orchestration with a test subclass overriding `CallServiceAsync`/`SendReplyAsync` (also add `protected virtual string ResolveConnection(string system, string environment, string connectionString)` wrapping `EIMUtilities.GetEnvironment`, because it reads the database). Scenarios:
  - *Success reply carries the sales id*
  - *Rejection reply carries the D365 error*
  - *Transient failure sends no reply* (503 `D365FSCException` and `TimeoutException`)
  - *Reply failure does not fail a created order*
  - *Message-queue service is no longer used* (assert the method passed to `CallServiceAsync`)
- [x] 3.5 `dotnet build ARM.Integration.Wilson.slnx` and all three test projects pass.

## 3R. Review round 2 (Luigi; design D8 and "Review round 2")

- [x] 3.6 D8: in `CaptureSalesOrderService.IsDefiniteTerminal`, return false for a `D365FSCException` with status 401, 403 or 404. Classification is unchanged; it still ends at status 3 through `DefaultFailureClassifier`. Update the XML docs and the class doc of `SalesOrder_To_D365FSC`.
- [x] 3.7 In `IsDefiniteTerminal`, return false for a `D365FSCException` whose `ODataErrorCode` is in the configured `RetryableODataErrorCodes`. Load the options from the same source the host and `DefaultFailureClassifier` use (check `RetryOptionsFactory`). Keep the helper unit-testable, for example with an overload or optional parameter that takes the code list.
- [x] 3.8 Tests in `Wilson.Integration.Tests/SalesOrderToD365FscTests.cs` for these scenarios:
  - *Setup failure sends no reply* (401, 403, 404: no reply, rethrown, classifier terminal)
  - *Reprocess after a setup failure sends one reply* (first run 401, second run success: exactly one reply in total, Success)
  - *Configured retryable OData code sends no reply*
  - Also update any existing test that expected a Failed reply for 404 (`Definite_d365_http_rejection_replies_failed`) to use a different terminal 4xx, such as 400 or 422.
- [x] 3.9 Change `_companyId` to `CompanyId` in the comment at `ARM.Integration.Dashboard.API/Services/SimulationService.cs:664` and in `docs/integration/sales-order-excel-import.md` (around lines 75–76). Text only.
- [x] 3.10 `dotnet build ARM.Integration.Wilson.slnx` and all three test projects pass.

## 3S. Review round 3 (Luigi; design D8 and "Review round 3")

- [x] 3.11 In `SalesOrder_To_D365FSC`, when the failure is a setup failure (401/403/404), log `stepLog.LogError("SetupFailure", "D365 returned <status> for PBLId <id>; reply held until reprocess")`. Log it only for those statuses. Reuse the 401/403/404 detection from `CaptureSalesOrderService` and expose a small helper if needed.
- [x] 3.12 In `CaptureSalesOrderService.IsDefiniteTerminal`, return false as soon as the walk reaches a 401/403/404 `D365FSCException` link (first decisive link wins). Reword the `SendResponse` skipped log, for example "Skipped: no reply for this failure; a later run sends it".
- [x] 3.13 Tests in `SalesOrderToD365FscTests.cs`:
  - extend *Setup failure sends no reply* to assert the `SetupFailure` step (status and PBLId);
  - *Other failures do not log a setup failure* (503, timeout, rejection);
  - *Setup failure wrapping another error still sends no reply* (401 with an inner `JsonException`).
- [x] 3.14 `docs/deployment/production-go-live.md` §7 "Alerts":
  - Add the `SetupFailure` log search alert as an `az monitor scheduled-query create` example in the same style as the existing alerts. Use the Automation Function App's Application Insights resource, looked up from the app (do not hard-code a name you can't verify), a 15-minute window, 5-minute evaluation, count > 0 and the production action group.
  - Add a short runbook: 401 → D365 app registration secret; 403 → privilege `WTC_CaptureSalesOrderServiceMaintain`; 404 → D365 service not deployed; then reprocess the status-3 `SalesOrderQueue` messages from the Dashboard.
  - Note that the `customDimensions` key should be confirmed against the first real event.
- [x] 3.15 `dotnet build ARM.Integration.Wilson.slnx` and all three test projects pass.

## 3T. Review round 4 (Luigi; Bowser round-3 minors)

- [x] 3.16 In `CaptureSalesOrderService.FindDecisiveLink`, treat `MissingEntityKeyException` as decisive, matching `DefaultFailureClassifier.IsPositivelyTerminal`. Add a `MissingEntityKeyException` row to `TerminalFailures` in `SalesOrderToD365FscTests.cs`, asserting it gets a Failed reply, no `SetupFailure` step, and that the classifier agrees.
- [x] 3.17 In the `docs/deployment/production-go-live.md` §7 runbook, add one sentence: a Dashboard reprocess that fails again is logged in the Dashboard API's telemetry and the message log, not the Automation app's Application Insights, so the alert does not fire a second time. Check the message log after reprocessing.
- [x] 3.18 `dotnet build ARM.Integration.Wilson.slnx` and all three test projects pass.

## 4. Deploy and verify (manual — not Luigi)

- [ ] 4.1 Confirm the D365 service is deployed in the target D365 environment before each Wilson deployment (bat/test, then prd).
- [ ] 4.2 bat: run the Dashboard simulation `capture-salesorder-import` with a sample order:
  - Status 2, `DESTINATIONID` = SalesId, Success reply on the response queue within seconds.
- [ ] 4.3 bat: reprocess the same message from the Dashboard → status 2, reply `AlreadyProcessed = true`, no duplicate PBL record or sales order.
- [ ] 4.4 bat: send an order with an invalid customer → status 3, D365 error text in MESSAGELOG, Failed reply with the same text.
- [ ] 4.5 Confirm with the Capture team that the reply shape (no `CreatedTransactionId`; `PBLId`/`SalesIds`/`AlreadyProcessed` added) is accepted (Q2).
- [ ] 4.6 At prd go-live (and optionally in bat), create the `SetupFailure` log search alert from `docs/deployment/production-go-live.md` §7. Once a test 401 has been triggered in bat (for example by sending with a wrong secret on a non-prod copy), confirm that the alert query (`traces | where message startswith '[SetupFailure] '`) matches the real event.
- [ ] 4.7 Note only (no work unless needed): if `RetryableODataErrorCodes` is ever added to deploy config, build the Dashboard reprocess classifier from `RetryOptionsFactory` as well. `EIMController` uses `new RetryOptions()` today, so the orchestration and the classifier would disagree.
