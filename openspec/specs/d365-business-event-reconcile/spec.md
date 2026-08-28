# d365-business-event-reconcile Specification

## Purpose

Consume D365 business events to reconcile the status of asynchronously-submitted messages against
their `DESTINATIONID` correlation, and escalate messages that remain stuck awaiting confirmation
beyond a configurable timeout.

## Requirements

### Requirement: Ingest D365 business events by event id
The system SHALL consume messages from the D365 business-event queue and route them by
`BusinessEventId`.

#### Scenario: Message-processed event is routed
- **WHEN** a message arrives on the business-event queue with `BusinessEventId = "SysMessageProcessorMessageProcessedBusinessEvent"`
- **THEN** it is handled by the message-state reconciliation logic

#### Scenario: Unregistered business event is skipped
- **WHEN** a business event with an unregistered `BusinessEventId` arrives
- **THEN** the handler completes the message without processing it

### Requirement: Reconcile async message status from business events
The system SHALL correlate the event's `CreatedTransactionId` to `MESSAGEQUEUETABLE.DESTINATIONID`
and update the existing row's status from `MessageProcessingResult`, which carries the terminal
outcome on both success and failure. `MessageState` describes the queue lifecycle position (it
reads `Queued` on a successfully processed message) and SHALL NOT be used to derive status.

#### Scenario: Processed event marks the row completed
- **WHEN** a reconciliation event has `MessageProcessingResult = "Processed"` and a matching `DESTINATIONID`
- **THEN** the row status is set to `2` (Completed)

#### Scenario: Failed, cancelled or error event marks the row failed
- **WHEN** a reconciliation event has `MessageProcessingResult` of `"Failed"`, `"Cancelled"` or `"Error"`
- **THEN** the row status is set to `3` (Failed) and `MessageInfolog` is written to the message log

#### Scenario: A success is not derived from MessageState
- **WHEN** a reconciliation event has `MessageState = "Queued"` and `MessageProcessingResult = "Processed"`
- **THEN** the row status is set to `2` (Completed), and `MessageState` is recorded only as diagnostic context

#### Scenario: Outcome matching is case-insensitive and whitespace-tolerant
- **WHEN** `MessageProcessingResult` is `" processed "` or `"PROCESSED"`
- **THEN** it is treated as `Processed`

#### Scenario: Reconciliation does not create a new message
- **WHEN** a reconciliation event is processed
- **THEN** no new `MESSAGEQUEUETABLE` row is inserted; only the correlated row is updated

#### Scenario: Late or duplicate events are handled idempotently
- **WHEN** a reconciliation event arrives after the row was already escalated or updated
- **THEN** the update is applied idempotently without error

### Requirement: Acknowledge non-terminal reconciliation events
The system SHALL treat a reconciliation event whose `MessageProcessingResult` is absent, empty,
or not a recognised terminal outcome as a progress signal rather than an error. The correlated
row's status SHALL be left unchanged, a processing step SHALL be recorded against it, and the
Service Bus message SHALL be completed so it is not redelivered.

#### Scenario: Queued progress event leaves the row awaiting
- **WHEN** an event arrives with `MessageState = "Queued"` and no terminal `MessageProcessingResult`
- **THEN** the correlated row remains at status `5` (AwaitingD365)
- **AND** a processing step recording the observed state is written against that row
- **AND** the Service Bus message is completed

#### Scenario: Unrecognised outcome value is acknowledged, not dropped silently
- **WHEN** an event arrives where neither outcome field carries a recognised terminal value
- **THEN** the value is logged at Warning with the event id and correlation id
- **AND** the row status is left unchanged

### Requirement: Reconciliation status updates are monotonic
The system SHALL NOT regress a row that has already reached a terminal reconciled status. A
status update driven by a business event SHALL apply only when the target status represents
progress from the row's current status. A row escalated to `4` (Dead Letter) by the stuck-message
sweep SHALL remain correctable by a late business event.

#### Scenario: A duplicate processed event does not rewrite a completed row
- **WHEN** a `Processed` event arrives for a row already at status `2`
- **THEN** the row remains at status `2` and no error is raised

#### Scenario: A late processed event does not overwrite a recorded failure
- **WHEN** a `Processed` event arrives for a row already at status `3` from an earlier `Failed` event
- **THEN** the row remains at status `3` and the discrepancy is logged at Warning

#### Scenario: An escalated row is still correctable
- **WHEN** a terminal event arrives for a row the sweep escalated to status `4`
- **THEN** the row is updated to the event's outcome (`2` or `3`)

### Requirement: Record the D365 response key on the reconciled row
The system SHALL persist the business key D365 returns with the terminal event so an operator can
locate the created record in D365 without decoding a transaction id. `MessageResponseKey` and
`MessageResponseKeyFieldName` SHALL be recorded against the correlated row.

#### Scenario: Sales order number is recorded on completion
- **WHEN** a `Processed` event carries `MessageResponseKeyFieldName = "SalesId"` and `MessageResponseKey = "200007"`
- **THEN** `200007` is recorded against the correlated row and is visible to an operator alongside its status

#### Scenario: Missing response key does not block reconciliation
- **WHEN** a terminal event carries no `MessageResponseKey`, including when it is sent as an empty string
- **THEN** the status is still reconciled, no key is recorded, and no error is raised

### Requirement: Notify the source system on both reconciliation outcomes
The system SHALL send a response message to the correlated row's `RESPONSEQUEUE` on every terminal
reconciliation, not only on failure. The message SHALL use the same envelope shape the synchronous
orchestration path emits, carrying `SourceId`, `Entity`, `Status` and a `Data` object containing
the D365 correlation id and, where present, the response key. A response-send failure SHALL NOT
change the reconciled status of the row.

#### Scenario: Successful reconciliation notifies the source system
- **WHEN** a row is reconciled to status `2` (Completed) and it has a non-empty `RESPONSEQUEUE`
- **THEN** a message with `Status = "Success"` is sent to that queue
- **AND** its `Data` carries the D365 `CreatedTransactionId` and the `MessageResponseKey`

#### Scenario: Failed reconciliation notifies the source system
- **WHEN** a row is reconciled to status `3` (Failed) and it has a non-empty `RESPONSEQUEUE`
- **THEN** a message with `Status = "Failed"` and `StatusMessage` set from `MessageInfolog` is sent to that queue

#### Scenario: Response entity is taken from the row, not hardcoded
- **WHEN** a response message is built for a reconciled row
- **THEN** its `Entity` is derived from the row's own entity fields rather than a fixed literal

#### Scenario: No response queue means no send
- **WHEN** a row is reconciled and its `RESPONSEQUEUE` is null or empty
- **THEN** no response message is sent and the reconciliation is still recorded as successful

#### Scenario: A response-send failure does not roll back the status
- **WHEN** sending the response message throws
- **THEN** the row retains its reconciled status
- **AND** the send failure is logged at Error and recorded as a processing step against the row

#### Scenario: A response-send failure does not redeliver the business event
- **WHEN** sending the response message throws after the status was already updated
- **THEN** the Service Bus business event is still completed, so the status update is not applied twice

### Requirement: Reconciliation give-up paths are observable and recoverable
Every path on which the system declines to reconcile an event SHALL emit a log at Warning or
above that identifies the Service Bus message id, the `BusinessEventId`, the correlation id where
known, and a truncated raw body. An event that cannot be correlated to any
`MESSAGEQUEUETABLE` row SHALL be dead-lettered with a reason rather than completed, so it is
recoverable.

#### Scenario: Unparseable body is dead-lettered with a reason
- **WHEN** the message body is not valid JSON
- **THEN** the message is dead-lettered with a reason identifying the parse failure
- **AND** a Warning log is emitted containing the message id and the truncated raw body

#### Scenario: Unmatched correlation id is dead-lettered
- **WHEN** the event's `CreatedTransactionId` matches no `MESSAGEQUEUETABLE.DESTINATIONID`
- **THEN** the message is dead-lettered with a reason identifying the unmatched correlation id
- **AND** a Warning log is emitted

#### Scenario: Unregistered business event is completed with a log
- **WHEN** an event arrives whose `BusinessEventId` the system does not handle
- **THEN** the message is completed and a log records the skipped `BusinessEventId`

#### Scenario: Terminal reconciliation writes an auditable processing step
- **WHEN** a terminal event reconciles a row
- **THEN** a processing step recording the event id, correlation id, observed outcome and resulting status is written against that row

### Requirement: Escalate stuck awaiting-confirmation messages
The system SHALL provide a scheduled sweep that escalates messages stuck at status `5` beyond a
configurable timeout to status `4` (Dead Letter) and raises an alert.

#### Scenario: Stuck message is escalated
- **WHEN** a message has `STATUS = 5` and `MODIFIEDDATETIME` older than the configured timeout
- **THEN** its status is set to `4` (Dead Letter) and an alert is emitted

#### Scenario: Escalated message can still be reconciled later
- **WHEN** a business event arrives for a message that was already escalated to `4`
- **THEN** the reconciliation still updates the status to the event's outcome
