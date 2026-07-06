## 1. Excel parsing library & models (API)

- [x] 1.1 Add an Excel reader package (ClosedXML preferred) to `ARM.Integration.Dashboard.API.csproj`
- [x] 1.2 Add API models: sheet-preview DTO (sheet name, PBLId, CustName, Quantity, line count), parse-result DTO (previews + per-sheet mapped order JSON or error), and a cell-level parse-error type (sheet, cell ref, message)

## 2. Field schemas & mapping

- [x] 2.1 Define the root/PBL field schema (name → type text/int/decimal/date, required) including canonical target casing
- [x] 2.2 Define the line-block field schema (`LineNum`, `ObjExcludeFromBOM`, `PBLId`, `Name`, `Pick`, `Qty`, `Price`, `Weight`, `Comment`)
- [x] 2.3 Define the trade-in field schema (`PBLId!`, `ItemId`, `LineId int`, `ProductLine`, `Make`, `Model`, `MfgYear int`, `VinNumber`, `Amount decimal`, `Warehouse`, `LocationId`)
- [x] 2.4 Implement case-insensitive header→field resolution that emits canonical casing and ignores unknown columns
- [x] 2.5 Implement per-field coercion + validation (text/int/decimal/date, required) that throws a cell-level error with sheet, cell, expected vs actual

## 3. Sheet parser (API)

- [x] 3.1 Implement fixed-row block reading (R1/R2 root, R3/R4 trade-in, R5 line header, R6+ lines)
- [x] 3.2 Add header-signature validation for the root (R1) and line (R5) headers; reject the sheet on mismatch
- [x] 3.3 Build the root object with defaulting of absent fields (`0`/`""`)
- [x] 3.4 Build the `Line` array from rows 6+ using the line schema
- [x] 3.5 Build the `TradeAllowance` array (one object when R3/R4 populated, else `[]`)
- [x] 3.6 Assemble the final Capture sales-order JSON object per sheet
- [x] 3.7 Enumerate all worksheets and return previews + mapped orders (or errors) per sheet

## 4. Upload endpoint & simulation branch (API)

- [x] 4.1 Add an upload/parse endpoint on `SimulationController` that accepts the `.xlsx` and returns the parse-result DTO
- [x] 4.2 Add a `capture-salesorder-import` branch in `SimulationService.RunAsync` that accepts the chosen order JSON + `dataAreaId`
- [x] 4.3 Inject `dataAreaId` into the order `Data`, wrap in an `Entity = "SalesOrderQueue"`, `Action = "New"` envelope, `Send` to the Capture → D365 queue, then `Listen` on the response queue

## 5. Dashboard UI (Web)

- [x] 5.1 Add the "Capture Sales Order (Excel import)" scenario with a file-upload control and a `dataAreaId` field (default `WTC`)
- [x] 5.2 Call the upload/parse endpoint, render the sheet picker (single-select) with preview summaries, and surface per-sheet validation errors
- [x] 5.3 On run, post the selected order + `dataAreaId` to the existing SSE run flow and reuse the streaming log display

## 6. Docs & validation

- [x] 6.1 Document the required Excel header renames and formatting rules (dates as ISO text, literals for enums, Text cells for string-like-numeric, blank cells not `NULL`) in `Script/Docs`
- [x] 6.2 Build the solution and resolve any errors
- [x] 6.3 Validate the change with `openspec validate add-salesorder-excel-import-simulation --strict`
