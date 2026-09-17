## Context

The sales-order Excel parser ([SalesOrderExcelParser.cs](../../../ARM.Integration.Dashboard.API/Services/SalesOrderExcelParser.cs)) maps workbook cells to the Capture sales-order JSON using a small type system defined by `SalesOrderFieldType` (`Text`, `Int`, `Decimal`, `Date`) in [SalesOrderImportModels.cs](../../../ARM.Integration.Dashboard.API/Models/SalesOrderImportModels.cs). Six flag fields are currently declared `Int`, so `0`/`1` is emitted instead of the expected `Yes`/`No`. The authoritative field reference shows these as yes/no flags, so the parser must model them as a yes/no flag type whose output is the literal string `"Yes"`/`"No"`.

## Goals / Non-Goals

**Goals:**
- Add a first-class yes/no flag field type to the parser's type system.
- Retype `FreightTaxable`, `DrawingRequired`, `DepositReqd`, `PaintSign`, `CustSuppTires`, `PriceLock` to the yes/no type.
- Accept `Yes`/`No`, `True`/`False`, `1`/`0` (case-insensitive); emit the literal string `"Yes"`/`"No"`; default absent/blank to `"No"`.

**Non-Goals:**
- Changing any other field's type or the fixed row-block layout.
- Altering the frontend simulation scenario field definitions.
- Reworking downstream D365 mapping beyond the JSON value change.

## Decisions

- **Add `YesNo` to `SalesOrderFieldType` rather than reuse `Int` or a JSON boolean.** A dedicated enum value keeps coercion, defaulting, and error messages type-accurate and lets `CoerceCell`/`DefaultFor` emit the canonical `"Yes"`/`"No"` strings the consumer expects. Alternatives (keep `Int` with 0/1, or emit JSON `true`/`false`) were rejected because the authoritative value is the literal `Yes`/`No` text.
- **Accept a fixed token set (`yes/no`, `true/false`, `1/0`), case-insensitive.** Mirrors the existing `NULL`→blank handling and the parser's "validate, don't translate" stance while tolerating common spreadsheet spellings. Any other non-blank value is a hard error pointing at the cell.
- **Default absent/blank to `"No"`.** Consistent with existing numeric-`0`/string-`""` defaulting so absent columns never fail the sheet.
- **Emit `JsonValue.Create("Yes")` / `JsonValue.Create("No")`** from `CoerceCell` and `DefaultFor`, producing the canonical string value in the assembled order.

## Risks / Trade-offs

- Output contract change: six fields go from numeric `0`/`1` to the literal strings `"Yes"`/`"No"`. The `SalesOrder_To_D365FSC` consumer expects these strings (confirmed), and the sample [sales-order-capture.json](../../../docs/samples/sales-order-capture.json) is refreshed as part of this change to match.
- Existing workbooks containing `0`/`1` in these columns keep working because those tokens remain accepted.
- Unexpected free-text in a flag cell now fails the sheet instead of silently defaulting; this is intended (surfaces bad data) but is stricter than before.
