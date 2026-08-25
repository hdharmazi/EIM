## Context

The sales-order Excel parser currently builds the root JSON object from a fixed schema in `SalesOrderExcelParser`. That schema includes `Responsible` but does not include `ResponsibleParty`, so workbooks that contain a `ResponsibleParty` column lose that value during parse. The downstream sales-order orchestration is pass-through, so the parser is the only place that needs behavior change.

## Goals / Non-Goals

**Goals:**
- Add `ResponsibleParty` to the parser's root sales-order schema.
- Preserve existing parser conventions by defaulting the field to an empty string when the workbook column is absent.
- Update the workbook format documentation to reflect the new supported field.

**Non-Goals:**
- Changing queue envelopes or `SalesOrder_To_D365FSC` behavior.
- Adding special-case conditional emission logic for `ResponsibleParty`.
- Introducing new parser DTOs, storage, or validation flows.

## Decisions

### Add `ResponsibleParty` as a standard root schema field
The parser should treat `ResponsibleParty` exactly like the other optional text root fields. This keeps the implementation consistent with the existing schema-driven parser design and requires only a small change to `RootSchema`. Alternative considered: emit the field only when present. Rejected because the parser already defaults absent schema fields and favors stable output shape.

### Keep downstream flow unchanged
No changes are needed outside the parser and documentation because the simulation service sends the assembled order JSON as-is and `SalesOrder_To_D365FSC` forwards it unchanged. Alternative considered: downstream mapping or transformation. Rejected because it would add complexity without benefit.

### Update workbook documentation alongside code
The workbook guide should stop describing `ResponsibleParty` as an ignored extra column and instead document it as a supported root field. Alternative considered: code-only change. Rejected because it would leave user-facing guidance incorrect.

## Risks / Trade-offs

- **Schema expansion changes output shape for all parsed orders** → Mitigation: accept the existing parser convention that absent text fields default to `""`, and keep the change explicitly documented.
- **No dedicated parser unit tests exist today** → Mitigation: keep the code change minimal and validate with project build plus manual parser/simulation verification if needed.
- **Downstream consumers could ignore or reject unexpected fields** → Mitigation: current orchestration is pass-through and the requested behavior is to preserve the field; monitor runtime behavior if external consumers apply stricter contracts.
