# capture-salesorder-submit Specification

## Purpose

Route Capture → D365 sales-order messages to the sales-order orchestration and submit them to
Dynamics 365 Finance & Supply Chain via the message-service action, recording the submission as
awaiting asynchronous confirmation.

## Requirements

### Requirement: Route sales-order messages to the sales-order orchestration
The system SHALL route Capture → D365 queue messages whose envelope `Entity` equals
`SalesOrderQueue` to the `SalesOrder_To_D365FSC` orchestration.

#### Scenario: Sales-order envelope is routed
- **WHEN** a message arrives on the Capture → D365 queue with `Entity = "SalesOrderQueue"` and `Action = "New"`
- **THEN** the handler dispatches it to the `SalesOrder_To_D365FSC` orchestration

#### Scenario: Unknown entity is skipped
- **WHEN** a message arrives with an `Entity` value that is not registered
- **THEN** the handler completes the message without dispatching and logs it as unsupported

### Requirement: Submit sales orders via the D365 service action
The system SHALL submit sales orders to D365 by calling the custom service operation
`WTC_Integration/WTC_CaptureSalesOrderService/CreateSalesOrder` with a body of the form
`{ "CompanyId": <company>, "SalesOrder": <order object> }`, and SHALL NOT call
`WTC_SysMessageService/SendMessageWithResponse` for sales orders.

#### Scenario: Direct service request is constructed
- **WHEN** a sales-order message with `Data = { "dataAreaId": "WTC", "PBLId": "PBL-1", "Line": [ ... ] }` is processed
- **THEN** the system POSTs to `api/services/WTC_Integration/WTC_CaptureSalesOrderService/CreateSalesOrder`
- **AND** the body has `CompanyId = "WTC"` and `SalesOrder` is a JSON object (not a string) with `PBLId = "PBL-1"` and the `Line` array

#### Scenario: Message-queue service is no longer used
- **WHEN** a sales-order message is processed
- **THEN** no request is made to `WTC_SysMessageService/SendMessageWithResponse`

### Requirement: Lift dataAreaId out of the message content
The system SHALL use the `Data.dataAreaId` value as `CompanyId` and SHALL exclude `dataAreaId`
from `SalesOrder`. A missing or blank `dataAreaId` MUST fail the message as terminal without
calling D365.

#### Scenario: dataAreaId becomes company id and is removed from the order
- **WHEN** the order `Data` contains `dataAreaId = "WTC"` plus order fields
- **THEN** `CompanyId = "WTC"` and `SalesOrder` does not contain a `dataAreaId` property

#### Scenario: Missing dataAreaId is terminal
- **WHEN** the order `Data` has no `dataAreaId` or it is blank
- **THEN** the build fails with a `JsonException`, no D365 call is made, and the failure is classified terminal

### Requirement: Forward the order body as pass-through JSON
The system SHALL forward the order body as the `SalesOrder` object without field-level mapping,
preserving all order fields, including nested `Line` and `TradeAllowance` collections.

#### Scenario: Unknown order fields are preserved
- **WHEN** the order `Data` contains fields not explicitly modeled by the integration
- **THEN** those fields are present verbatim in `SalesOrder`

#### Scenario: Nested collections are preserved
- **WHEN** the order `Data` contains `Line` with 56 items and `TradeAllowance` with 1 item (sample `Wilson.Integration.Tests/TestData/SalesOrderCapture.json` (copy of the Capture v2 sample))
- **THEN** `SalesOrder.Line` has 56 items and `SalesOrder.TradeAllowance` has 1 item with unchanged values

### Requirement: Record the synchronous service result
The system SHALL map the `CreateSalesOrder` response to a final message status in the same run:
`Success = true` MUST complete the message (status 2) with `DESTINATIONID` set to the returned
`SalesId`, and `Success = false` MUST fail the message as terminal (status 3) with the D365
`ErrorMessage` in the message log.

#### Scenario: Created order completes the message
- **WHEN** D365 returns `{ "Success": true, "PBLId": "PBL-1", "SalesId": "200007", "SalesIds": ["200007"], "AlreadyProcessed": false }`
- **THEN** the orchestration returns a Completed result with `DestinationId = "200007"`

#### Scenario: Already-processed order completes the message
- **WHEN** D365 returns `{ "Success": true, "SalesId": "200007", "AlreadyProcessed": true }`
- **THEN** the orchestration returns a Completed result with `DestinationId = "200007"`

#### Scenario: Rejected order fails as terminal
- **WHEN** D365 returns `{ "Success": false, "PBLId": "PBL-1", "ErrorMessage": "Customer account C999 does not exist." }`
- **THEN** the orchestration throws `D365ServiceRejectedException` whose message contains `Customer account C999 does not exist.`

#### Scenario: Unrecognised response fails as terminal
- **WHEN** D365 returns HTTP 200 with a body that is not JSON or has no `Success` member
- **THEN** the orchestration fails with a `JsonException`

### Requirement: Reply to Capture after the service call
The system SHALL send the reply to the message's `ResponseQueue` from the orchestration, using the
envelope `{ SourceId, Action: "Reconcile", Entity, Status, StatusMessage, Data }`. It MUST send
`Status = "Success"` with `Data` containing `SalesId`, `SalesIds`, `PBLId` and `AlreadyProcessed`
on success. It MUST send `Status = "Failed"` with the error text only for terminal failures caused
by the order or the response. It MUST NOT send a reply for transient failures that will be retried,
or for D365 setup failures (HTTP 401, 403, 404) that are resolved by fixing the setup and
reprocessing. A failure to send the reply MUST NOT change the message outcome.

#### Scenario: Success reply carries the sales id
- **WHEN** an order with `SourceId = "CAP-1"` and `Entity = "SalesOrderQueue"` is created as `SalesId = "200007"`
- **THEN** the reply has `SourceId = "CAP-1"`, `Action = "Reconcile"`, `Entity = "SalesOrderQueue"`, `Status = "Success"`, and `Data.SalesId = "200007"`

#### Scenario: Rejection reply carries the D365 error
- **WHEN** D365 returns `Success = false` with `ErrorMessage = "Route R1 is not valid."`
- **THEN** the reply has `Status = "Failed"` and `StatusMessage` contains `Route R1 is not valid.`

#### Scenario: Transient failure sends no reply
- **WHEN** the call fails with a `D365FSCException` of status 503 or a timeout
- **THEN** no reply is sent and the exception is rethrown for the retry schedule

#### Scenario: Setup failure sends no reply
- **WHEN** the call fails with a `D365FSCException` of status 401, 403 or 404
- **THEN** no reply is sent, the exception is rethrown, and the message is classified terminal (status 3) so that it can be reprocessed
- **AND** an error step named `SetupFailure` is logged with the HTTP status and the `PBLId`

#### Scenario: Other failures do not log a setup failure
- **WHEN** the call fails with a 503, a timeout, or a D365 rejection
- **THEN** no `SetupFailure` step is logged

#### Scenario: Setup failure wrapping another error still sends no reply
- **WHEN** the call fails with a `D365FSCException` of status 401 whose inner exception is a `JsonException`
- **THEN** no reply is sent, matching the classifier, which decides on the first decisive link

#### Scenario: Reprocess after a setup failure sends one reply
- **WHEN** a message that failed with 401 is reprocessed after the setup is fixed and D365 returns `Success = true`
- **THEN** exactly one reply is sent for that message, with `Status = "Success"`

#### Scenario: Configured retryable OData code sends no reply
- **WHEN** the call fails with a 4xx `D365FSCException` whose `ODataErrorCode` is listed in the configured `RetryableODataErrorCodes`
- **THEN** no reply is sent and the exception is rethrown for the retry schedule

#### Scenario: Reply failure does not fail a created order
- **WHEN** D365 returns `Success = true` but sending the reply throws
- **THEN** the error is logged and the orchestration still returns a Completed result

### Requirement: Classify D365 service rejections as terminal
The system SHALL classify `D365ServiceRejectedException` as a terminal failure in
`DefaultFailureClassifier`.

#### Scenario: Rejection is not retried
- **WHEN** an orchestration throws `D365ServiceRejectedException`
- **THEN** the classifier returns a terminal disposition and the message is set to status 3 without a retry schedule
