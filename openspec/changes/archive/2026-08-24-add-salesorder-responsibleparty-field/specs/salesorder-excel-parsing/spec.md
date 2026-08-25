## MODIFIED Requirements

### Requirement: Assemble the target Capture sales-order JSON
The system SHALL assemble the root fields, line array, and trade-in array into a single JSON object matching the Capture sales-order shape consumed by the `SalesOrder_To_D365FSC` integration. The assembled root object SHALL include `ResponsibleParty` as an optional text field and SHALL set it to `""` when the workbook does not contain a matching column.

#### Scenario: Assembled order carries all blocks
- **WHEN** a valid sheet is fully parsed
- **THEN** the output JSON contains the root fields, a `Line` array of all line rows, and a `TradeAllowance` array

#### Scenario: ResponsibleParty column is present
- **WHEN** a valid sheet contains a root header column named `ResponsibleParty`
- **THEN** the assembled root JSON contains `ResponsibleParty` with the parsed text value from that column

#### Scenario: ResponsibleParty column is absent
- **WHEN** a valid sheet does not contain a root header column named `ResponsibleParty`
- **THEN** the assembled root JSON contains `ResponsibleParty` with the value `""`
