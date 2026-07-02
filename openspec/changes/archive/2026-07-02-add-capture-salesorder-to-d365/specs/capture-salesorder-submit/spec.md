## ADDED Requirements

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
The system SHALL submit sales orders to D365 by calling the service action
`WTC_Integration/WTC_SysMessageService/SendMessageWithResponse` with a `MessageQueueContract`
body, rather than an OData entity CRUD call.

#### Scenario: Message contract is constructed
- **WHEN** a sales-order message is processed
- **THEN** the request body sets `_messageQueue = "CaptureIntegration"`, `_messageType = "CaptureCreateSalesOrder"`, `_companyId` to the order's `dataAreaId`, and `_messageContent` to the JSON string of the order data

### Requirement: Lift dataAreaId out of the message content
The system SHALL use the `Data.dataAreaId` value as `_companyId` and SHALL exclude `dataAreaId`
from the serialized `_messageContent`.

#### Scenario: dataAreaId becomes company id and is removed from content
- **WHEN** the order `Data` contains `dataAreaId = "WTC"` plus order fields
- **THEN** `_companyId = "WTC"` and the serialized `_messageContent` does not contain a `dataAreaId` property

### Requirement: Forward the order body as pass-through JSON
The system SHALL forward the order body without field-level mapping, preserving all order fields
including nested `Line` and `TradeAllowance` collections.

#### Scenario: Unknown order fields are preserved
- **WHEN** the order `Data` contains fields not explicitly modeled by the integration
- **THEN** those fields are preserved verbatim in `_messageContent`

### Requirement: Record the submission result as awaiting confirmation
On a successful submit, the system SHALL persist the returned `CreatedTransactionId` and set the
message status to `5` (Submitted / Awaiting D365).

#### Scenario: Successful submit records transaction id and status 5
- **WHEN** `SendMessageWithResponse` returns `{ "CreatedTransactionId": 164012 }`
- **THEN** the message row stores `DESTINATIONID = "164012"` and `STATUS = 5`

#### Scenario: Submit failure records a failed status
- **WHEN** the service action call fails
- **THEN** the message is recorded as failed and the error is logged
