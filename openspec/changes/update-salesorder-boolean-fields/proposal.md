## Why

Six sales-order flag fields in the Excel-import simulation (`FreightTaxable`, `DrawingRequired`, `DepositReqd`, `PaintSign`, `CustSuppTires`, `PriceLock`) are declared as integers, but their real-world values are yes/no flags (e.g. `DepositReqd = Yes`, `PriceLock = Yes`, the rest `No`). Typing them as integers mis-represents the data and emits `0`/`1` instead of the expected `Yes`/`No` value, so the parser must treat them as yes/no flags to produce the correct value.

## What Changes

- Introduce a yes/no flag field type in the sales-order Excel parser's type system.
- Retype the six flag fields (`FreightTaxable`, `DrawingRequired`, `DepositReqd`, `PaintSign`, `CustSuppTires`, `PriceLock`) from integer to the yes/no flag type.
- Coerce cell values case-insensitively: `Yes`/`No`, `True`/`False`, and `1`/`0` all normalize to the literal output strings `"Yes"`/`"No"`; a blank/absent field defaults to `"No"`.
- Reject cells that are neither a recognized yes/no token nor blank, reporting the offending sheet and cell.

## Capabilities

### New Capabilities
<!-- None -->

### Modified Capabilities
- `salesorder-excel-parsing`: adds a yes/no flag field type to per-field coercion and defaulting, and reclassifies the six flag fields from integer to the yes/no type.

## Impact

- Code: `ARM.Integration.Dashboard.API/Models/SalesOrderImportModels.cs` (`SalesOrderFieldType` enum), `ARM.Integration.Dashboard.API/Services/SalesOrderExcelParser.cs` (root schema field types, `CoerceCell`, `DefaultFor`).
- Output contract: the six fields change from numeric `0`/`1` to the literal strings `"Yes"`/`"No"` in the assembled Capture sales-order JSON consumed by the `SalesOrder_To_D365FSC` integration.
- Sample data: `Script/Docs/Capture1.json` is updated so the six flag fields use `"Yes"`/`"No"` instead of `0`.
- No new dependencies; frontend simulation field definitions are unaffected.
