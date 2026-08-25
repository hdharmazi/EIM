## 1. Parser schema update

- [x] 1.1 Add `ResponsibleParty` as an optional text root field in `SalesOrderExcelParser.RootSchema`
- [x] 1.2 Preserve existing defaulting behavior so parsed orders emit `ResponsibleParty` as `""` when the column is absent

## 2. Documentation update

- [x] 2.1 Update `Script/Docs/SalesOrderExcelImport.md` to document `ResponsibleParty` as a supported root field instead of an ignored extra column

## 3. Validation

- [x] 3.1 Build the affected project(s) to verify the parser/documentation change does not introduce errors
