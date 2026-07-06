## Context

The Dashboard simulation page (`ARM.Integration.Dashboard.Web` + `.API`) lets testers send sample messages to Service Bus queues and watch responses stream back over SSE. Each existing scenario builds a small payload from a handful of hand-entered fields (`SimulationService.RunAsync` → `Send` → `Listen`).

Sales orders can't work that way — one trailer spec is a header plus 50–80 line items plus an optional trade-in. The `SalesOrder_To_D365FSC` integration already exists and consumes a Capture envelope (`Entity = "SalesOrderQueue"`, `Action = "New"`, `Data = <order JSON with dataAreaId>`) from the Capture → D365 queue, lifting `dataAreaId` into `_companyId`. What's missing is a way to *produce* that message from a real exported order.

Source data arrives as an `.xlsx` workbook: **one sheet per sales order**, sheet name = quote number. Each sheet is a flattened relational export with a fixed stacked layout (verified across 5 sample sheets):

```
R1  PBL/root header        (always)
R2  PBL/root data (1 row)  (always)
R3  TradeAllowance header  ┐ present → exactly 1 trade-in
R4  TradeAllowance data    ┘ blank   → no trade-in
R5  Line header            (always — never shifts)
R6+ Line data (N rows)
```

Constraints: reuse the existing simulation Send/Listen pipeline; keep the importer as simple as possible; fail loudly (never silently mis-map) on bad data.

## Goals / Non-Goals

**Goals:**
- Add one new simulation scenario that imports an `.xlsx`, lets the user pick one sheet, previews the mapped order, and sends it to the Capture → D365 queue.
- Parse server-side into the target Capture sales-order JSON with precise, cell-level validation errors.
- Keep the mapping layer minimal by aligning Excel headers to JSON keys (case-insensitive) and validating-not-translating values.
- Model the trade-in block now; emit `[]` until data exists.

**Non-Goals:**
- No change to `SalesOrder_To_D365FSC`, the DB, or Service Bus contracts.
- No enum/code translation tables (values must already be final literals).
- No multi-sheet/batch runs (single order per run).
- No new persistent storage — the flow is stateless.
- No handling of the async business-event reconciliation (out of scope for the simulator; it just sends + listens like other scenarios).

## Decisions

### D1 — Server-side parse (not browser)
Parse in `ARM.Integration.Dashboard.API` with an Excel reader library (**ClosedXML** preferred for a simpler API; OpenXML SDK acceptable). Keeps the mapping/validation logic in one C# place and off the client. *Alternative — SheetJS in the browser:* rejected; would duplicate mapping logic and weaken validation.

### D2 — Fixed reserved-row layout, validated by header text
Read blocks by fixed position (R1/R2 root, R3/R4 trade-in, R5 line header, R6+ lines). This is safe because the trade-in block is capped at exactly 2 rows, so the line header never moves. Before mapping, **assert** R1 and R5 match expected header signatures; reject the sheet otherwise. *Alternative — header-signature scanning:* unnecessary given fixed sizes. *Alternative — explicit Excel markers:* rejected; adds manual burden.

### D3 — Case-insensitive header → field mapping, canonical output casing
Match header cells to target field names case-insensitively (lowercase both sides); emit the canonical target casing so D365 receives e.g. `RouteId`. Because headers are aligned to JSON keys, no rename map is needed for most columns. Remaining real renames are Excel-side (see D8). Within a block, headers must be unique when lowercased.

### D4 — Validate, don't translate
Values must arrive in final form: dates as ISO text (`yyyy-MM-dd`), enums as literals (`SOUsage = "Purchased"`, `IsUsedTrailer = "No"`). The parser coerces each cell to its schema type and validates; on any mismatch it aborts the sheet with `sheet + cell + expected vs got`. No enum lookup tables. *Alternative — translate codes→literals:* rejected for simplicity; would require maintaining full code tables.

### D5 — One field-schema table per block drives coerce + validate + error
Each block (root, line, trade-in) has a small in-code table: `field → type (text|int|decimal|date) → required?`. One loop uses it to coerce cells, validate, and produce cell-level errors. Output is a `JsonObject`/`JsonArray` — **no DTO classes**. Fields present in the schema but absent from the sheet are defaulted: `0` for numeric, `""` for string; trade-in absent → `[]`.

### D6 — `dataAreaId` from user input (default `WTC`)
`dataAreaId` is not in the workbook. The scenario UI provides a text field (default `WTC`); the value is injected into the assembled order's `Data` so `SalesOrder_To_D365FSC` can lift it to `_companyId`.

### D7 — Stateless two-step over the existing pipeline
(1) **Upload/parse** endpoint accepts the `.xlsx`, returns per-sheet metadata (sheet name + preview: PBLId, CustName, Quantity, line count) and the fully-mapped order JSON per sheet — or the validation error. (2) The **run** step reuses the existing SSE `Send`/`Listen`: the browser posts the chosen order + `dataAreaId` to a new `capture-salesorder-import` branch in `SimulationService`, which builds the `SalesOrderQueue`/`New` envelope, sends to the Capture → D365 queue, and listens on the response queue as other scenarios do. No server-side file cache.

### D8 — Excel authoring changes (documented, user-owned)
To keep mapping generic, source workbooks need: header renames — root `RequestedDate→RequestDate`, `ResponsibleName→Responsible` (case-only `ROUTEID` handled automatically); line `PickName→Name`, `ValueString→Pick`, `ValueNumeric→Qty`, `ValuePrice→Price`, `ValueWeight→Weight`, `ValueComment→Comment`; trade-in header named per schema. Formatting — dates as ISO text, string-like-numeric (`CustPONumber`, `PBLId`) as Text cells, empty cells left blank (not the literal `NULL`). UsedTrailer fields (`VIN/ProductLine/Make/Model/ItemId/Warehouse/Location`) ride in as ordinary root columns when added later.

### D9 — Trade-in modeled now, emitted empty until present
Trade-in JSON shape: `{ PBLId, ItemId, LineId, ProductLine, Make, Model, MfgYear, VinNumber, Amount, Warehouse, LocationId }` with schema `PBLId text!, ItemId text, LineId int, ProductLine text, Make text, Model text, MfgYear int, VinNumber text, Amount decimal, Warehouse text, LocationId text`. R3 non-empty → array of one; blank → `[]`.

## Risks / Trade-offs

- **Excel authoring drift** (wrong casing, stray `NULL`, date serials) → Mitigated by case-insensitive matching, header-signature assertions, and fail-fast cell-level errors that name the offending sheet/cell.
- **Line header shifts if trade-in ever exceeds 2 rows** → Mitigated by the documented invariant (max one trade-in). If that ever changes, revisit D2 (switch to header-signature scan for the line block).
- **New library dependency (ClosedXML)** → Low risk; mature, MIT-licensed, no native deps. OpenXML SDK is a fallback with zero third-party deps.
- **Defaulting hides missing data** (e.g. `SalesPrice` absent → `0`) → Acceptable for a simulator; required fields are enforced by the schema so structural gaps still error.
- **Large payloads over SSE** → Same pattern already used; one order (~60 lines) is well within limits.
