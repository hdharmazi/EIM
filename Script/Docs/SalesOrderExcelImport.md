# Sales Order Excel Import — Workbook Format

The simulation page scenario **"Capture → Sales Order (Excel import)"** replays a real
Capture sales order through the Capture → D365 queue. It reads an `.xlsx` workbook where
**each worksheet is one sales order** (the sheet name is the quote number) and lets you
pick one sheet to send.

The parser is strict: it **validates values, it does not translate them**. Any bad or
mismatched cell fails that sheet with a precise `sheet + cell` error.

## Sheet layout (fixed rows)

| Row(s) | Block | Notes |
|--------|-------|-------|
| 1 | Root header | Column names (see renames below) |
| 2 | Root data | One row |
| 3 | Trade-in header | Optional — leave blank if no trade-in |
| 4 | Trade-in data | One row (only one trade-in per order) |
| 5 | Line header | Always row 5 — never moves |
| 6+ | Line data | One row per line item |

The line header **must** stay on row 5. The trade-in block is fixed at two rows, so it never
pushes the line block down.

## Header renames

Headers are matched **case-insensitively** (`ROUTEID` = `RouteId`), but these columns are
*different words* and must be renamed so they match the target JSON keys:

**Root block**

| Current header | Rename to |
|----------------|-----------|
| `RequestedDate` | `RequestDate` |
| `ResponsibleName` | `Responsible` |

**Line block**

| Current header | Rename to |
|----------------|-----------|
| `PickName` | `Name` |
| `ValueString` | `Pick` |
| `ValueNumeric` | `Qty` |
| `ValuePrice` | `Price` |
| `ValueWeight` | `Weight` |
| `ValueComment` | `Comment` |

**Trade-in block** — name the header cells exactly:
`PBLId, ItemId, LineId, ProductLine, Make, Model, MfgYear, VinNumber, Amount, Warehouse, LocationId`

Unknown/extra columns (e.g. `ResponsibleParty`) are ignored.

## Cell formatting rules

| Kind | Store as | Why |
|------|----------|-----|
| Dates (`RequestDate`) | **Text** `yyyy-MM-dd` (e.g. `2024-11-09`) | Not an Excel date serial — parser validates the text |
| Enums (`SOUsage`, `IsUsedTrailer`) | **Literal** values (`Purchased`, `No`) | The parser does not translate codes |
| String-like-numeric (`CustPONumber`, `PBLId`) | Cell format **Text** | Keeps `0` as `"0"` |
| Real numbers (`Qty`, `Price`, `Weight`, `TotalPrice`, `Amount`, `MfgYear`, …) | **Number** cells | Become JSON numbers |
| Empty values | Leave the cell **blank** | Do **not** type the word `NULL` |

## Fields defaulted when absent

Fields in the target JSON that have no column are defaulted automatically: `0` for numeric
fields, `""` for string fields. This includes the UsedTrailer fields
(`VIN`, `ProductLine`, `Make`, `Model`, `ItemId`, `Warehouse`, `Location`) — add those as
ordinary root columns when you have used-trailer data.

## dataAreaId

`dataAreaId` is **not** read from the workbook. Enter it on the simulation page (default
`WTC`); it is injected into the order before sending and lifted to `_companyId` by the
`SalesOrder_To_D365FSC` integration.
