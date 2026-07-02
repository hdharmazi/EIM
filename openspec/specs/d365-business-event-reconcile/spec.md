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
and update the existing row's status based on `MessageState`.

#### Scenario: Processed event marks the row completed
- **WHEN** a reconciliation event has `MessageState = "Processed"` and a matching `DESTINATIONID`
- **THEN** the row status is set to `2` (Completed)

#### Scenario: Failed or cancelled event marks the row failed
- **WHEN** a reconciliation event has `MessageState = "Failed"` or `"Cancelled"`
- **THEN** the row status is set to `3` (Failed) and `MessageInfolog` is written to the message log

#### Scenario: Reconciliation does not create a new message
- **WHEN** a reconciliation event is processed
- **THEN** no new `MESSAGEQUEUETABLE` row is inserted; only the correlated row is updated

#### Scenario: Late or duplicate events are handled idempotently
- **WHEN** a reconciliation event arrives after the row was already escalated or updated
- **THEN** the update is applied idempotently without error

### Requirement: Escalate stuck awaiting-confirmation messages
The system SHALL provide a scheduled sweep that escalates messages stuck at status `5` beyond a
configurable timeout to status `4` (Dead Letter) and raises an alert.

#### Scenario: Stuck message is escalated
- **WHEN** a message has `STATUS = 5` and `MODIFIEDDATETIME` older than the configured timeout
- **THEN** its status is set to `4` (Dead Letter) and an alert is emitted

#### Scenario: Escalated message can still be reconciled later
- **WHEN** a business event arrives for a message that was already escalated to `4`
- **THEN** the reconciliation still updates the status to the event's outcome
