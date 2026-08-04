## ADDED Requirements

### Requirement: Yes/No flag fields
The system SHALL treat the sales-order flag fields `FreightTaxable`, `DrawingRequired`, `DepositReqd`, `PaintSign`, `CustSuppTires`, and `PriceLock` as yes/no flag fields whose assembled JSON value is the literal string `"Yes"` or `"No"`.

#### Scenario: Yes/No normalized to canonical casing
- **WHEN** a flag field's cell contains `yes` or `no` (in any letter casing)
- **THEN** the assembled order sets that field to the literal string `"Yes"` or `"No"` respectively

#### Scenario: True/False and 1/0 accepted
- **WHEN** a flag field's cell contains `True`/`False` or `1`/`0`
- **THEN** the assembled order sets that field to `"Yes"` or `"No"` respectively

#### Scenario: Unrecognized flag token rejected
- **WHEN** a flag field's cell contains a non-blank value that is not a recognized yes/no token
- **THEN** the system fails the sheet and reports the offending sheet and cell with expected-versus-actual detail

## MODIFIED Requirements

### Requirement: Per-field type coercion and cell-level errors
The system SHALL coerce each cell to its declared field type (text, integer, decimal, date, or yes/no flag), SHALL enforce required fields, and SHALL report validation failures with the sheet name, cell reference, and expected-versus-actual detail. For yes/no flag fields the system SHALL accept `Yes`/`No`, `True`/`False`, and `1`/`0` case-insensitively and emit the literal string `"Yes"` or `"No"`.

#### Scenario: Required field missing
- **WHEN** a required field's cell is blank
- **THEN** the system fails the sheet and identifies the missing field by sheet and cell

#### Scenario: Wrong type in a numeric field
- **WHEN** a numeric field contains non-numeric text
- **THEN** the system fails the sheet and reports the cell with expected type and actual value

#### Scenario: Wrong token in a yes/no field
- **WHEN** a yes/no flag field contains a non-blank value that is not a recognized token
- **THEN** the system fails the sheet and reports the cell with expected type and actual value

### Requirement: Default absent fields
The system SHALL default target fields that are absent from the sheet to `0` for numeric types, `"No"` for yes/no flag types, and `""` for string types.

#### Scenario: Absent numeric field defaults to zero
- **WHEN** a numeric target field has no corresponding column
- **THEN** the assembled order sets that field to `0`

#### Scenario: Absent yes/no field defaults to No
- **WHEN** a yes/no flag target field has no corresponding column or its cell is blank
- **THEN** the assembled order sets that field to `"No"`

#### Scenario: Absent string field defaults to empty
- **WHEN** a string target field has no corresponding column
- **THEN** the assembled order sets that field to `""`
