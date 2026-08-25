## Why

Sales-order simulation imports currently drop the `ResponsibleParty` root field because it is not part of the parser schema. Users need that field preserved in the parsed payload so the simulation output matches the source sales-order data more closely.

## What Changes

- Extend the sales-order Excel parser root schema to include `ResponsibleParty` as a first-class root field.
- Preserve the existing parser behavior for absent fields by defaulting `ResponsibleParty` to an empty string when the column is not present.
- Update workbook format documentation so `ResponsibleParty` is documented as a supported field instead of an ignored extra column.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `salesorder-excel-parsing`: expand the assembled root sales-order JSON shape to include the `ResponsibleParty` field when present in the workbook, defaulting it when absent.

## Impact

- Affected code: `ARM.Integration.Dashboard.API/Services/SalesOrderExcelParser.cs`
- Affected documentation: `Script/Docs/SalesOrderExcelImport.md`
- Downstream effect: parsed sales-order payloads sent through simulation will include a `ResponsibleParty` field in the root object, flowing unchanged through `SalesOrder_To_D365FSC`.
