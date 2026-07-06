## Why

Sales orders carry far too much data (a single trailer spec is 50–80 line items plus header and optional trade-in) to hand-enter on the existing simulation page. Testers need a way to replay real Capture sales orders — exported to Excel — through the Capture → D365 queue so the `SalesOrder_To_D365FSC` integration can be exercised end to end without manual data entry.

## What Changes

- Add a new **"Capture Sales Order (Excel import)"** scenario to the existing Dashboard simulation page.
- User uploads an `.xlsx` workbook where **each sheet is one sales order** (sheet name = quote number) and picks **one** sheet to simulate.
- The API parses the selected sheet server-side using a **fixed reserved-row layout** (R1/R2 = header, R3/R4 = optional trade-in, R5 = line header, R6+ = lines), **validated by header text**.
- Column headers are matched **case-insensitively** to the target Capture JSON keys; the parser emits the canonical target casing.
- Values are **validated, not translated**: dates must be ISO text, enums must be final literals; any bad/mismatched cell **fails the sheet with a precise `sheet + cell` error**.
- Fields absent from the workbook are **defaulted** (`0` numeric / `""` string); `dataAreaId` is supplied by a **user input field (default `WTC`)**.
- The assembled order is wrapped in the standard envelope (`Entity = "SalesOrderQueue"`, `Action = "New"`), **sent to the Capture → D365 queue**, and the run **listens on the response queue** like other simulation scenarios.
- Trade-in block is **modeled now** (per the provided TradeAllowance schema) but emits `[]` until sheets contain trade-in data.

## Capabilities

### New Capabilities
- `salesorder-excel-parsing`: Reads an uploaded workbook into the target Capture sales-order JSON — fixed-row block detection, header-signature validation, case-insensitive header-to-field mapping, per-field type coercion/validation with precise cell-level errors, defaulting of absent fields, and modeling of the optional trade-in block.
- `salesorder-import-simulation`: The Dashboard simulation scenario that drives the parse — sheet listing/selection, `dataAreaId` input, order preview, and sending the assembled envelope to the Capture → D365 queue with the existing send/listen streaming behavior.

### Modified Capabilities
<!-- None. The existing simulation scenarios are not tracked as OpenSpec specs, and the SalesOrder_To_D365FSC integration behavior is unchanged. -->

## Impact

- **`ARM.Integration.Dashboard.API`**: new Excel-parsing service + models, a new upload/parse endpoint, and a new branch in `SimulationService` for the import scenario. New dependency on an Excel reader library (e.g. ClosedXML or the OpenXML SDK).
- **`ARM.Integration.Dashboard.Web`**: new simulation scenario UI (file upload, sheet picker, `dataAreaId` field, preview panel) reusing the existing SSE log display.
- **No change** to `ARM.Integration.EIM` / `SalesOrder_To_D365FSC` or the Service Bus / DB contracts — this feature only *produces* the message an operator would otherwise send from Capture.
- **Excel authoring**: source workbooks need a small set of header renames (documented in design) so headers align with the target JSON keys.
