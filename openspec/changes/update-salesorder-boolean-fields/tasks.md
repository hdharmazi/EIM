## 1. Type system

- [x] 1.1 Add `YesNo` to the `SalesOrderFieldType` enum in `ARM.Integration.Dashboard.API/Models/SalesOrderImportModels.cs`

## 2. Parser coercion and defaults

- [x] 2.1 Retype `FreightTaxable`, `DrawingRequired`, `DepositReqd`, `PaintSign`, `CustSuppTires`, `PriceLock` from `SalesOrderFieldType.Int` to `SalesOrderFieldType.YesNo` in `RootSchema` in `SalesOrderExcelParser.cs`
- [x] 2.2 Add a `YesNo` case to `CoerceCell` that accepts `Yes`/`No`, `True`/`False`, `1`/`0` (case-insensitive), emits the literal string `"Yes"`/`"No"`, treats blank as `"No"`, and throws a `SalesOrderCellException` (with sheet + cell + expected-versus-actual) for any other non-blank token
- [x] 2.3 Add a `YesNo` case to `DefaultFor` returning `JsonValue.Create("No")`

## 3. Sample data

- [x] 3.1 Update `Script/Docs/Capture1.json` so `FreightTaxable`, `DrawingRequired`, `DepositReqd`, `PaintSign`, `CustSuppTires`, `PriceLock` use the strings `"Yes"`/`"No"` (per the authoritative values: `DepositReqd` and `PriceLock` = `"Yes"`, the rest = `"No"`)

## 4. Verify

- [x] 4.1 Build `ARM.Integration.Dashboard.API` and confirm no compile errors
- [x] 4.2 Parse a sample sheet with `Yes`/`No` flag values and confirm the assembled JSON emits `"Yes"`/`"No"` for the six fields
- [x] 4.3 Confirm an unrecognized flag token fails the sheet with a cell-level error
