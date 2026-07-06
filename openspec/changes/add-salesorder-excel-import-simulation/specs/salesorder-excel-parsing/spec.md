## ADDED Requirements

### Requirement: One sheet represents one sales order
The system SHALL treat each worksheet in the uploaded workbook as a single sales order and SHALL use the worksheet name as the order's quote identifier.

#### Scenario: Multiple sheets are enumerated
- **WHEN** a workbook with several worksheets is uploaded
- **THEN** the system lists each worksheet as a separately selectable sales order identified by its sheet name

### Requirement: Fixed reserved-row block layout
The system SHALL read each sheet using a fixed row layout: row 1 = root header, row 2 = root data, rows 3–4 = optional trade-in header/data, row 5 = line header, rows 6+ = line data.

#### Scenario: Blocks are read from fixed rows
- **WHEN** a valid sheet is parsed
- **THEN** the root object is built from rows 1–2, line items from row 5's header and rows 6 onward, and the trade-in from rows 3–4 when present

### Requirement: Header-signature validation
The system SHALL validate that the root header (row 1) and the line header (row 5) match the expected header signatures before mapping, and SHALL reject the sheet with a clear error when they do not.

#### Scenario: Line header missing at row 5
- **WHEN** row 5 does not match the expected line-block header signature
- **THEN** the system rejects the sheet and reports which header row failed validation

### Requirement: Case-insensitive header mapping with canonical output
The system SHALL match Excel header cells to target field names case-insensitively and SHALL emit each field using the canonical target casing.

#### Scenario: Differently cased header maps correctly
- **WHEN** a header cell reads `ROUTEID`
- **THEN** the value is mapped to the field and written to the output JSON as `RouteId`

#### Scenario: Unknown extra columns are ignored
- **WHEN** the sheet contains a column with no matching target field
- **THEN** the system ignores that column without error

### Requirement: Validate values without translation
The system SHALL require values in their final form — ISO date text (`yyyy-MM-dd`) and literal enum values — and SHALL NOT translate codes into literals.

#### Scenario: Enum code rejected
- **WHEN** a field expecting a literal enum contains a raw code instead of the literal
- **THEN** the system fails the sheet and points to the offending sheet and cell

#### Scenario: Improperly formatted date rejected
- **WHEN** a date field cannot be read as a valid `yyyy-MM-dd` value
- **THEN** the system fails the sheet and points to the offending sheet and cell

### Requirement: Per-field type coercion and cell-level errors
The system SHALL coerce each cell to its declared field type (text, integer, decimal, or date), SHALL enforce required fields, and SHALL report validation failures with the sheet name, cell reference, and expected-versus-actual detail.

#### Scenario: Required field missing
- **WHEN** a required field's cell is blank
- **THEN** the system fails the sheet and identifies the missing field by sheet and cell

#### Scenario: Wrong type in a numeric field
- **WHEN** a numeric field contains non-numeric text
- **THEN** the system fails the sheet and reports the cell with expected type and actual value

### Requirement: Default absent fields
The system SHALL default target fields that are absent from the sheet to `0` for numeric types and `""` for string types.

#### Scenario: Absent numeric field defaults to zero
- **WHEN** a numeric target field has no corresponding column
- **THEN** the assembled order sets that field to `0`

#### Scenario: Absent string field defaults to empty
- **WHEN** a string target field has no corresponding column
- **THEN** the assembled order sets that field to `""`

### Requirement: Optional trade-in block
The system SHALL emit the trade-in as an array containing one object when rows 3–4 are populated, and SHALL emit an empty array when they are blank. The trade-in object SHALL contain `PBLId`, `ItemId`, `LineId`, `ProductLine`, `Make`, `Model`, `MfgYear`, `VinNumber`, `Amount`, `Warehouse`, and `LocationId`.

#### Scenario: No trade-in present
- **WHEN** rows 3–4 are blank
- **THEN** the assembled order sets `TradeAllowance` to `[]`

#### Scenario: One trade-in present
- **WHEN** rows 3–4 contain a trade-in header and data row
- **THEN** the assembled order sets `TradeAllowance` to an array with one object mapped from those rows

### Requirement: Assemble the target Capture sales-order JSON
The system SHALL assemble the root fields, line array, and trade-in array into a single JSON object matching the Capture sales-order shape consumed by the `SalesOrder_To_D365FSC` integration.

#### Scenario: Assembled order carries all blocks
- **WHEN** a valid sheet is fully parsed
- **THEN** the output JSON contains the root fields, a `Line` array of all line rows, and a `TradeAllowance` array
