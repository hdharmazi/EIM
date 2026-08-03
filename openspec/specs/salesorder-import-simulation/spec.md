# salesorder-import-simulation Specification

## Purpose

Provide a Dashboard simulation scenario that uploads an Excel workbook, previews and selects a sheet, and simulates Capture sending the assembled sales order to the Capture → D365 queue with streamed results.

## Requirements

### Requirement: Excel-import sales-order simulation scenario
The system SHALL provide a Dashboard simulation scenario that lets a user upload an `.xlsx` workbook, select one sheet, and simulate Capture sending that sales order to the Capture → D365 queue.

#### Scenario: Scenario is available on the simulation page
- **WHEN** a user opens the simulation page
- **THEN** a "Capture Sales Order (Excel import)" scenario is available for selection

### Requirement: Upload and preview sheets
The system SHALL accept an uploaded workbook, parse it server-side, and return a per-sheet preview showing the quote/sheet name, `PBLId`, customer name, quantity, and line-item count. When a sheet fails validation, the system SHALL return its cell-level error instead of a preview.

#### Scenario: Valid workbook yields previews
- **WHEN** a user uploads a workbook whose sheets pass validation
- **THEN** the system returns a selectable list of sheets each showing its preview summary

#### Scenario: Invalid sheet surfaces a precise error
- **WHEN** an uploaded sheet fails validation
- **THEN** the system reports the sheet name and offending cell rather than a preview for that sheet

### Requirement: Single-order selection per run
The system SHALL allow the user to select exactly one sheet to simulate per run.

#### Scenario: One sheet is chosen
- **WHEN** the user selects a sheet and starts the run
- **THEN** only that sheet's assembled order is sent

### Requirement: dataAreaId input with default
The system SHALL provide a `dataAreaId` input field defaulting to `WTC`, and SHALL inject its value into the assembled order before sending.

#### Scenario: Default company id used
- **WHEN** the user starts a run without changing the `dataAreaId` field
- **THEN** the assembled order is sent with `dataAreaId` set to `WTC`

#### Scenario: Overridden company id used
- **WHEN** the user sets `dataAreaId` to another value before starting the run
- **THEN** the assembled order is sent with that value

### Requirement: Send envelope to the Capture → D365 queue
The system SHALL wrap the assembled order in an envelope with `Entity = "SalesOrderQueue"` and `Action = "New"` and SHALL send it to the Capture → D365 queue using the existing simulation send behavior.

#### Scenario: Order is enqueued
- **WHEN** a run is started for a selected sheet
- **THEN** the system sends a `SalesOrderQueue`/`New` envelope carrying the assembled order to the Capture → D365 queue and streams a success event

### Requirement: Stream results like other scenarios
The system SHALL stream the payload, send confirmation, and response-queue listening as server-sent events, consistent with the existing simulation scenarios.

#### Scenario: Run streams events
- **WHEN** a run executes
- **THEN** the system streams the payload, a send-success event, and the response-queue listen result to the browser
